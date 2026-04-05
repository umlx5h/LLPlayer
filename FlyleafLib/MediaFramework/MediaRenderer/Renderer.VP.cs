using System.Text.Json.Serialization;
using System.Windows;

using WPoint = System.Windows.Point;

using Vortice.Direct3D11;
using Vortice.Mathematics;

using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaStream;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer : IVP
{
    public event EventHandler ViewportChanged;

    public VideoProcessors  VideoProcessor  { get; private set; } = VideoProcessors.Auto; // Ensures we catch the 'change' initially
    public Viewport         Viewport        { get; private set; }
    public int              ControlWidth    { get; private set; }
    public int              ControlHeight   { get; private set; }
    public uint             VisibleWidth    { get; private set; }
    public uint             VisibleHeight   { get; private set; }
    public AspectRatio      DAR             { get; private set; }

    VideoStream     scfg;

    CropRect        crop;
    uint            rotation;

    public int      SideXPixels     => sideXPixels;
    public int      SideYPixels     => sideYPixels;
    int             sideXPixels, sideYPixels;
    internal double curRatio, keepRatio, fillRatio;

    bool            canFL, canD3;
    VPRequestType   vpRequestsIn, vpRequests; // In: From User | ProcessRequests Copy

    internal unsafe delegate VideoFrame FillPlanesDelegate(ref AVFrame* frame);
    internal FillPlanesDelegate FillPlanes;

    void IVP.VPRequest(VPRequestType request)
        => VPRequest(request);
    internal void VPRequest(VPRequestType request)
    {
        vpRequestsIn |= request;
        RenderRequest();
    }

    VideoProcessors VPSelection()
    {
        /* D3 Pros
         * - Performance & Power Efficiency
         * - Deinterlace & Super Resolution     ** Might Switch VP
         * - Scaling (?) & Extra/More Accurate Filters (?)
         * 
         * D3 Cons
         * - Extra rendering steps for SW Frames
         * 
         * FL Pros
         * - Alpha Channel & Split Frame Alpha  ** Might Switch VP
         * - HV Flip                            ** Might Switch VP
         * - HDR to SDR
         * 
         * Sws Pros
         * - More Pixel Formats (e.g. Paletted, BE, higher bit depth?)
         * - More Scaling Algorithms (but we don't use them currently as FillPlanes is at pre-Scale stage *only for Bitmap subs)
         * 
         * Sws Cons
         * - Slow Performance (CPU/RAM)
         */

        canD3 = vp != null && (VideoDecoder.VideoAccelerated ||
            (!scfg.VFlip && scfg.PixelComp0Depth == 8));
        
        canFL = VideoDecoder.VideoAccelerated ||
            (!scfg.PixelFormatDesc->flags.HasFlag(PixFmtFlags.Pal) && (!scfg.PixelFormatDesc->flags.HasFlag(PixFmtFlags.Be) || scfg.PixelComp0Depth <= 8));

        if (ucfg.VideoProcessor == VideoProcessors.D3D11    && canD3)
            return VideoProcessors.D3D11;

        if (ucfg.VideoProcessor == VideoProcessors.Flyleaf  && canFL)
            return VideoProcessors.Flyleaf;

        if (ucfg.VideoProcessor == VideoProcessors.SwsScale) // always support?
            return VideoProcessors.SwsScale;

        // Auto Selection | TBR: (Maybe AutoD3D11, AutoFlyleaf or user selection with callback?)

        var fieldType = ucfg.DeInterlace == DeInterlace.Auto ? scfg.FieldOrder : (VideoFrameFormat)ucfg.DeInterlace;

        if (canFL &&
            // Alpha | Split Frame Alpha | HV Flip | BT.2020
            (!canD3 || scfg.ColorSpace == ColorSpace.Bt2020 || ucfg.hflip || ucfg.vflip || ucfg.SplitFrameAlphaPosition != SplitFrameAlphaPosition.None || scfg.PixelFormatDesc->flags.HasFlag(PixFmtFlags.Alpha)) ||
            // SW w/o Deinterlace | Super Resolution
            (!VideoDecoder.VideoAccelerated && fieldType == VideoFrameFormat.Progressive && !ucfg.SuperResolution))
            return VideoProcessors.Flyleaf;

        if (canD3)
            return VideoProcessors.D3D11;

        return VideoProcessors.SwsScale;
    }

    internal bool VPConfig(VideoStream videoStream, AVFrame* frame)
    {
        lock (lockRenderLoops)
            Frames.SetRendererFrame(null);
        
        scfg = videoStream;
        VPConfigHelper();

        return true; // todo
    }
    internal void VPSwitchCheck()
    {   // User's VP Switch
        if (scfg != null && ucfg.VideoProcessor != VideoProcessor && VideoProcessor != VPSelection())
            VPRequest(VPRequestType.ReConfigVP);
    }
    void VPSwitch()
    {   // Called from ProcessRequests (RenderLoop) | lockRenderLoops
        bool wasRunning = VideoDecoder.IsRunning;
        if (wasRunning)
            VideoDecoder.Pause(); // don't call me from lock (Frames) - deadlock with Runinternal

        lock(Frames)
        {
            var oldvp = VideoProcessor;
            VPConfigHelper(request: false); // this comes from RenderLoop

            if (oldvp == VideoProcessor)
                return;

            if (!VideoDecoder.VideoAccelerated)
            {
                Frames.Dispose();

                // Clear Screen
                context.OMSetRenderTargets(SwapChain.BackBufferRtv);
                context.ClearRenderTargetView(SwapChain.BackBufferRtv, ucfg.flBackColor);
                SwapChain.Present(1, Vortice.DXGI.PresentFlags.None);

                return;
            }

            bool renderFrameDone = false;
            var curFrame = Frames.First;
            while (curFrame != null)
            {
                if (Frames.RendererFrame != curFrame)
                    renderFrameDone = true;

                VPSwitchFrame(curFrame);
                curFrame = curFrame.Next;
            }

            if (!renderFrameDone && Frames.RendererFrame != null)
                VPSwitchFrame(Frames.RendererFrame);

            if (wasRunning)
                VideoDecoder.Start();
        }
    }
    void VPSwitchFrame(VideoFrame mFrame)
    {   // TBR: Cannot run in parallel with Fill Frames!!! (same txt/srv/swsframe etc..)
        mFrame.DisposeTexture();

        if (mFrame.AVFrame != null) // HW Frame
        {
            if (VideoProcessor == VideoProcessors.D3D11)
            {
                vpivd.Texture2D.ArraySlice = (uint)mFrame.AVFrame->data[1];
                mFrame.VPIV = vd.CreateVideoProcessorInputView(ffTexture, ve, vpivd);
            }
            else if (VideoProcessor == VideoProcessors.Flyleaf)
            {
                srvDesc[0].Texture2DArray.FirstArraySlice = srvDesc[1].Texture2DArray.FirstArraySlice = (uint)mFrame.AVFrame->data[1];
                mFrame.SRV = [
                    device.CreateShaderResourceView(ffTexture, srvDesc[0]),
                    device.CreateShaderResourceView(ffTexture, srvDesc[1])];
            }
            else if (VideoProcessor == VideoProcessors.SwsScale)
            {
                var frame   = av_frame_alloc();
                int ret     = av_hwframe_transfer_data(frame, mFrame.AVFrame, 0);
                ret         = av_frame_copy_props(frame, mFrame.AVFrame);
                SwsFillPlanesHelper(mFrame, frame);
                av_frame_free(&frame);
            }
        }
    }
    void VPConfigHelper(bool request = true)
    {
        vpRequestsIn   &= ~VPRequestType.ReConfigVP;
        var oldVP       = VideoProcessor;
        var vpRequests  = VPRequestType.RotationFlip | VPRequestType.Crop | VPRequestType.UpdatePS; // TBR: we should set them all here as we don't compare with previous states
        VideoProcessor  = VPSelection();

        if (CanTrace) Log.Trace($"Preparing planes for {scfg.PixelFormatStr} with {VideoProcessor}");

        if (VideoProcessor == VideoProcessors.D3D11)
        {
            if (VideoProcessor != oldVP)
            {
                RaiseUI(nameof(VideoProcessor));
                vpRequests |= VPRequestType.Resize;
                D3FiltersSync();
            }

            D3Config();
        }
        else
        {
            if (VideoProcessor != oldVP)
            {
                RaiseUI(nameof(VideoProcessor));

                if (oldVP == VideoProcessors.D3D11)
                {
                    SwapChain.VPOV?.Dispose();

                    if (psCase == PSCase.SWD3) // prev
                    {
                        context.VSSetShader(vsMain);
                        psIdPrev = "f^"; // force SetPS
                    }

                    if (FieldType != VideoFrameFormat.Progressive)
                    {
                        FieldType = VideoFrameFormat.Progressive;
                        RaiseUI(nameof(FieldType));
                    }

                    FLFiltersSync();
                }
            }

            FLSwsConfig();
        }

        if (player != null)
        {
            if (request)
                VPRequest(vpRequests);
            else
                vpRequestsIn |= vpRequests;
        }
        else // TBR: Extractor
        {
            vpRequestsIn |= vpRequests;
            vpRequestsIn &= ~VPRequestType.Resize; // No SwapChain

            if (VideoProcessor == VideoProcessors.D3D11)
                D3ProcessRequests();
            else
                FLProcessRequests();
        }

        if (CanDebug) Log.Debug($"Prepared planes for {scfg.PixelFormatStr} with {VideoProcessor} [{psCase}]");
    }

    void IVP.MonitorChanged(GPUOutput monitor)
    {
        ucfg.MaxVerticalResolutionAuto  = monitor.Height;
        ucfg.SDRDisplayNitsAuto         = monitor.MaxLuminance;
        // currently not used (int accurate instead of double)
        //refreshRateTicks = (int)((1.0 / monitor.RefreshRate) * 1000 * 10000);
    }
    void IVP.UpdateSize(int width, int height)
    {
        ControlWidth    = width;
        ControlHeight   = height;
    }
    void SetSize()
    {
        SwapChain.SetSize();

        fillRatio = ControlWidth / (double)ControlHeight;
        if (ucfg.AspectRatio == AspectRatio.Fill)
            curRatio = fillRatio;

        vpRequests &= ~VPRequestType.Resize;
        vpRequests |=  VPRequestType.Viewport;
    }
    void SetViewport(int width, int height)
    {
        int x, y, newWidth, newHeight, xZoomPixels, yZoomPixels;

        var shouldFill = player?.Host?.Player_HandlesRatioResize(width, height);

        if (curRatio < fillRatio)
        {
            newHeight   = (int)(height * ucfg.zoom);
            newWidth    = (shouldFill.HasValue && shouldFill.Value) ? (int)(width * ucfg.zoom) : (int)(newHeight * curRatio);

            sideXPixels = ((int) (width - (height * curRatio))) & ~1;
            sideYPixels = 0;

            y = (int)(height * ucfg.panYOffset);
            x = (int)(width  * ucfg.panXOffset) + (sideXPixels / 2);

            yZoomPixels = newHeight - height;
            xZoomPixels = newWidth - (width - sideXPixels);
        }
        else
        {
            newWidth    = (int)(width * ucfg.zoom);
            newHeight   = (shouldFill.HasValue && shouldFill.Value) || curRatio == fillRatio ? (int)(height * ucfg.zoom) : (int)(newWidth / curRatio);

            sideYPixels = ((int) (height - (width / curRatio))) & ~1;
            sideXPixels = 0;

            x = (int)(width  * ucfg.panXOffset);
            y = (int)(height * ucfg.panYOffset) + (sideYPixels / 2);

            xZoomPixels = newWidth - width;
            yZoomPixels = newHeight - (height - sideYPixels);
        }

        Viewport = new((int)(x - xZoomPixels * (float)ucfg.zoomCenter.X), (int)(y - yZoomPixels * (float)ucfg.zoomCenter.Y), newWidth, newHeight);
        ViewportChanged?.Invoke(this, new());
    }
    void SetRotation()
    {
        bool was0_180   = rotation == 0 || rotation == 180;
        rotation        = (ucfg.rotation + scfg.Rotation) % 360;
        bool is0_180    = rotation == 0 || rotation == 180;

        if (was0_180 != is0_180 && !vpRequests.HasFlag(VPRequestType.Crop)) // TBR: Crop / AspectRatio will check too
        {
            curRatio = 1 / curRatio;
            if (ucfg.AspectRatio == AspectRatio.Keep)
                player?.Host?.Player_RatioChanged(curRatio);
        }
        
        vpRequests &= ~VPRequestType.RotationFlip;
        vpRequests |=  VPRequestType.Viewport;
    }
    void SetAspectRatio()
    {
        bool isKeep = ucfg.AspectRatio == AspectRatio.Keep;
        if (isKeep)
            curRatio = keepRatio;
        else if (ucfg.AspectRatio == AspectRatio.Fill)
            curRatio = fillRatio;
        else
            curRatio = ucfg.AspectRatio == AspectRatio.Custom ? ucfg.AspectRatioCustom.Value : ucfg.AspectRatio.Value;

        if (rotation == 90 || rotation == 270)
            curRatio = 1 / curRatio;

        if (isKeep)
            player?.Host?.Player_RatioChanged(curRatio); // return handled and avoid SetViewport?*

        vpRequests &= ~VPRequestType.AspectRatio;
        vpRequests |=  VPRequestType.Viewport;
    }

    void SetVisibleSizeAndRatioHelper()
    {
        int x, y;
        _ = av_reduce(&x, &y, VisibleWidth * scfg.SAR.Num, VisibleHeight * scfg.SAR.Den, 1024 * 1024);
        DAR = new(x, y);
        keepRatio = DAR.Value;

        player?.Video.SetUISize((int)VisibleWidth, (int)VisibleHeight, DAR);

        if (ucfg.AspectRatio == AspectRatio.Keep)
        {
            curRatio = rotation == 0 || rotation == 180 ? keepRatio : 1 / keepRatio;
            player?.Host?.Player_RatioChanged(curRatio);
        }
    }

    internal void SyncFilters()
    {   // Forces resync of current's VideoProcessor Filters to others (useful before saving)
        lock (lockDevice)
        {
            if (D3Disposed)
                return;

            if (VideoProcessor == VideoProcessors.D3D11)
                FLFiltersSync();
            else
                D3FiltersSync();
        }
    }
}

[Flags]
enum VPRequestType
{
    Empty           = 0,

    BackColor       = 1 << 0,
    ReConfigVP      = 1 << 1,   // User VP Switch + FL PS Update (e.g. w/o Filters)

    RotationFlip    = 1 << 2,   // Both - Flyleaf (for Flip)
    Resize          = 1 << 3,
    Crop            = 1 << 4,
    AspectRatio     = 1 << 5,
    Viewport        = 1 << 6,

    Deinterlace     = 1 << 7,   // D3D11
    HDRtoSDR        = 1 << 8,   // Flyleaf
    UpdatePS        = 1 << 9,   // Flyleaf
    UpdateVS        = 1 << 10,  // Flyleaf
}

public class VPConfig : NotifyPropertyChanged
{
    internal IVP vp;

    // === VP / Swap Chain ===

    public CornerRadius     CornerRadius            { get => cornerRadius; internal set { if (Set(ref cornerRadius, value)) vp?.SwapChain.SetClip(); } }
    internal CornerRadius cornerRadius;

    public SwapChainFormat  SwapChainFormat         { get; set; } = SwapChainFormat.BGRA;

    /// <summary>
    /// Whether VSync should be enabled (0: Disabled, 1: Enabled)
    /// </summary>
    public uint             VSync                   { get; set; } = 1;

    /// <summary>
    /// Background color of the player's control
    /// </summary>
    public System.Windows.Media.Color
                            BackColor               { get => VorticeToWPFColor(flBackColor);  set { Set(ref flBackColor, WPFToVorticeColor(value)); { WPFToVideoColor(value); vp?.VPRequest(VPRequestType.BackColor); } } }
    internal Color flBackColor = new(0, 0, 0, 1);
    internal VideoColor d3BackColor = new() { Rgba = new() { R = 0, G = 0, B = 0, A = 1 } };

    // === Viewport ===

    /// <summary>
    /// Video aspect ratio
    /// </summary>
    public AspectRatio      AspectRatio             { get => aspectRatio;          set { if (Set(ref aspectRatio, value)) vp?.VPRequest(VPRequestType.AspectRatio); } }
    AspectRatio aspectRatio = AspectRatio.Keep;
    public void ToggleKeepRatio()
    {
        if (AspectRatio == AspectRatio.Keep)
            AspectRatio = AspectRatio.Fill;
        else if (AspectRatio == AspectRatio.Fill)
            AspectRatio = AspectRatio.Keep;
    }

    /// <summary>
    /// Custom aspect ratio (AspectRatio must be set to Custom to have an effect)
    /// </summary>
    public AspectRatio      AspectRatioCustom       { get => aspectRatioCustom;    set { if (Set(ref aspectRatioCustom, value) && AspectRatio == AspectRatio.Custom) { aspectRatio = AspectRatio.Fill; AspectRatio = AspectRatio.Custom; } } }
    AspectRatio aspectRatioCustom = new(16, 9);

    /// <summary>
    /// Cropping rectagle to crop the output frame (based on frame size)
    /// </summary>
    [JsonIgnore]
    public CropRect         Crop                    { get => crop;                  set { if (!Set(ref crop,        value)) return; HasUserCrop = crop != CropRect.Empty; vp?.VPRequest(VPRequestType.Crop); } }
    internal CropRect crop = CropRect.Empty;
    internal bool HasUserCrop = false;

    // TODO: if we clamp to -0.5 / +0.5 for example it will not work properly with zoom point

    /// <summary>
    /// Pan X Offset to change the X location
    /// </summary>
    [JsonIgnore]
    public double           PanXOffset              { get => panXOffset;            set { if (Set(ref panXOffset,   Math.Clamp(value, -10, 10))) vp?.VPRequest(VPRequestType.Viewport); } }
    internal double panXOffset;

    /// <summary>
    /// Pan Y Offset to change the Y location
    /// </summary>
    [JsonIgnore]
    public double           PanYOffset              { get => panYOffset;            set { if (Set(ref panYOffset,   Math.Clamp(value, -10, 10))) vp?.VPRequest(VPRequestType.Viewport); } }
    internal double panYOffset;

    /// <summary>
    /// Pan rotation angle (for D3D11 VP allowed values are 0, 90, 180, 270 only)
    /// </summary>
    [JsonIgnore]
    public uint             Rotation                { get => rotation;              set { if (Set(ref rotation,     value)) vp?.VPRequest(VPRequestType.RotationFlip); } }
    internal uint rotation;

    [JsonIgnore]
    public bool             HFlip                   { get => hflip;                 set { if (Set(ref hflip,        value)) vp?.VPRequest(VPRequestType.RotationFlip); } }
    internal bool hflip;

    [JsonIgnore]
    public bool             VFlip                   { get => vflip;                 set { if (Set(ref vflip,        value)) vp?.VPRequest(VPRequestType.RotationFlip); } }
    internal bool vflip;

    [JsonIgnore]
    public double           Zoom                    { get => SnapToInt(zoom * 100); set { if (Set(ref zoom, SnapToInt(value / 100))) vp?.VPRequest(VPRequestType.Viewport); } }
    internal double zoom = 1;

    [JsonIgnore]
    public WPoint
                            ZoomCenter              { get => zoomCenter;            set { if (Set(ref zoomCenter,   value)) vp?.VPRequest(VPRequestType.Viewport); } }
    internal WPoint zoomCenter = new(0.5, 0.5);

    public int              ZoomOffset              { get => zoomOffset;            set { Set(ref zoomOffset,       value); } }
    int zoomOffset = 10;

    public void ResetViewport(uint rotation = 0)
        => SetViewport(0, 0, rotation, 100, new(0.5, 0.5), CropRect.Empty, AspectRatio.Keep, false, false);

    public void SetViewport(int panX, int panY, uint rotation, double zoom, WPoint p, CropRect crop, AspectRatio ratio, bool hflip, bool vflip)
    {
        AspectRatio = ratio;
        Zoom        = zoom;
        ZoomCenter  = p;
        PanXOffset  = panX;
        PanYOffset  = panY;
        Crop        = crop;
        Rotation    = rotation;
        HFlip       = hflip;
        VFlip       = vflip;

        vp?.VPRequest(VPRequestType.Crop | VPRequestType.RotationFlip);
    }

    public void RotateRight()   => Rotation = (rotation + 90) % 360;
    public void RotateLeft()    => Rotation = rotation < 90 ? 360 + rotation - 90 : rotation - 90;

    public void ZoomIn()        => Zoom += ZoomOffset;
    public void ZoomOut()       => Zoom = Math.Max(Zoom - ZoomOffset, 0);
    public void ZoomIn (WPoint p) { if (vp == null) return; SetZoomWithCenterPoint(p, zoom + ZoomOffset / 100.0); }
    public void ZoomOut(WPoint p) { if (vp == null) return; double zoom = this.zoom - ZoomOffset / 100.0; if (zoom < 0.001) return; SetZoomWithCenterPoint(p, zoom); }
    public void SetZoomAndCenter(double zoom, WPoint p)
    {
        Zoom        = zoom;
        ZoomCenter  = p;
        vp?.VPRequest(VPRequestType.Viewport);
    }
    internal void SetZoomWithCenterPoint(Point p, double zoom)
    {
        /* Notes
         * Zooms in a way that the specified point before zoom will be at the same position after zoom
         * 
         * Zoomed Point (ZP)    // the current point in a -possible- zoomed viewport
         * Zoom (Z)
         * Unzoomed Point (UP)  // the actual pixel of the current point
         * Viewport Point (VP)
         * Center Point (CP)
         *
         * UP = (VP + ZP) / Z =>
         * ZP = (UP * Z) - VP
         * CP = VP / (ZP - 1) (when UP = ZP)
         */

        zoom = SnapToInt(zoom);
        Viewport view = vp.Viewport;

        if (!(p.X >= view.X && p.X < view.X + view.Width && p.Y >= view.Y && p.Y < view.Y + view.Height)) // Point out of view
        {
            Zoom = zoom * 100;
            return;
        }

        Point viewport = new(view.X, view.Y);
        RemoveViewportOffsets(ref viewport);
        RemoveViewportOffsets(ref p);

        // Finds the required center point so that p will have the same pixel after zoom
        Point zoomCenter = new(
            GetCenterPoint(zoom, ((p.X - viewport.X) / (this.zoom / zoom)) - p.X) / (view.Width  / this.zoom),
            GetCenterPoint(zoom, ((p.Y - viewport.Y) / (this.zoom / zoom)) - p.Y) / (view.Height / this.zoom));

        // TBR: bug
        //if (zoomCenter.X > 10 || zoomCenter.X < -10)

        SetZoomAndCenter(zoom * 100, zoomCenter);

        void RemoveViewportOffsets(ref Point p)
        {
            p.X -= (vp.SideXPixels / 2 + (panXOffset * vp.ControlWidth));
            p.Y -= (vp.SideYPixels / 2 + (panYOffset * vp.ControlHeight));
        }
    
        double GetCenterPoint(double zoom, double offset)
            => zoom == 1 ? offset : offset / SnapToInt(zoom - 1); // possible bug when zoom = 1 (noticed in out of bounds zoom out)
    }
}

internal interface IVP
{   // TODO: To use different VPs (such as Child / Extractor etc.)
    public int          ControlWidth    { get; }
    public int          ControlHeight   { get; }
    public int          SideXPixels     { get; }
    public int          SideYPixels     { get; }
    public SwapChain    SwapChain       { get; }
    public Viewport     Viewport        { get; }

    void VPRequest(VPRequestType request);
    void UpdateSize(int width, int height);
    void MonitorChanged(GPUOutput monitor);
}

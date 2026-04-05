using System.Runtime.InteropServices;

using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using ID3D11VideoContext    = Vortice.Direct3D11.ID3D11VideoContext;
using ID3D11VideoDevice     = Vortice.Direct3D11.ID3D11VideoDevice;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    public bool                     D3Disposed      { get; private set; } = true;
    object lockD3 = new();

    public VideoFrameFormat         FieldType       { get; private set; } = VideoFrameFormat.Progressive;
    public bool                     SuperResolution { get; private set; }

    ID3D11VideoDevice               vd;
    ID3D11VideoProcessor            vp;
    ID3D11VideoContext              vc;
    ID3D11VideoProcessorEnumerator  ve;
    static VideoProcessorContentDescription
                                    vped        = new()
    {   // TBR: should have max sizes here or possible fail on blt?
        Usage           = VideoUsage.PlaybackNormal,
        InputFrameFormat= VideoFrameFormat.Progressive,

        InputFrameRate  = new(1, 1),
        OutputFrameRate = new(1, 1),
        InputWidth      = 1,
        InputHeight     = 1,
        OutputWidth     = 1,
        OutputHeight    = 1,
    };
    static VideoProcessorOutputViewDescription  vpovd = new() { ViewDimension = VideoProcessorOutputViewDimension.Texture2D };

    VideoProcessorStream[]          vpsa        = [new() { Enable = true }];
    VideoProcessorInputViewDescription
                                    vpivd       = new()
        {
            FourCC          = 0, // TBR: if required to specify this (uint)Format.NV12,
            ViewDimension   = VideoProcessorInputViewDimension.Texture2D,
            Texture2D       = new() { MipSlice = 0, ArraySlice = 0 }
        };
    VideoProcessorColorSpace        d3ColorIn   = new() { Usage = 0, YCbCr_xvYCC = 0 };
    static VideoProcessorColorSpace d3ColorOut  = new()
    {
        Usage           = 0,
        RGB_Range       = 0,
        YCbCr_Matrix    = 1,
        YCbCr_xvYCC     = 0,
        Nominal_Range   = 2
    };
    ID3D11PixelShader               d3psY, d3psUV;
    string                          psYId, psYIdPrev, psUVId, psUVIdPrev;
    Texture2DDescription            d3txtDesc   = new()
    {
        Usage       = ResourceUsage.Default,
        BindFlags   = BindFlags.ShaderResource | BindFlags.RenderTarget,
        ArraySize   = 1,
        MipLevels   = 1,
        SampleDescription = new(1, 0)
    };
    RenderTargetViewDescription[]   d3rtvDesc   = new RenderTargetViewDescription[2];
    FillPlanesDelegate              D3FillPlanesStage;

    bool                            d3CanPresent; // Don't render / present during out of bounds viewport

    void D3Init()
    {
        d3rtvDesc[0].ViewDimension = d3rtvDesc[1].ViewDimension = RenderTargetViewDimension.Texture2D;
        d3rtvDesc[0].Format = Format.R8_UNorm;
        d3rtvDesc[1].Format = Format.R8G8_UNorm;
        d3txtDesc.Format    = Format.NV12;
    }

    void D3Setup()
    {   // Called by Device Setup only* (shared lock)
        D3Disposed = false;

        var d3CacheEntry = D3CacheEntry.Get(GPUAdapter.Luid, out var needsFillUnlock);
        if (d3CacheEntry.Failed && !needsFillUnlock)
            return;
        
        vd = device.QueryInterface<ID3D11VideoDevice>();
        if (vd == null)
        {
            if (needsFillUnlock) Monitor.Exit(d3CacheEntry);

            return;
        }
        
        vc = context.QueryInterface<ID3D11VideoContext>();
        if (vc != null)
            if (vd.CreateVideoProcessorEnumerator   (ref vped, out ve).Success)    // TBR: vpcd config (maybe requires max sizes)
                vd.CreateVideoProcessor             (ve, 0, out vp);               // TBR: config for which rate index?

        if (vp == null)
        {
            if (ve != null) { ve.Dispose(); ve = null; }
            if (vc != null) { vc.Dispose(); vc = null; }

            if (needsFillUnlock) Monitor.Exit(d3CacheEntry);

            return;
        }

        if (CanDebug && needsFillUnlock) Log.Debug($"D3D11 Video Processor\r\n{GetDump()}");

        vc.VideoProcessorSetStreamAutoProcessingMode(vp, 0, false);
        vc.VideoProcessorSetOutputColorSpace(vp, d3ColorOut);
        vc.VideoProcessorSetOutputBackgroundColor(vp, false, ucfg.d3BackColor);

        if (FieldType != VideoFrameFormat.Progressive)
        {
            FieldType = VideoFrameFormat.Progressive;
            RaiseUI(nameof(FieldType));
        }

        if (SuperResolution)
        {
            SuperResolution = false;
            RaiseUI(nameof(SuperResolution));
        }

        D3FiltersSetup(d3CacheEntry, needsFillUnlock);
        FFmpegSetup();
    }

    bool D3Config()
    {
        if (VideoDecoder.VideoAccelerated)
            D3HWConfig();
        else
            D3SWConfig();

        D3Deinterlace(); // TBR: maybe request instead?

        if (scfg.ColorRange == ColorRange.Full)
        {
            d3ColorIn.RGB_Range     = 0;
            d3ColorIn.Nominal_Range = 2;
        }
        else
        {
            d3ColorIn.RGB_Range     = 1;
            d3ColorIn.Nominal_Range = 1;
        }

        d3ColorIn.YCbCr_Matrix = scfg.ColorSpace != ColorSpace.Bt601? 1u : 0u;

        vc.VideoProcessorSetStreamColorSpace(vp, 0, d3ColorIn);

        return true;
    }
    bool D3HWConfig()
    {
        FillPlanes  = D3HWFillPlanes;
        psCase      = PSCase.HWD3;

        d3txtDesc.Width = scfg.txtWidth;
        d3txtDesc.Height= scfg.txtHeight;

        return true;
    }
    bool D3SWConfig()
    {
        if (scfg.ColorType == ColorType.RGB && scfg.PixelFormat != AVPixelFormat.Rgba)
        {   // TBR: re-ordered RGB offsets?* extra pass*
            SwsConfig();
            canFL = false;
        }
        else
            FLSwsConfig();

        context.VSSetShader(vsSimple);
        vpivd.Texture2D.ArraySlice = 0;

        D3FillPlanesStage   = FillPlanes;
        FillPlanes          = D3SWFillPlanes;
        psCase              = PSCase.SWD3;
        d3txtDesc.Width     = scfg.txtWidth  & ~1u;
        d3txtDesc.Height    = scfg.txtHeight & ~1u;

        // RGB
        // Single Plane (Packed): RGBA
        // RGBA Direct Support

        if (canFL && scfg.ColorType == ColorType.YUV)
        {   // YUV

            // Single Plane (Packed): Y210le, Yuyv422, Yvyu422, Uyvy422
            // YUY2 Direct Support

            if (scfg.PixelPlanes == 2)
            {   // Two Planes (Semi-Planar Y_UV - No Alpha) | nv12,nv21,nv24,nv42,p010le,p016le,p410le,p416le | (log2_chroma_w != log2_chroma_h / Interleaved) (? nv16,nv20le,p210le,p216le)
                psYId = "d3a";
                if (psYId == psYIdPrev)
                    return true;

                D3SetPSY(psYId, @"
color = float4(Texture1.Sample(Sampler, input.Texture).r, 0, 0, 1);
");
                psUVId = "d3uvs";
                D3SetPSUV(psUVId, @"
color = float4(Texture2.Sample(Sampler, input.Texture).rg, 0, 1);
");
            }
            else if (scfg.PixelPlanes > 2) // Possible Alpha
            {   // Three Planes (Planar Y_U_V(_A) - Alpha)
                psYId = "d3b";
                if (psYId == psYIdPrev)
                    return true;

                D3SetPSY(psYId, @"
color = float4(Texture1.Sample(Sampler, input.Texture).r, 0, 0, 1);
");
                psUVId = "d3uv";
                D3SetPSUV(psUVId, @"
color = float4(Texture2.Sample(Sampler, input.Texture).r, Texture3.Sample(Sampler, input.Texture).r, 0, 1);
");
            }
        }

        return true;
    }

    VideoFrame D3HWFillPlanes(ref AVFrame* frame)
    {
        if (frame->data[0] != ffTexture.NativePointer)
        {
            Log.Error($"[V] Frame Dropped (Invalid HW Texture Pointer)");
            av_frame_unref(frame);
            return null;
        }

        vpivd.Texture2D.ArraySlice = (uint)frame->data[1];

        VideoFrame mFrame = new()
        {
            AVFrame     = frame,
            Timestamp   = (long)(frame->pts * scfg.Timebase) - VideoDecoder.Demuxer.StartTime,
            VPIV        = vd.CreateVideoProcessorInputView(ffTexture, ve, vpivd)
        };

        frame = av_frame_alloc();
        return mFrame;
    }
    VideoFrame D3SWFillPlanes(ref AVFrame* frame)
    {
        var mFrame = D3FillPlanesStage(ref frame);

        if (txtDesc[0].Format == Format.YUY2 || txtDesc[0].Format == Format.R8G8B8A8_UNorm) // TBR: Direct YUY2 | RGBA
        {
            // TODO: Convert to NV12 as well in Pixel Shader to support better quality on D3 | https://github.com/SuRGeoNix/Flyleaf/issues/658
            mFrame.VPIV = vd.CreateVideoProcessorInputView(mFrame.Texture[0], ve, vpivd);
            return mFrame;
        }

        var nv12 = device.CreateTexture2D(d3txtDesc);
        var rtvY = device.CreateRenderTargetView(nv12, d3rtvDesc[0]);

        context.PSSetShaderResources(0, mFrame.SRV);
        context.OMSetRenderTargets(rtvY);
        context.RSSetViewport(0, 0, d3txtDesc.Width, d3txtDesc.Height);
        context.PSSetShader(d3psY);
        context.Draw(6, 0);
        rtvY.Dispose();

        if (mFrame.SRV.Length > 1)
        {
            var rtvUV = device.CreateRenderTargetView(nv12, d3rtvDesc[1]);
            context.OMSetRenderTargets(rtvUV);
            context.RSSetViewport(0, 0, d3txtDesc.Width >> 1, d3txtDesc.Height >> 1);
            context.PSSetShader(d3psUV);
            context.Draw(6, 0);
            rtvUV.Dispose();
        }
        
        mFrame.Dispose();
        mFrame.Texture  = [nv12];
        mFrame.VPIV     = vd.CreateVideoProcessorInputView(nv12, ve, vpivd);

        return mFrame;
    }

    void D3SetPSY(string uniqueId, ReadOnlySpan<char> sampleHLSL)
    {
        if (!psShader.TryGetValue(psYId, out var shader))
        {
            shader = ShaderCompiler.CompilePS(device, uniqueId, sampleHLSL);
            psShader[psYId] = shader;
        }

        d3psY       = shader;
        psYIdPrev   = psYId;
    }
    void D3SetPSUV(string uniqueId, ReadOnlySpan<char> sampleHLSL)
    {
        if (!psShader.TryGetValue(psUVId, out var shader))
        {
            shader = ShaderCompiler.CompilePS(device, uniqueId, sampleHLSL);
            psShader[psUVId] = shader;
        }

        d3psUV      = shader;
        psUVIdPrev  = psUVId;
    }

    internal void D3SetBackColor()
    {   // Direct Call from Config
        if (vc != null)
        {
            vc.VideoProcessorSetOutputBackgroundColor(vp, false, ucfg.d3BackColor);
            RenderRequest();
        }
    }
    void D3SetViewport(int width, int height)
    {   // NOTE: D3 expects even width/height for output/dst (it will crop it internally)
        SetViewport(width, height);

        Viewport view = Viewport;

        if (!ucfg.SuperResolution)
            DisableSuperRes();
        else
        {
            if (scfg.PixelComp0Depth <= 8 && // Seems it crashes with 10-bit?
               (((rotation ==  0 || rotation == 180) && view.Width > VisibleWidth  && view.Height > VisibleHeight) ||
                ((rotation == 90 || rotation == 270) && view.Width > VisibleHeight && view.Height > VisibleWidth)))
                EnableSuperRes();
            else
                DisableSuperRes();
        }

        int right   = (int)(view.X + view.Width);
        int bottom  = (int)(view.Y + view.Height);

        if (view.Width < 1 || view.Y >= height || view.X >= width || bottom <= 0 || right <= 0)
        {
            d3CanPresent = false;
            return;
        }

        d3CanPresent = true;

        RawRect dst = new(
                Math.Max((int)view.X, 0),
                Math.Max((int)view.Y, 0),
                Math.Min(right      , width),
                Math.Min(bottom     , height));
            
        double croppedWidth     = d3txtDesc.Width   - crop.Width;
        double croppedHeight    = d3txtDesc.Height  - crop.Height;
        int dstWidth            = dst.Right  - dst.Left;
        int dstHeight           = dst.Bottom - dst.Top;

        int     cropLeft,   cropTop,    cropRight,  cropBottom;
        int     srcLeft,    srcTop,     srcRight,   srcBottom;
        double  scaleX,     scaleY,     scaleXRot,  scaleYRot;

        if (rotation == 0)
        {
            cropLeft    = view.X < 0 ? (int)(-view.X) : 0;
            cropTop     = view.Y < 0 ? (int)(-view.Y) : 0;

            scaleX      = croppedWidth  / view.Width;
            scaleY      = croppedHeight / view.Height;

            srcLeft     = (int)(crop.Left + cropLeft * scaleX);
            srcTop      = (int)(crop.Top  + cropTop  * scaleY);
            srcRight    = srcLeft + (int)(dstWidth  * scaleX);
            srcBottom   = srcTop  + (int)(dstHeight * scaleY);
        }
        else if (rotation == 180)
        {
            cropRight   = right  > width  ? right  - width  : 0;
            cropBottom  = bottom > height ? bottom - height : 0;

            scaleX      = croppedWidth  / view.Width;
            scaleY      = croppedHeight / view.Height;
                
            srcLeft     = (int)(crop.Left + cropRight  * scaleX);
            srcTop      = (int)(crop.Top  + cropBottom * scaleY);
            srcRight    = srcLeft + (int)(dstWidth  * scaleX);
            srcBottom   = srcTop  + (int)(dstHeight * scaleY);
        }
        else if (rotation == 90)
        {
            cropTop     = view.Y < 0 ? (int)(-view.Y) : 0;
            cropRight   = right > width ? right - width : 0;

            scaleXRot   = croppedWidth  / view.Height;
            scaleYRot   = croppedHeight / view.Width;
                
            srcLeft     = (int)(crop.Left + cropTop    * scaleXRot);
            srcTop      = (int)(crop.Top  + cropRight  * scaleYRot);
            srcRight    = srcLeft + (int)(dstHeight * scaleXRot);
            srcBottom   = srcTop  + (int)(dstWidth  * scaleYRot);
        }
        else if (rotation == 270)
        {
            cropLeft    = view.X < 0 ? (int)(-view.X) : 0;
            cropBottom  = bottom > height ? bottom - height : 0;

            scaleXRot   = croppedWidth  / view.Height;
            scaleYRot   = croppedHeight / view.Width;
                
            srcLeft     = (int)(crop.Left + cropBottom * scaleXRot);
            srcTop      = (int)(crop.Top  + cropLeft   * scaleYRot);
            srcRight    = srcLeft + (int)(dstHeight * scaleXRot);
            srcBottom   = srcTop  + (int)(dstWidth  * scaleYRot);
        }
        else
            srcLeft = srcTop = srcRight = srcBottom = 0;
            
        RawRect src = new(
            Math.Max(srcLeft    , 0),
            Math.Max(srcTop     , 0),
            Math.Min(srcRight   , (int)d3txtDesc.Width),
            Math.Min(srcBottom  , (int)d3txtDesc.Height));
            
        vc.VideoProcessorSetStreamSourceRect(vp, 0, true, src);
        vc.VideoProcessorSetStreamDestRect  (vp, 0, true, dst);
    }
    void D3SetSize()
    {
        SwapChain.VPOV?.Dispose();
        SetSize();
        SwapChain.VPOV = vd.CreateVideoProcessorOutputView(SwapChain.BackBuffer, ve, vpovd);
        vc.VideoProcessorSetOutputTargetRect(vp, true, new(0, 0, ControlWidth, ControlHeight));
    }
    void D3SetRotationFlip()
    {
        SetRotation();
        vc.VideoProcessorSetStreamRotation(vp, 0, true, ToD3Rotation());

        VideoProcessorRotation ToD3Rotation()
        {
            if (rotation < 45 || rotation == 360)
                return VideoProcessorRotation.Identity;

            if (rotation < 135)
                return VideoProcessorRotation.Rotation90;

            if (rotation < 225)
                return VideoProcessorRotation.Rotation180;

            return VideoProcessorRotation.Rotation270;
        }
    }
    void D3Deinterlace()
    {
        FieldType = ucfg.DeInterlace == DeInterlace.Auto ? scfg.FieldOrder : (VideoFrameFormat)ucfg.DeInterlace;
        vc.VideoProcessorSetStreamFrameFormat(vp, 0, FieldType);
        RaiseUI(nameof(FieldType));
    }

    #region Super Resolution
    [StructLayout(LayoutKind.Sequential)]
    struct SuperResNvidia(bool enable)
    {
        uint version = 0x1;
        uint method  = 0x2;
        uint enabled = enable ? 1u : 0u;
    }
    static readonly SuperResNvidia  SuperResEnabledNvidia   = new(true);
    static readonly SuperResNvidia  SuperResDisabledNvidia  = new(false);
    static readonly Guid            GUID_SUPERRES_NVIDIA    = Guid.Parse("d43ce1b3-1f4b-48ac-baee-c3c25375e6f7");

    [StructLayout(LayoutKind.Sequential)]
    struct SuperResIntel
    {
        public IntelFunction    function;
        public IntPtr           param;
    }
    enum IntelFunction : uint
    {
        kIntelVpeFnVersion  = 0x01,
        kIntelVpeFnMode     = 0x20,
		kIntelVpeFnScaling  = 0x37
    }
    static readonly Guid            GUID_SUPERRES_INTEL     = Guid.Parse("edd1d4b9-8659-4cbc-a4d6-9831a2163ac3");

    void EnableSuperRes()
    {
        if (SuperResolution)
            return;

        SuperResolution = true;
        RaiseUI(nameof(SuperResolution));

        if (GPUAdapter.Vendor == GPUVendor.Nvidia)
            fixed (SuperResNvidia* ptr = &SuperResEnabledNvidia)
                vc.VideoProcessorSetStreamExtension(vp, 0, GUID_SUPERRES_NVIDIA, (uint)sizeof(SuperResNvidia), (nint)ptr);
        else if (GPUAdapter.Vendor == GPUVendor.Intel)
            UpdateSuperResIntel(true);
    }

    void DisableSuperRes()
    {
        if (!SuperResolution)
            return;

        SuperResolution = false;
        RaiseUI(nameof(SuperResolution));

        if (GPUAdapter.Vendor == GPUVendor.Nvidia)
            fixed (SuperResNvidia* ptr = &SuperResDisabledNvidia)
                vc.VideoProcessorSetStreamExtension(vp, 0, GUID_SUPERRES_NVIDIA, (uint)sizeof(SuperResNvidia), (nint)ptr);
        else if (GPUAdapter.Vendor == GPUVendor.Intel)
            UpdateSuperResIntel(false);
    }

    void UpdateSuperResIntel(bool enabled)
    {
        IntPtr          paramPtr    = Marshal.AllocHGlobal(sizeof(uint));
        SuperResIntel   intel       = new() { param = paramPtr };
        GCHandle        handle      = GCHandle.Alloc(intel, GCHandleType.Pinned);
            
        intel.function = IntelFunction.kIntelVpeFnVersion;
        Marshal.WriteInt32(paramPtr, 3); // kIntelVpeVersion3
        vc.VideoProcessorSetOutputExtension(vp,     GUID_SUPERRES_INTEL, (uint)sizeof(SuperResIntel), handle.AddrOfPinnedObject());

        intel.function = IntelFunction.kIntelVpeFnMode;
        Marshal.WriteInt32(paramPtr, enabled ? 1 : 0); // kIntelVpeModePreproc : kIntelVpeModeNone
        vc.VideoProcessorSetOutputExtension(vp,     GUID_SUPERRES_INTEL, (uint)sizeof(SuperResIntel), handle.AddrOfPinnedObject());

        intel.function = IntelFunction.kIntelVpeFnScaling;
        Marshal.WriteInt32(paramPtr, enabled ? 2 : 0); // kIntelVpeScalingSuperResolution : kIntelVpeScalingDefault
        vc.VideoProcessorSetStreamExtension(vp, 0,  GUID_SUPERRES_INTEL, (uint)sizeof(SuperResIntel), handle.AddrOfPinnedObject());

        handle.Free();
        Marshal.FreeHGlobal(paramPtr);
    }
    #endregion

    void D3ProcessRequests()
    {
        while (vpRequestsIn != VPRequestType.Empty)
        {
            if (vpRequestsIn.HasFlag(VPRequestType.ReConfigVP))
            {
                VPSwitch();
                return;
            }

            vpRequests  = vpRequestsIn;
            vpRequestsIn= VPRequestType.Empty;

            if (vpRequests.HasFlag(VPRequestType.RotationFlip))
                D3SetRotationFlip();

            if (vpRequests.HasFlag(VPRequestType.Crop))
                SetCrop();

            if (vpRequests.HasFlag(VPRequestType.Resize))
                D3SetSize();

            if (vpRequests.HasFlag(VPRequestType.AspectRatio))
                SetAspectRatio();

            if (vpRequests.HasFlag(VPRequestType.Viewport))
                D3SetViewport(ControlWidth, ControlHeight);

            if (vpRequests.HasFlag(VPRequestType.Deinterlace))
                D3Deinterlace();
        }
    }
    void D3Render(VideoFrame frame, bool secondField)
    {
        if (frame.VPIV == null)
            return; // TODO: when we dispose on switch

        /* [Undocumented Deinterlace]
        * Currently we don't use Past/Future frames so we consider only Bob/Weave methods (we also consider that are supported by D3D11VP)
        * For Bob -double rate- we set the second field (InputFrameOrField/OutputIndex)
        * TODO: Bring secondField to renderer so Render refreshes can work also with it (maybe ShowFrameX too)
        * TBR: Vortice bug with Past / Future surfaces (no support for now - only useful for deinterlace?*)
        */
        vpsa[0].InputSurface= frame.VPIV;
        vpsa[0].OutputIndex = vpsa[0].InputFrameOrField = secondField ? 1u : 0u;
        vc.VideoProcessorBlt(vp, SwapChain.VPOV, 0, 1, vpsa);

        if (context2d != null)
            ucfg.OnD2DDraw(this, context2d);

        D3SubsRender();
    }
    void D3Render(ID3D11VideoProcessorInputView srv, ID3D11VideoProcessorOutputView rtv, RawRect view, bool secondField = false)
    {
        vc.VideoProcessorSetStreamDestRect  (vp, 0, true, view);
        vc.VideoProcessorSetOutputTargetRect(vp,    true, view);

        vpsa[0].InputSurface= srv;
        vpsa[0].OutputIndex = vpsa[0].InputFrameOrField = secondField ? 1u : 0u;
        vc.VideoProcessorBlt(vp, rtv, 0, 1, vpsa);
    }

    void D3Dispose()
    {   // Called by Device Dispose only* (shared lock)
        if (D3Disposed)
            return;

        D3Disposed = true;

        FFmpegDispose();

        if (vp != null)
        {
            vp.Dispose(); vp = null;
            ve.Dispose(); ve = null;
            vc.Dispose(); vc = null;
            vd.Dispose(); vd = null;
        }

        psYIdPrev = psUVIdPrev = null;
    }

    string GetDump()
    {
        string dump = "";
        var vpCaps  = ve.VideoProcessorCaps;

        dump += $"=====================================================\r\n";
        dump += $"MaxInputStreams           {vpCaps.MaxInputStreams}\r\n";
        dump += $"MaxStreamStates           {vpCaps.MaxStreamStates}\r\n";

        dump += $"\n[Video Processor Device Caps]\r\n";
        foreach (VideoProcessorDeviceCaps cap in Enum.GetValues<VideoProcessorDeviceCaps>())
            dump += $"{cap,-25} {((vpCaps.DeviceCaps & cap) != 0 ? "yes" : "no")}\r\n";

        dump += $"\n[Video Processor Feature Caps]\r\n";
        foreach (VideoProcessorFeatureCaps cap in Enum.GetValues<VideoProcessorFeatureCaps>())
            dump += $"{cap,-25} {((vpCaps.FeatureCaps & cap) != 0 ? "yes" : "no")}\r\n";

        dump += $"\n[Video Processor Stereo Caps]\r\n";
        foreach (VideoProcessorStereoCaps cap in Enum.GetValues<VideoProcessorStereoCaps>())
            dump += $"{cap,-25} {((vpCaps.StereoCaps & cap) != 0 ? "yes" : "no")}\r\n";

        dump += $"\n[Video Processor Input Format Caps]\r\n";
        foreach (VideoProcessorFormatCaps cap in Enum.GetValues<VideoProcessorFormatCaps>())
            dump += $"{cap,-25} {((vpCaps.InputFormatCaps & cap) != 0 ? "yes" : "no")}\r\n";

        dump += $"\n[Video Processor Filter Caps]\r\n";
        
        foreach (VideoProcessorFilterCaps filter in D3CacheEntry.AllFilterCaps)
            if ((vpCaps.FilterCaps & filter) != 0)
            {
                ve.GetVideoProcessorFilterRange(ToVideoProcessorFilter(filter), out var range);
                dump += $"{filter,-25} [{range.Minimum,6} - {range.Maximum,4}] | x{range.Multiplier,4} | *{range.Default}\r\n";
            }
            else
                dump += $"{filter,-25} no\r\n";

        dump += $"\n[Video Processor Auto Stream Caps]\r\n";
        foreach (VideoProcessorAutoStreamCaps cap in Enum.GetValues<VideoProcessorAutoStreamCaps>())
            dump += $"{cap,-25} {((vpCaps.AutoStreamCaps & cap) != 0 ? "yes" : "no")}\r\n";

        VideoProcessorRateConversionCaps rcCap = new();
        for (uint i = 0; i < vpCaps.RateConversionCapsCount; i++)
        {
            ve.GetVideoProcessorRateConversionCaps(i, out rcCap);
            VideoProcessorProcessorCaps pCaps = (VideoProcessorProcessorCaps) rcCap.ProcessorCaps;

            dump += $"\n[Video Processor Rate Conversion Caps #{i}]\r\n";

            dump += $"\n\t[Video Processor Rate Conversion Caps]\r\n";
            var fields = typeof(VideoProcessorRateConversionCaps).GetFields();
            foreach (var field in fields)
                dump += $"\t{field.Name,-35} {field.GetValue(rcCap)}\r\n";

            dump += $"\n\t[Video Processor Processor Caps]\r\n";
            foreach (VideoProcessorProcessorCaps cap in Enum.GetValues(typeof(VideoProcessorProcessorCaps)))
                dump += $"\t{cap,-35} {(((VideoProcessorProcessorCaps)rcCap.ProcessorCaps & cap) != 0 ? "yes" : "no")}\r\n";
        }

        return dump;
    }
}

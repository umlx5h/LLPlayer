using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;

using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaPlayer;

namespace FlyleafLib.MediaFramework.MediaRenderer;

/* TODO
 * Improve A/V Sync
 *
 *  a. vsync / vblack
 *  b. Present can cause a delay (based on device load), consider using more buffers for high frame rates that could minimize the delay
 *  c. display refresh rate vs input rate, consider using max latency > 1 ?
 *  d. frame drops
 *
 *      swapChain.GetFrameStatistics(out var stats);
 *      swapChain.LastPresentCount - stats.PresentCount;
 */

public partial class Renderer : NotifyPropertyChanged, IDisposable
{
    public Config           Config          { get; private set; }
    public int              ControlWidth    { get; private set; }
    public int              ControlHeight   { get; private set; }
    internal nint           ControlHandle;
    
    internal Action<IDXGISwapChain2>
                            SwapChainWinUIClbk;

    public bool             Disposed        { get; private set; } = true;
    public bool             SCDisposed      { get; private set; } = true;
    public bool             D3D11VPFailed   => vc == null;
    public int              MaxOffScreenTextures
                                            { get; set; } = 20;
    public VideoDecoder     VideoDecoder    { get; internal set; }
    internal VideoStream    VideoStream;

    public Viewport         GetViewport     { get; private set; }
    public event EventHandler ViewportChanged;

    public uint             VisibleWidth    { get; private set; }
    public uint             VisibleHeight   { get; private set; }
    public AspectRatio      DAR             { get; set; }
    public double           CurRatio        => curRatio;
    double curRatio, keepRatio, fillRatio;
    CropRect cropRect; // + User's Cropping
    uint textWidth, textHeight; // Padded (Codec/Texture)

    public CornerRadius     CornerRadius    { get => cornerRadius;              set => UpdateCornerRadius(value); }
    CornerRadius cornerRadius = new(0);
    bool cornerRadiusNeedsUpdate;

    public int              SideXPixels     { get; private set; }
    public int              SideYPixels     { get; private set; }

    public int              PanXOffset      { get => panXOffset;                set => SetPanX(value); }
    int panXOffset;
    public void SetPanX(int panX, bool refresh = true)
    {
        panXOffset = panX;
        SetViewport(refresh);
    }

    public int              PanYOffset      { get => panYOffset;                set => SetPanY(value); }
    int panYOffset;
    public void SetPanY(int panY, bool refresh = true)
    {
        panYOffset = panY;
        SetViewport(refresh);
    }

    public uint             Rotation        { get => _RotationAngle;            set => UpdateRotation(value); }
    uint _RotationAngle;
    VideoProcessorRotation _d3d11vpRotation  = VideoProcessorRotation.Identity;
    bool hasLinesizeVFlip; // if negative should be vertically flipped

    public bool             HFlip           { get => _HFlip;                    set { _HFlip = value; UpdateRotation(_RotationAngle); } }
    bool _HFlip;

    public bool             VFlip           { get => _VFlip;                    set { _VFlip = value; UpdateRotation(_RotationAngle); } }
    bool _VFlip;

    public VideoFrameFormat FieldType       { get => _FieldType;                private  set => SetUI(ref _FieldType, value); }
    VideoFrameFormat _FieldType = VideoFrameFormat.Progressive;

    public VideoProcessors  VideoProcessor  { get => videoProcessor;            private  set => SetUI(ref videoProcessor, value); }
    VideoProcessors videoProcessor = VideoProcessors.Flyleaf;

    /// <summary>
    /// Zoom percentage (100% equals to 1.0)
    /// </summary>
    public double              Zoom         { get => zoom;                      set => SetZoom(value); }
    double zoom = 1;
    public void SetZoom(double zoom, bool refresh = true)
    {
        this.zoom = zoom;
        SetViewport(refresh);
    }

    public Point            ZoomCenter      { get => zoomCenter;                set => SetZoomCenter(value); }
    Point zoomCenter = ZoomCenterPoint;
    internal static Point ZoomCenterPoint = new(0.5, 0.5);
    public void SetZoomCenter(Point p, bool refresh = true)
    {
        zoomCenter = p;
        SetViewport(refresh);
    }
    public void SetZoomAndCenter(double zoom, Point p, bool refresh = true)
    {
        this.zoom = zoom;
        zoomCenter = p;
        SetViewport(refresh);
    }
    public void SetPanAll(int panX, int panY, uint rotation, double zoom, Point p, bool refresh = true)
    {
        panXOffset = panX;
        panYOffset = panY;
        this.zoom = zoom;
        zoomCenter = p;
        UpdateRotation(rotation, false);
        SetViewport(refresh);
    }

    public int              UniqueId        { get; private set; }
    public bool             HasFLFilters    { get; private set; }
    public VideoFrame       LastFrame       { get; set; }
    public RawRect          VideoRect       { get; set; }

    LogHandler Log;
    Player player;

    private Renderer(nint handle, int uniqueId, Config config)
    {
        UniqueId            = uniqueId == -1 ? GetUniqueId() : uniqueId;
        wndProcDelegate     = new(WndProc);
        wndProcDelegatePtr  = Marshal.GetFunctionPointerForDelegate(wndProcDelegate);
        ControlHandle       = handle;
        Config              = config;
        BGRA_OR_RGBA        = Config.Video.SwapForceR8G8B8A8 ? Format.R8G8B8A8_UNorm : Format.B8G8R8A8_UNorm;
        player              = Config.Player.player;

        overlayTextureDesc = new()
        {
            Usage               = ResourceUsage.Default,
            Width               = 0,
            Height              = 0,
            Format              = BGRA_OR_RGBA,
            ArraySize           = 1,
            MipLevels           = 1,
            BindFlags           = BindFlags.ShaderResource,
            SampleDescription   = new(1, 0)
        };
        singleStageDesc = new()
        {
            Usage               = ResourceUsage.Staging,
            Format              = BGRA_OR_RGBA,
            ArraySize           = 1,
            MipLevels           = 1,
            BindFlags           = BindFlags.None,
            CPUAccessFlags      = CpuAccessFlags.Read,
            SampleDescription   = new(1, 0),
            Width               = 0,
            Height              = 0
        };
        singleGpuDesc = new()
        {
            Usage               = ResourceUsage.Default,
            Format              = BGRA_OR_RGBA,
            ArraySize           = 1,
            MipLevels           = 1,
            BindFlags           = BindFlags.RenderTarget | BindFlags.ShaderResource,
            SampleDescription   = new(1, 0)
        };

        var confAdapter = Config.Video.GPUAdapter;
        if (string.IsNullOrEmpty(confAdapter))
            return;

        if (confAdapter.Equals("WARP", StringComparison.CurrentCultureIgnoreCase))
            gpuForceWarp = true;
        else
        {
            foreach (var gpuAdapter in Engine.Video.GPUAdapters.Values)
                if (Regex.IsMatch(gpuAdapter.Description,      confAdapter, RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(gpuAdapter.Luid.ToString(),  confAdapter, RegexOptions.IgnoreCase))
                {
                    this.gpuAdapter = gpuAdapter;
                    dxgiAdapter     = gpuAdapter.dxgiAdapter;
                    break;
                }
        }
    }
    public Renderer(VideoDecoder videoDecoder, nint handle = 0, int uniqueId = -1) : this(handle, uniqueId, videoDecoder.Config)
    {
        Log                 = new(("[#" + UniqueId + "]").PadRight(8, ' ') + " [Renderer      ] ");
        VideoDecoder        = videoDecoder;
        use2d               = Config.Video.Use2DGraphics;

        Initialize();
    }
}

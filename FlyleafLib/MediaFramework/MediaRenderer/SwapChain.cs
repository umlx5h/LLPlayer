using System.Runtime.InteropServices;
using System.Windows;

using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using FrameStatistics = Vortice.DXGI.FrameStatistics;

using static FlyleafLib.Utils.NativeMethods;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe class SwapChain
{
    public Renderer                 Renderer        { get; private set; }
    public bool                     Disposed        { get; private set; } = true;
    public GPUOutput                Monitor         { get; private set; }
    public nint                     ControlHwnd     { get; private set; }
    public bool                     CanPresent      { get; internal set; } // Don't render / present during minimize (or invalid size)

    public ID3D11VideoProcessorOutputView
                                    VPOV            { get; internal set; }
    public ID3D11Texture2D          BackBuffer => bb;
    ID3D11Texture2D bb;
    public ID3D11RenderTargetView   BackBufferRtv => bbRtv;
    ID3D11RenderTargetView bbRtv;

    IDXGISwapChain1             sc;
    IDCompositionDevice         dcDevice;
    IDCompositionVisual         dcVisual;
    IDCompositionTarget         dcTarget;
    IDCompositionRectangleClip  dcClip;

    ID2D1DeviceContext          context2d; // ref only
    ID2D1Bitmap1                bitmap2d;
    static BitmapProperties1    bitmapProps2d   = new()
    {
        BitmapOptions   = BitmapOptions.Target | BitmapOptions.CannotDraw,
        PixelFormat     = Vortice.DCommon.PixelFormat.Premultiplied
    };

    int                 controlWidth, controlHeight; // TBR: Updates earlier and waits Resize to update ControlWidth/ControlHeight
    Action<IDXGISwapChain2>
                        WinUIClbk;
    bool                isCornerRadiusEmpty = true;
    IVP                 vp;
    LogHandler          Log;
    VPConfig            ucfg;

    internal SwapChain(Renderer renderer, IVP vp = null)
    {
        Renderer    = renderer;
        this.vp     = vp ?? renderer;

        Log         = renderer.Log;
        ucfg        = renderer.ucfg;

        wndProcDelegate     = new(WndProc);
        wndProcDelegatePtr  = Marshal.GetFunctionPointerForDelegate(wndProcDelegate);
    }

    SwapChainDescription1 Desc() => new()
    {
            BufferUsage         = Usage.RenderTargetOutput,
            Format              = (Format)ucfg.SwapChainFormat,
            Width               = 2,
            Height              = 2,
            AlphaMode           = AlphaMode.Premultiplied,  // TBR
            SwapEffect          = SwapEffect.FlipDiscard,   
            Scaling             = Scaling.Stretch,          // DComp can't validate widhth/height?*
            BufferCount         = 2,
            SampleDescription   = new SampleDescription(1, 0),
            Flags               = SwapChainFlags.None
    };

    public void Setup(nint hwnd)
    {
        lock (Renderer.lockDevice)
        {
            if (!Disposed)
            {
                if (ControlHwnd == hwnd)
                    return;

                DisposeLocal();
            }

            ControlHwnd = hwnd;

            if (hwnd == 0)
                return;

            if (Renderer.Disposed)
                Renderer.SetupLocal();
            else
                SetupLocal();
        }
    }
    internal void Setup()
    {
        if (WinUIClbk != null)
            SetupLocalWinUI();
        else if (ControlHwnd != 0)
            SetupLocal();
    }
    void SetupLocal()
    {
        try
        {
            if (CanDebug) Log.Debug($"SC Initializing [Hwnd: {ControlHwnd}, Fmt: {ucfg.SwapChainFormat}]");

            Disposed        = false;
            RECT rect       = new();
            GetWindowRect(ControlHwnd, ref rect);
            controlWidth    = rect.Right  - rect.Left;
            controlHeight   = rect.Bottom - rect.Top;

            sc = Engine.Video.Factory.CreateSwapChainForComposition(Renderer.Device, Desc()); // we will resize on rendering
            DComp.DCompositionCreateDevice(Renderer.DXGIDevice, out dcDevice).CheckError();
            dcDevice.CreateTargetForHwnd(ControlHwnd, false, out dcTarget).CheckError();
            dcDevice.CreateVisual(out dcVisual).CheckError();
            dcVisual.SetContent(sc).CheckError();
            dcTarget.SetRoot(dcVisual).CheckError();
            dcDevice.CreateRectangleClip(out dcClip).CheckError();

            if (!isCornerRadiusEmpty)
            {
                SetClipHelper();
                dcVisual.SetClip(dcClip).CheckError();
            }

            dcDevice.Commit().CheckError();

            Engine.Video.Factory.MakeWindowAssociation(ControlHwnd, WindowAssociationFlags.IgnoreAll);
            AddSubClass();

            SetupLocalHelper();

            if (CanInfo) Log.Info($"SC Initialized [Hwnd: {ControlHwnd}, Fmt: {ucfg.SwapChainFormat}]");
        }
        catch (Exception e) // Should handle device lost etc..
        {
            Log.Error($"SC Initialization failed [Hwnd: {ControlHwnd}, Fmt: {ucfg.SwapChainFormat}] ({e.Message})");
            DisposeLocal();
        }
    }

    public void SetupWinUI(Action<IDXGISwapChain2> scClbkWinUI)
    {
        lock (Renderer.lockDevice)
        {
            if (!Disposed)
            {
                if (WinUIClbk == scClbkWinUI)
                    return;

                DisposeLocal();
            }

            WinUIClbk = scClbkWinUI;

            if (scClbkWinUI == null)
                return;

            if (Renderer.Disposed)
                Renderer.SetupLocal();
            else
                SetupLocalWinUI();
        }
    }
    internal void SetupLocalWinUI()
    {
        try
        {
            if (CanDebug) Log.Debug($"SC Initializing [Fmt: {ucfg.SwapChainFormat}]");

            Disposed = false;

            sc = Engine.Video.Factory.CreateSwapChainForComposition(Renderer.Device, Desc());

            SetupLocalHelper();

            WinUIClbk.Invoke(sc.QueryInterface<IDXGISwapChain2>());
        }
        catch (Exception e)
        {
            Log.Error($"SC Initialization failed [Fmt: {ucfg.SwapChainFormat}] ({e.Message})");
            DisposeLocal();
            return;
        }
    }
    void SetupLocalHelper()
    {
        context2d   = Renderer.context2d;

        // Only to avoid nulls on resize
        bb          = sc.GetBuffer<ID3D11Texture2D>(0);
        bbRtv       = Renderer.Device.CreateRenderTargetView(bb);

        UpdateDisplay(true); // don't force if we let WndProc run without our swapchain

        // Ensures that it will run ResizeBuffers initially
        if (controlWidth > 0 && controlHeight > 0)
        {
            CanPresent = true;
            vp.VPRequest(VPRequestType.Resize);
        }
    }

    public void Dispose(bool rendererFrame = true)
    {   // External calls will not allow re-creation of previous swap chain | During swap players we keep rendererFrame alive
        lock (Renderer.lockDevice)
        {
            DisposeLocal(rendererFrame);
            ControlHwnd = 0;
            WinUIClbk = null;
        }
    }
    internal void DisposeLocal(bool rendererFrame = true)
    {
        if (Disposed)
            return;

        Renderer.ClearScreen(force: true, rendererFrame: rendererFrame);
        Disposed    = true;
        CanPresent  = false;

        if (WinUIClbk != null)
        {
            DisposeLocalWinUI();
            return;
        }

        if (CanDebug) Log.Debug($"SC Disposing [Hwnd: {ControlHwnd}]");

        RemoveSubClass();

        if (dcClip != null)
        {
            dcClip.Dispose();
            dcClip = null;
        }

        if (dcTarget != null)
        {
            dcTarget.SetRoot(null);
            dcTarget.Dispose();
            dcTarget = null;
        }

        if (dcVisual != null)
        {
            dcVisual.SetContent(null);
            dcVisual.Dispose();
            dcVisual = null;
        }

        DisposeHelper();

        if (sc != null)
        {
            sc.Dispose();
            sc = null;
        }
            
        if (dcDevice != null)
        {
            dcDevice.Dispose();
            dcDevice = null;
        }

        if (CanInfo) Log.Info($"SC Disposed [Hwnd: {ControlHwnd}]");
    }
    void DisposeLocalWinUI()
    {
        if (CanDebug) Log.Debug($"SC Disposing");

        WinUIClbk.Invoke(null);

        DisposeHelper();

        if (sc != null)
        {
            sc.Release(); // TBR: SwapChainPanel (SCP) should be disposed and create new instance instead (currently used from Template)
            sc.Dispose();
            sc = null;
        }

        if (CanInfo) Log.Info($"SC Disposed [Hwnd: {ControlHwnd}]");
    }
    void DisposeHelper()
    {
        if (bitmap2d != null)
        {
            bitmap2d.Dispose();
            bitmap2d = null;
        }
        
        if (VPOV != null)
        {
            VPOV.Dispose();
            VPOV = null;
        }

        if (bbRtv != null)
        {
            bbRtv.Dispose();
            bbRtv = null;
        }

        if (bb != null)
        {
            bb.Dispose();
            bb = null;
        }
    }

    public void Resize(int width, int height)
    {   // Externally used when a WndProc hook is not available (e.g. WinUI)
        controlWidth    = width;
        controlHeight   = height;

        CanPresent = controlWidth > 0 && controlHeight > 0;
        if (controlWidth != vp.ControlWidth || controlHeight != vp.ControlHeight) // TBR: It will not refresh on restore from minimize (same sizes)
            vp.VPRequest(VPRequestType.Resize);
    }

    public Result Present()
        => sc.Present(ucfg.VSync, PresentFlags.None);

    public Result Present(uint syncInterval, PresentFlags flags)
        => sc.Present(syncInterval, flags);

    internal void SetSize()
    {
        // TBR lock with Resize*
        vp.UpdateSize(controlWidth, controlHeight);

        if (!isCornerRadiusEmpty)
        {
            dcClip.SetRight (vp.ControlWidth);
            dcClip.SetBottom(vp.ControlHeight);
            dcDevice.Commit().CheckError();
        }

        if (bitmap2d != null)
        {
            context2d.Target = null;
            bitmap2d.Dispose();
        }

        bbRtv.  Dispose();
        bb.     Dispose();
        sc.     ResizeBuffers(0, (uint)vp.ControlWidth, (uint)vp.ControlHeight, Format.Unknown, SwapChainFlags.None);
        bb      = sc.GetBuffer<ID3D11Texture2D>(0);
        bbRtv   = Renderer.Device.CreateRenderTargetView(bb);

        if (context2d != null)
        {
            using var surface = bb.QueryInterface<IDXGISurface>();
            bitmap2d = context2d.CreateBitmapFromDxgiSurface(surface, bitmapProps2d);
            context2d.Target = bitmap2d;
        }
    }

    internal void SetClip()
    {   // Dynamic Corner Radius Change
        lock (Renderer.lockDevice)
        {
            if (Disposed)
                return;

            try
            {
                bool wasEmpty = isCornerRadiusEmpty;
                isCornerRadiusEmpty = ucfg.cornerRadius == CornerRadiusEmpty; // Let SCInit do it when SCDisposed

                // Don't use canRenderPresent as we will need to keep track to set it when changes
                if (Disposed)
                    return;

                if (isCornerRadiusEmpty)
                {
                    // Don't set clip when empty might cause issues with fullscreen for example
                    dcVisual.SetClip(null).CheckError();
                    dcDevice.Commit().CheckError();
                    return;
                }

                SetClipHelper();

                if (wasEmpty)
                    dcVisual.SetClip(dcClip).CheckError();

                dcDevice.Commit().CheckError();
            }
            catch (Exception e)
            {
                Log.Error($"Failed to set CornerRadius = {ucfg.cornerRadius} ({e.Message})");
            }
        }
    }
    void SetClipHelper()
    {
        dcClip.SetTop   (0);
        dcClip.SetLeft  (0);
        dcClip.SetRight (controlWidth);
        dcClip.SetBottom(controlHeight);
        
        dcClip.SetTopLeftRadiusX        ((float)ucfg.cornerRadius.TopLeft);
        dcClip.SetTopLeftRadiusY        ((float)ucfg.cornerRadius.TopLeft);
        dcClip.SetTopRightRadiusX       ((float)ucfg.cornerRadius.TopRight);
        dcClip.SetTopRightRadiusY       ((float)ucfg.cornerRadius.TopRight);
        dcClip.SetBottomLeftRadiusX     ((float)ucfg.cornerRadius.BottomLeft);
        dcClip.SetBottomLeftRadiusY     ((float)ucfg.cornerRadius.BottomLeft);
        dcClip.SetBottomRightRadiusX    ((float)ucfg.cornerRadius.BottomRight);
        dcClip.SetBottomRightRadiusY    ((float)ucfg.cornerRadius.BottomRight);
    }

    public FrameStatistics GetFrameStatistics()
    {
        lock (Renderer.lockDevice)
        {
            if (Disposed)
                return new();

            FrameStatistics stats;
            int retries = 7;
            while(sc.GetFrameStatistics(out stats).Failure && retries-- > 0);

            #if DEBUG
            if (retries == 0 && CanDebug) Log.Debug("GetFrameStatistics failed");
            #endif

            return stats;
        }
    }

    #region Display | GPUOutput | Monitor
    nint displayHwnd;
    void UpdateDisplay(bool force = false)
    {
        nint newDisplayHwnd = MonitorFromWindow(ControlHwnd, MonitorOptions.MONITOR_DEFAULTTONEAREST);
        if (displayHwnd == newDisplayHwnd && !force)
            return;

        displayHwnd = newDisplayHwnd;

        var displays = Engine.Video.GetGPUOutputs(Renderer.DXGIAdapter);
        foreach(var display in displays)
            if (displayHwnd == display.Hwnd)
            {
                Monitor = display;
                vp.MonitorChanged(Monitor);
                if (CanDebug) Log.Debug($"{display}");

                return;
            }
    }
    #endregion

    #region WndProc
    SubclassWndProc wndProcDelegate;
    IntPtr          wndProcDelegatePtr;
    bool            hasSubClass;

    void AddSubClass()
    {
        if (!hasSubClass)
        {
            hasSubClass = true;
            if (Environment.CurrentManagedThreadId == Application.Current.Dispatcher.Thread.ManagedThreadId)
                SetWindowSubclass(ControlHwnd, wndProcDelegatePtr, 0, 0);
            else
                UI(() => SetWindowSubclass(ControlHwnd, wndProcDelegatePtr, 0, 0));
        }
    }
    void RemoveSubClass()
    {
        if (hasSubClass)
        {
            hasSubClass = false;
            if (Environment.CurrentManagedThreadId == Application.Current.Dispatcher.Thread.ManagedThreadId)
                RemoveWindowSubclass(ControlHwnd, wndProcDelegatePtr, 0);
            else
                UI(() => RemoveWindowSubclass(ControlHwnd, wndProcDelegatePtr, 0));
        }
    }
    nint WndProc(nint hWnd, WndProcMessages msg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData)
    {
        switch (msg)
        {
            case WndProcMessages.WM_NCDESTROY:
                if (Disposed)
                    RemoveSubClass();
                else
                    Dispose();
                break;

            // TODO: currently disabled for performance (we only change recommeded resolution/sdrnits) | when more added (Dpi/HDR native etc.)
            //case WndProcMessages.WM_MOVE:
            //    UpdateDisplay();
            //    break;

            case WndProcMessages.WM_DISPLAYCHANGE: // top-level window only (any display) - should refresh all and check if current changed
                UpdateDisplay(true);
                break;

            case WndProcMessages.WM_SIZE:
                Resize(SignedLOWORD(lParam), SignedHIWORD(lParam));
                break;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
    #endregion
}

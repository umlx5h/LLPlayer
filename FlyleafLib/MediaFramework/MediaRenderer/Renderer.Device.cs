using System.Numerics;
using System.Runtime.InteropServices;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.DXGI.Debug;

using ID3D11DeviceContext   = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Device          = Vortice.Direct3D11.ID3D11Device;
using ID2D1Device           = Vortice.Direct2D1.ID2D1Device;
using ID2D1DeviceContext    = Vortice.Direct2D1.ID2D1DeviceContext;
using ID2D1Bitmap1          = Vortice.Direct2D1.ID2D1Bitmap1;
using BitmapProperties1     = Vortice.Direct2D1.BitmapProperties1;

using FlyleafLib.MediaFramework.MediaDecoder;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    static InputElementDescription[] inputElements =
    {
        new("POSITION", 0, Format.R32G32B32_Float,     0),
        new("TEXCOORD", 0, Format.R32G32_Float,        0),
    };

    static BufferDescription vertexBufferDesc = new()
    {
        BindFlags = BindFlags.VertexBuffer
    };

    static float[] vertexBufferData =
    [
        -1.0f,  -1.0f,  0,      0.0f, 1.0f,
        -1.0f,   1.0f,  0,      0.0f, 0.0f,
         1.0f,  -1.0f,  0,      1.0f, 1.0f,

         1.0f,  -1.0f,  0,      1.0f, 1.0f,
        -1.0f,   1.0f,  0,      0.0f, 0.0f,
         1.0f,   1.0f,  0,      1.0f, 0.0f
    ];

    static FeatureLevel[] featureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
        FeatureLevel.Level_9_3,
        FeatureLevel.Level_9_2,
        FeatureLevel.Level_9_1
    ];

    static BlendDescription blendDesc = new();

    static Renderer()
    {
        blendDesc.RenderTarget[0].BlendEnable           = true;
        blendDesc.RenderTarget[0].SourceBlend           = Blend.SourceAlpha;
        blendDesc.RenderTarget[0].DestinationBlend      = Blend.InverseSourceAlpha;
        blendDesc.RenderTarget[0].BlendOperation        = BlendOperation.Add;
        blendDesc.RenderTarget[0].SourceBlendAlpha      = Blend.Zero;
        blendDesc.RenderTarget[0].DestinationBlendAlpha = Blend.Zero;
        blendDesc.RenderTarget[0].BlendOperationAlpha   = BlendOperation.Add;
        blendDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteEnable.All;
    }

    internal ID3D11Device   Device;
    IDXGIDevice1            dxgiDevice;
    public FeatureLevel     FeatureLevel    { get; private set; }
    public GPUAdapter       GPUAdapter      => gpuAdapter;
    IDXGIAdapter            dxgiAdapter;
    GPUAdapter              gpuAdapter;
    bool                    gpuForceWarp;

    ID3D11DeviceContext     context;

    ID3D11Buffer            vertexBuffer;
    ID3D11InputLayout       inputLayout;
    ID3D11RasterizerState   rasterizerState;
    ID3D11BlendState        blendStateAlpha;

    ID3D11VertexShader      ShaderVS;
    ID3D11PixelShader       ShaderPS;
    ID3D11PixelShader       ShaderBGRA;

    ID3D11Buffer            psBuffer;
    PSBufferType            psBufferData    = new();

    ID3D11Buffer            vsBuffer;
    VSBufferType            vsBufferData    = new();

    internal object         lockDevice      = new();
    bool                    isFlushing;

    bool                    use2d;
    ID2D1Device             device2d;
    ID2D1DeviceContext      context2d;
    ID2D1Bitmap1            bitmap2d;
    BitmapProperties1       bitmapProps2d = new()
    {
        BitmapOptions   = Vortice.Direct2D1.BitmapOptions.Target | Vortice.Direct2D1.BitmapOptions.CannotDraw,
        PixelFormat     = Vortice.DCommon.PixelFormat.Premultiplied
    };

    public void Initialize(bool swapChain = true)
    {
        lock (lockDevice)
        {
            try
            {
                if (CanDebug) Log.Debug("Initializing");

                if (!Disposed)
                    Dispose();

                var creationFlags       = DeviceCreationFlags.BgraSupport /*| DeviceCreationFlags.VideoSupport*/; // Let FFmpeg failed for VA if does not support it
                var creationFlagsWarp   = DeviceCreationFlags.None;

                #if DEBUG
                if (D3D11.SdkLayersAvailable())
                {
                    creationFlags       |= DeviceCreationFlags.Debug;
                    creationFlagsWarp   |= DeviceCreationFlags.Debug;
                }
                #endif

                // Config or Default (null)
                if (!gpuForceWarp && D3D11.D3D11CreateDevice(dxgiAdapter, dxgiAdapter == null ? DriverType.Hardware : DriverType.Unknown, creationFlags, featureLevels, out Device).Failure)
                    gpuForceWarp = true;

                // Forced or Fallback to WARP
                if (gpuForceWarp)
                    D3D11.D3D11CreateDevice(null, DriverType.Warp, creationFlagsWarp, featureLevels, out Device).CheckError();

                Disposed    = false;
                dxgiDevice  = Device.QueryInterface<IDXGIDevice1>();

                // Find Device's Adapter from GPUAdapters (Dispose extra adapter) | Until we use a single device per adapter
                if (dxgiAdapter == null)
                {
                    var gpuAdapters = Engine.Video.GPUAdapters;
                    dxgiAdapter     = dxgiDevice.GetAdapter();
                    var desc        = dxgiAdapter.Description;
                    bool dxgiExists = true;

                    if (!gpuAdapters.TryGetValue(desc.Luid, out gpuAdapter))
                    {
                        lock(gpuAdapters)
                            if (!gpuAdapters.TryGetValue(desc.Luid, out gpuAdapter))
                            {
                                dxgiExists  = false;
                                gpuAdapter  = Engine.Video.GetGPUAdapter(dxgiAdapter, desc);
                                gpuAdapters.Add(gpuAdapter.Luid, gpuAdapter);
                            }
                    }

                    if (dxgiExists)
                    {
                        dxgiAdapter.Dispose();
                        dxgiAdapter = gpuAdapter.dxgiAdapter;
                    }
                }

                FeatureLevel = Device.FeatureLevel;
                context = Device.ImmediateContext;
                dxgiDevice.MaximumFrameLatency = Config.Video.MaxFrameLatency;
                using (var mthread = Device.QueryInterface<ID3D11Multithread>())
                    mthread.SetMultithreadProtected(true);

                if (use2d)
                {
                    device2d  = Vortice.Direct2D1.D2D1.D2D1CreateDevice(dxgiDevice);
                    context2d = device2d.CreateDeviceContext();
                    Config.Video.OnD2DInitialized(this, context2d);
                }

                // Input Layout
                inputLayout = Device.CreateInputLayout(inputElements, ShaderCompiler.VSBlob);
                context.IASetInputLayout(inputLayout);
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

                // Vertex Shader
                vertexBuffer = Device.CreateBuffer<float>(vertexBufferData, vertexBufferDesc);
                context.IASetVertexBuffer(0, vertexBuffer, sizeof(float) * 5);

                ShaderVS = Device.CreateVertexShader(ShaderCompiler.VSBlob);
                context.VSSetShader(ShaderVS);

                vsBuffer = Device.CreateBuffer(new()
                {
                    Usage           = ResourceUsage.Default,
                    BindFlags       = BindFlags.ConstantBuffer,
                    CPUAccessFlags  = CpuAccessFlags.None,
                    ByteWidth       = (uint)(sizeof(VSBufferType) + (16 - (sizeof(VSBufferType) % 16)))
                });
                context.VSSetConstantBuffer(0, vsBuffer);

                vsBufferData.mat = Matrix4x4.Identity;
                context.UpdateSubresource(vsBufferData, vsBuffer);

                // Pixel Shader
                InitPS();
                psBuffer = Device.CreateBuffer(new()
                {
                    Usage           = ResourceUsage.Default,
                    BindFlags       = BindFlags.ConstantBuffer,
                    CPUAccessFlags  = CpuAccessFlags.None,
                    ByteWidth       = (uint)(sizeof(PSBufferType) + (16 - (sizeof(PSBufferType) % 16)))
                });
                context.PSSetConstantBuffer(0, psBuffer);

                // subs
                ShaderBGRA = ShaderCompiler.CompilePS(Device, "bgra", @"color = float4(Texture1.Sample(Sampler, input.Texture).rgba);", null);

                // Blend State (currently used for OverlayTexture)
                blendStateAlpha = Device.CreateBlendState(blendDesc);

                // Rasterizer (Will change CullMode to None for H-V Flip)
                rasterizerState = Device.CreateRasterizerState(new(CullMode.Back, FillMode.Solid));
                context.RSSetState(rasterizerState);

                if (!gpuForceWarp)
                    InitializeVideoProcessor();

                if (CanInfo) Log.Info($"Initialized with Feature Level {(int)FeatureLevel >> 12}.{((int)FeatureLevel >> 8) & 0xf}");

            }
            catch (Exception e)
            {
                if (!gpuForceWarp)
                {
                    gpuForceWarp = true;
                    Log.Error($"Initialization failed ({e.Message}). Failling back to WARP device.");
                    Flush();
                }
                else
                {
                    Log.Error($"Initialization failed ({e.Message})");
                    Dispose();
                    return;
                }
            }

            if (swapChain)
            {
                if (ControlHandle != 0)
                    InitializeSwapChain(ControlHandle);
                else if (SwapChainWinUIClbk != null)
                    InitializeWinUISwapChain();
            }
        }
    }

    public void Dispose()
    {
        lock (lockDevice)
        {
            if (Disposed)
                return;

            Disposed = true;

            if (CanDebug) Log.Debug("Disposing");

            lock (lockRenderLoops)
            {
                VideoDecoder.DisposeFrame(LastFrame);
                LastFrame = null;
            }

            DisposeSwapChain();

            if (use2d)
            {
                Config.Video.OnD2DDisposing(this, context2d);
                bitmap2d?.Dispose();
                context2d?.Dispose();
                device2d?.Dispose();
            }

            DisposeVideoProcessor();

            ShaderVS?.Dispose();
            ShaderPS?.Dispose();
            prevPSUniqueId = curPSUniqueId = ""; // Ensure we re-create ShaderPS for FlyleafVP on ConfigPlanes
            psBuffer?.Dispose();
            vsBuffer?.Dispose();
            inputLayout?.Dispose();
            vertexBuffer?.Dispose();
            rasterizerState?.Dispose();
            blendStateAlpha?.Dispose();

            overlayTexture?.Dispose();
            overlayTextureSrv?.Dispose();
            ShaderBGRA?.Dispose();

            singleGpu?.Dispose();
            singleStage?.Dispose();
            singleGpuRtv?.Dispose();
            singleStageDesc.Width = 0; // ensures re-allocation

            if (rtv2 != null)
            {
                for(int i = 0; i < rtv2.Length; i++)
                    rtv2[i].Dispose();

                rtv2 = null;
            }

            if (backBuffer2 != null)
            {
                for(int i = 0; i < backBuffer2.Length; i++)
                    backBuffer2[i]?.Dispose();

                backBuffer2 = null;
            }

            if (Device != null)
            {
                context.ClearState();
                context.Flush();
                context.Dispose();
                Device.Dispose();
                dxgiDevice.Dispose();
                Device = null;
            }

            #if DEBUG
            ReportLiveObjects();
            #endif

            DAR         = new(0, 1);
            curRatio    = 1.0f;
            if (CanInfo) Log.Info("Disposed");
        }
    }

    public void Flush()
    {
        lock (lockDevice)
        {
            isFlushing = true;
            var controlHandle = ControlHandle;
            var swapChainClbk = SwapChainWinUIClbk;

            Dispose();
            ControlHandle = controlHandle;
            SwapChainWinUIClbk = swapChainClbk;

            Initialize();
            isFlushing = false;
        }
    }

    void HandleDeviceLost()
    {
        Thread.Sleep(100);

        var stream = VideoDecoder.VideoStream;
        if (stream == null)
        {
            Flush();
            return;
        }

        var running = VideoDecoder.IsRunning;

        VideoDecoder.Dispose();
        Flush();
        VideoDecoder.Open(stream); // Should Re-ConfigPlanes()
        VideoDecoder.keyPacketRequired = !VideoDecoder.isIntraOnly;
        VideoDecoder.keyFrameRequired  = false;
        if (running)
            VideoDecoder.Start();
    }

    #if DEBUG
    public static void ReportLiveObjects()
    {
        try
        {
            if (DXGI.DXGIGetDebugInterface1(out IDXGIDebug1 dxgiDebug).Success)
            {
                dxgiDebug.ReportLiveObjects(DXGI.DebugAll, ReportLiveObjectFlags.Summary | ReportLiveObjectFlags.IgnoreInternal);
                dxgiDebug.Dispose();
            }
        } catch { }
    }
    #endif

    [StructLayout(LayoutKind.Sequential)]
    struct PSBufferType
    {
        public int coefsIndex;

        // Filters
        public float brightness;    // -0.5  to 0.5     (0.0 default)
        public float contrast;      //  0.0  to 2.0     (1.0 default)
        public float hue;           // -3.14 to 3.14    (0.0 default)
        public float saturation;    //  0.0  to 2.0     (1.0 default)

        public float uvOffset;
        public HDRtoSDRMethod tonemap;
        public float hdrtone;

        public PSBufferType()
        {
            brightness  = 0;
            contrast    = 1;
            hue         = 0;
            saturation  = 1;
            tonemap     = HDRtoSDRMethod.Hable;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VSBufferType
    {
        public Matrix4x4    mat;
        public Vector4      cropRegion;

        public VSBufferType()
            => cropRegion = new(0, 0, 1, 1);
    }
}

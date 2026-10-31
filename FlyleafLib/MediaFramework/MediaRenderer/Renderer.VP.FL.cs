using System.Diagnostics;
using System.Numerics;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    internal ID3D11Buffer           vertexBuffer;
    internal ID3D11InputLayout      inputLayout;
    internal ID3D11RasterizerState  rsStateHVFlip;
    internal ID3D11BlendState       blendStateAlpha;

    internal ID3D11VertexShader     vsMain;
    internal ID3D11VertexShader     vsSimple;
    internal ID3D11SamplerState     samplerLinear, samplerPoint;

    internal Dictionary<string, ID3D11PixelShader>
                    psShader = [];

    ID3D11Buffer    psBuffer;
    PSBufferType    psData    = new();

    ID3D11Buffer    hdrBuffer;
    HDRBufferType   hdrData   = new();
    bool            checkHDRConfig;

    ID3D11Buffer    doviBuffer;

    ID3D11Buffer    vsBuffer;
    VSBufferType    vsData    = new();
    bool            vflip;

    ID3D11Buffer    panoBuffer;
    PanoBufferType  panoData  = new() { PanoParams = new(0.5f, 0.5f, 0.5f, 90f), AspectRatio = 1.778f };

    ID3D11ShaderResourceView
                    iccSrv;
    ID3D11Texture2D iccTxt;
    static readonly nint    // TBR: But consider global support not just here* OpenMonitorProfile(SwapChain.Monitor.Hwnd)
                    iccDst  = NativeMethods.OpenSRgbProfile();

    static InputElementDescription[] inputElements =
    {
        new("POSITION", 0, Format.R32G32B32_Float,  0),
        new("TEXCOORD", 0, Format.R32G32_Float,     0),
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
    static SamplerDescription samplerLinearDesc = new()
    {
        Filter          = Filter.MinMagMipLinear,
        AddressU        = TextureAddressMode.Clamp,
        AddressV        = TextureAddressMode.Clamp, 
        AddressW        = TextureAddressMode.Clamp,
        ComparisonFunc  = ComparisonFunction.Never,
        MinLOD          = 0,
        MaxLOD          = float.MaxValue
    };
    static SamplerDescription samplerPointDesc = new()
    {
        Filter          = Filter.MinMagMipPoint,
        AddressU        = TextureAddressMode.Clamp,
        AddressV        = TextureAddressMode.Clamp, 
        AddressW        = TextureAddressMode.Clamp,
        ComparisonFunc  = ComparisonFunction.Never,
        MinLOD          = 0,
        MaxLOD          = float.MaxValue
    };
    static BlendDescription blendDesc = new()
    {
        RenderTarget =
        {
            [0] = new()
            {
                BlendEnable           = true,
                SourceBlend           = Blend.SourceAlpha,
                DestinationBlend      = Blend.InverseSourceAlpha,
                BlendOperation        = BlendOperation.Add,
                SourceBlendAlpha      = Blend.Zero,
                DestinationBlendAlpha = Blend.Zero,
                BlendOperationAlpha   = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All
            }
        }
    };

    static BufferDescription psDesc = new()
    {
        Usage           = ResourceUsage.Default,
        BindFlags       = BindFlags.ConstantBuffer,
        CPUAccessFlags  = CpuAccessFlags.None,
        ByteWidth       = (uint)sizeof(PSBufferType)
    };

    struct PSBufferType
    {
        public int   CoeffsIndex;
        public float Brightness;    // -0.5  to 0.5     (0.0 default)
        public float Contrast;      //  0.0  to 2.0     (1.0 default)
        public float Hue;           // -3.14 to 3.14    (0.0 default)

        public float Saturation;    //  0.0  to 2.0     (1.0 default)
        public float UVOffset;
        Vector2 pad;

        public PSBufferType()
        {
            Brightness = 0;
            Contrast   = 1;
            Hue        = 0;
            Saturation = 1;
        }
    }

    static BufferDescription hdrDesc = new()
    {
        Usage           = ResourceUsage.Default,
        BindFlags       = BindFlags.ConstantBuffer,
        CPUAccessFlags  = CpuAccessFlags.None,
        ByteWidth       = (uint)sizeof(HDRBufferType)
    };

    struct HDRBufferType
    {
        public ToneSplineParams Spline; // 6 floats
        public float SourceMinNits;
        public float SourcePeakNits;

        public float TargetMinNits;
        public float TargetPeakNits;
        public int   NativeOutput;
        public float HLGGamma;

        public float BT1886BlackRoot;
        public float BT1886InvRange;
        public float GamutMinPQ;
        public float GamutInvRangePQ;

        public HDRBufferType()
        {
            TargetMinNits  = 0.203f;
            TargetPeakNits = 203;
            HLGGamma       = 1.2f * MathF.Pow(1.111f, MathF.Log2(TargetPeakNits / 1000.0f));
        }
    }

    struct ToneSplineParams
    {
        public float SrcPivot;
        public float DstPivot;
        public float Pa;
        public float Slope;

        public float Qa;
        public float Qb;
    }

    static BufferDescription vsDesc = new()
    {
        Usage           = ResourceUsage.Default,
        BindFlags       = BindFlags.ConstantBuffer,
        CPUAccessFlags  = CpuAccessFlags.None,
        ByteWidth       = (uint)sizeof(VSBufferType)
    };

    struct VSBufferType
    {
        public Matrix4x4    Matrix; // Rotation | HV Flip
        public Vector4      Crop;
        
        public VSBufferType()
        {
            Matrix  = Matrix4x4.Identity;
            Crop    = new(0, 0, 1, 1);
        }
    }

    static BufferDescription panoDesc = new()
    {
        Usage           = ResourceUsage.Default,
        BindFlags       = BindFlags.ConstantBuffer,
        CPUAccessFlags  = CpuAccessFlags.None,
        ByteWidth       = (uint)sizeof(PanoBufferType)
    };

    struct PanoBufferType
    {
        public Vector4 PanoParams;  // rotationX, rotationY, zoom, fov
        public float AspectRatio;
        Vector3 pad;
    }

    void FLInit()
    {
        Debug.Assert(sizeof(PSBufferType)  % 16 == 0);
        Debug.Assert(sizeof(HDRBufferType) % 16 == 0);
        Debug.Assert(sizeof(VSBufferType)  % 16 == 0);
        Debug.Assert(sizeof(PanoBufferType)% 16 == 0);

        for (int i = 0; i < txtDesc.Length; i++)
        {   // TBR: For SW disposing per frame... Immutable might not worth it (D3 requires RenderTarget / Default usage)
            txtDesc[i].Usage                = ResourceUsage.Default;
            txtDesc[i].BindFlags            = BindFlags.ShaderResource | BindFlags.RenderTarget;
            txtDesc[i].SampleDescription    = new(1, 0);
            txtDesc[i].ArraySize            = 1;
            txtDesc[i].MipLevels            = 1;
        }

        for (int i = 0; i < txtDesc.Length; i++)
        {
            srvDesc[i].Texture2D        = new() { MipLevels = 1, MostDetailedMip = 0 };
            srvDesc[i].Texture2DArray   = new() { MipLevels = 1, ArraySize = 1 };
        }
    }

    void FLSetup()
    {
        vsBuffer        = device.CreateBuffer(vsDesc);
        psBuffer        = device.CreateBuffer(psDesc);
        hdrBuffer       = device.CreateBuffer(hdrDesc);
        doviBuffer      = device.CreateBuffer(doviDesc);
        panoBuffer      = device.CreateBuffer(panoDesc);
        vertexBuffer    = device.CreateBuffer<float>(vertexBufferData, vertexBufferDesc);
        inputLayout     = device.CreateInputLayout(inputElements, ShaderCompiler.VSBlob);
        samplerLinear   = device.CreateSamplerState(samplerLinearDesc);
        samplerPoint    = device.CreateSamplerState(samplerPointDesc);
        vsMain          = device.CreateVertexShader(ShaderCompiler.VSBlob);
        vsSimple        = device.CreateVertexShader(ShaderCompiler.VSSimpleBlob);
        rsStateHVFlip   = device.CreateRasterizerState(new(CullMode.None, FillMode.Solid));
        
        // TBR: Currently Bitmap Subs only (possible ChildRenderer too - might separate them or create separate PS for it)
        blendStateAlpha = device.CreateBlendState(blendDesc);
        psShader["rgba"]= ShaderCompiler.CompilePS(device, "rgba", "color = float4(Texture1.Sample(Sampler, input.Texture).rgba);");

        iccTxt          = device.CreateTexture2D(Format.R16G16B16A16_UNorm, 33 * 33, 33);
        iccSrv          = device.CreateShaderResourceView(iccTxt);
        context.PSSetShaderResource     (4, iccSrv);
        
        context.IASetVertexBuffer       (0, vertexBuffer, sizeof(float) * 5);
        context.IASetInputLayout        (inputLayout);
        context.IASetPrimitiveTopology  (PrimitiveTopology.TriangleList);
        context.PSSetConstantBuffer     (0, psBuffer);
        context.PSSetConstantBuffer     (1, hdrBuffer);
        context.PSSetConstantBuffer     (2, doviBuffer);
        context.PSSetConstantBuffer     (3, panoBuffer);
        context.UpdateSubresource       (hdrData, hdrBuffer);
        context.VSSetConstantBuffer     (0, vsBuffer);
        context.VSSetShader             (vsMain);
        context.PSSetSampler            (0, samplerLinear);

        FLFiltersSetup();
    }

    void FLSetViewport()
    {
        SetViewport(ControlWidth, ControlHeight);
        context.RSSetViewport(Viewport);

        // Update pano aspectRatio when viewport changes
        if (ucfg.Pano360._enabled)
            vpRequests |= VPRequestType.UpdatePano;
    }
    void FLSetRotationFlip()
    {
        SetRotation();

        vsData.Matrix = Matrix4x4.CreateFromYawPitchRoll(0.0f, 0.0f, (float) (Math.PI / 180 * rotation));

        vflip = ucfg.vflip ^ scfg.VFlip;

        if (ucfg.hflip || vflip)
        {
            vsData.Matrix *= Matrix4x4.CreateScale(ucfg.hflip ? -1 : 1, vflip ? -1 : 1, 1);
            context.RSSetState(rsStateHVFlip);
        }
        else
            context.RSSetState(null);

        vpRequests |= VPRequestType.UpdateVS;
    }
    void FLSetCrop()
    {
        uint sourceWidth;
        uint sourceHeight;
        CropRect sourceCrop;

        if (VideoProcessor == VideoProcessors.SwsScale)
        {
            sourceWidth  = swsWidth;
            sourceHeight = swsHeight;
            sourceCrop   = swsCrop;
        }
        else
        {
            sourceWidth  = scfg.txtWidth;
            sourceHeight = scfg.txtHeight;
            sourceCrop   = scfg.Crop;
        }

        crop = sourceCrop + ucfg.crop;
        VisibleWidth  = sourceWidth  - crop.Width;
        VisibleHeight = sourceHeight - crop.Height;

        vsData.Crop = new()
        {
            X = crop.Left / (float)sourceWidth,
            Y = crop.Top  / (float)sourceHeight,
            Z = (sourceWidth  - crop.Right)  / (float)sourceWidth,
            W = (sourceHeight - crop.Bottom) / (float)sourceHeight
        };

        var alphaPos = ucfg._SplitFrameAlphaPosition;

        if (alphaPos != SplitFrameAlphaPosition.None)
        {
            if (alphaPos is SplitFrameAlphaPosition.Left or SplitFrameAlphaPosition.Right)
                VisibleWidth /= 2;
            else
                VisibleHeight /= 2;
        }

        SetVisibleSizeAndRatioHelper();

        vpRequests |= VPRequestType.Viewport | VPRequestType.UpdateVS;
    }

    internal void FLUpdateTargetNitsConfig(bool hdr)
    {
        if (hdr == hdrSwapchain && UsesDisplayMapping)
        {
            FLUpdateTargetNits();
            RenderRequest();
        }
        else
            checkHDRConfig = true;
    }
    void FLUpdateTargetNits()
    {
        checkHDRConfig = false;

        float autoTargetPeak;
        float autoTargetMin;
        float configTargetPeak;
        float configTargetMin;

        if (hdrSwapchain)
        {
            autoTargetPeak  = float.IsFinite(autoHDRPeakNits) && autoHDRPeakNits > 0 ? autoHDRPeakNits : 1000.0f;
            autoTargetMin   = float.IsFinite(autoHDRMinNits)  && autoHDRMinNits >= 0 ? autoHDRMinNits : 0.0f;
            configTargetPeak= ucfg.TargetHDRPeakNits;
            configTargetMin = ucfg.TargetHDRMinNits;
            hdrData.NativeOutput = 1;
        }
        else
        {
            autoTargetPeak  = float.IsFinite(autoSDRPeakNits) && autoSDRPeakNits > 0 ? autoSDRPeakNits : 203.0f;
            autoTargetMin   = float.IsFinite(autoSDRMinNits)  && autoSDRMinNits >= 0 ? autoSDRMinNits : 0.203f;
            configTargetPeak= ucfg.TargetSDRPeakNits;
            configTargetMin = ucfg.TargetSDRMinNits;
            hdrData.NativeOutput = 0;
        }

        hdrData.TargetPeakNits  = configTargetPeak > 0 ? configTargetPeak : autoTargetPeak;
        hdrData.TargetMinNits   = configTargetMin >= 0 ? configTargetMin  : hdrData.TargetPeakNits * autoTargetMin / autoTargetPeak;

        // Keep the target range sane even if a display API/config reports broken values.
        hdrData.TargetPeakNits = Math.Max(hdrData.TargetPeakNits, 0.1f);
        hdrData.TargetMinNits  = Math.Clamp(hdrData.TargetMinNits, 0.0f, hdrData.TargetPeakNits - 0.001f);

        hdrData.BT1886BlackRoot= MathF.Pow(hdrData.TargetMinNits / hdrData.TargetPeakNits, 1.0f / 2.4f);
        hdrData.BT1886InvRange = 1.0f / (1.0f - hdrData.BT1886BlackRoot);

        if (hdrData.NativeOutput == 0)
            FLGamutUpdateTarget();

        hdrData.HLGGamma = hdrData.TargetPeakNits >= 400.0f && hdrData.TargetPeakNits <= 2000.0f ?
            1.2f + 0.42f * MathF.Log10(hdrData.TargetPeakNits / 1000.0f) :          // BT.2100-3
            1.2f * MathF.Pow(1.111f, MathF.Log2(hdrData.TargetPeakNits / 1000.0f)); // BT.2100-3 extended range

        if (hdrHasStats)
        {
            FLHDRApply();
            vpRequestsIn &= ~VPRequestType.UpdateHDR;
        }
        else
            vpRequestsIn |= VPRequestType.UpdateHDR;
    }
    void FLSetPano360()
    {
        var pano = ucfg.Pano360;
        panoData.PanoParams = new((float)pano._rotationX, (float)pano._rotationY, (float)pano._zoom, (float)pano._fov);
        // aspectRatio based on control (viewport) size, not video source size
        panoData.AspectRatio = ControlWidth > 0 && ControlHeight > 0 ? (float)ControlWidth / ControlHeight : 1.778f;
        context.UpdateSubresource(panoData, panoBuffer);
    }
    
    void FLProcessRequests()
    {
        while (vpRequestsIn != VPRequestType.Empty)
        {
            if (vpRequestsIn.HasFlag(VPRequestType.ReConfigVP))
            {
                VPSwitch();

                if (VideoProcessor == VideoProcessors.D3D11)
                    return;
            }

            vpRequests  = vpRequestsIn;
            vpRequestsIn= VPRequestType.Empty;

            if (vpRequests.HasFlag(VPRequestType.BackColor))
                SetBackColor();

            if (vpRequests.HasFlag(VPRequestType.UpdateSwapChain))
                UpdateHDRSwapchain();

            if (vpRequests.HasFlag(VPRequestType.RotationFlip))
                FLSetRotationFlip();

            if (vpRequests.HasFlag(VPRequestType.Crop))
                FLSetCrop();

            if (vpRequests.HasFlag(VPRequestType.Resize))
                SetSize();

            if (vpRequests.HasFlag(VPRequestType.AspectRatio))
                SetAspectRatio();

            if (vpRequests.HasFlag(VPRequestType.Viewport))
                FLSetViewport();

            if (vpRequests.HasFlag(VPRequestType.UpdateVS))
                context.UpdateSubresource(vsData, vsBuffer);

            if (vpRequests.HasFlag(VPRequestType.UpdatePS))
                context.UpdateSubresource(psData, psBuffer);

            if (vpRequests.HasFlag(VPRequestType.UpdateHDR))
                context.UpdateSubresource(hdrData, hdrBuffer);

            if (vpRequests.HasFlag(VPRequestType.UpdatePano))
                FLSetPano360();
        }
    }
    void FLRender(VideoFrame frame)
    {
        if (frame.SRV == null)
            return; // TODO: when we dispose on switch

        context.OMSetRenderTargets(SwapChain.BackBufferRtv);
        context.ClearRenderTargetView(SwapChain.BackBufferRtv, ucfg.flBackColor);
        context.PSSetShaderResources(0, frame.SRV);

        if (isPQSpline)
        {
            if (isDovi)
                FLDoviApply(frame);

            FLHDRDetect();
        }
        
        context.Draw(6, 0);

        if (context2d != null)
            ucfg.OnD2DDraw(this, context2d);

        FLSubsRender();
    }
    void FLRender(VideoFrame frame, ID3D11RenderTargetView rtv, Viewport view)
    {
        context.PSSetShaderResources(0, frame.SRV);

        if (isPQSpline)
        {
            if (isDovi)
                FLDoviApply(frame);

            FLHDRDetectReset();
            FLHDRDetect();
        }
        
        context.OMSetRenderTargets(rtv);
        context.RSSetViewport(view);
        context.Draw(6, 0);
    }

    void FLDispose()
    {   // Called by Device Dispose only* (shared lock)

        hdrSwapchain = false;
        hdrData.NativeOutput = 0;

        // TODO: Dispose filters?*
        SwsDispose();
        SubsDispose();
        FLHDRDispose();

        if (snapshot != null)
        {
            snapshot.Dispose();
            snapshot = null;
        }

        foreach(var shader in psShader.Values)
            shader.Dispose();
        psShader.Clear();
        psIdPrev = "f^";
        
        vsBuffer.       Dispose();
        psBuffer.       Dispose();
        hdrBuffer.      Dispose();
        doviBuffer.     Dispose();
        panoBuffer.     Dispose();
        inputLayout.    Dispose();
        vertexBuffer.   Dispose();
        samplerLinear.  Dispose();
        samplerPoint.   Dispose();
        vsMain.         Dispose();
        vsSimple.       Dispose();
        rsStateHVFlip.  Dispose();
        blendStateAlpha.Dispose();
        iccSrv.         Dispose();
        iccTxt.         Dispose();
        FLGamutDispose();
    }
}

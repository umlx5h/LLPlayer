using System.Numerics;
using System.Runtime.InteropServices;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

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

    ID3D11Buffer    vsBuffer;
    VSBufferType    vsData    = new();

    bool            vflip;

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

    void FLInit()
    {
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
        
        context.IASetVertexBuffer       (0, vertexBuffer, sizeof(float) * 5);
        context.IASetInputLayout        (inputLayout);
        context.IASetPrimitiveTopology  (PrimitiveTopology.TriangleList);
        context.PSSetConstantBuffer     (0, psBuffer);
        context.VSSetConstantBuffer     (0, vsBuffer);
        context.VSSetShader             (vsMain);
        context.PSSetSampler            (0, samplerLinear);

        FLFiltersSetup();
    }

    void FLSetViewport()
    {
        SetViewport(ControlWidth, ControlHeight);
        context.RSSetViewport(Viewport);
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
        crop            = scfg.Crop + ucfg.crop;
        VisibleWidth    = scfg.txtWidth  - crop.Width;
        VisibleHeight   = scfg.txtHeight - crop.Height;

        if (VideoProcessor == VideoProcessors.SwsScale &&
            (scfg.Cropping.HasFlag(Cropping.Codec) || scfg.Cropping.HasFlag(Cropping.Texture)))
        {   // SwsScale does codec's cropping and we don't use texture cropping
            crop = scfg.cropStream + ucfg.crop;

            var totalWidth  = VisibleWidth  + scfg.cropStream.Width;
            var totalHeight = VisibleHeight + scfg.cropStream.Height;

            vsData.Crop = new()
            {
                X = crop.Left / ((float)totalWidth),
                Y = crop.Top  / ((float)totalHeight),
                Z = (totalWidth  - crop.Right)  / ((float)totalWidth),
                W = (totalHeight - crop.Bottom) / ((float)totalHeight)
            };
        }
        else
            vsData.Crop = new()
            {
                X = crop.Left / (float)scfg.txtWidth,
                Y = crop.Top  / (float)scfg.txtHeight,
                Z = (scfg.txtWidth  - crop.Right)  / (float)scfg.txtWidth, //1.0f - (right  / (float)textWidth),
                W = (scfg.txtHeight - crop.Bottom) / (float)scfg.txtHeight //1.0f - (bottom / (float)textHeight)
            };

        var alphaPos = ucfg._SplitFrameAlphaPosition;
        if (alphaPos != SplitFrameAlphaPosition.None)
        {
            if      (alphaPos == SplitFrameAlphaPosition.Left  || alphaPos == SplitFrameAlphaPosition.Right)
                VisibleWidth /= 2;
            else if (alphaPos == SplitFrameAlphaPosition.Top   || alphaPos == SplitFrameAlphaPosition.Bottom)
                VisibleHeight /= 2;
        }

        SetVisibleSizeAndRatioHelper();

        vpRequests &= ~VPRequestType.Crop;
        vpRequests |=  VPRequestType.Viewport | VPRequestType.UpdateVS;
    }
    void FLSetHDRtoSDR()
    {
        psData.Tonemap = ucfg.HDRtoSDRMethod;

        switch (psData.Tonemap)
        {
            case HDRtoSDRMethod.Hable:
                psData.HDRTone = 10_000f / ucfg.SDRDisplayNits;
                break;

            case HDRtoSDRMethod.Reinhard:
                psData.HDRTone = (10_000f / ucfg.SDRDisplayNits) / 2f;
                break;

            case HDRtoSDRMethod.Aces:
                psData.HDRTone = (10_000f / ucfg.SDRDisplayNits) / 7f;
                break;
        }

        vpRequests &= ~VPRequestType.HDRtoSDR;
        vpRequests |=  VPRequestType.UpdatePS;
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

            if (vpRequests.HasFlag(VPRequestType.HDRtoSDR))
                FLSetHDRtoSDR();

            if (vpRequests.HasFlag(VPRequestType.UpdateVS))
                context.UpdateSubresource(vsData, vsBuffer);

            if (vpRequests.HasFlag(VPRequestType.UpdatePS))
                context.UpdateSubresource(psData, psBuffer);   
                
        }
    }
    void FLRender(VideoFrame frame)
    {
        if (frame.SRV == null)
            return; // TODO: when we dispose on switch

        context.OMSetRenderTargets(SwapChain.BackBufferRtv);
        context.ClearRenderTargetView(SwapChain.BackBufferRtv, ucfg.flBackColor);
        context.PSSetShaderResources(0, frame.SRV);
        context.Draw(6, 0);

        if (context2d != null)
            ucfg.OnD2DDraw(this, context2d);

        FLSubsRender();
    }
    void FLRender(ID3D11ShaderResourceView[] srvs, ID3D11RenderTargetView rtv, Viewport view)
    {
        context.OMSetRenderTargets(rtv);
        context.RSSetViewport(view);
        context.PSSetShaderResources(0, srvs);
        context.Draw(6, 0);
    }

    void FLDispose()
    {   // Called by Device Dispose only* (shared lock)

        // TODO: Dispose filters?*
        SwsDispose();
        SubsDispose();

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
        inputLayout.    Dispose();
        vertexBuffer.   Dispose();
        samplerLinear.  Dispose();
        samplerPoint.   Dispose();
        vsMain.         Dispose();
        vsSimple.       Dispose();
        rsStateHVFlip.  Dispose();
        blendStateAlpha.Dispose();
    }

    static BufferDescription psDesc = new()
    {
        Usage           = ResourceUsage.Default,
        BindFlags       = BindFlags.ConstantBuffer,
        CPUAccessFlags  = CpuAccessFlags.None,
        ByteWidth       = (uint)(sizeof(PSBufferType) + (16 - (sizeof(PSBufferType) % 16)))
    };

    [StructLayout(LayoutKind.Sequential)]
    struct PSBufferType
    {
        public int CoeffsIndex;

        public float Brightness;    // -0.5  to 0.5     (0.0 default)
        public float Contrast;      //  0.0  to 2.0     (1.0 default)
        public float Hue;           // -3.14 to 3.14    (0.0 default)
        public float Saturation;    //  0.0  to 2.0     (1.0 default)

        public float UVOffset;
        public HDRtoSDRMethod Tonemap;
        public float HDRTone;

        public PSBufferType()
        {
            Brightness  = 0;
            Contrast    = 1;
            Hue         = 0;
            Saturation  = 1;
            Tonemap     = HDRtoSDRMethod.Hable;
        }
    }

    internal static BufferDescription vsDesc = new()
    {
        Usage           = ResourceUsage.Default,
        BindFlags       = BindFlags.ConstantBuffer,
        CPUAccessFlags  = CpuAccessFlags.None,
        ByteWidth       = (uint)(sizeof(VSBufferType) + (16 - (sizeof(VSBufferType) % 16)))
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct VSBufferType
    {
        public Matrix4x4    Matrix; // Rotation | HV Flip
        public Vector4      Crop;
        
        public VSBufferType()
        {
            Matrix  = Matrix4x4.Identity;
            Crop    = new(0, 0, 1, 1);
        }
    }
}

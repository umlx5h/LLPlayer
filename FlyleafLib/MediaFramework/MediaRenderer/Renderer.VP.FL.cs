using System.Numerics;
using System.Runtime.InteropServices;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using FlyleafLib.MediaFramework.MediaFrame;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

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

    ID3D11Buffer    panoBuffer;
    PanoBufferType  panoData  = new() { PanoParams = new(0.5f, 0.5f, 0.5f, 90f), AspectRatio = 1.778f };

    ID3D11ShaderResourceView
                    iccSrv;
    ID3D11Texture2D iccTxt;
    static readonly nint    // TBR: But consider global support not just here* OpenMonitorProfile(SwapChain.Monitor.Hwnd)
                    iccDst  = NativeMethods.OpenSRgbProfile();

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

    static BufferDescription psDesc = new()
    {
        Usage           = ResourceUsage.Default,
        BindFlags       = BindFlags.ConstantBuffer,
        CPUAccessFlags  = CpuAccessFlags.None,
        ByteWidth       = (uint)((sizeof(PSBufferType) + 15) & ~15)
    };

    [StructLayout(LayoutKind.Sequential)]
    struct PSBufferType
    {
        public int CoeffsIndex;

        public float HDRBrightness; // 0.25  to 4.0     (0.0 default) | 2^(-2) -> 2^2
        public float Brightness;    // -0.5  to 0.5     (0.0 default)
        public float Contrast;      //  0.0  to 2.0     (1.0 default)
        public float Hue;           // -3.14 to 3.14    (0.0 default)
        public float Saturation;    //  0.0  to 2.0     (1.0 default)

        public float UVOffset;

        public ToneSplineParams Spline;

        public float PQScale;
        public float SourceMinNits;
        public float SourcePeakNits;
        public float TargetMinNits;
        public float TargetPeakNits;

        public PSBufferType()
        {
            Brightness = 0;
            Contrast   = 1;
            Hue        = 0;
            Saturation = 1;

            Spline = new ToneSplineParams { Slope = 1 };
            TargetPeakNits = 100;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ToneSplineParams
    {
        public float SrcPivot;
        public float DstPivot;

        public float Pa;
        public float Slope;

        public float Qa;
        public float Qb;
    }

    internal static BufferDescription vsDesc = new()
    {
        Usage           = ResourceUsage.Default,
        BindFlags       = BindFlags.ConstantBuffer,
        CPUAccessFlags  = CpuAccessFlags.None,
        ByteWidth       = (uint)((sizeof(VSBufferType) + 15) & ~15)
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

    static BufferDescription panoDesc = new()
    {
        Usage           = ResourceUsage.Default,
        BindFlags       = BindFlags.ConstantBuffer,
        CPUAccessFlags  = CpuAccessFlags.None,
        ByteWidth       = (uint)((sizeof(PanoBufferType) + 15) & ~15)
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct PanoBufferType
    {
        public Vector4 PanoParams;  // rotationX, rotationY, zoom, fov
        public float AspectRatio;
        float _pad1, _pad2, _pad3;  // 16-byte alignment
    }

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
        context.PSSetConstantBuffer     (1, panoBuffer);
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
            vpRequests |= VPRequestType.Pano360;
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
    internal void FLSetHDRBrightness(bool request = true)
    {
        psData.HDRBrightness = MathF.Pow(2f, Scale(ucfg.HDRBrightness, -100, 100, -2, 2));
        if (request)
            VPRequest(VPRequestType.UpdatePS);
    }
    void FLSetHDRtoSDR()
    {
        if (scfg == null || scfg.HDRFormat == HDRFormat.None) // TBR scfg?
            return;

        var targetMinNits  = SwapChain.Monitor.MinLuminance;
        var targetPeakNits = SwapChain.Monitor.MaxLuminance;
        if (targetPeakNits == 0)
            targetPeakNits = 203;

        psData.SourceMinNits    = scfg.sourceMinNits;
        psData.SourcePeakNits   = scfg.sourcePeakNits;
        psData.TargetMinNits    = targetMinNits;
        psData.TargetPeakNits   = targetPeakNits;;

        if (scfg.HDRFormat == HDRFormat.HLG) { }
        else if (scfg.sourcePeakNits <= targetPeakNits)
            psData.PQScale = 10_000f / targetPeakNits;
        else
            psData.Spline = GetSplineParams(
                sourceMinNits:  scfg.sourceMinNits,
                sourcePeakNits: scfg.sourcePeakNits,
                sourceAvgNits:  scfg.sourceAvgNits,
                targetMinNits:  targetMinNits,
                targetPeakNits: targetPeakNits);
        
        vpRequests &= ~VPRequestType.HDRtoSDR;
        vpRequests |=  VPRequestType.UpdatePS;
    }
    void FLSetPano360()
    {
        var pano = ucfg.Pano360;
        panoData.PanoParams = new((float)pano._rotationX, (float)pano._rotationY,
                                 (float)pano._zoom, (float)pano._fov);
        // aspectRatio based on control (viewport) size, not video source size
        panoData.AspectRatio = ControlWidth > 0 && ControlHeight > 0
            ? (float)ControlWidth / ControlHeight : 1.778f;
        context.UpdateSubresource(panoData, panoBuffer);
        vpRequests &= ~VPRequestType.Pano360;
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

            if (vpRequests.HasFlag(VPRequestType.Pano360))
                FLSetPano360();

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
    }

    #region HDR -> SDR
    internal static bool GetHdr10PlusPeak(AVDynamicHDRPlus* hdr, out float peakNits, out float avgNits)
    {
        ref var p = ref hdr->@params._0;

        double r = p.maxscl[0].ToDouble();
        double g = p.maxscl[1].ToDouble();
        double b = p.maxscl[2].ToDouble();

        double maxRgb = Math.Max(r, Math.Max(g, b));

        if (maxRgb > 0)
        {
            double peak =
                0.2627 * r +
                0.6780 * g +
                0.0593 * b;

            double avg =
                p.average_maxrgb.ToDouble() *
                peak / maxRgb;

            peakNits = (float)(peak * 10000.0);
            avgNits  = (float)(avg  * 10000.0);

            return float.IsFinite(peakNits) && peakNits > 0;
        }

        double max = 0;

        for (int i = 0; i < p.num_distribution_maxrgb_percentiles; i++)
            max = Math.Max(
                max,
                p.distribution_maxrgb[i].percentile.ToDouble());

        if (max <= 0)
        {
            peakNits = 0;
            avgNits  = 0;
            return false;
        }

        peakNits = (float)(max * 10000.0);
        avgNits  = (float)(p.average_maxrgb.ToDouble() * 10000.0);

        return true;
    }

    internal struct AVDOVIDecoderConfigurationRecord
    {
        public byte dv_version_major;
        public byte dv_version_minor;
        public byte dv_profile;
        public byte dv_level;
        public byte rpu_present_flag;
        public byte el_present_flag;
        public byte bl_present_flag;
        public byte dv_bl_signal_compatibility_id;
        public byte dv_md_compression;
    }
    static double NitsToPQ(double nits)
    {
        const double m1 = 0.1593017578125;
        const double m2 = 78.84375;
        const double c1 = 0.8359375;
        const double c2 = 18.8515625;
        const double c3 = 18.6875;

        double x = Math.Clamp(nits / 10_000.0, 0.0, 1.0);

        x = Math.Pow(x, m1);
        x = (c1 + c2 * x) / (1.0 + c3 * x);

        return Math.Pow(x, m2);
    }
    static double SmoothStep(double edge0, double edge1, double x)
    {
        x = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return x * x * (3.0 - 2.0 * x);
    }

    static ToneSplineParams GetSplineParams(
        double sourceMinNits,
        double sourcePeakNits,
        double sourceAvgNits,
        double targetMinNits,
        double targetPeakNits,
        double contrast = 0.5)
    {
        const double kneeAdaptation = 0.4;
        const double kneeMinimum    = 0.1;
        const double kneeMaximum    = 0.8;
        const double kneeDefault    = 0.4;

        const double slopeTuning = 1.5;
        const double slopeOffset = 0.2;

        double srcMin = NitsToPQ(sourceMinNits);
        double srcMax = NitsToPQ(sourcePeakNits);

        double dstMin = NitsToPQ(targetMinNits);
        double dstMax = NitsToPQ(targetPeakNits);

        // ---- Choose source pivot ---------------------------------------

        double srcKneeMin =
            srcMin + (srcMax - srcMin) * kneeMinimum;

        double srcKneeMax =
            srcMin + (srcMax - srcMin) * kneeMaximum;

        double srcPivot;

        if (sourceAvgNits > 0)
        {
            srcPivot = NitsToPQ(sourceAvgNits);
        }
        else
        {
            srcPivot =
                srcMin + (srcMax - srcMin) * kneeDefault;
        }

        srcPivot = Math.Clamp(srcPivot, srcKneeMin, srcKneeMax);

        // ---- Choose destination pivot --------------------------------

        double target =
            (srcPivot - srcMin) / (srcMax - srcMin);

        double adapted =
            dstMin + (dstMax - dstMin) * target;

        double dstKneeMin =
            dstMin + (dstMax - dstMin) * kneeMinimum;

        double dstKneeMax =
            dstMin + (dstMax - dstMin) * kneeMaximum;

        double tuning =
            1.0 -
            SmoothStep(kneeMaximum, kneeDefault, target) *
            SmoothStep(kneeMinimum, kneeDefault, target);

        double adaptation =
            kneeAdaptation +
            (1.0 - kneeAdaptation) * tuning;

        double dstPivot =
            srcPivot + (adapted - srcPivot) * adaptation;

        dstPivot = Math.Clamp(dstPivot, dstKneeMin, dstKneeMax);

        // ---- Slope at pivot ------------------------------------------

        double slope =
            (dstPivot - dstMin) /
            (srcPivot - srcMin);

        double ratio =
            srcMax / dstMax - 1.0;

        ratio = Math.Clamp(
            slopeTuning * ratio,
            slopeOffset,
            1.0 + slopeOffset);

        slope = Math.Pow(
            slope,
            (1.0 - contrast) * ratio);

        // ---- Polynomial coefficients --------------------------------

        double inMin  = srcMin - srcPivot;
        double inMax  = srcMax - srcPivot;

        double outMin = dstMin - dstPivot;
        double outMax = dstMax - dstPivot;

        // Lower side: quadratic
        double pa =
            (outMin - slope * inMin) /
            (inMin * inMin);

        // Upper side: cubic
        double t = 2.0 * inMax * inMax;

        double qa =
            (slope * inMax - outMax) /
            (inMax * t);

        double qb =
            -3.0 * (slope * inMax - outMax) / t;

        return new()
        {
            SrcPivot = (float)srcPivot,
            DstPivot = (float)dstPivot,
            Pa       = (float)pa,
            Slope    = (float)slope,
            Qa       = (float)qa,
            Qb       = (float)qb,
        };
    }

    /* TODO: HDR10Plus during FLRender (Spline will not work as-is, it flashes frame by frame)
        if (scfg.HDRFormat == HDRFormat.HDRPlus)
        {
            var hdrPlusSide = av_frame_get_side_data(frame.AVFrame, AVFrameSideDataType.DynamicHdrPlus);
            if (hdrPlusSide != null)
            {
                var hdrPlus = (AVDynamicHDRPlus*) hdrPlusSide->data;
                if (hdrPlus != null && hdrPlus->num_windows != 0 && hdrPlus->application_version <= 1)
                {
                    if (!MediaStream.VideoStream.GetHdr10PlusPeak(hdrPlus, out var peak, out var avg))
                        Log.Error("========================================================");

                    if (peak != scfg.sourcePeakNits || avg != scfg.sourceAvgNits)
                    {
                        Log.Debug($"{peak} ({avg})");
                        scfg.sourcePeakNits = peak;
                        scfg.sourceAvgNits  = avg;
                        FLSetHDRtoSDR();
                        context.UpdateSubresource(psData, psBuffer);
                        vpRequests &= ~VPRequestType.UpdatePS;
                    }
                }
            }
        }
        */
    #endregion
}

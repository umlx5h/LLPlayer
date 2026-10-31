using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using static FlyleafLib.Utils.NativeMethods;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    static string[] pixelOffsets = ["r", "g", "b", "a"];

    const string dYUVLimited    = "dYUVLimited";
    const string dYUVFull       = "dYUVFull";
    const string dYUV16         = "dYUV16";

    const string dBT1886ToLinear= "dBT1886ToLinear";
    const string dBT2020        = "dBT2020";
    const string dHLG           = "dHLG";
    const string dPQSpline      = "dPQSpline";
    const string dDovi          = "dDovi";

    const string dFilters       = "dFilters";
    const string dPano360       = "dPano360";
    const string dICC           = "dICC";

    List<string> defines = [];

    static ReadOnlySpan<char> HWSAMPLE => @"
color = float4(
    Texture1.Sample(Sampler, input.Texture).r,
    Texture2.Sample(Sampler, input.Texture).rg,
    1.0f);
";
    Texture2DDescription[]          txtDesc     = new Texture2DDescription[4];              // SW Textures (TODO: Array)
    ShaderResourceViewDescription[] srvDesc     = new ShaderResourceViewDescription[4];     // SW / HW SRV Desc
    SubresourceData[]               subData     = new SubresourceData[1];                   // SW -> HW DataPointer / RowPitch

    PSCase  psCase;
    string  psId, psIdPrev;
    nint    iccSrc;
    bool    isPQSpline;
    bool    isDovi;

    bool    UsesDisplayMapping => defines.Contains(dBT2020);
    bool    UsesNits           => isPQSpline || defines.Contains(dHLG);
    bool    CanHDRDisplay      => VideoProcessor == VideoProcessors.Flyleaf && ucfg.AllowHDRSwapchain && displayHDREnabled && UsesNits;

    bool FLSwsConfig(AVFrame* frame)
    {
        psCase  = PSCase.None;
        psId    = "";
        defines = [];

        if (ucfg.Pano360._enabled)
        {
            psId += "P";
            defines.Add(dPano360);
        }

        if (ucfg.hasFLFilters) // TODO: fix vp switch when set filters or unset*
        {
            psId += "-";
            defines.Add(dFilters);
        }

        isDovi = canFL && VideoProcessor != VideoProcessors.SwsScale && FLDoviSupported();
        bool iccApplied = false;

        if (!isDovi && scfg.iccData != null && iccDst != 0 && (iccSrc = OpenColorProfile(scfg.iccData)) != 0)
        {
            var iccTransform = CreateTransform(iccSrc, iccDst);
            if (iccTransform != 0)
            {
                var iccLut = CreateIccLut(iccTransform);
                if (iccLut.Length > 0)
                {
                    try
                    {
                        psId += "i";
                        defines.Add(dICC);
                        fixed (ushort* ptr = iccLut)
                            context.UpdateSubresource(iccTxt, 0, null, (nint)ptr, 33 * 33 * 4 * sizeof(ushort), 0);
                        iccApplied = true;
                        scfg.HDRFormat = HDRFormat.None; // should keep this local
                    } catch { }
                }
                DeleteColorTransform(iccTransform);
            }
            CloseColorProfile(iccSrc);
        }

        if (isDovi)
        {
            psId += "v";
            defines.Add(dBT2020);
            defines.Add(dDovi);
            defines.Add(dPQSpline);
            isPQSpline = true;
            FLHDRDetectReset();
            FLDoviReset();
            if (CanTrace)
                Log.Trace(FLDoviDump(frame));
        }
        else if (scfg.ColorSpace == ColorSpace.Bt2020 && !iccApplied)
        {
            defines.Add(dBT2020);

            if (scfg.HDRFormat == HDRFormat.None)
            {
                psId += "b";
                defines.Add(dBT1886ToLinear);
            }
            else if (scfg.HDRFormat == HDRFormat.HLG)
            {
                psId += "g";
                defines.Add(dHLG);
            }
            else // HDR10 / HDR10+
            {
                psId += "p";
                defines.Add(dPQSpline);
                isPQSpline = true;
                FLHDRDetectReset();
            }
        }

        if (checkHDRConfig && UsesDisplayMapping)
        {
            checkHDRConfig = false;
            FLUpdateTargetNits();
        }

        if (canFL && VideoProcessor != VideoProcessors.SwsScale)
        {
            if (VideoDecoder.VideoAccelerated) // VP can be D3D11 but never with VA*?
                FLHWConfig();
            else
            {
                FLSWConfig();

                if (psCase == PSCase.None)
                {
                    // TBR: Fallback (recursion for psId/defines mainly*?) | TBR: if enabled (maybe only for Y210? old GPUs?)
                    // if (txtDesc[0].Format != Format.Unknown && !device.CheckFormatSupport(txtDesc[0].Format).HasFlag(FormatSupport.Texture2D)) \ Log.Warn($"GPU does not support {txtDesc[0].Format} texture format");
                    if (VideoProcessor != VideoProcessors.D3D11)
                    {   // Might use D3SWS | D3FL separate video processors*
                        VideoProcessor = VideoProcessors.SwsScale;
                        RaiseUI(nameof(VideoProcessor));
                        Log.Warn($"Preparing planes for {scfg.PixelFormatStr} with {VideoProcessor} (SwsScale fallback)");
                    }

                    canFL = false;
                    return FLSwsConfig(frame);
                }
            }
        }
        else
            SwsConfig(frame);

        return true;
    }
    bool FLHWConfig()
    {
        FillPlanes = FLHWFillPlanes;

        psCase = PSCase.HW;
        psId  += "1";

        if (scfg.ColorRange == ColorRange.Limited)
            defines.Add(dYUVLimited);
        else
        {
            psId += "f";
            defines.Add(dYUVFull);
        }

        if (scfg.ColorSpace == ColorSpace.Bt709)
            psData.CoeffsIndex = 1;
        else if (scfg.ColorSpace == ColorSpace.Bt2020)
            psData.CoeffsIndex = 0;
        else
            psData.CoeffsIndex = 2;

        if (scfg.PixelComp0Depth > 8)
        {
            psId += "x";
            defines.Add(dYUV16);
            srvDesc[0].Format = Format.R16_UNorm;
            srvDesc[1].Format = Format.R16G16_UNorm;
        }
        else
        {
            srvDesc[0].Format = Format.R8_UNorm;
            srvDesc[1].Format = Format.R8G8_UNorm;
        }

        srvDesc[0].ViewDimension = srvDesc[1].ViewDimension = ShaderResourceViewDimension.Texture2DArray;

        switch (ucfg._SplitFrameAlphaPosition)
        {
            case SplitFrameAlphaPosition.None:
                SetPS(HWSAMPLE, defines);
                break;

            case SplitFrameAlphaPosition.Left:
                psId += "l";
                SetPS(@"
color.rgb = float3(
Texture1.Sample(Sampler, float2(0.5 + (input.Texture.x / 2), input.Texture.y)).r,
Texture2.Sample(Sampler, float2(0.5 + (input.Texture.x / 2), input.Texture.y)).rg);" +
SampleSplitFrameAlpha("input.Texture.x / 2", "input.Texture.y"), defines);
                break;

                case SplitFrameAlphaPosition.Right:
                psId += "r";
                SetPS(@"
color.rgb = float3(
Texture1.Sample(Sampler, float2(input.Texture.x / 2, input.Texture.y)).r,
Texture2.Sample(Sampler, float2(input.Texture.x / 2, input.Texture.y)).rg);" +
SampleSplitFrameAlpha("0.5 + (input.Texture.x / 2)", "input.Texture.y"), defines);
                break;

                case SplitFrameAlphaPosition.Top:
                psId += "t";
                SetPS(@"
color.rgb = float3(
Texture1.Sample(Sampler, float2(input.Texture.x, 0.5 + (input.Texture.y / 2))).r,
Texture2.Sample(Sampler, float2(input.Texture.x, 0.5 + (input.Texture.y / 2))).rg);" +
SampleSplitFrameAlpha("input.Texture.x", "input.Texture.y / 2"), defines);
                break;

                case SplitFrameAlphaPosition.Bottom:
                psId += "b";
                SetPS(@"
color.rgb = float3(
Texture1.Sample(Sampler, float2(input.Texture.x, input.Texture.y / 2)).r,
Texture2.Sample(Sampler, float2(input.Texture.x, input.Texture.y / 2)).rg);" +
SampleSplitFrameAlpha("input.Texture.x", "0.5 + (input.Texture.y / 2)"), defines);
                break;
        }

        return true;
    }
    bool FLSWConfig()
    {
        bool ret;

        if (scfg.ColorType == ColorType.YUV)
            ret = FLSWYUVConfig();
        else if (scfg.ColorType == ColorType.RGB)
            ret = FLSWRGBConfig();
        else
            ret = FLSWGrayConfig();

        FillPlanes = scfg.VFlip ? FLSWFillPlanesFlip : FLSWFillPlanes;

        for (int i = 0; i < scfg.PixelPlanes; i++)
            srvDesc[i].ViewDimension = ShaderResourceViewDimension.Texture2D;

        return ret;
    }
    bool FLSWYUVConfig()
    {
        if (scfg.ColorRange == ColorRange.Limited)
            defines.Add(dYUVLimited);
        else
        {
            psId += "f";
            defines.Add(dYUVFull);
        }

        if (scfg.ColorSpace == ColorSpace.Bt709)
            psData.CoeffsIndex = 1;
        else if (scfg.ColorSpace == ColorSpace.Bt2020)
            psData.CoeffsIndex = 0;
        else
            psData.CoeffsIndex = 2;

        if (scfg.PixelPlanes == 1 && ( // No Alpha
            scfg.PixelFormat == AVPixelFormat.Y210le  || // Not tested
            scfg.PixelFormat == AVPixelFormat.Yuyv422 ||
            scfg.PixelFormat == AVPixelFormat.Yvyu422 ||
            scfg.PixelFormat == AVPixelFormat.Uyvy422 ))
        {
            psCase  = PSCase.YUVPacked;
            psId   += ((int)psCase).ToString();

            psData.UVOffset = 1.0f / (scfg.txtWidth >> 1);
            txtDesc[0].Width   = scfg.txtWidth;
            txtDesc[0].Height  = scfg.txtHeight;

            if (scfg.PixelComp0Depth > 8)
            {
                psId += "x";
                txtDesc[0].Format   = Format.Y210;
                srvDesc[0].Format   = Format.R16G16B16A16_UNorm;
            }
            else
            {
                txtDesc[0].Format   = Format.YUY2;
                srvDesc[0].Format   = Format.R8G8B8A8_UNorm;
            }

            string header = @"
float  posx = input.Texture.x - (Config.uvOffset * 0.25);
float  fx = frac(posx / Config.uvOffset);
float  pos1 = posx + ((0.5 - fx) * Config.uvOffset);
float  pos2 = posx + ((1.5 - fx) * Config.uvOffset);

float4 c1 = Texture1.Sample(Sampler, float2(pos1, input.Texture.y));
float4 c2 = Texture1.Sample(Sampler, float2(pos2, input.Texture.y));
";
            if (scfg.PixelFormat == AVPixelFormat.Yuyv422 ||
                scfg.PixelFormat == AVPixelFormat.Y210le)
            {
                psId += "a";

                SetPS(header + @"
float  leftY    = lerp(c1.r, c1.b, fx * 2);
float  rightY   = lerp(c1.b, c2.r, fx * 2 - 1);
float2 outUV    = lerp(c1.ga, c2.ga, fx);
float  outY     = lerp(leftY, rightY, step(0.5, fx));
color = float4(outY, outUV, 1.0f);
", defines);
            }
            else if (scfg.PixelFormat == AVPixelFormat.Yvyu422)
            {
                psId += "b";

                SetPS(header + @"
float  leftY    = lerp(c1.r, c1.b, fx * 2);
float  rightY   = lerp(c1.b, c2.r, fx * 2 - 1);
float2 outUV    = lerp(c1.ag, c2.ag, fx);
float  outY     = lerp(leftY, rightY, step(0.5, fx));
color = float4(outY, outUV, 1.0f);
", defines);
            }
            else if (scfg.PixelFormat == AVPixelFormat.Uyvy422)
            {
                psId += "c";

                SetPS(header + @"
float  leftY    = lerp(c1.g, c1.a, fx * 2);
float  rightY   = lerp(c1.a, c2.g, fx * 2 - 1);
float2 outUV    = lerp(c1.rb, c2.rb, fx);
float  outY     = lerp(leftY, rightY, step(0.5, fx));
color = float4(outY, outUV, 1.0f);
", defines);
            }
        }

        // Y_UV | nv12,nv21,nv24,nv42,p010le,p016le,p410le,p416le | (log2_chroma_w != log2_chroma_h / Interleaved) (? nv16,nv20le,p210le,p216le)
        // This covers all planes == 2 YUV (Semi-Planar)
        else if (scfg.PixelPlanes == 2) // No Alpha
        {
            psCase  = PSCase.YUVSemiPlanar;
            psId   += ((int)psCase).ToString();

            txtDesc[0].Width    = scfg.txtWidth;
            txtDesc[0].Height   = scfg.txtHeight;
            txtDesc[1].Width    = scfg.txtWidth  >> scfg.PixelFormatDesc->log2_chroma_w;
            txtDesc[1].Height   = scfg.txtHeight >> scfg.PixelFormatDesc->log2_chroma_h;

            string offsets = scfg.PixelComps[1].offset > scfg.PixelComps[2].offset ? "gr" : "rg";
            psId += offsets;

            if (scfg.PixelComp0Depth > 8)
            {
                psId += "x";
                defines.Add(dYUV16);
                txtDesc[0].Format = srvDesc[0].Format = Format.R16_UNorm;
                txtDesc[1].Format = srvDesc[1].Format = Format.R16G16_UNorm;
            }
            else
            {
                txtDesc[0].Format = srvDesc[0].Format = Format.R8_UNorm;
                txtDesc[1].Format = srvDesc[1].Format = Format.R8G8_UNorm;
            }

            switch (ucfg._SplitFrameAlphaPosition)
            {
                case SplitFrameAlphaPosition.None:
                    SetPS(@"
color = float4(
Texture1.Sample(Sampler, input.Texture).r,
Texture2.Sample(Sampler, input.Texture)." + offsets + @",
1.0);
", defines);
                    break;
                case SplitFrameAlphaPosition.Left:
                    psId += "l";
                    SetPS(@"
color.rgb = float3(
Texture1.Sample(Sampler, float2(0.5 + (input.Texture.x / 2), input.Texture.y)).r,
Texture2.Sample(Sampler, float2(0.5 + (input.Texture.x / 2), input.Texture.y))." + offsets +
SampleSplitFrameAlpha("input.Texture.x / 2", "input.Texture.y"), defines);
                    break;
                case SplitFrameAlphaPosition.Right:
                    psId += "r";
                    SetPS(@"
color.rgb = float3(
Texture1.Sample(Sampler, float2(input.Texture.x / 2, input.Texture.y)).r,
Texture2.Sample(Sampler, float2(input.Texture.x / 2, input.Texture.y))." + offsets +
SampleSplitFrameAlpha("0.5 + (input.Texture.x / 2)", "input.Texture.y"), defines);
                    break;
                case SplitFrameAlphaPosition.Top:
                    psId += "t";
                    SetPS(@"
color.rgb = float3(
Texture1.Sample(Sampler, float2(input.Texture.x, 0.5 + (input.Texture.y / 2))).r,
Texture2.Sample(Sampler, float2(input.Texture.x, 0.5 + (input.Texture.y / 2)))." + offsets +
SampleSplitFrameAlpha("input.Texture.x", "input.Texture.y / 2"), defines);
                    break;
                case SplitFrameAlphaPosition.Bottom:
                    psId += "b";
                    SetPS(@"
color.rgb = float3(
Texture1.Sample(Sampler, float2(input.Texture.x, input.Texture.y / 2)).r,
Texture2.Sample(Sampler, float2(input.Texture.x, input.Texture.y / 2))." + offsets +
SampleSplitFrameAlpha("input.Texture.x", "0.5 + (input.Texture.y / 2)"), defines);
                    break;
            }

        }

        // Y_U_V
        else if (scfg.PixelPlanes > 2) // Possible Alpha
        {
            psCase  = PSCase.YUVPlanar;
            psId   += ((int)psCase).ToString();

            txtDesc[0].Width    = txtDesc[3].Width = scfg.txtWidth;
            txtDesc[0].Height   = txtDesc[3].Height= scfg.txtHeight;
            txtDesc[1].Width    = txtDesc[2].Width = scfg.txtWidth  >> scfg.PixelFormatDesc->log2_chroma_w;
            txtDesc[1].Height   = txtDesc[2].Height= scfg.txtHeight >> scfg.PixelFormatDesc->log2_chroma_h;

            string shader = @"
color.r = Texture1.Sample(Sampler, input.Texture).r;
color.g = Texture2.Sample(Sampler, input.Texture).r;
color.b = Texture3.Sample(Sampler, input.Texture).r;
";
            // TODO: eg. Gamma28 => color.r = pow(color.r, 2.8); and then it needs back after yuv->rgb with c = pow(c, 1.0 / 2.8);

            if (scfg.PixelPlanes == 4)
            {
                psId += "x";

                shader += @"
color.a = Texture4.Sample(Sampler, input.Texture).r;
";
            }

            Format  curFormat = Format.R8_UNorm;
            int     maxBits   = 8;
            if (scfg.PixelComp0Depth > 8)
            {
                psId += "a";
                defines.Add(dYUV16);
                curFormat = Format.R16_UNorm;
                maxBits = 16;
            }

            for (int i = 0; i < scfg.PixelPlanes; i++)
                txtDesc[i].Format = srvDesc[i].Format = curFormat;

            // TBR: This is an estimation from N-bits to eg 16-bits (should include alpha!?)
            if (maxBits - scfg.PixelComp0Depth != 0)
            {
                psId += scfg.PixelComp0Depth;
                shader += @"
color *= pow(2, " + (maxBits - scfg.PixelComp0Depth) + @");
";
            }

            if (scfg.PixelPlanes < 4)
                switch (ucfg._SplitFrameAlphaPosition)
                {
                    case SplitFrameAlphaPosition.None:
                        shader += @"
color.a = 1.0f;
";
                        break;
                    case SplitFrameAlphaPosition.Left:
                        psId += "l";
                        shader = @"
float2 uv = float2(0.5 + (input.Texture.x / 2), input.Texture.y);
color.r = Texture1.Sample(Sampler, uv).r;
color.g = Texture2.Sample(Sampler, uv).r;
color.b = Texture3.Sample(Sampler, uv).r;" +
SampleSplitFrameAlpha("input.Texture.x / 2", "input.Texture.y");

                        break;
                    case SplitFrameAlphaPosition.Right:
                        psId += "r";
                        shader = @"
float2 uv = float2(input.Texture.x / 2, input.Texture.y);
color.r = Texture1.Sample(Sampler, uv).r;
color.g = Texture2.Sample(Sampler, uv).r;
color.b = Texture3.Sample(Sampler, uv).r;" +
SampleSplitFrameAlpha("0.5 + (input.Texture.x / 2)", "input.Texture.y");

                        break;
                    case SplitFrameAlphaPosition.Top:
                        psId += "t";
                        shader = @"
float2 uv = float2(input.Texture.x, 0.5 + (input.Texture.y / 2));
color.r = Texture1.Sample(Sampler, uv).r;
color.g = Texture2.Sample(Sampler, uv).r;
color.b = Texture3.Sample(Sampler, uv).r;" +
SampleSplitFrameAlpha("input.Texture.x", "input.Texture.y / 2");

                        break;
                    case SplitFrameAlphaPosition.Bottom:
                        psId += "b";
                        shader = @"
float2 uv = float2(input.Texture.x, input.Texture.y / 2);
color.r = Texture1.Sample(Sampler, uv).r;
color.g = Texture2.Sample(Sampler, uv).r;
color.b = Texture3.Sample(Sampler, uv).r;" +
SampleSplitFrameAlpha("input.Texture.x", "0.5 + (input.Texture.y / 2)");

                        break;
                }

            SetPS(shader, defines);
        }

        return true;
    }
    bool FLSWRGBConfig()
    {
        // [RGB0]32 | [RGBA]32 | [RGBA]64
        if (scfg.PixelPlanes == 1 && ( // Possible Alpha
            scfg.PixelFormat == AVPixelFormat._0RGB  ||
            scfg.PixelFormat == AVPixelFormat.Rgb0   ||
            scfg.PixelFormat == AVPixelFormat._0BGR  ||
            scfg.PixelFormat == AVPixelFormat.Bgr0   ||

            scfg.PixelFormat == AVPixelFormat.Argb   ||
            scfg.PixelFormat == AVPixelFormat.Rgba   ||
            scfg.PixelFormat == AVPixelFormat.Abgr   ||
            scfg.PixelFormat == AVPixelFormat.Bgra   ||

            scfg.PixelFormat == AVPixelFormat.Rgba64le||
            scfg.PixelFormat == AVPixelFormat.Bgra64le))
        {
            psCase  = PSCase.RGBPacked;
            psId   += ((int)psCase).ToString();

            txtDesc[0].Width   = scfg.txtWidth;
            txtDesc[0].Height  = scfg.txtHeight;

            if (scfg.PixelComp0Depth > 8)
            {
                psId += "1";
                txtDesc[0].Format = srvDesc[0].Format = Format.R16G16B16A16_UNorm;
            }
            else
                txtDesc[0].Format = srvDesc[0].Format = Format.R8G8B8A8_UNorm; // B8G8R8X8_UNorm for 0[rgb]?

            string offsets = "";
            for (int i = 0; i < scfg.PixelComps.Length; i++)
                offsets += pixelOffsets[(int) (scfg.PixelComps[i].offset / Math.Ceiling(scfg.PixelComp0Depth / 8.0))];

            // TBR: [RGB0]32 has no alpha remove it
            if (scfg.PixelFormatStr[0] == '0')
                offsets = offsets[1..];
            else if (scfg.PixelFormatStr[^1] == '0')
                offsets = offsets[..^1];

            psId += offsets;

            string shader;
            if (scfg.PixelComps.Length > 3 && offsets.Length > 3)
                shader = @$"
color = Texture1.Sample(Sampler, input.Texture).{offsets};
";
            else
                shader = @$"
color = float4(Texture1.Sample(Sampler, input.Texture).{offsets}, 1.0f);
";
            // TODO: Should transfer it to pixel shader
            if (scfg.ColorRange == ColorRange.Limited)
            {   // RGBLimitedToFull
                psId += "k";
                shader += @"
color.rgb = (color.rgb - rgbOffset) * rgbScale;
";
            }

            SetPS(shader, defines);
        }

        // [BGR/RGB]16
        else if (scfg.PixelPlanes == 1 && (
            scfg.PixelFormat == AVPixelFormat.Rgb444le||
            scfg.PixelFormat == AVPixelFormat.Bgr444le))
        {
            psCase  = PSCase.RGBPacked2;
            psId   += ((int)psCase).ToString();

            txtDesc[0].Width    = scfg.txtWidth;
            txtDesc[0].Height   = scfg.txtHeight;
            txtDesc[0].Format   = srvDesc[0].Format = Format.B4G4R4A4_UNorm;

            string shader;
            if (scfg.PixelFormat == AVPixelFormat.Rgb444le)
            {
                psId += "a";
                shader = @"
color = float4(Texture1.Sample(Sampler, input.Texture).rgb, 1.0f);
";
            }
            else
                shader = @"
color = float4(Texture1.Sample(Sampler, input.Texture).bgr, 1.0f);
";
            // TODO: Should transfer it to pixel shader
            if (scfg.ColorRange == ColorRange.Limited)
            {   // RGBLimitedToFull
                psId += "k";
                shader += @"
color.rgb = (color.rgb - rgbOffset) * rgbScale;
";
            }

            SetPS(shader, defines);
        }

        // GBR(A)
        else if (scfg.PixelPlanes > 2) // Possible Alpha | TBR: Usually transfer func 'Linear' for > 8-bit which requires pow (*?)
        {
            psCase  = PSCase.RGBPlanar;
            psId   += ((int)psCase).ToString();

            for (int i = 0; i < scfg.PixelPlanes; i++)
            {
                txtDesc[i].Width    = scfg.txtWidth;
                txtDesc[i].Height   = scfg.txtHeight;
            }

            string shader = @"
color.g = Texture1.Sample(Sampler, input.Texture).r;
color.b = Texture2.Sample(Sampler, input.Texture).r;
color.r = Texture3.Sample(Sampler, input.Texture).r;
";
            if (scfg.PixelPlanes == 4)
            {
                psId += "x";

                shader += @"
color.a = Texture4.Sample(Sampler, input.Texture).r;
";
            }

            /* TODO:
                * Using pow for scale/normalize is not accurate (when maxBits != scfg.PixelComp0Depth)
                * Mainly affects gbrp10 (should prefer Texture2D<float> for more accurate and better performance)
                */

            Format  curFormat = Format.R8_UNorm;
            int     maxBits   = 8;
            if (scfg.PixelComp0Depth > 16)
            {
                psId += "a";
                curFormat   = Format.R32_Float;
                maxBits     = 32;
            }
            else if (scfg.PixelComp0Depth > 8)
            {
                psId += "b";
                curFormat   = Format.R16_UNorm;
                maxBits     = 16;
            }

            for (int i = 0; i < scfg.PixelPlanes; i++)
                txtDesc[i].Format = srvDesc[i].Format = curFormat;

            if (maxBits - scfg.PixelComp0Depth != 0)
            {
                psId += scfg.PixelComp0Depth;
                shader += @"
color *= pow(2, " + (maxBits - scfg.PixelComp0Depth) + @");
";
            }

            // TODO: Should transfer it to pixel shader
            if (scfg.ColorRange == ColorRange.Limited)
            {   // RGBLimitedToFull
                psId += "k";
                shader += @"
color.rgb = (color.rgb - rgbOffset) * rgbScale;
";
            }

            if (scfg.PixelPlanes < 4)
                shader += @"
color.a = 1.0f;
";
            SetPS(shader, defines);
        }

        return true;
    }
    bool FLSWGrayConfig()
    {
        // Gray (Single Plane)
        psCase  = PSCase.Gray;
        psId   += ((int)psCase).ToString();

        txtDesc[0].Width    = scfg.txtWidth;
        txtDesc[0].Height   = scfg.txtHeight;

        string shader = @"
color = float4(Texture1.Sample(Sampler, input.Texture).r, Texture1.Sample(Sampler, input.Texture).r, Texture1.Sample(Sampler, input.Texture).r, 1.0f);
";
        int maxBits = 8;
        if (scfg.PixelComp0Depth > 8)
        {
            psId += "x";
            maxBits = 16;
            txtDesc[0].Format = srvDesc[0].Format = Format.R16_UNorm;
        }
        else
            txtDesc[0].Format = srvDesc[0].Format = Format.R8_UNorm;

        if (maxBits - scfg.PixelComp0Depth != 0)
        {
            psId += scfg.PixelComp0Depth;
            shader += @"
color.rgb *= pow(2, " + (maxBits - scfg.PixelComp0Depth) + @");
";
        }

        // TODO: Should transfer it to pixel shader
        if (scfg.ColorRange == ColorRange.Limited)
        {   // RGBLimitedToFull
            psId += "k";
            shader += @"
color.rgb = (color.rgb - rgbOffset) * rgbScale;
";
        }

        SetPS(shader, defines);

        return true;
    }

    static string SampleSplitFrameAlpha(string x, string y) => $@"
#if defined(dYUVLimited)
color.a = YUVToRGBLimited(float3(Texture1.Sample(Sampler, float2({x}, {y})).r, float2(0.5, 0.5))).r;
#else
color.a = YUVToRGBFull(float3(Texture1.Sample(Sampler, float2({x}, {y})).r, float2(0.5, 0.5))).r;
#endif
";

    VideoFrame FLHWFillPlanes(ref AVFrame* frame)
    {
        if (frame->data[0] != ffTexture.NativePointer)
        {
            Log.Error($"[V] Frame Dropped (Invalid HW Texture Pointer)");
            av_frame_unref(frame);
            return null;
        }

        srvDesc[0].Texture2DArray.FirstArraySlice = srvDesc[1].Texture2DArray.FirstArraySlice = (uint)frame->data[1];

        VideoFrame mFrame = new()
        {
            AVFrame     = frame,
            Timestamp   = (long)(frame->pts * scfg.Timebase) - VideoDecoder.Demuxer.StartTime,
            SRV         = [
                device.CreateShaderResourceView(ffTexture, srvDesc[0]),
                device.CreateShaderResourceView(ffTexture, srvDesc[1])],
        };

        if (isDovi)
            mFrame.Dovi = FLDoviPrepare(frame);

        frame = av_frame_alloc();
        return mFrame;
    }
    VideoFrame FLSWFillPlanes(ref AVFrame* frame)
    {
        VideoFrame mFrame = new()
        {
            Timestamp   = (long)(frame->pts * scfg.Timebase) - VideoDecoder.Demuxer.StartTime,
            Texture     = new ID3D11Texture2D            [scfg.PixelPlanes],
            SRV         = new ID3D11ShaderResourceView   [scfg.PixelPlanes]
        };

        if (isDovi)
            mFrame.Dovi = FLDoviPrepare(frame);

        for (int i = 0; i < scfg.PixelPlanes; i++)
        {
            subData[0].RowPitch     = (uint)frame->linesize[i];
            subData[0].DataPointer  = frame->data[i];

            if (subData[0].RowPitch < txtDesc[i].Width)
            {   // Prevent reading more than the actual data (Access Violation #424)
                av_frame_unref(frame);
                mFrame.Dispose();
                return null;
            }

            mFrame.Texture[i]  = device.CreateTexture2D         (txtDesc[i],        subData);
            mFrame.SRV[i]      = device.CreateShaderResourceView(mFrame.Texture[i], srvDesc[i]);
        }

        av_frame_unref(frame);
        return mFrame;
    }
    VideoFrame FLSWFillPlanesFlip(ref AVFrame* frame)
    {   // Negative linesize needs vertical flipping | [Bottom -> Top] data[i] points to last row and we need to move at first (height - 1) rows
        VideoFrame mFrame = new()
        {
            Timestamp   = (long)(frame->pts * scfg.Timebase) - VideoDecoder.Demuxer.StartTime,
            Texture     = new ID3D11Texture2D            [scfg.PixelPlanes],
            SRV         = new ID3D11ShaderResourceView   [scfg.PixelPlanes]
        };

        if (isDovi)
            mFrame.Dovi = FLDoviPrepare(frame);

        for (int i = 0; i < scfg.PixelPlanes; i++)
        {
            int stride = frame->linesize[i];

            subData[0].RowPitch     = (uint)Math.Abs(stride);
            subData[0].DataPointer  = stride < 0 ? frame->data[i] + stride * ((int)txtDesc[i].Height - 1) : frame->data[i];

            if (subData[0].RowPitch < txtDesc[i].Width)
            {   // Prevent reading more than the actual data (Access Violation #424)
                av_frame_unref(frame);
                mFrame.Dispose();
                return null;
            }

            mFrame.Texture[i]  = device.CreateTexture2D         (txtDesc[i],        subData);
            mFrame.SRV[i]      = device.CreateShaderResourceView(mFrame.Texture[i], srvDesc[i]);
        }

        av_frame_unref(frame);
        return mFrame;
    }

    void SetPS(ReadOnlySpan<char> sampleHLSL, List<string> defines = null)
    {
        // Already set with PSSetShader
        if (VideoProcessor == VideoProcessors.D3D11 || psId == psIdPrev)
            return;

        // Check local cache (TBR: might up limit?)
        if (!psShader.TryGetValue(psId, out var shader))
        {   // Check global/static cache for Blob
            shader = ShaderCompiler.CompilePS(device, psId, sampleHLSL, defines);
            psShader[psId] = shader;
        }

        // Save CurShader so we can set it back again if we switch temporary?*
        context.PSSetShader(shader);
        psIdPrev = psId;

        FLHDRSetPS(sampleHLSL, defines);
    }
}

enum PSCase : int
{
    None,
    HW,
    HWD3,
    SWD3,
        
    Gray,
    RGBPacked,
    RGBPacked2,
    RGBPlanar,

    YUVPacked,
    YUVSemiPlanar,
    YUVPlanar,
    SwsScale
}

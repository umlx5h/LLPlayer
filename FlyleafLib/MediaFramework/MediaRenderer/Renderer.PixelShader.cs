using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaStream;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace FlyleafLib.MediaFramework.MediaRenderer;

unsafe public partial class Renderer
{
    static string[] pixelOffsets = ["r", "g", "b", "a"];

    const string dYUVLimited    = "dYUVLimited";
    const string dYUVFull       = "dYUVFull";
    const string dBT2020        = "dBT2020";
    const string dPQToLinear    = "dPQToLinear";
    const string dHLGToLinear   = "dHLGToLinear";
    const string dTone          = "dTone";
    const string dFilters       = "dFilters";

    enum PSCase : int
    {
        None,
        HW,
        HWD3D11VP,
        
        Gray,
        RGBPacked,
        RGBPacked2,
        RGBPlanar,

        YUVPacked,
        YUVSemiPlanar,
        YUVPlanar,
        SwsScale
    }

    PSCase  curPSCase;
    string  curPSUniqueId;
    string  prevPSUniqueId;
    internal bool forceNotExtractor; // TBR: workaround until we separate the Extractor?

    Texture2DDescription[]          textDesc= new Texture2DDescription[4];
    ShaderResourceViewDescription[] srvDesc = new ShaderResourceViewDescription[4];
    SubresourceData[]               subData = new SubresourceData[1];

    void InitPS()
    {
        for (int i=0; i<textDesc.Length; i++)
        {
            textDesc[i].Usage               = ResourceUsage.Default;
            textDesc[i].BindFlags           = BindFlags.ShaderResource;// | BindFlags.RenderTarget;
            textDesc[i].SampleDescription   = new(1, 0);
            textDesc[i].ArraySize           = 1;
            textDesc[i].MipLevels           = 1;
        }

        for (int i=0; i<textDesc.Length; i++)
        {
            srvDesc[i].Texture2D        = new() { MipLevels = 1, MostDetailedMip = 0 };
            srvDesc[i].Texture2DArray   = new() { MipLevels = 1, ArraySize = 1 };
        }
    }

    internal bool ConfigPlanes(AVFrame* frame = null)
    {
        bool error = false;

        try
        {
            Monitor.Enter(VideoDecoder.lockCodecCtx);
            Monitor.Enter(lockDevice);

            // Don't use SCDisposed as we need to allow config planes even before swapchain creation
            // TBR: Possible run ConfigPlanes after swapchain creation instead (currently we don't access any resources of the swapchain here and is safe)
            if (Disposed)
                return false;

            if (frame != null) // Called from Stream / Codec (requires full reset)
            {
                VideoDecoder.DisposeFrame(LastFrame); LastFrame = null; // We could still keep it if same config? same processor?

                VideoStream = VideoDecoder.VideoStream;
                if (VideoStream == null)
                    return false;

                if (VideoDecoder.VideoAccelerated)
                {
                    var desc    = VideoDecoder.textureFFmpeg.Description;
                    textWidth   = desc.Width;
                    textHeight  = desc.Height;
                }
                else
                {
                    /* TODO
                     * 1) Use Texture Array for SW Frames
                     * 2) Use Padded Width / Height & Crop after | Width: linesize[0] / D3D11TextureByteSize, Height: Coded_Height (=frame->height padded)
                     * 3) The we can render odd visible width properly
                     */

                    textWidth   = (uint)frame->width;   // (uint)(frame->width  & ~1);   //((frame->width  + 1) & ~1);
                    textHeight  = (uint)frame->height;  // (uint)(frame->height & ~1);  //((frame->height + 1) & ~1);
                }

                VideoRect       = new(0, 0, (int)textWidth, (int)textHeight);
                hasLinesizeVFlip= false;

                UpdateRotationUnSafe(_RotationAngle, false);
                UpdateHDRtoSDRUnSafe(false);
                UpdateCroppingUnSafe(false);
            }
            else if (VideoStream == null)
                return false;

            var oldVP       = videoProcessor;
            VideoProcessor  = GetVP();

            if (oldVP != videoProcessor)
            {
                VideoDecoder.DisposeFrame(LastFrame); LastFrame = null;
                VideoDecoder.DisposeFrames();
            }

            curPSCase       = PSCase.None;
            prevPSUniqueId  = curPSUniqueId;
            curPSUniqueId   = "";

            if (CanTrace)
                Log.Trace($"Preparing planes for {VideoStream.PixelFormatStr} with {videoProcessor}");

            bool supported = VideoDecoder.VideoAccelerated || (!VideoStream.PixelFormatDesc->flags.HasFlag(PixFmtFlags.Pal) && (!VideoStream.PixelFormatDesc->flags.HasFlag(PixFmtFlags.Be) || VideoStream.PixelComp0Depth <= 8));
            if (supported)
            {
                if (videoProcessor == VideoProcessors.D3D11)
                {
                    curPSCase = PSCase.HWD3D11VP;

                    inputColorSpace = new()
                    {
                        Usage           = 0u,
                        RGB_Range       = VideoStream.ColorRange == ColorRange.Full  ? 0u : 1u,
                        YCbCr_Matrix    = VideoStream.ColorSpace != ColorSpace.Bt601 ? 1u : 0u,
                        YCbCr_xvYCC     = 0u,
                        Nominal_Range   = VideoStream.ColorRange == ColorRange.Full  ? 2u : 1u
                    };

                    vpov?.Dispose();
                    vd1.CreateVideoProcessorOutputView(backBuffer, vpe, vpovd, out vpov);
                    vc.VideoProcessorSetStreamColorSpace(vp, 0, inputColorSpace);
                    vc.VideoProcessorSetOutputColorSpace(vp, outputColorSpace);
                }
                else if (!Config.Video.SwsForce || VideoDecoder.VideoAccelerated) // FlyleafVP
                {
                    List<string> defines = [];

                    if (HasFLFilters)
                    {
                        curPSUniqueId += "-";
                        defines.Add(dFilters);
                    }

                    if (VideoStream.HDRFormat != HDRFormat.None)
                    {
                        if (VideoStream.HDRFormat == HDRFormat.HLG)
                        {
                            curPSUniqueId += "g";
                            defines.Add(dHLGToLinear);
                        }
                        else
                        {
                            curPSUniqueId += "p";
                            defines.Add(dPQToLinear);
                        }

                        defines.Add(dTone);
                    }
                    else if (VideoStream.ColorSpace == ColorSpace.Bt2020)
                    {
                        defines.Add(dBT2020);
                        curPSUniqueId += "b";
                    }

                    for (int i = 0; i < srvDesc.Length; i++)
                        srvDesc[i].ViewDimension = ShaderResourceViewDimension.Texture2D;

                    // 1. HW Decoding
                    if (VideoDecoder.VideoAccelerated)
                    {
                        if (VideoStream.ColorRange == ColorRange.Limited)
                            defines.Add(dYUVLimited);
                        else
                        {
                            curPSUniqueId += "f";
                            defines.Add(dYUVFull);
                        }

                        if (VideoStream.ColorSpace == ColorSpace.Bt709)
                            psBufferData.coefsIndex = 1;
                        else if (VideoStream.ColorSpace == ColorSpace.Bt2020)
                            psBufferData.coefsIndex = 0;
                        else
                            psBufferData.coefsIndex = 2;

                        if (VideoStream.PixelComp0Depth > 8)
                        {
                            srvDesc[0].Format = Format.R16_UNorm;
                            srvDesc[1].Format = Format.R16G16_UNorm;
                        }
                        else
                        {
                            srvDesc[0].Format = Format.R8_UNorm;
                            srvDesc[1].Format = Format.R8G8_UNorm;
                        }

                        curPSCase = PSCase.HW;
                        srvDesc[0].ViewDimension = ShaderResourceViewDimension.Texture2DArray;
                        srvDesc[1].ViewDimension = ShaderResourceViewDimension.Texture2DArray;

                        // HW || HWZeroCopy | TODO: Fix calculation of uniqueId (those are actually same sampling - without different defines)
                        curPSUniqueId += "3";

                        SetPS(curPSUniqueId, @"
    color = float4(
        Texture1.Sample(Sampler, input.Texture).r,
        Texture2.Sample(Sampler, input.Texture).rg,
        1.0f);
", defines);
                    }

                    else if (VideoStream.ColorType == ColorType.YUV)
                    {
                        if (VideoStream.ColorRange == ColorRange.Limited)
                            defines.Add(dYUVLimited);
                        else
                        {
                            curPSUniqueId += "f";
                            defines.Add(dYUVFull);
                        }

                        if (VideoStream.ColorSpace == ColorSpace.Bt709)
                            psBufferData.coefsIndex = 1;
                        else if (VideoStream.ColorSpace == ColorSpace.Bt2020)
                            psBufferData.coefsIndex = 0;
                        else
                            psBufferData.coefsIndex = 2;

                        if (VideoStream.PixelPlanes == 1 && ( // No Alpha
                            VideoStream.PixelFormat == AVPixelFormat.Y210le  || // Not tested
                            VideoStream.PixelFormat == AVPixelFormat.Yuyv422 ||
                            VideoStream.PixelFormat == AVPixelFormat.Yvyu422 ||
                            VideoStream.PixelFormat == AVPixelFormat.Uyvy422 ))
                        {
                            curPSCase = PSCase.YUVPacked;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            psBufferData.uvOffset = 1.0f / (textWidth >> 1);
                            textDesc[0].Width   = textWidth;
                            textDesc[0].Height  = textHeight;

                            if (VideoStream.PixelComp0Depth > 8)
                            {
                                curPSUniqueId += "x";
                                textDesc[0].Format  = Format.Y210;
                                srvDesc[0].Format   = Format.R16G16B16A16_UNorm;
                            }
                            else
                            {
                                textDesc[0].Format  = Format.YUY2;
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
                            if (VideoStream.PixelFormat == AVPixelFormat.Yuyv422 ||
                                VideoStream.PixelFormat == AVPixelFormat.Y210le)
                            {
                                curPSUniqueId += "a";

                                SetPS(curPSUniqueId, header + @"
        float  leftY    = lerp(c1.r, c1.b, fx * 2);
        float  rightY   = lerp(c1.b, c2.r, fx * 2 - 1);
        float2 outUV    = lerp(c1.ga, c2.ga, fx);
        float  outY     = lerp(leftY, rightY, step(0.5, fx));
        color = float4(outY, outUV, 1.0f);
    ", defines);
                            }
                            else if (VideoStream.PixelFormat == AVPixelFormat.Yvyu422)
                            {
                                curPSUniqueId += "b";

                                SetPS(curPSUniqueId, header + @"
        float  leftY    = lerp(c1.r, c1.b, fx * 2);
        float  rightY   = lerp(c1.b, c2.r, fx * 2 - 1);
        float2 outUV    = lerp(c1.ag, c2.ag, fx);
        float  outY     = lerp(leftY, rightY, step(0.5, fx));
        color = float4(outY, outUV, 1.0f);
    ", defines);
                            }
                            else if (VideoStream.PixelFormat == AVPixelFormat.Uyvy422)
                            {
                                curPSUniqueId += "c";

                                SetPS(curPSUniqueId, header + @"
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
                        else if (VideoStream.PixelPlanes == 2) // No Alpha
                        {
                            curPSCase = PSCase.YUVSemiPlanar;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            textDesc[0].Width   = textWidth;
                            textDesc[0].Height  = textHeight;
                            textDesc[1].Width   = textWidth  >> VideoStream.PixelFormatDesc->log2_chroma_w;
                            textDesc[1].Height  = textHeight >> VideoStream.PixelFormatDesc->log2_chroma_h;

                            string offsets = VideoStream.PixelComps[1].offset > VideoStream.PixelComps[2].offset ? "gr" : "rg";
                            curPSUniqueId += offsets;

                            if (VideoStream.PixelComp0Depth > 8)
                            {
                                curPSUniqueId += "x";
                                textDesc[0].Format  = srvDesc[0].Format = Format.R16_UNorm;
                                textDesc[1].Format  = srvDesc[1].Format = Format.R16G16_UNorm;
                            }
                            else
                            {
                                textDesc[0].Format = srvDesc[0].Format = Format.R8_UNorm;
                                textDesc[1].Format = srvDesc[1].Format = Format.R8G8_UNorm;
                            }

                            SetPS(curPSUniqueId, @"
    color = float4(
        Texture1.Sample(Sampler, input.Texture).r,
        Texture2.Sample(Sampler, input.Texture)." + offsets + @",
        1.0);
    ", defines);
                        }

                        // Y_U_V
                        else if (VideoStream.PixelPlanes > 2) // Possible Alpha
                        {
                            curPSCase = PSCase.YUVPlanar;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            textDesc[0].Width   = textDesc[3].Width = textWidth;
                            textDesc[0].Height  = textDesc[3].Height= textHeight;
                            textDesc[1].Width   = textDesc[2].Width = textWidth  >> VideoStream.PixelFormatDesc->log2_chroma_w;
                            textDesc[1].Height  = textDesc[2].Height= textHeight >> VideoStream.PixelFormatDesc->log2_chroma_h;

                            string shader = @"
    color.r = Texture1.Sample(Sampler, input.Texture).r;
    color.g = Texture2.Sample(Sampler, input.Texture).r;
    color.b = Texture3.Sample(Sampler, input.Texture).r;
";
                            // TODO: eg. Gamma28 => color.r = pow(color.r, 2.8); and then it needs back after yuv->rgb with c = pow(c, 1.0 / 2.8);

                            if (VideoStream.PixelPlanes == 4)
                            {
                                curPSUniqueId += "x";

                                shader += @"
    color.a = Texture4.Sample(Sampler, input.Texture).r;
";
                            }

                            Format  curFormat = Format.R8_UNorm;
                            int     maxBits   = 8;
                            if (VideoStream.PixelComp0Depth > 8)
                            {
                                curPSUniqueId += "a";
                                curFormat = Format.R16_UNorm;
                                maxBits = 16;
                            }

                            for (int i = 0; i < VideoStream.PixelPlanes; i++)
                                textDesc[i].Format = srvDesc[i].Format = curFormat;

                            // TBR: This is an estimation from N-bits to eg 16-bits (should include alpha!?)
                            if (maxBits - VideoStream.PixelComp0Depth != 0)
                            {
                                curPSUniqueId += VideoStream.PixelComp0Depth;
                                shader += @"
    color *= pow(2, " + (maxBits - VideoStream.PixelComp0Depth) + @");
";
                            }

                            if (VideoStream.PixelPlanes < 4)
                                shader += @"
    color.a = 1.0f;
";
                            SetPS(curPSUniqueId, shader, defines);
                        }
                    }

                    else if (VideoStream.ColorType == ColorType.RGB)
                    {
                        // [RGB0]32 | [RGBA]32 | [RGBA]64
                        if (VideoStream.PixelPlanes == 1 && ( // Possible Alpha
                            VideoStream.PixelFormat == AVPixelFormat._0RGB  ||
                            VideoStream.PixelFormat == AVPixelFormat.Rgb0   ||
                            VideoStream.PixelFormat == AVPixelFormat._0BGR  ||
                            VideoStream.PixelFormat == AVPixelFormat.Bgr0   ||

                            VideoStream.PixelFormat == AVPixelFormat.Argb   ||
                            VideoStream.PixelFormat == AVPixelFormat.Rgba   ||
                            VideoStream.PixelFormat == AVPixelFormat.Abgr   ||
                            VideoStream.PixelFormat == AVPixelFormat.Bgra   ||

                            VideoStream.PixelFormat == AVPixelFormat.Rgba64le||
                            VideoStream.PixelFormat == AVPixelFormat.Bgra64le))
                        {
                            curPSCase = PSCase.RGBPacked;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            textDesc[0].Width   = textWidth;
                            textDesc[0].Height  = textHeight;

                            if (VideoStream.PixelComp0Depth > 8)
                            {
                                curPSUniqueId += "1";
                                textDesc[0].Format  = srvDesc[0].Format = Format.R16G16B16A16_UNorm;
                            }
                            else
                                textDesc[0].Format  = srvDesc[0].Format = Format.R8G8B8A8_UNorm; // B8G8R8X8_UNorm for 0[rgb]?

                            string offsets = "";
                            for (int i = 0; i < VideoStream.PixelComps.Length; i++)
                                offsets += pixelOffsets[(int) (VideoStream.PixelComps[i].offset / Math.Ceiling(VideoStream.PixelComp0Depth / 8.0))];

                            // TBR: [RGB0]32 has no alpha remove it
                            if (VideoStream.PixelFormatStr[0] == '0')
                                offsets = offsets[1..];
                            else if (VideoStream.PixelFormatStr[^1] == '0')
                                offsets = offsets[..^1];

                            curPSUniqueId += offsets;

                            string shader;
                            if (VideoStream.PixelComps.Length > 3 && offsets.Length > 3)
                                shader = @$"
    color = Texture1.Sample(Sampler, input.Texture).{offsets};
";
                            else
                                shader = @$"
    color = float4(Texture1.Sample(Sampler, input.Texture).{offsets}, 1.0f);
";
                            // TODO: Should transfer it to pixel shader
                            if (VideoStream.ColorRange == ColorRange.Limited)
                            {   // RGBLimitedToFull
                                curPSUniqueId += "k";
                                shader += @"
    color.rgb = (color.rgb - rgbOffset) * rgbScale;
";
                            }

                            SetPS(curPSUniqueId, shader, defines);
                        }

                        // [BGR/RGB]16
                        else if (VideoStream.PixelPlanes == 1 && (
                            VideoStream.PixelFormat == AVPixelFormat.Rgb444le||
                            VideoStream.PixelFormat == AVPixelFormat.Bgr444le))
                        {
                            curPSCase = PSCase.RGBPacked2;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            textDesc[0].Width   = textWidth;
                            textDesc[0].Height  = textHeight;
                            textDesc[0].Format  = srvDesc[0].Format = Format.B4G4R4A4_UNorm;

                            string shader;
                            if (VideoStream.PixelFormat == AVPixelFormat.Rgb444le)
                            {
                                curPSUniqueId += "a";
                                shader = @"
    color = float4(Texture1.Sample(Sampler, input.Texture).rgb, 1.0f);
";
                            }
                            else
                                shader = @"
    color = float4(Texture1.Sample(Sampler, input.Texture).bgr, 1.0f);
";
                            // TODO: Should transfer it to pixel shader
                            if (VideoStream.ColorRange == ColorRange.Limited)
                            {   // RGBLimitedToFull
                                curPSUniqueId += "k";
                                shader += @"
    color.rgb = (color.rgb - rgbOffset) * rgbScale;
";
                            }

                            SetPS(curPSUniqueId, shader, defines);
                        }

                        // GBR(A)
                        else if (VideoStream.PixelPlanes > 2) // Possible Alpha | TBR: Usually transfer func 'Linear' for > 8-bit which requires pow (*?)
                        {
                            curPSCase = PSCase.RGBPlanar;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            for (int i = 0; i < VideoStream.PixelPlanes; i++)
                            {
                                textDesc[i].Width   = textWidth;
                                textDesc[i].Height  = textHeight;
                            }

                            string shader = @"
    color.g = Texture1.Sample(Sampler, input.Texture).r;
    color.b = Texture2.Sample(Sampler, input.Texture).r;
    color.r = Texture3.Sample(Sampler, input.Texture).r;
";
                            if (VideoStream.PixelPlanes == 4)
                            {
                                curPSUniqueId += "x";

                                shader += @"
    color.a = Texture4.Sample(Sampler, input.Texture).r;
";
                            }

                            /* TODO:
                             * Using pow for scale/normalize is not accurate (when maxBits != VideoStream.PixelComp0Depth)
                             * Mainly affects gbrp10 (should prefer Texture2D<float> for more accurate and better performance)
                             */

                            Format  curFormat = Format.R8_UNorm;
                            int     maxBits   = 8;
                            if (VideoStream.PixelComp0Depth > 16)
                            {
                                curPSUniqueId += "a";
                                curFormat   = Format.R32_Float;
                                maxBits     = 32;
                            }
                            else if (VideoStream.PixelComp0Depth > 8)
                            {
                                curPSUniqueId += "b";
                                curFormat   = Format.R16_UNorm;
                                maxBits     = 16;
                            }

                            for (int i = 0; i < VideoStream.PixelPlanes; i++)
                                textDesc[i].Format = srvDesc[i].Format = curFormat;

                            if (maxBits - VideoStream.PixelComp0Depth != 0)
                            {
                                curPSUniqueId += VideoStream.PixelComp0Depth;
                                shader += @"
    color *= pow(2, " + (maxBits - VideoStream.PixelComp0Depth) + @");
";
                            }

                            // TODO: Should transfer it to pixel shader
                            if (VideoStream.ColorRange == ColorRange.Limited)
                            {   // RGBLimitedToFull
                                curPSUniqueId += "k";
                                shader += @"
    color.rgb = (color.rgb - rgbOffset) * rgbScale;
";
                            }

                            if (VideoStream.PixelPlanes < 4)
                                shader += @"
    color.a = 1.0f;
";
                            SetPS(curPSUniqueId, shader, defines);
                        }
                    }
                    else // Gray (Single Plane)
                    {
                        curPSCase = PSCase.Gray;
                        curPSUniqueId += ((int)curPSCase).ToString();

                        textDesc[0].Width   = textWidth;
                        textDesc[0].Height  = textHeight;

                        string shader = @"
    color = float4(Texture1.Sample(Sampler, input.Texture).r, Texture1.Sample(Sampler, input.Texture).r, Texture1.Sample(Sampler, input.Texture).r, 1.0f);
";
                        int maxBits = 8;
                        if (VideoStream.PixelComp0Depth > 8)
                        {
                            curPSUniqueId += "x";
                            maxBits = 16;
                            textDesc[0].Format  = srvDesc[0].Format = Format.R16_UNorm;
                        }
                        else
                            textDesc[0].Format  = srvDesc[0].Format = Format.R8_UNorm;

                        if (maxBits - VideoStream.PixelComp0Depth != 0)
                        {
                            curPSUniqueId += VideoStream.PixelComp0Depth;
                            shader += @"
    color.rgb *= pow(2, " + (maxBits - VideoStream.PixelComp0Depth) + @");
";
                        }

                        // TODO: Should transfer it to pixel shader
                        if (VideoStream.ColorRange == ColorRange.Limited)
                        {   // RGBLimitedToFull
                            curPSUniqueId += "k";
                            shader += @"
    color.rgb = (color.rgb - rgbOffset) * rgbScale;
";
                        }

                        SetPS(curPSUniqueId, shader, defines);
                    }
                }
            }

            if (textDesc[0].Format != Format.Unknown && !Device.CheckFormatSupport(textDesc[0].Format).HasFlag(FormatSupport.Texture2D))
            {
                Log.Warn($"GPU does not support {textDesc[0].Format} texture format");
                curPSCase = PSCase.None;
            }

            if (curPSCase == PSCase.None)
            {
                if (!Config.Video.SwsForce)
                    Log.Warn($"{VideoStream.PixelFormatStr} not supported. Falling back to SwsScale");

                if (!VideoDecoder.SetupSws())
                {
                    Log.Error($"SwsScale setup failed");
                    return false;
                }

                curPSCase           = PSCase.SwsScale;
                curPSUniqueId       = ((int)curPSCase).ToString();

                textDesc[0].Width   = (uint)VideoDecoder.CodecCtx->width; // Visible dimensions!
                textDesc[0].Height  = (uint)VideoDecoder.CodecCtx->height;
                textDesc[0].Format  = srvDesc[0].Format = Format.R8G8B8A8_UNorm;
                srvDesc[0].ViewDimension = ShaderResourceViewDimension.Texture2D;

                SetPS(curPSUniqueId, @"
    color = float4(Texture1.Sample(Sampler, input.Texture).rgb, 1.0);
");
            }

            Log.Debug($"Prepared planes for {VideoStream.PixelFormatStr} with {videoProcessor} [{curPSCase}]");

            return true;

        }
        catch (Exception e)
        {
            Log.Error($"{VideoStream.PixelFormatStr} not supported? ({e.Message}");
            error = true;
            return false;

        }
        finally
        {
            if (!error && curPSCase != PSCase.None)
            {
                context.UpdateSubresource(psBufferData, psBuffer);

                if (ControlHandle != IntPtr.Zero || SwapChainWinUIClbk != null)
                    SetViewport();
                else if (!forceNotExtractor)
                    PrepareForExtract();
            }
            Monitor.Exit(lockDevice);
            Monitor.Exit(VideoDecoder.lockCodecCtx);
        }
    }

    internal VideoFrame FillPlanes(AVFrame* frame)
    {   // TBR: lockDevice?
        try
        {
            VideoFrame mFrame = new();
            mFrame.timestamp = (long)(frame->pts * VideoStream.Timebase) - VideoDecoder.Demuxer.StartTime;
            if (CanTrace) Log.Trace($"Processes {TicksToTime(mFrame.timestamp)}");

            if (curPSCase == PSCase.HW)
            {
                mFrame.srvs     = new ID3D11ShaderResourceView[2];
                srvDesc[0].Texture2DArray.FirstArraySlice = srvDesc[1].Texture2DArray.FirstArraySlice = (uint) frame->data[1];

                mFrame.srvs[0]  = Device.CreateShaderResourceView(VideoDecoder.textureFFmpeg, srvDesc[0]);
                mFrame.srvs[1]  = Device.CreateShaderResourceView(VideoDecoder.textureFFmpeg, srvDesc[1]);

                mFrame.avFrame = av_frame_alloc();
                av_frame_move_ref(mFrame.avFrame, frame);
                return mFrame;
            }

            else if (curPSCase == PSCase.HWD3D11VP)
            {
                mFrame.avFrame = av_frame_alloc();
                av_frame_move_ref(mFrame.avFrame, frame);
                return mFrame;
            }

            else if (curPSCase == PSCase.SwsScale)
            {
                mFrame.textures         = new ID3D11Texture2D[1];
                mFrame.srvs             = new ID3D11ShaderResourceView[1];

                _ = sws_scale(VideoDecoder.swsCtx, frame->data.ToRawArray(), frame->linesize.ToArray(), 0, frame->height, VideoDecoder.swsData.ToRawArray(), VideoDecoder.swsLineSize.ToArray());

                subData[0].DataPointer  = VideoDecoder.swsData[0];
                subData[0].RowPitch     = (uint)VideoDecoder.swsLineSize[0];

                mFrame.textures[0]      = Device.CreateTexture2D(textDesc[0], subData);
                mFrame.srvs[0]          = Device.CreateShaderResourceView(mFrame.textures[0], srvDesc[0]);
            }

            else
            {
                mFrame.textures = new ID3D11Texture2D[VideoStream.PixelPlanes];
                mFrame.srvs     = new ID3D11ShaderResourceView[VideoStream.PixelPlanes];

                bool vflip = false;
                for (int i = 0; i < VideoStream.PixelPlanes; i++)
                {
                    if (frame->linesize[i] < 0) // Negative linesize needs vertical flipping | [Bottom -> Top] data[i] points to last row and we need to move at first (height - 1) rows
                    {
                        vflip = true;
                        subData[0].RowPitch     = (uint)(-1 * frame->linesize[i]);
                        subData[0].DataPointer  = frame->data[i] + (frame->linesize[i] * (frame->height - 1));
                    }
                    else
                    {
                        vflip = false;
                        subData[0].RowPitch     = (uint)frame->linesize[i];
                        subData[0].DataPointer  = frame->data[i];
                    }

                    if (subData[0].RowPitch < textDesc[i].Width) // Prevent reading more than the actual data (Access Violation #424)
                    {
                        av_frame_unref(frame);
                        return null;
                    }

                    mFrame.textures[i]  = Device.CreateTexture2D(textDesc[i], subData);
                    mFrame.srvs[i]      = Device.CreateShaderResourceView(mFrame.textures[i], srvDesc[i]);
                }

                if (vflip != hasLinesizeVFlip)
                {
                    hasLinesizeVFlip = vflip;
                    UpdateRotation(_RotationAngle);
                }
            }

            av_frame_unref(frame);

            return mFrame;
        }
        catch (SharpGenException e)
        {
            av_frame_unref(frame);

            if (IsDeviceError(e.ResultCode))
            {
                Log.Error($"[FillPlanes] Device Lost ({e.ResultCode.NativeApiCode} | {Device.DeviceRemovedReason} | {e.Message})");
                Thread.Sleep(100);
                VideoDecoder.handleDeviceReset = true; // We can't stop from RunInternal
            }
            else
                Log.Error($"[FillPlanes] Failed ({e.Message})");

            return null;
        }
        catch (Exception e)
        {
            av_frame_unref(frame);
            Log.Error($"[FillPlanes] Failed ({e.Message})");

            return null;
        }
    }

    void SetPS(string uniqueId, ReadOnlySpan<char> sampleHLSL, List<string> defines = null)
    {
        if (curPSUniqueId == prevPSUniqueId)
            return;

        ShaderPS?.Dispose();
        ShaderPS = ShaderCompiler.CompilePS(Device, uniqueId, sampleHLSL, defines);
        context.PSSetShader(ShaderPS);
    }
}

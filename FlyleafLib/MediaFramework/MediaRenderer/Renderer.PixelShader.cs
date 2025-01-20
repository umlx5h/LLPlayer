using System.Collections.Generic;
using System.Threading;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;

using static FlyleafLib.Logger;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace FlyleafLib.MediaFramework.MediaRenderer;

unsafe public partial class Renderer
{
    static string[] pixelOffsets = new[] { "r", "g", "b", "a" };
    enum PSDefines { HDR, HLG, YUV }
    enum PSCase : int
    {
        None,
        HWD3D11VP,
        HWD3D11VPZeroCopy,
        HW,
        HWZeroCopy,

        RGBPacked,
        RGBPacked2,
        RGBPlanar,

        YUVPacked,
        YUVSemiPlanar,
        YUVPlanar,
        SwsScale
    }

    bool    checkHDR;
    PSCase  curPSCase;
    string  curPSUniqueId;
    float   curRatio = 1.0f;
    string  prevPSUniqueId;
    internal bool forceNotExtractor; // TBR: workaround until we separate the Extractor?

    Texture2DDescription[]          textDesc= new Texture2DDescription[4];
    ShaderResourceViewDescription[] srvDesc = new ShaderResourceViewDescription[4];
    SubresourceData[]               subData = new SubresourceData[1];
    Box                             cropBox = new(0, 0, 0, 0, 0, 1);

    void InitPS()
    {
        for (int i=0; i<textDesc.Length; i++)
        {
            textDesc[i].Usage               = ResourceUsage.Default;
            textDesc[i].BindFlags           = BindFlags.ShaderResource;// | BindFlags.RenderTarget;
            textDesc[i].SampleDescription   = new SampleDescription(1, 0);
            textDesc[i].ArraySize           = 1;
            textDesc[i].MipLevels           = 1;
        }

        for (int i=0; i<textDesc.Length; i++)
        {
            srvDesc[i].Texture2D        = new() { MipLevels = 1, MostDetailedMip = 0 };
            srvDesc[i].Texture2DArray   = new Texture2DArrayShaderResourceView() { ArraySize = 1, MipLevels = 1 };
        }
    }

    internal bool ConfigPlanes() //bool isNewInput = true) // currently not used
    {
        bool error = false;

        try
        {
            Monitor.Enter(VideoDecoder.lockCodecCtx);
            Monitor.Enter(lockDevice);

            // Don't use SCDisposed as we need to allow config planes even before swapchain creation
            // TBR: Possible run ConfigPlanes after swapchain creation instead (currently we don't access any resources of the swapchain here and is safe)
            if (Disposed || VideoStream == null)
                return false;

            VideoDecoder.DisposeFrame(LastFrame);

            curRatio    = VideoStream.AspectRatio.Value;
            IsHDR       = VideoStream.ColorSpace == ColorSpace.BT2020 && ( // TBR: Should transfer IsHDR to VideoStream? | Unspecified (Space/Transfer)
                          VideoStream.ColorTransfer == AVColorTransferCharacteristic.Smpte2084 ||  // PQ
                          VideoStream.ColorTransfer == AVColorTransferCharacteristic.AribStdB67);// HLG
            VideoRect   = new RawRect(0, 0, (int)VideoStream.Width, (int)VideoStream.Height);
            rotationLinesize
                        = false;
            UpdateRotation(_RotationAngle, false);

            if (IsHDR)
            {
                hdrPlusData = null;
                displayData = new();
                lightData   = new();
                checkHDR    = true;
            }
            else
                checkHDR = false;

            var oldVP = videoProcessor;

            // Defaults to D3D11VP
            //VideoProcessor = !VideoDecoder.VideoAccelerated || D3D11VPFailed || Config.Video.VideoProcessor == VideoProcessors.Flyleaf || (Config.Video.VideoProcessor == VideoProcessors.Auto && isHDR && !Config.Video.Deinterlace) ? VideoProcessors.Flyleaf : VideoProcessors.D3D11;

            // Defaults to FlyleafVP
            VideoProcessor = !D3D11VPFailed && VideoDecoder.VideoAccelerated &&
                (Config.Video.Deinterlace || Config.Video.VideoProcessor == VideoProcessors.D3D11) ?
                VideoProcessors.D3D11 : VideoProcessors.Flyleaf;

            textDesc[0].BindFlags &= ~BindFlags.RenderTarget; // Only D3D11VP without ZeroCopy requires it
            curPSCase = PSCase.None;
            prevPSUniqueId = curPSUniqueId;
            curPSUniqueId = "";

            Log.Debug($"Preparing planes for {VideoStream.PixelFormatStr} with {videoProcessor}");
            if ((VideoStream.PixelFormatDesc->flags & PixFmtFlags.Be) == 0) // We currently force SwsScale for BE (RGBA64/BGRA64 BE noted that could work as is?*)
            {
                if (videoProcessor == VideoProcessors.D3D11)
                {
                    if (oldVP != videoProcessor)
                    {
                        VideoDecoder.DisposeFrames();
                        Config.Video.Filters[VideoFilters.Brightness].Value = Config.Video.Filters[VideoFilters.Brightness].DefaultValue;
                        Config.Video.Filters[VideoFilters.Contrast].Value = Config.Video.Filters[VideoFilters.Contrast].DefaultValue;
                    }

                    inputColorSpace = new()
                    {
                        Usage           = 0,
                        RGB_Range       = VideoStream.AVStream->codecpar->color_range == AVColorRange.Jpeg ? (uint) 0 : 1,
                        YCbCr_Matrix    = VideoStream.ColorSpace != ColorSpace.BT601 ? (uint) 1 : 0,
                        YCbCr_xvYCC     = 0,
                        Nominal_Range   = VideoStream.AVStream->codecpar->color_range == AVColorRange.Jpeg ? (uint) 2 : 1
                    };

                    vpov?.Dispose();
                    vd1.CreateVideoProcessorOutputView(backBuffer, vpe, vpovd, out vpov);
                    vc.VideoProcessorSetStreamColorSpace(vp, 0, inputColorSpace);
                    vc.VideoProcessorSetOutputColorSpace(vp, outputColorSpace);

                    if (child != null)
                    {
                        child.vpov?.Dispose();
                        vd1.CreateVideoProcessorOutputView(child.backBuffer, vpe, vpovd, out child.vpov);
                    }

                    if (VideoDecoder.ZeroCopy)
                        curPSCase = PSCase.HWD3D11VPZeroCopy;
                    else
                    {
                        curPSCase = PSCase.HWD3D11VP;

                        textDesc[0].BindFlags |= BindFlags.RenderTarget;

                        cropBox.Right       = (int)VideoStream.Width;
                        textDesc[0].Width   = VideoStream.Width;
                        cropBox.Bottom      = (int)VideoStream.Height;
                        textDesc[0].Height  = VideoStream.Height;
                        textDesc[0].Format  = VideoDecoder.textureFFmpeg.Description.Format;
                    }
                }
                else if (!Config.Video.SwsForce || VideoDecoder.VideoAccelerated) // FlyleafVP
                {
                    List<string> defines = new();

                    if (oldVP != videoProcessor)
                    {
                        VideoDecoder.DisposeFrames();
                        Config.Video.Filters[VideoFilters.Brightness].Value = Config.Video.Filters[VideoFilters.Brightness].Minimum + ((Config.Video.Filters[VideoFilters.Brightness].Maximum - Config.Video.Filters[VideoFilters.Brightness].Minimum) / 2);
                        Config.Video.Filters[VideoFilters.Contrast].Value   = Config.Video.Filters[VideoFilters.Contrast].Minimum + ((Config.Video.Filters[VideoFilters.Contrast].Maximum - Config.Video.Filters[VideoFilters.Contrast].Minimum) / 2);
                    }

                    if (IsHDR)
                    {
                        // TBR: It currently works better without it
                        //if (VideoStream.ColorTransfer == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
                        //{
                        //    defines.Add(PSDefines.HLG.ToString());
                        //    curPSUniqueId += "g";
                        //}

                        curPSUniqueId += "h";
                        defines.Add(PSDefines.HDR.ToString());
                        psBufferData.coefsIndex = 0;
                        UpdateHDRtoSDR(false);
                    }
                    else
                        psBufferData.coefsIndex = VideoStream.ColorSpace == ColorSpace.BT709 ? 1 : 2;

                    for (int i=0; i<srvDesc.Length; i++)
                        srvDesc[i].ViewDimension = ShaderResourceViewDimension.Texture2D;

                    // 1. HW Decoding
                    if (VideoDecoder.VideoAccelerated)
                    {
                        defines.Add(PSDefines.YUV.ToString());

                        if (VideoDecoder.VideoStream.PixelComp0Depth > 8)
                        {
                            srvDesc[0].Format = Format.R16_UNorm;
                            srvDesc[1].Format = Format.R16G16_UNorm;
                        }
                        else
                        {
                            srvDesc[0].Format = Format.R8_UNorm;
                            srvDesc[1].Format = Format.R8G8_UNorm;
                        }

                        if (VideoDecoder.ZeroCopy)
                        {
                            curPSCase = PSCase.HWZeroCopy;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            for (int i=0; i<srvDesc.Length; i++)
                                srvDesc[i].ViewDimension = ShaderResourceViewDimension.Texture2DArray;
                        }
                        else
                        {
                            curPSCase = PSCase.HW;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            cropBox.Right       = (int)VideoStream.Width;
                            textDesc[0].Width   = VideoStream.Width;
                            cropBox.Bottom      = (int)VideoStream.Height;
                            textDesc[0].Height  = VideoStream.Height;
                            textDesc[0].Format  = VideoDecoder.textureFFmpeg.Description.Format;
                        }

                        SetPS(curPSUniqueId, @"
    color = float4(
        Texture1.Sample(Sampler, input.Texture).r,
        Texture2.Sample(Sampler, input.Texture).rg,
        1.0);
    ", defines);
                    }

                    else if (VideoStream.IsRGB)
                    {
                        // [RGB0]32 | [RGBA]32 | [RGBA]64
                        if (VideoStream.PixelPlanes == 1 && (
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

                            textDesc[0].Width   = VideoStream.Width;
                            textDesc[0].Height  = VideoStream.Height;

                            if (VideoStream.PixelComp0Depth > 8)
                            {
                                curPSUniqueId += "x";
                                textDesc[0].Format  = srvDesc[0].Format = Format.R16G16B16A16_UNorm;
                            }
                            else if (VideoStream.PixelComp0Depth > 4)
                                textDesc[0].Format  = srvDesc[0].Format = Format.R8G8B8A8_UNorm; // B8G8R8X8_UNorm for 0[rgb]?

                            string offsets = "";
                            for (int i = 0; i < VideoStream.PixelComps.Length; i++)
                                offsets += pixelOffsets[(int) (VideoStream.PixelComps[i].offset / Math.Ceiling(VideoStream.PixelComp0Depth / 8.0))];

                            curPSUniqueId += offsets;

                            if (VideoStream.PixelComps.Length > 3)
                                SetPS(curPSUniqueId, $"color = Texture1.Sample(Sampler, input.Texture).{offsets};");
                            else
                                SetPS(curPSUniqueId, $"color = float4(Texture1.Sample(Sampler, input.Texture).{offsets}, 1.0);");
                        }

                        // [BGR/RGB]16
                        else if (VideoStream.PixelPlanes == 1 && (
                            VideoStream.PixelFormat == AVPixelFormat.Rgb444le||
                            VideoStream.PixelFormat == AVPixelFormat.Bgr444le))
                        {
                            curPSCase = PSCase.RGBPacked2;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            textDesc[0].Width   = VideoStream.Width;
                            textDesc[0].Height  = VideoStream.Height;

                            textDesc[0].Format  = srvDesc[0].Format = Format.B4G4R4A4_UNorm;

                            if (VideoStream.PixelFormat == AVPixelFormat.Rgb444le)
                            {
                                curPSUniqueId += "a";
                                SetPS(curPSUniqueId, $"color = float4(Texture1.Sample(Sampler, input.Texture).rgb, 1.0);");
                            }
                            else
                            {
                                curPSUniqueId += "b";
                                SetPS(curPSUniqueId, $"color = float4(Texture1.Sample(Sampler, input.Texture).bgr, 1.0);");
                            }
                        }

                        // GBR(A) <=16
                        else if (VideoStream.PixelPlanes > 2 && VideoStream.PixelComp0Depth <= 16)
                        {
                            curPSCase = PSCase.RGBPlanar;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            for (int i=0; i<VideoStream.PixelPlanes; i++)
                            {
                                textDesc[i].Width   = VideoStream.Width;
                                textDesc[i].Height  = VideoStream.Height;
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

                            if (VideoStream.PixelComp0Depth > 8)
                            {
                                curPSUniqueId += VideoStream.PixelComp0Depth;

                                for (int i=0; i<VideoStream.PixelPlanes; i++)
                                    textDesc[i].Format = srvDesc[i].Format = Format.R16_UNorm;

                                shader += @"
        color = color * pow(2, " + (16 - VideoStream.PixelComp0Depth) + @");
    ";
                            }
                            else
                            {
                                curPSUniqueId += "b";

                                for (int i=0; i<VideoStream.PixelPlanes; i++)
                                    textDesc[i].Format = srvDesc[i].Format = Format.R8_UNorm;
                            }

                            // if (VideoStream.PixelPlanes != 4) // TBR: seems causing issues
                            SetPS(curPSUniqueId, shader + @"
        color.a = 1;
    ", defines);
                        }
                    }

                    else // YUV
                    {
                        defines.Add(PSDefines.YUV.ToString());

                        if (VideoStream.PixelPlanes == 1 && (
                            VideoStream.PixelFormat == AVPixelFormat.Y210le  || // Not tested
                            VideoStream.PixelFormat == AVPixelFormat.Yuyv422 ||
                            VideoStream.PixelFormat == AVPixelFormat.Yvyu422 ||
                            VideoStream.PixelFormat == AVPixelFormat.Uyvy422 ))
                        {
                            curPSCase = PSCase.YUVPacked;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            psBufferData.texWidth = 1.0f / (VideoStream.Width >> 1);
                            textDesc[0].Width   = VideoStream.Width;
                            textDesc[0].Height  = VideoStream.Height;

                            if (VideoStream.PixelComp0Depth > 8)
                            {
                                curPSUniqueId += $"{VideoStream.Width}_";
                                textDesc[0].Format  = Format.Y210;
                                srvDesc[0].Format   = Format.R16G16B16A16_UNorm;
                            }
                            else
                            {
                                curPSUniqueId += $"{VideoStream.Width}";
                                textDesc[0].Format  = Format.YUY2;
                                srvDesc[0].Format   = Format.R8G8B8A8_UNorm;
                            }

                            string header = @"
        float  posx = input.Texture.x - (texWidth * 0.25);
        float  fx = frac(posx / texWidth);
        float  pos1 = posx + ((0.5 - fx) * texWidth);
        float  pos2 = posx + ((1.5 - fx) * texWidth);

        float4 c1 = Texture1.Sample(Sampler, float2(pos1, input.Texture.y));
        float4 c2 = Texture1.Sample(Sampler, float2(pos2, input.Texture.y));

    ";
                            if (VideoStream.PixelFormat == AVPixelFormat.Yuyv422 ||
                                VideoStream.PixelFormat == AVPixelFormat.Y210le)
                            {
                                curPSUniqueId += $"a";

                                SetPS(curPSUniqueId, header + @"
        float  leftY    = lerp(c1.r, c1.b, fx * 2);
        float  rightY   = lerp(c1.b, c2.r, fx * 2 - 1);
        float2 outUV    = lerp(c1.ga, c2.ga, fx);
        float  outY     = lerp(leftY, rightY, step(0.5, fx));
        color = float4(outY, outUV, 1.0);
    ", defines);
                            } else if (VideoStream.PixelFormat == AVPixelFormat.Yvyu422)
                            {
                                curPSUniqueId += $"b";

                                SetPS(curPSUniqueId, header + @"
        float  leftY    = lerp(c1.r, c1.b, fx * 2);
        float  rightY   = lerp(c1.b, c2.r, fx * 2 - 1);
        float2 outUV    = lerp(c1.ag, c2.ag, fx);
        float  outY     = lerp(leftY, rightY, step(0.5, fx));
        color = float4(outY, outUV, 1.0);
    ", defines);
                            } else if (VideoStream.PixelFormat == AVPixelFormat.Uyvy422)
                            {
                                curPSUniqueId += $"c";

                                SetPS(curPSUniqueId, header + @"
        float  leftY    = lerp(c1.g, c1.a, fx * 2);
        float  rightY   = lerp(c1.a, c2.g, fx * 2 - 1);
        float2 outUV    = lerp(c1.rb, c2.rb, fx);
        float  outY     = lerp(leftY, rightY, step(0.5, fx));
        color = float4(outY, outUV, 1.0);
    ", defines);
                            }
                        }

                        // Y_UV | nv12,nv21,nv24,nv42,p010le,p016le,p410le,p416le | (log2_chroma_w != log2_chroma_h / Interleaved) (? nv16,nv20le,p210le,p216le)
                        // This covers all planes == 2 YUV (Semi-Planar)
                        else if (VideoStream.PixelPlanes == 2) // && VideoStream.PixelSameDepth) && !VideoStream.PixelInterleaved)
                        {
                            curPSCase = PSCase.YUVSemiPlanar;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            textDesc[0].Width   = VideoStream.Width;
                            textDesc[0].Height  = VideoStream.Height;
                            textDesc[1].Width   = VideoStream.PixelFormatDesc->log2_chroma_w > 0 ? (VideoStream.Width  + 1) >> VideoStream.PixelFormatDesc->log2_chroma_w : VideoStream.Width  >> VideoStream.PixelFormatDesc->log2_chroma_w;
                            textDesc[1].Height  = VideoStream.PixelFormatDesc->log2_chroma_h > 0 ? (VideoStream.Height + 1) >> VideoStream.PixelFormatDesc->log2_chroma_h : VideoStream.Height >> VideoStream.PixelFormatDesc->log2_chroma_h;

                            string offsets = VideoStream.PixelComps[1].offset > VideoStream.PixelComps[2].offset ? "gr" : "rg";

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
                        else if (VideoStream.PixelPlanes > 2)
                        {
                            curPSCase = PSCase.YUVPlanar;
                            curPSUniqueId += ((int)curPSCase).ToString();

                            textDesc[0].Width   = textDesc[3].Width = VideoStream.Width;
                            textDesc[0].Height  = textDesc[3].Height= VideoStream.Height;
                            textDesc[1].Width   = textDesc[2].Width = VideoStream.PixelFormatDesc->log2_chroma_w > 0 ? (VideoStream.Width  + 1) >> VideoStream.PixelFormatDesc->log2_chroma_w : VideoStream.Width  >> VideoStream.PixelFormatDesc->log2_chroma_w;
                            textDesc[1].Height  = textDesc[2].Height= VideoStream.PixelFormatDesc->log2_chroma_h > 0 ? (VideoStream.Height + 1) >> VideoStream.PixelFormatDesc->log2_chroma_h : VideoStream.Height >> VideoStream.PixelFormatDesc->log2_chroma_h;

                            string shader = @"
        color.r = Texture1.Sample(Sampler, input.Texture).r;
        color.g = Texture2.Sample(Sampler, input.Texture).r;
        color.b = Texture3.Sample(Sampler, input.Texture).r;
    ";

                            if (VideoStream.PixelPlanes == 4)
                            {
                                curPSUniqueId += "x";

                                shader += @"
        color.a = Texture4.Sample(Sampler, input.Texture).r;
    ";
                            }

                            if (VideoStream.PixelComp0Depth > 8)
                            {
                                curPSUniqueId += VideoStream.PixelComp0Depth;

                                for (int i=0; i<VideoStream.PixelPlanes; i++)
                                    textDesc[i].Format = srvDesc[i].Format = Format.R16_UNorm;

                                shader += @"
        color = color * pow(2, " + (16 - VideoStream.PixelComp0Depth) + @");
    ";
                            }
                            else
                            {
                                curPSUniqueId += "b";

                                for (int i=0; i<VideoStream.PixelPlanes; i++)
                                    textDesc[i].Format = srvDesc[i].Format = Format.R8_UNorm;
                            }

                            SetPS(curPSUniqueId, shader + @"
        color.a = 1;
    ", defines);
                        }
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
                Log.Warn($"{VideoStream.PixelFormatStr} not supported. Falling back to SwsScale");

                if (!VideoDecoder.SetupSws())
                {
                    Log.Error($"SwsScale setup failed");
                    return false;
                }

                curPSCase = PSCase.SwsScale;
                curPSUniqueId = ((int)curPSCase).ToString();

                textDesc[0].Width   = VideoStream.Width;
                textDesc[0].Height  = VideoStream.Height;
                textDesc[0].Format  = srvDesc[0].Format = Format.R8G8B8A8_UNorm;
                srvDesc[0].ViewDimension = ShaderResourceViewDimension.Texture2D;

                // TODO: should add HDR?
                SetPS(curPSUniqueId, @"
color = float4(Texture1.Sample(Sampler, input.Texture).rgb, 1.0);
");
            }

            //AV_PIX_FMT_FLAG_ALPHA (currently used only for RGBA?)
            //context.OMSetBlendState(curPSCase == PSCase.RGBPacked || (curPSCase == PSCase.RGBPlanar && VideoStream.PixelPlanes == 4) ? blendStateAlpha : null);
            context.OMSetBlendState(curPSCase == PSCase.RGBPacked ? blendStateAlpha : null);

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

                if (child != null)
                {
                    //replica.ConfigPlanes();
                    child.curRatio      = curRatio;
                    child.VideoRect     = VideoRect;
                    child.videoProcessor= videoProcessor;
                    child.SetViewport();
                }
            }
            Monitor.Exit(lockDevice);
            Monitor.Exit(VideoDecoder.lockCodecCtx);
        }
    }
    internal VideoFrame FillPlanes(AVFrame* frame)
    {
        try
        {
            VideoFrame mFrame = new();
            mFrame.timestamp = (long)(frame->pts * VideoStream.Timebase) - VideoDecoder.Demuxer.StartTime;
            if (CanTrace) Log.Trace($"Processes {Utils.TicksToTime(mFrame.timestamp)}");

            if (checkHDR)
            {
                // TODO Dolpy Vision / Vivid
                //var hdrSideDolpyDynamic = av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA);

                var hdrSideDynamic = av_frame_get_side_data(frame, AVFrameSideDataType.DynamicHdrPlus);

                if (hdrSideDynamic != null && hdrSideDynamic->data != null)
                {
                    hdrPlusData = (AVDynamicHDRPlus*) hdrSideDynamic->data;
                    UpdateHDRtoSDR();
                }
                else
                {
                    var lightSide   = av_frame_get_side_data(frame, AVFrameSideDataType.ContentLightLevel);
                    var displaySide = av_frame_get_side_data(frame, AVFrameSideDataType.MasteringDisplayMetadata);

                    if (lightSide != null && lightSide->data != null && ((AVContentLightMetadata*)lightSide->data)->MaxCLL != 0)
                    {
                        lightData = *((AVContentLightMetadata*) lightSide->data);
                        checkHDR = false;
                        UpdateHDRtoSDR();
                    }

                    if (displaySide != null && displaySide->data != null && ((AVMasteringDisplayMetadata*)displaySide->data)->has_luminance != 0)
                    {
                        displayData = *((AVMasteringDisplayMetadata*) displaySide->data);
                        checkHDR = false;
                        UpdateHDRtoSDR();
                    }
                }
            }

            if (curPSCase == PSCase.HWZeroCopy)
            {
                mFrame.srvs         = new ID3D11ShaderResourceView[2];
                srvDesc[0].Texture2DArray.FirstArraySlice = srvDesc[1].Texture2DArray.FirstArraySlice = (uint) frame->data[1];

                mFrame.srvs[0]      = Device.CreateShaderResourceView(VideoDecoder.textureFFmpeg, srvDesc[0]);
                mFrame.srvs[1]      = Device.CreateShaderResourceView(VideoDecoder.textureFFmpeg, srvDesc[1]);

                mFrame.avFrame = av_frame_alloc();
                av_frame_move_ref(mFrame.avFrame, frame);
                return mFrame;
            }

            else if (curPSCase == PSCase.HW)
            {
                mFrame.textures     = new ID3D11Texture2D[1];
                mFrame.srvs         = new ID3D11ShaderResourceView[2];

                mFrame.textures[0]  = Device.CreateTexture2D(textDesc[0]);
                context.CopySubresourceRegion(
                    mFrame.textures[0], 0, 0, 0, 0, // dst
                    VideoDecoder.textureFFmpeg, (uint) frame->data[1],  // src
                    cropBox); // crop decoder's padding

                mFrame.srvs[0]      = Device.CreateShaderResourceView(mFrame.textures[0], srvDesc[0]);
                mFrame.srvs[1]      = Device.CreateShaderResourceView(mFrame.textures[0], srvDesc[1]);
            }

            else if (curPSCase == PSCase.HWD3D11VPZeroCopy)
            {
                mFrame.avFrame = av_frame_alloc();
                av_frame_move_ref(mFrame.avFrame, frame);
                return mFrame;
            }

            else if (curPSCase == PSCase.HWD3D11VP)
            {
                mFrame.textures     = new ID3D11Texture2D[1];
                mFrame.textures[0]  = Device.CreateTexture2D(textDesc[0]);
                context.CopySubresourceRegion(
                    mFrame.textures[0], 0, 0, 0, 0, // dst
                    VideoDecoder.textureFFmpeg, (uint) frame->data[1],  // src
                    cropBox); // crop decoder's padding
            }

            else if (curPSCase == PSCase.SwsScale)
            {
                mFrame.textures         = new ID3D11Texture2D[1];
                mFrame.srvs             = new ID3D11ShaderResourceView[1];

                sws_scale(VideoDecoder.swsCtx, frame->data.ToRawArray(), frame->linesize.ToArray(), 0, frame->height, VideoDecoder.swsData.ToRawArray(), VideoDecoder.swsLineSize.ToArray());

                subData[0].DataPointer  = VideoDecoder.swsData[0];
                subData[0].RowPitch     = (uint)VideoDecoder.swsLineSize[0];

                mFrame.textures[0]      = Device.CreateTexture2D(textDesc[0], subData);
                mFrame.srvs[0]          = Device.CreateShaderResourceView(mFrame.textures[0], srvDesc[0]);
            }

            else
            {
                mFrame.textures = new ID3D11Texture2D[VideoStream.PixelPlanes];
                mFrame.srvs     = new ID3D11ShaderResourceView[VideoStream.PixelPlanes];

                bool newRotationLinesize = false;
                for (int i = 0; i < VideoStream.PixelPlanes; i++)
                {
                    if (frame->linesize[i] < 0)
                    {
                        // Negative linesize for vertical flipping [TBR: might required for HW as well? (SwsScale does that)] http://ffmpeg.org/doxygen/trunk/structAVFrame.html#aa52bfc6605f6a3059a0c3226cc0f6567
                        newRotationLinesize     = true;
                        subData[0].RowPitch     = (uint)(-1 * frame->linesize[i]);
                        subData[0].DataPointer  = frame->data[i];
                        subData[0].DataPointer -= (nint)((subData[0].RowPitch * (VideoStream.Height - 1)));
                    }
                    else
                    {
                        newRotationLinesize     = false;
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

                if (newRotationLinesize != rotationLinesize)
                {
                    rotationLinesize = newRotationLinesize;
                    UpdateRotation(_RotationAngle);
                }
            }

            av_frame_unref(frame);
            return mFrame;
        }
        catch (Exception e)
        {
            av_frame_unref(frame);
            Log.Error($"Failed to process frame ({e.Message})");
            return null;
        }
    }

    void SetPS(string uniqueId, string sampleHLSL, List<string> defines = null)
    {
        if (curPSUniqueId == prevPSUniqueId)
            return;

        ShaderPS?.Dispose();
        ShaderPS = ShaderCompiler.CompilePS(Device, uniqueId, sampleHLSL, defines);
        context.PSSetShader(ShaderPS);
    }
}

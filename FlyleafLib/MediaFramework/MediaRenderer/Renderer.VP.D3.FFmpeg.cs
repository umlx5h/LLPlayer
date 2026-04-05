using Vortice.Direct3D11;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

using FlyleafLib.MediaFramework.MediaDecoder;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    internal AVBufferRef*           ffDevice, ffFrames;
    internal ID3D11Texture2D        ffTexture; // required from videostream?
    internal Texture2DDescription   ffTextureDesc;
    internal FFFramesInfo           ffFramesInfo = new();
    internal class FFFramesInfo { public AVCodecID CodecId; public int CodedWidth; public int CodedHeight; }

    void FFmpegSetup()
    {   /* Creates FFDevice which holds our Device/Context and VideoDevice/VideoContext (extra refs)
         * - Called during Device Setup (after D3Setup success)
         * - Locked by lockDevice (No lockCodecCtx is required we don't access Decoder here) */

        ffDevice = av_hwdevice_ctx_alloc(VideoDecoder.HW_DEVICE);
        if (ffDevice == null)
        {
            Log.Error("Failed to allocate FFmpeg HW device context");
            return;
        }

        var device_ctx          = (AVHWDeviceContext*)                  ffDevice->data;
        var hwCtx               = (AVD3D11VADeviceContext*)             device_ctx->hwctx;

        hwCtx->device           = (Flyleaf.FFmpeg.ID3D11Device*)        device. NativePointer;
        hwCtx->device_context   = (Flyleaf.FFmpeg.ID3D11DeviceContext*) context.NativePointer;
        hwCtx->video_device     = (Flyleaf.FFmpeg.ID3D11VideoDevice*)   vd.     NativePointer;
        hwCtx->video_context    = (Flyleaf.FFmpeg.ID3D11VideoContext*)  vc.     NativePointer;

        int ret = av_hwdevice_ctx_init(ffDevice);
        if (ret == 0)
        {   // FFmpeg refs
            device. AddRef();
            context.AddRef();
            vd.     AddRef();
            vc.     AddRef();
        }
        else
        {   // Should never fail (av_hwdevice_ctx_init will find all pointers set)
            Log.Error($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

            if (ffDevice != null)
            {
                fixed (AVBufferRef** ptr = &ffDevice)
                    av_buffer_unref(ptr);

                ffDevice = null;
            }
        }
    }

    internal bool ConfigHWFrames()
    {   /* (Re-)Allocates FFFrames -if required- and "extracts" the HW Texture Array (extra ref)
         * - Called by GetFormat
         * - Locked by lockCodecCtx */

        var codecCtx = VideoDecoder.CodecCtx;

        if (ffFrames != null)
        {
            if (NeedsHWFrames(codecCtx))
            {
                if (CanDebug) Log.Debug("Re-allocating HW frames");
                fixed(AVBufferRef** ptr = &ffFrames) av_buffer_unref(ptr);
                Frames.Dispose();
                ffTexture.Dispose();
            }
            else
            {
                if (CanDebug) Log.Debug("HW frames already allocated");
                return true;
            }
        }
        
        int ret;
       
        fixed(AVBufferRef** ptr = &ffFrames)
            if ((ret = avcodec_get_hw_frames_parameters(codecCtx, ffDevice, VideoDecoder.HW_PIX_FMT, ptr)) != 0)
            {
                ffFramesInfo.CodecId = AVCodecID.None;
                Log.Error($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");
                return false;
            }
            
        var framesCtx       = (AVHWFramesContext*)ffFrames->data;
        var reqSize         = framesCtx->initial_pool_size;
        var hwCtx           = (AVD3D11VAFramesContext *)framesCtx->hwctx;
        hwCtx->BindFlags   |= (uint)BindFlags.Decoder | (uint)BindFlags.ShaderResource;

        if ((ret = av_hwframe_ctx_init(ffFrames)) != 0)
        {
            ffFramesInfo.CodecId = AVCodecID.None;
            Log.Error($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");
            return false;
        }

        if (reqSize != framesCtx->initial_pool_size)
        {
            codecCtx->extra_hw_frames = codecCtx->extra_hw_frames - Math.Abs(reqSize - framesCtx->initial_pool_size);
            Log.Warn($"HW frames changed from {Config.Decoder.MaxVideoFrames} to {codecCtx->extra_hw_frames - 1}");
            Config.Decoder.SetMaxVideoFrames(codecCtx->extra_hw_frames - 1);
        }

        ffTexture = new((nint) hwCtx->texture);
        ffTexture.AddRef(); // FFmpeg ref
        ffTextureDesc = ffTexture.Description;

        ffFramesInfo.CodecId    = codecCtx->codec_id;
        ffFramesInfo.CodedWidth = codecCtx->coded_width;
        ffFramesInfo.CodedHeight= codecCtx->coded_height;

        if (CanDebug) Log.Debug($"HW frames allocated ({framesCtx->initial_pool_size})");

        return true;
    }

    bool NeedsHWFrames(AVCodecContext* codecCtx)
    {   // Checks whether we need to Re-Allocate FFFrames - Called by ConfigHWFrames (Helper)

        var codecId     = codecCtx->codec_id;
        var codedWidth  = codecCtx->coded_width;
        var codedHeight = codecCtx->coded_height;

        if (ffFramesInfo.CodecId        == codecId      &&
            ffFramesInfo.CodedWidth     == codedWidth   &&
            ffFramesInfo.CodedHeight    == codedHeight)
            return false;

        // https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/dxva2.c#L593
        int surface_alignment;

        if (     codecId == AVCodecID.Mpeg2video)
            surface_alignment = 32;
        else if (codecId == AVCodecID.Hevc || codecId == AVCodecID.Av1)
            surface_alignment = 128;
        else
            surface_alignment = 16;

        if (FFALIGN(codedWidth,  surface_alignment) != ffTextureDesc.Width ||
            FFALIGN(codedHeight, surface_alignment) != ffTextureDesc.Height)
            return true;

        int num_surfaces = 1;
        if (     codecId == AVCodecID.H264|| codecId == AVCodecID.Hevc)
            num_surfaces += 16;
        else if (codecId == AVCodecID.Vp9 || codecId == AVCodecID.Av1)
            num_surfaces += 8;
        else
            num_surfaces += 2;

        if (num_surfaces + Config.Decoder.MaxVideoFrames + 1 != ffTextureDesc.ArraySize)
            return true;

        return false;
    }

    void FFmpegDispose()
    {
        if (ffFrames != null)
        {
            av_buffer_unref(ref ffFrames);
            ffFrames = null;
        }

        if (ffTexture != null)
        {
            ffTexture.Dispose();
            ffTexture = null;
        }

        if (ffDevice != null)
        {
            fixed(AVBufferRef** ptr = &ffDevice)
                av_buffer_unref(ptr);

            ffDevice = null;
        }
            
    }
}

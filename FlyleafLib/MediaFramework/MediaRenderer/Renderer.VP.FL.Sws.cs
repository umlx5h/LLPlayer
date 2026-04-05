using Vortice.Direct3D;
using Vortice.DXGI;

using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    AVFrame*        swsFrame;
    SwsContext*     swsCtx;

    bool SwsConfig()
    {   // Sws Color Convert (no scalling) | Visible dimensions (still needs manual user's + stream's crop)
        SwsDispose();

        var codecCtx = VideoDecoder.CodecCtx;

        swsFrame = av_frame_alloc();
        swsFrame->format= (int)AVPixelFormat.Rgba;
        swsFrame->width = codecCtx->width;
        swsFrame->height= codecCtx->height;
        _ = av_frame_get_buffer(swsFrame, 0);

        swsCtx = sws_getContext(
            swsFrame->width,
            swsFrame->height,
            VideoDecoder.VideoAccelerated ? ((AVHWFramesContext*)ffFrames->data)->sw_format : codecCtx->pix_fmt,
            swsFrame->width,
            swsFrame->height,
            AVPixelFormat.Rgba, SwsFlags.None, null, null, null);
        
        if (swsCtx == null)
        {
            if (swsFrame != null)
            {
                fixed(AVFrame** ptr = &swsFrame) av_frame_free(ptr);
                swsFrame = null;
            }
            
            Log.Error($"Failed to allocate SwsContext");

            return false;
        }

        FillPlanes  = VideoDecoder.VideoAccelerated ? SwsHWFillPlanes : SwsSWFillPlanes;
        psCase      = PSCase.SwsScale;

        txtDesc[0].Width   = (uint)swsFrame->width;
        txtDesc[0].Height  = (uint)swsFrame->height;
        txtDesc[0].Format  = srvDesc[0].Format = Format.R8G8B8A8_UNorm;
        srvDesc[0].ViewDimension = ShaderResourceViewDimension.Texture2D;

        if (VideoProcessor != VideoProcessors.D3D11)
        {   // We don't render with FL/Sws no need to set this with D3
            psId += "rgb";
            SetPS(psId, @"color = float4(Texture1.Sample(Sampler, input.Texture).rgb, 1.0);", defines);
        }
        
        return true;
    }

    VideoFrame SwsHWFillPlanes(ref AVFrame* hwframe)
    {   // TBR: av_frame_unref(hwframe);? | we don't need extra frames here since we transfer to sw directly (might use this only for VP switch?)

        if (hwframe->data[0] != ffTexture.NativePointer)
        {
            Log.Error($"[V] Frame Dropped (Invalid HW Texture Pointer)");
            av_frame_unref(hwframe);
            return null;
        }

        VideoFrame mFrame = new()
        {
            AVFrame     = hwframe,
            Timestamp   = (long)(hwframe->pts * scfg.Timebase) - VideoDecoder.Demuxer.StartTime
        };

        var frame   = av_frame_alloc();
        int ret     = av_hwframe_transfer_data(frame, hwframe, 0);
        ret         = av_frame_copy_props(frame, hwframe);
        hwframe     = av_frame_alloc();

        SwsFillPlanesHelper(mFrame, frame);
        av_frame_free(&frame);
        return mFrame;
    }
    VideoFrame SwsSWFillPlanes(ref AVFrame* frame)
    {
        VideoFrame mFrame = new()
        {
            Timestamp   = (long)(frame->pts * scfg.Timebase) - VideoDecoder.Demuxer.StartTime
        };

        SwsFillPlanesHelper(mFrame, frame);
        av_frame_unref(frame);
        return mFrame;
    }
    VideoFrame SwsFillPlanesHelper(VideoFrame mFrame, AVFrame* frame)
    {
        int ret = sws_scale(swsCtx,
            frame->data.        ToRawArray(),
            frame->linesize.    ToArray(),
            0,
            swsFrame->height,
            swsFrame->data.     ToRawArray(),
            swsFrame->linesize. ToArray());

        subData[0].DataPointer  = swsFrame->data[0];
        subData[0].RowPitch     = (uint)swsFrame->linesize[0];

        mFrame.Texture  = [device.CreateTexture2D(txtDesc[0], subData)];
        mFrame.SRV      = [device.CreateShaderResourceView(mFrame.Texture[0], srvDesc[0])];

        return mFrame;
    }

    void SwsDispose()
    {
        if (swsFrame != null)
        {
            av_frame_free(ref swsFrame);
            swsFrame = null;
        }

        if (swsCtx != null)
        {
            sws_freeContext(swsCtx);
            swsCtx = null;
        }
    }
}

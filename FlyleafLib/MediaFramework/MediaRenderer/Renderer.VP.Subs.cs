using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{   // TODO: Separate class?
    SubtitlesFrame              subsFrame;
    ID3D11Texture2D             subsTxt;
    Texture2DDescription        subsTxtDesc = new()
    {
        Usage               = ResourceUsage.Default,
        Width               = 0,
        Height              = 0,
        Format              = Format.B8G8R8A8_UNorm,
        ArraySize           = 1,
        MipLevels           = 1,
        BindFlags           = BindFlags.ShaderResource,
        SampleDescription   = new(1, 0)
    };
    ID3D11ShaderResourceView[]  subsSRV = new ID3D11ShaderResourceView[1];
    RectI                       subsRect;
    SizeI                       subsSize;
    Size                        subsLastViewport;
    Viewport                    subsViewport;   
    float                       subsRatioX, subsRatioY;

    internal void SubsConfig(int width, int height)
    {
        SubsDispose();
        subsSize = new(width, height);
    }

    internal void SubsFillRects(SubtitlesFrame frame)
    {
        SubsDispose();
        subsFrame = frame;
    }

    void FLSubsRender()
    {
        if (subsFrame == null)
            return;

        SubsRender();
        context.PSSetShader(psShader[psId]);
        
    }
    void D3SubsRender()
    {
        if (subsFrame == null)
            return;

        context.OMSetRenderTargets(SwapChain.BackBufferRtv);
        SubsRender();
    }
    void SubsRender()
    {
        Viewport view = Viewport;        
        if (subsLastViewport.Width != view.Width || subsLastViewport.Height != view.Height)
            SubsScale();

        context.RSSetViewport(subsViewport);
        context.PSSetSampler(0, samplerPoint);
        context.VSSetShader(vsSimple);
        context.OMSetBlendState(blendStateAlpha);
        context.PSSetShaderResources(0, subsSRV);
        context.PSSetShader(psShader["rgba"]);
        context.Draw(6, 0);

        context.OMSetBlendState(null);
        context.RSSetViewport(Viewport);
        context.VSSetShader(vsMain);
        context.PSSetSampler(0, samplerLinear);
    }

    void SubsScale()
    {
        AVFrame*    swsFrame;
        SwsContext* swsCtx;
        Viewport    view = Viewport;

        var rect            = subsFrame.sub.rects[0];
        subsRect            = new(rect->x, rect->y, rect->w, rect->h);
        subsLastViewport    = new(view.Width, Viewport.Height);
        subsRatioX          = subsLastViewport.Width    / subsSize.Width;
        subsRatioY          = subsLastViewport.Height   / subsSize.Height;
        subsTxtDesc.Width   = (uint)(subsRect.Width     * subsRatioX);
        subsTxtDesc.Height  = (uint)(subsRect.Height    * subsRatioY);
        subsViewport        = new(view.X + subsRect.X * subsRatioX, view.Y + subsRect.Y * subsRatioY, subsTxtDesc.Width, subsTxtDesc.Height);

        swsFrame = av_frame_alloc();
        swsFrame->format= (int)AVPixelFormat.Rgba;
        swsFrame->width = (int)subsTxtDesc.Width;
        swsFrame->height= (int)subsTxtDesc.Height;
        _ = av_frame_get_buffer(swsFrame, 0);

        swsCtx = sws_getContext(
            rect->w,
            rect->h,
            AVPixelFormat.Pal8,
            (int)subsTxtDesc.Width,
            (int)subsTxtDesc.Height,
            AVPixelFormat.Rgba, ucfg.BitmapSubsScaleQuality, null, null, null);

        int ret = sws_scale(swsCtx,
            rect->data.         ToRawArray(),
            rect->linesize.     ToArray(),
            0,
            rect->h,
            swsFrame->data.     ToRawArray(),
            swsFrame->linesize. ToArray());

        subsTxt     = device.CreateTexture2D(subsTxtDesc, [new SubresourceData() { DataPointer = swsFrame->data[0], RowPitch = (uint)swsFrame->linesize[0] }]);
        subsSRV[0]  = device.CreateShaderResourceView(subsTxt);

        av_frame_free(&swsFrame);
        sws_freeContext(swsCtx);
    }

    internal void SubsDispose()
    {
        if (subsFrame == null)
            return;

        SubtitlesDecoder.DisposeFrame(subsFrame);
        subsFrame = null;
        subsLastViewport.Width = 0;

        if (subsTxt != null)
        {
            subsSRV[0]?.Dispose(); subsSRV[0]   = null;
            subsTxt.    Dispose(); subsTxt      = null;
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

using Vortice.DXGI;
using Vortice.Direct3D11;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;

using static FlyleafLib.Logger;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    bool    isPresenting;
    long    lastPresentAt       = 0;
    long    lastPresentRequestAt= 0;
    object  lockPresentTask     = new();

    public bool Present(VideoFrame frame, bool forceWait = true)
    {
        if (Monitor.TryEnter(lockDevice, frame.timestamp == 0 ? 100 : 5)) // Allow more time for first frame
        {
            try
            {
                PresentInternal(frame, forceWait);
                VideoDecoder.DisposeFrame(LastFrame);
                LastFrame = frame;

                if (child != null)
                    child.LastFrame = frame;

                return true;

            }
            catch (Exception e)
            {
                if (CanWarn) Log.Warn($"Present frame failed {e.Message} | {Device?.DeviceRemovedReason}");
                VideoDecoder.DisposeFrame(frame);

                vpiv?.Dispose();

                return false;

            }
            finally
            {
                Monitor.Exit(lockDevice);
            }
        }

        if (CanDebug) Log.Debug("Dropped Frame - Lock timeout " + (frame != null ? Utils.TicksToTime(frame.timestamp) : ""));
        VideoDecoder.DisposeFrame(frame);

        return false;
    }
    public void Present()
    {
        if (SCDisposed)
            return;

        // NOTE: We don't have TimeBeginPeriod, FpsForIdle will not be accurate
        lock (lockPresentTask)
        {
            if ((Config.Player.player == null || !Config.Player.player.requiresBuffering) && VideoDecoder.IsRunning && (VideoStream == null || VideoStream.FPS > 10)) // With slow FPS we need to refresh as fast as possible
                return;

            if (isPresenting)
            {
                lastPresentRequestAt = DateTime.UtcNow.Ticks;
                return;
            }

            isPresenting = true;
        }

        Task.Run(() =>
        {
            long presentingAt;
            do
            {
                long sleepMs = DateTime.UtcNow.Ticks - lastPresentAt;
                sleepMs = sleepMs < (long)(1.0 / Config.Player.IdleFps * 1000 * 10000) ? (long) (1.0 / Config.Player.IdleFps * 1000) : 0;
                if (sleepMs > 2)
                    Thread.Sleep((int)sleepMs);

                presentingAt = DateTime.UtcNow.Ticks;
                RefreshLayout();
                lastPresentAt = DateTime.UtcNow.Ticks;

            } while (lastPresentRequestAt > presentingAt);

            isPresenting = false;
        });
    }
    internal void PresentInternal(VideoFrame frame, bool forceWait = true)
    {
        if (SCDisposed)
            return;

        // TBR: Replica performance issue with D3D11 (more zoom more gpu overload)
        if (frame.srvs == null) // videoProcessor can be FlyleafVP but the player can send us a cached frame from prev videoProcessor D3D11VP (check frame.srv instead of videoProcessor)
        {
            if (frame.avFrame != null)
            {
                vpivd.Texture2D.ArraySlice = (uint) frame.avFrame->data[1];
                vd1.CreateVideoProcessorInputView(VideoDecoder.textureFFmpeg, vpe, vpivd, out vpiv);
            }
            else
            {
                vpivd.Texture2D.ArraySlice = 0;
                vd1.CreateVideoProcessorInputView(frame.textures[0], vpe, vpivd, out vpiv);
            }

            vpsa[0].InputSurface = vpiv;
            vc.VideoProcessorBlt(vp, vpov, 0, 1, vpsa);
            swapChain.Present(Config.Video.VSync, forceWait ? PresentFlags.None : Config.Video.PresentFlags);

            vpiv.Dispose();
        }
        else
        {
            context.OMSetRenderTargets(backBufferRtv);
            context.ClearRenderTargetView(backBufferRtv, Config.Video._BackgroundColor);
            context.RSSetViewport(GetViewport);
            context.PSSetShaderResources(0, frame.srvs);
            context.Draw(6, 0);

            if (overlayTexture != null)
            {
                // Don't stretch the overlay (reduce height based on ratiox) | Sub's stream size might be different from video size (fix y based on percentage)
                var ratiox = (double)GetViewport.Width / overlayTextureOriginalWidth;
                var ratioy = (double)overlayTextureOriginalPosY / overlayTextureOriginalHeight;

                context.OMSetBlendState(blendStateAlpha);
                context.PSSetShaderResources(0, overlayTextureSRVs);
                context.RSSetViewport((float) (GetViewport.X + (overlayTextureOriginalPosX * ratiox)), (float) (GetViewport.Y + (GetViewport.Height * ratioy)), (float) (overlayTexture.Description.Width * ratiox), (float) (overlayTexture.Description.Height * ratiox));
                context.PSSetShader(ShaderBGRA);
                context.Draw(6, 0);

                // restore context
                context.PSSetShader(ShaderPS);
                context.OMSetBlendState(curPSCase == PSCase.RGBPacked ? blendStateAlpha : null);
            }

            swapChain.Present(Config.Video.VSync, forceWait ? PresentFlags.None : Config.Video.PresentFlags);
        }

        child?.PresentInternal(frame);
    }

    public void ClearOverlayTexture()
    {
        if (overlayTexture == null)
            return;

        overlayTexture?.Dispose();
        overlayTextureSrv?.Dispose();
        overlayTexture      = null;
        overlayTextureSrv   = null;
    }

    internal void CreateOverlayTexture(SubtitlesFrame frame, int streamWidth, int streamHeight)
    {
        var rect    = frame.sub.rects[0];
        var stride  = rect->linesize[0] * 4;

        overlayTextureOriginalWidth = streamWidth;
        overlayTextureOriginalHeight= streamHeight;
        overlayTextureOriginalPosX  = rect->x;
        overlayTextureOriginalPosY  = rect->y;
        overlayTextureDesc.Width    = (uint)rect->w;
        overlayTextureDesc.Height   = (uint)rect->h;

        byte[] data = new byte[rect->w * rect->h * 4];

        fixed(byte* ptr = data)
        {
            uint[] colors   = new uint[256];
            var colorsData  = new Span<uint>((byte*)rect->data[1], rect->nb_colors);

            for (int i = 0; i < colorsData.Length; i++)
                colors[i] = colorsData[i];

            ConvertPal(colors, 256, false);

            for (int y = 0; y < rect->h; y++)
            {
                uint* xout =(uint*) (ptr + y * stride);
                byte* xin = ((byte*)rect->data[0]) + y * rect->linesize[0];

                for (int x = 0; x < rect->w; x++)
                    *xout++ = colors[*xin++];
            }

            SubresourceData subData = new()
            {
                DataPointer = (nint)ptr,
                RowPitch    = (uint)stride
            };

            overlayTexture?.Dispose();
            overlayTextureSrv?.Dispose();
            overlayTexture          = Device.CreateTexture2D(overlayTextureDesc, new SubresourceData[] { subData });
            overlayTextureSrv       = Device.CreateShaderResourceView(overlayTexture);
            overlayTextureSRVs[0]   = overlayTextureSrv;
        }
    }

    static void ConvertPal(uint[] colors, int count, bool gray) // subs bitmap (source: mpv)
    {
        for (int n = 0; n < count; n++)
        {
            uint c = colors[n];
            uint b = c & 0xFF;
            uint g = (c >> 8) & 0xFF;
            uint r = (c >> 16) & 0xFF;
            uint a = (c >> 24) & 0xFF;

            if (gray)
                r = g = b = (r + g + b) / 3;

            // from straight to pre-multiplied alpha
            b = b * a / 255;
            g = g * a / 255;
            r = r * a / 255;
            colors[n] = b | (g << 8) | (r << 16) | (a << 24);
        }
    }

    public void RefreshLayout()
    {
        if (Monitor.TryEnter(lockDevice, 5))
        {
            try
            {
                if (SCDisposed)
                    return;

                if (LastFrame != null && (LastFrame.textures != null || LastFrame.avFrame != null))
                    PresentInternal(LastFrame);
                else if (Config.Video.ClearScreen)
                {
                    context.ClearRenderTargetView(backBufferRtv, Config.Video._BackgroundColor);
                    swapChain.Present(Config.Video.VSync, PresentFlags.None);
                }
            }
            catch (Exception e)
            {
                if (CanWarn) Log.Warn($"Present idle failed {e.Message} | {Device.DeviceRemovedReason}");
            }
            finally
            {
                Monitor.Exit(lockDevice);
            }
        }
    }
    public void ClearScreen()
    {
        ClearOverlayTexture();
        VideoDecoder.DisposeFrame(LastFrame);
        Present();
    }
}

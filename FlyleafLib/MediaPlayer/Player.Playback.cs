using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;
using static FlyleafLib.Utils;
using static FlyleafLib.Logger;

namespace FlyleafLib.MediaPlayer;

partial class Player
{
    string stoppedWithError = null;

    /// <summary>
    /// Fires on playback stopped by an error or completed / ended successfully <see cref="Status"/>
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler<PlaybackStoppedArgs> PlaybackStopped;
    protected virtual void OnPlaybackStopped(string error = null)
    {
        if (error != null && LastError == null)
        {
            lastError = error;
            UI(() => LastError = LastError);
        }

        PlaybackStopped?.Invoke(this, new PlaybackStoppedArgs(error));
    }

    /// <summary>
    /// Fires on seek completed for the specified ms (ms will be -1 on failure)
    /// </summary>
    public event EventHandler<int> SeekCompleted;

    /// <summary>
    /// Plays AVS streams
    /// </summary>
    public void Play()
    {
        lock (lockActions)
        {
            if (!CanPlay || Status == Status.Playing || Status == Status.Ended)
                return;

            status = Status.Playing;
            UI(() => Status = Status);
        }

        while (taskPlayRuns || taskSeekRuns) Thread.Sleep(5);
        taskPlayRuns = true;

        Thread t = new(() =>
        {
            try
            {
                Engine.TimeBeginPeriod1();
                NativeMethods.SetThreadExecutionState(NativeMethods.EXECUTION_STATE.ES_CONTINUOUS | NativeMethods.EXECUTION_STATE.ES_SYSTEM_REQUIRED | NativeMethods.EXECUTION_STATE.ES_DISPLAY_REQUIRED);

                onBufferingStarted   = 0;
                onBufferingCompleted = 0;
                requiresBuffering    = true;

                if (LastError != null)
                {
                    lastError = null;
                    UI(() => LastError = LastError);
                }

                if (Config.Player.Usage == Usage.Audio || !Video.IsOpened)
                    ScreamerAudioOnly();
                else
                {
                    if (ReversePlayback)
                    {
                        shouldFlushNext = true;
                        ScreamerReverse();
                    }
                    else
                    {
                        shouldFlushPrev = true;
                        Screamer();
                    }

                }

            }
            catch (Exception ex)
            {
                Log.Error($"Playback failed ({ex.Message})");
                RaiseUnknownErrorOccurred($"Playback failed: {ex.Message}", UnknownErrorType.Playback, ex);
            }
            finally
            {
                VideoDecoder.DisposeFrame(vFrame);
                vFrame = null;
                for (int i = 0; i < subNum; i++)
                {
                    sFrames[i] = null;
                }

                if (Status == Status.Stopped)
                    decoder?.Initialize();
                else if (decoder != null)
                {
                    decoder.PauseOnQueueFull();
                    decoder.PauseDecoders();
                }

                Audio.ClearBuffer();
                Engine.TimeEndPeriod1();
                NativeMethods.SetThreadExecutionState(NativeMethods.EXECUTION_STATE.ES_CONTINUOUS);
                stoppedWithError = null;

                if (IsPlaying)
                {
                    if (decoderHasEnded)
                        status = Status.Ended;
                    else
                    {
                        if (Video.IsOpened && VideoDemuxer.Interrupter.Timedout)
                            stoppedWithError = "Timeout";
                        else if (onBufferingStarted - 1 == onBufferingCompleted)
                        {
                            stoppedWithError = "Playback stopped unexpectedly";
                            OnBufferingCompleted("Buffering failed");
                        }
                        else
                        {
                            if (!ReversePlayback)
                            {
                                if (isLive || Math.Abs(Duration - CurTime) > 3 * 1000 * 10000)
                                    stoppedWithError = "Playback stopped unexpectedly";
                            }
                            else if (CurTime > 3 * 1000 * 10000)
                                stoppedWithError = "Playback stopped unexpectedly";
                        }

                        status = Status.Paused;
                    }
                }

                OnPlaybackStopped(stoppedWithError);
                if (CanDebug) Log.Debug($"[SCREAMER] Finished (Status: {Status}, Error: {stoppedWithError})");

                UI(() =>
                {
                    Status = Status;
                    UpdateCurTime();
                });

                taskPlayRuns = false;
            }
        });
        t.Priority = Config.Player.ThreadPriority;
        t.Name = $"[#{PlayerId}] Playback";
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>
    /// Pauses AVS streams
    /// </summary>
    public void Pause()
    {
        lock (lockActions)
        {
            if (!CanPlay || Status == Status.Ended)
                return;

            status = Status.Paused;
            UI(() => Status = Status);

            while (taskPlayRuns) Thread.Sleep(5);
        }
    }

    public void TogglePlayPause()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    public void ToggleReversePlayback()
        => ReversePlayback = !ReversePlayback;

    public void ToggleLoopPlayback()
        => LoopPlayback = !LoopPlayback;

    /// <summary>
    /// Seeks backwards or forwards based on the specified ms to the nearest keyframe
    /// </summary>
    /// <param name="ms"></param>
    /// <param name="forward"></param>
    public void Seek(int ms, bool forward = false) => Seek(ms, forward, false);

    /// <summary>
    /// Seeks at the exact timestamp (with half frame distance accuracy)
    /// </summary>
    /// <param name="time"></param>
    /// <param name="subIndex"></param>
    public void SeekAccurate(TimeSpan time, int subIndex = -1)
    {
        int ms = (int)time.TotalMilliseconds;
        if (subIndex != -1 && time.Microseconds > 0)
        {
            // When seeking subtitles, rounding down the milliseconds or less will cause the previous subtitle
            // ASR subtitles are in microseconds, so if you truncate, you'll end up one before the current subtitle.
            ms += 1;
        }

        SeekAccurate(ms, subIndex);
    }

    /// <summary>
    /// Seeks at the exact timestamp (with half frame distance accuracy)
    /// </summary>
    /// <param name="ms"></param>
    /// <param name="subIndex"></param>
    public void SeekAccurate(int ms, int subIndex = -1)
    {
        if (subIndex >= 0)
        {
            ms += (int)(Config.Subtitles[subIndex].Delay / 10000);
        }

        Seek(ms, false, !IsLive);
    }

    public void ToggleSeekAccurate()
        => Config.Player.SeekAccurate = !Config.Player.SeekAccurate;

    private void Seek(int ms, bool forward, bool accurate)
    {
        if (!CanPlay)
            return;

        lock (seeks)
        {
            _CurTime = curTime = ms * (long)10000;
            seeks.Push(new SeekData(ms, forward, accurate));
        }

        // Set timestamp to Manager immediately after seek to enable continuous seek
        // (Without this, seek is called with the same timestamp on successive calls, so it will seek to the same subtitle.)
        SubtitlesManager.SetCurrentTime(new TimeSpan(curTime));

        Raise(nameof(CurTime));
        Raise(nameof(RemainingDuration));

        if (Status == Status.Playing)
            return;

        lock (seeks)
        {
            if (taskSeekRuns)
                return;

            taskSeekRuns = true;
        }

        Task.Run(() =>
        {
            int ret;
            bool wasEnded = false;
            SeekData seekData = null;

            try
            {
                Engine.TimeBeginPeriod1();

                while (true)
                {
                    lock (seeks)
                    {
                        if (!(seeks.TryPop(out seekData) && CanPlay && !IsPlaying))
                        {
                            taskSeekRuns = false;
                            break;
                        }

                        seeks.Clear();
                    }

                    if (Status == Status.Ended)
                    {
                        wasEnded = true;
                        status = Status.Paused;
                        UI(() => Status = Status);
                    }

                    for (int i = 0; i < subNum; i++)
                    {
                        // Display subtitles from cache when seeking while paused
                        bool display = false;
                        var cur = SubtitlesManager[i].GetCurrent();
                        if (cur != null)
                        {
                            if (!string.IsNullOrEmpty(cur.DisplayText))
                            {
                                SubtitleDisplay(cur.DisplayText, i, cur.UseTranslated);
                                display = true;
                            }
                            else if (cur.IsBitmap && cur.Bitmap != null)
                            {
                                SubtitleDisplay(cur.Bitmap, i);
                                display = true;
                            }

                            if (display)
                            {
                                sFramesPrev[i] = new SubtitlesFrame
                                {
                                    timestamp = cur.StartTime.Ticks + Config.Subtitles[i].Delay,
                                    duration = (uint)cur.Duration.TotalMilliseconds,
                                    text = cur.DisplayText
                                };
                            }
                        }

                        // clear subtitles
                        // but do not clear when cache hit
                        if (!display && sFramesPrev[i] != null)
                        {
                            sFramesPrev[i] = null;
                            SubtitleClear(i);
                        }
                    }

                    if (!Video.IsOpened)
                    {
                        if (AudioDecoder.OnVideoDemuxer)
                        {
                            ret = decoder.Seek(seekData.ms, seekData.forward);
                            if (CanWarn && ret < 0)
                                Log.Warn("Seek failed 2");

                            VideoDemuxer.Start();
                            SeekCompleted?.Invoke(this, -1);
                        }
                        else
                        {
                            ret = decoder.SeekAudio(seekData.ms, seekData.forward);
                            if (CanWarn && ret < 0)
                                Log.Warn("Seek failed 3");

                            AudioDemuxer.Start();
                            SeekCompleted?.Invoke(this, -1);
                        }

                        decoder.PauseOnQueueFull();
                        SeekCompleted?.Invoke(this, seekData.ms);
                    }
                    else
                    {
                        decoder.PauseDecoders();
                        ret = decoder.Seek(seekData.accurate ? Math.Max(0, seekData.ms - (int) new TimeSpan(Config.Player.SeekAccurateFixMargin).TotalMilliseconds) : seekData.ms, seekData.forward, !seekData.accurate); // 3sec ffmpeg bug for seek accurate when fails to seek backwards (see videodecoder getframe)
                        if (ret < 0)
                        {
                            if (CanWarn) Log.Warn("Seek failed");
                            SeekCompleted?.Invoke(this, -1);
                        }
                        else if (!ReversePlayback && CanPlay)
                        {
                            decoder.GetVideoFrame(seekData.accurate ? seekData.ms * (long)10000 : -1);
                            ShowOneFrame();
                            VideoDemuxer.Start();
                            AudioDemuxer.Start();
                            //for (int i = 0; i < subNum; i++)
                            //{
                            //    SubtitlesDemuxers[i].Start();
                            //}
                            DataDemuxer.Start();
                            decoder.PauseOnQueueFull();
                            SeekCompleted?.Invoke(this, seekData.ms);
                        }
                    }

                    Thread.Sleep(20);
                }
            }
            catch (Exception e)
            {
                lock (seeks) taskSeekRuns = false;
                Log.Error($"Seek failed ({e.Message})");
            }
            finally
            {
                decoder.OpenedPlugin?.OnBufferingCompleted();
                Engine.TimeEndPeriod1();
                if ((wasEnded && Config.Player.AutoPlay) || stoppedWithError != null) // TBR: Possible race condition with if (Status == Status.Playing)
                    Play();
            }
        });
    }

    /// <summary>
    /// Flushes the buffer (demuxers (packets) and decoders (frames))
    /// This is useful mainly for live streams to push the playback at very end (low latency)
    /// </summary>
    public void Flush()
    {
        decoder.Flush();
        OSDMessage = "Buffer Flushed";
    }

    /// <summary>
    /// Stops and Closes AVS streams
    /// </summary>
    public void Stop()
    {
        lock (lockActions)
        {
            Initialize();
            renderer?.Flush();
        }
    }
    public void SubtitleClear()
    {
        for (int i = 0; i < subNum; i++)
        {
            SubtitleClear(i);
        }
    }

    public void SubtitleClear(int subIndex)
    {
        Subtitles[subIndex].Data.Clear();
        //renderer.ClearOverlayTexture();
    }

    /// <summary>
    /// Updated text format subtitle display
    /// </summary>
    /// <param name="text"></param>
    /// <param name="subIndex"></param>
    /// <param name="isTranslated"></param>
    public void SubtitleDisplay(string text, int subIndex, bool isTranslated)
    {
        UI(() =>
        {
            Subtitles[subIndex].Data.IsTranslated = isTranslated;
            Subtitles[subIndex].Data.Language = isTranslated
                ? Config.Subtitles.TranslateLanguage
                : SubtitlesManager[subIndex].Language;

            Subtitles[subIndex].Data.Text = text;
            Subtitles[subIndex].Data.Bitmap = null;
        });
    }

    public void SubtitleDisplay(SubtitleBitmapData bitmapData, int subIndex)
    {
        if (bitmapData.Sub.num_rects == 0)
        {
            return;
        }

        (byte[] data, AVSubtitleRect rect) = bitmapData.SubToBitmap(false);

        SubtitlesFrameBitmap bitmap = new()
        {
            data = data,
            width = rect.w,
            height = rect.h,
            x = rect.x,
            y = rect.y,
        };

        SubtitleDisplay(bitmap, subIndex);
    }

    /// <summary>
    /// Update bitmap format subtitle display
    /// </summary>
    /// <param name="bitmap"></param>
    /// <param name="subIndex"></param>
    public void SubtitleDisplay(SubtitlesFrameBitmap bitmap, int subIndex)
    {
        // TODO: L: refactor

        // Each subtitle has a different size and needs to be generated each time.
        WriteableBitmap wb = new(
            bitmap.width, bitmap.height,
            NativeMethods.DpiXSource, NativeMethods.DpiYSource,
            PixelFormats.Bgra32, null
        );
        Int32Rect rect = new(0, 0, bitmap.width, bitmap.height);
        wb.Lock();

        Marshal.Copy(bitmap.data, 0, wb.BackBuffer, bitmap.data.Length);

        wb.AddDirtyRect(rect);
        wb.Unlock();
        // Note that you will get a UI thread error if you don't call
        wb.Freeze();

        int x = bitmap.x;
        int y = bitmap.y;
        int w = bitmap.width;
        int h = bitmap.height;

        SubsBitmap subsBitmap = new()
        {
            X = x,
            Y = y,
            Width = w,
            Height = h,
            Source = wb,
        };

        UI(() =>
        {
            Subtitles[subIndex].Data.Bitmap = subsBitmap;
            Subtitles[subIndex].Data.Text = "";
        });
    }
}

public class PlaybackStoppedArgs : EventArgs
{
    public string   Error       { get; }
    public bool     Success     { get; }

    public PlaybackStoppedArgs(string error)
    {
        Error   = error;
        Success = Error == null;
    }
}

class SeekData
{
    public int  ms;
    public bool forward;
    public bool accurate;
    public SeekData(int ms, bool forward, bool accurate)
        { this.ms = ms; this.forward = forward && !accurate; this.accurate = accurate; }
}

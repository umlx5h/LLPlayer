﻿using System.Diagnostics;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaPlayer;

unsafe partial class Player
{
    /// <summary>
    /// Fires on Data frame when it's supposed to be shown according to the stream
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler<DataFrame> OnDataFrame;

    /// <summary>
    /// Fires on buffering started
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler BufferingStarted;
    protected virtual void OnBufferingStarted()
    {
        if (onBufferingStarted != onBufferingCompleted) return;
        BufferingStarted?.Invoke(this, new EventArgs());
        onBufferingStarted++;

        if (CanDebug) Log.Debug($"OnBufferingStarted");
    }

    /// <summary>
    /// Fires on buffering completed (will fire also on failed buffering completed)
    /// (BufferDration > Config.Player.MinBufferDuration)
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler<BufferingCompletedArgs> BufferingCompleted;
    protected virtual void OnBufferingCompleted(string error = null)
    {
        if (onBufferingStarted - 1 != onBufferingCompleted) return;

        if (error != null && lastError == null)
        {
            lastError = error;
            UI(() => LastError = lastError);
        }

        BufferingCompleted?.Invoke(this, new BufferingCompletedArgs(error));
        onBufferingCompleted++;
        if (CanDebug) Log.Debug($"OnBufferingCompleted{(error != null ? $" (Error: {error})" : "")}");
    }

    long    onBufferingStarted;
    long    onBufferingCompleted;

    Stopwatch sw = new();

    private void ShowOneFrame()
    {
        for (int i = 0; i < subNum; i++)
        {
            sFrames[i] = null;
        }
        if (!vFrames.TryDequeue(out var vFrame))
            return;

        Log.Error($"ShowOneFrame #{vFrame.Id}");
        Renderer.RenderRequest(vFrame);

        UpdateCurTime(vFrame.Timestamp);
        showFrameCount++;

        for (int i = 0; i < subNum; i++)
        {
            // Prevents blinking after seek
            if (sFramesPrev[i] != null)
            {
                var cur = SubtitlesManager[i].GetCurrent();
                if (cur != null)
                {
                    if (!string.IsNullOrEmpty(cur.DisplayText) || (cur.IsBitmap && cur.Bitmap != null))
                    {
                        continue;
                    }
                }
            }

            // Clear last subtitles text if video timestamp is not within subs timestamp + duration (to prevent clearing current subs on pause/play)
            if (sFramesPrev[i] == null || sFramesPrev[i].Timestamp > vFrame.Timestamp || (sFramesPrev[i].Timestamp + (sFramesPrev[i].duration * (long)10000)) < vFrame.Timestamp)
            {
                sFramesPrev[i] = null;
                SubtitleClear(i);
            }
        }

        // Required for buffering on paused
        if (decoder.RequiresResync && !IsPlaying && seeks.IsEmpty)
            decoder.Resync(vFrame.Timestamp);
    }

    private void AudioBuffer()
    {
        if (CanTrace) Log.Trace("Buffering");

        while ((isVideoSwitch || isAudioSwitch) && IsPlaying)
            Thread.Sleep(10);

        if (!IsPlaying)
            return;

        aFrame = null;
        Audio.ClearBuffer();
        decoder.AudioStream.Demuxer.Start();
        AudioDecoder.Start();

        while(AudioDecoder.Frames.IsEmpty && IsPlaying && AudioDecoder.IsRunning)
            Thread.Sleep(10);

        AudioDecoder.Frames.TryPeek(out aFrame);

        if (aFrame == null)
            return;

        UpdateCurTime(aFrame.Timestamp, false);

        while(seeks.IsEmpty && decoder.AudioStream.Demuxer.BufferedDuration < Config.Player.MinBufferDuration && AudioDecoder.Frames.Count < Config.Decoder.MaxAudioFrames / 2 && IsPlaying && decoder.AudioStream.Demuxer.IsRunning && decoder.AudioStream.Demuxer.Status != MediaFramework.Status.QueueFull)
            Thread.Sleep(20);
    }
    private void ScreamerAudioOnly()
    {   // TODO: Needs Recoding (old code)
        long bufferedDuration = 0;

        while (IsPlaying)
        {
            if (seeks.TryPop(out var seekData))
            {
                seeks.Clear();
                requiresBuffering = true;

                if (AudioDecoder.OnVideoDemuxer)
                {
                    if (decoder.Seek(seekData.ms, seekData.forward) < 0)
                        Log.Warn("Seek failed 1");
                }
                else
                {
                    if (decoder.SeekAudio(seekData.ms, seekData.forward) < 0)
                        Log.Warn("Seek failed 2");
                }
            }

            if (requiresBuffering)
            {
                OnBufferingStarted();
                AudioBuffer();
                requiresBuffering = false;

                if (!seeks.IsEmpty)
                    continue;

                if (!IsPlaying || AudioDecoder.Frames.IsEmpty)
                    break;

                OnBufferingCompleted();
            }

            if (AudioDecoder.Frames.IsEmpty)
            {
                if (bufferedDuration == 0)
                {
                    if (!IsPlaying || AudioDecoder.Status == MediaFramework.Status.Ended)
                        break;

                    Log.Warn("No audio frames");
                    requiresBuffering = true;
                }
                else
                {
                    Thread.Sleep(50); // waiting for audio buffer to be played before end
                    bufferedDuration = Audio.GetBufferedDuration();
                }

                continue;
            }

            // Support only ASR subtitle for audio
            foreach (int i in SubtitlesASR.SubIndexSet)
            {
                SubtitlesManager[i].SetCurrentTime(new TimeSpan(curTime));

                var cur = SubtitlesManager[i].GetCurrent();
                if (cur != null && !string.IsNullOrEmpty(cur.DisplayText))
                {
                    SubtitleDisplay(cur.DisplayText, i, cur.UseTranslated);
                }
                else
                {
                    SubtitleClear(i);
                }
            }

            bufferedDuration = Audio.GetBufferedDuration();

            if (bufferedDuration < 300 * 10000)
            {
                do
                {
                    AudioDecoder.Frames.TryDequeue(out aFrame);
                    if (aFrame == null || !IsPlaying)
                        break;

                    Audio.AddSamples(aFrame);
                    bufferedDuration += (long) ((aFrame.dataLen / 4) * Audio.Timebase);
                    UpdateCurTime(aFrame.Timestamp, false);
                } while (bufferedDuration < 100 * 10000);

                Thread.Sleep(20);
            }
            else
                Thread.Sleep(50);
        }
    }

    private void ScreamerReverse()
    {   // TBR: Timings / Rebuffer

        int vDistanceMs, sleepMs;
        long elapsedTicks, startTicks = 0;

        while (status == Status.Playing)
        {
            if (seeks.TryPop(out var seekData))
            {
                vFrame = null;
                Renderer.RenderIdleStart();

                seeks.Clear();
                if (decoder.Seek(seekData.ms, seekData.forward) < 0)
                    Log.Warn("Seek failed");
            }

            if (vFrame == null)
            {
                if (VideoDecoder.Status == MediaFramework.Status.Ended)
                    break;

                Renderer.RenderIdleStart();
                OnBufferingStarted();
                if (reversePlaybackResync)
                {
                    decoder.Flush();
                    VideoDemuxer.EnableReversePlayback(CurTime);
                    reversePlaybackResync = false;
                }
                VideoDemuxer.Start();
                VideoDecoder.Start();

                // Recoding*
                while (vFrames.IsEmpty && status == Status.Playing && VideoDecoder.IsRunning) Thread.Sleep(15);
                OnBufferingCompleted();
                if (!vFrames.TryDequeue(out vFrame))
                    { Log.Warn("No video frame"); break; }

                startTicks = vFrame.Timestamp;
                UpdateCurTime(vFrame.Timestamp, false);
                Renderer.RenderIdleStop();
                sw.Restart();
            }

            elapsedTicks    = (long)(sw.ElapsedTicks * SWFREQ_TO_TICKS);
            vDistanceMs     = (int) ((((startTicks - vFrame.Timestamp) / speed) - elapsedTicks) / 10000);
            sleepMs         = vDistanceMs - 1;

            if (sleepMs < 0) sleepMs = 0;

            if (Math.Abs(vDistanceMs - sleepMs) > 5)
            {
                vFrame = null; // should never be LastFrame
                Thread.Sleep(5);
                continue; // rebuffer
            }

            if (sleepMs > 2)
            {
                if (sleepMs > 2000)
                {
                    vFrame = null; // should never be LastFrame
                    Thread.Sleep(5);
                    continue;
                }

                Thread.Sleep(sleepMs);
            }

            if (Renderer.RenderPlay(vFrame, false))
                Renderer.PresentPlay();

            UpdateCurTime(vFrame.Timestamp, false);

            vFrame = null;
            int dequeueRetries  = MAX_DEQUEUE_RETRIES;
            while (!isVideoSwitch && !vFrames.TryDequeue(out vFrame) && dequeueRetries-- > 0)
                Thread.Sleep(1);
        }

        Renderer.RenderIdleStart(true);
        vFrame = null;
        if (CanInfo) Log.Info($"Finished at {TicksToTimeMini(curTime)}");
    }

    private void ScreamerZeroLatency()
    {   // Video Only | IsLive | No Deinterlacing | No Frame Stepping | No Bitrate Stats | No Buffered Duration | Frame rate = receiving packets rate
        VideoDemuxer.Pause();
        VideoDecoder.Pause();
        VideoDemuxer.DisposePackets();
        VideoDecoder.Flush();

        Renderer.RenderIdleStop();
        while (status == Status.Playing)
        {
            vFrame = VideoDecoder.GetFrameNext();
            if (vFrame == null)
                break;

            var prevFrame = Renderer.Frames.RendererFrame;
            if (Renderer.RenderPlay(vFrame, false))
            {
                Renderer.PresentPlay();
                prevFrame?.Dispose();
                UpdateCurTime(vFrame.Timestamp, false);
            }
            else
                vFrame.Dispose();
        }

        vFrame = null;
        Renderer.RenderIdleStart(true);

        if (CanInfo) Log.Info($"Finished at {TicksToTimeMini(curTime)}");
    }
}

public class BufferingCompletedArgs : EventArgs
{
    public string   Error       { get; }
    public bool     Success     { get; }

    public BufferingCompletedArgs(string error)
    {
        Error   = error;
        Success = Error == null;
    }
}

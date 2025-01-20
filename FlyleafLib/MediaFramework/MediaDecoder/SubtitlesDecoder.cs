using System.Collections.Concurrent;
using System.Threading;

using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaFramework.MediaFrame;

using static FlyleafLib.Logger;

namespace FlyleafLib.MediaFramework.MediaDecoder;

public unsafe class SubtitlesDecoder : DecoderBase
{
    public SubtitlesStream  SubtitlesStream     => (SubtitlesStream) Stream;

    public ConcurrentQueue<SubtitlesFrame>
                            Frames              { get; protected set; } = new ConcurrentQueue<SubtitlesFrame>();

    public SubtitlesDecoder(Config config, int uniqueId = -1) : base(config, uniqueId) { }

    protected override int Setup(AVCodec* codec)
    {
        lock (lockCodecCtx)
        {
            if (demuxer.avioCtx != null)
            {
                // Disable check since already converted to UTF-8
                codecCtx->sub_charenc_mode = SubCharencModeFlags.Ignore;
            }
        }

        return 0;
    }

    protected override void DisposeInternal()
        => DisposeFrames();

    public void Flush()
    {
        lock (lockActions)
        lock (lockCodecCtx)
        {
            if (Disposed) return;

            if (Status == Status.Ended) Status = Status.Stopped;
            //else if (Status == Status.Draining) Status = Status.Stopping;

            DisposeFrames();
            avcodec_flush_buffers(codecCtx);
        }
    }

    protected override void RunInternal()
    {
        int ret = 0;
        int allowedErrors = Config.Decoder.MaxErrors;
        AVPacket *packet;

        do
        {
            // Wait until Queue not Full or Stopped
            if (Frames.Count >= Config.Decoder.MaxSubsFrames)
            {
                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueFull;

                while (Frames.Count >= Config.Decoder.MaxSubsFrames && Status == Status.QueueFull) Thread.Sleep(20);

                lock (lockStatus)
                {
                    if (Status != Status.QueueFull) break;
                    Status = Status.Running;
                }
            }

            // While Packets Queue Empty (Ended | Quit if Demuxer stopped | Wait until we get packets)
            if (demuxer.SubtitlesPackets.Count == 0)
            {
                CriticalArea = true;

                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueEmpty;

                while (demuxer.SubtitlesPackets.Count == 0 && Status == Status.QueueEmpty)
                {
                    if (demuxer.Status == Status.Ended)
                    {
                        Status = Status.Ended;
                        break;
                    }
                    else if (!demuxer.IsRunning)
                    {
                        if (CanDebug) Log.Debug($"Demuxer is not running [Demuxer Status: {demuxer.Status}]");

                        int retries = 5;

                        while (retries > 0)
                        {
                            retries--;
                            Thread.Sleep(10);
                            if (demuxer.IsRunning) break;
                        }

                        lock (demuxer.lockStatus)
                        lock (lockStatus)
                        {
                            if (demuxer.Status == Status.Pausing || demuxer.Status == Status.Paused)
                                Status = Status.Pausing;
                            else if (demuxer.Status != Status.Ended)
                                Status = Status.Stopping;
                            else
                                continue;
                        }

                        break;
                    }

                    Thread.Sleep(20);
                }

                lock (lockStatus)
                {
                    CriticalArea = false;
                    if (Status != Status.QueueEmpty) break;
                    Status = Status.Running;
                }
            }

            lock (lockCodecCtx)
            {
                if (Status == Status.Stopped || demuxer.SubtitlesPackets.Count == 0) continue;
                packet = demuxer.SubtitlesPackets.Dequeue();

                int gotFrame = 0;
                SubtitlesFrame subFrame = new();

                fixed(AVSubtitle* subPtr = &subFrame.sub)
                    ret = avcodec_decode_subtitle2(codecCtx, subPtr, &gotFrame, packet);

                if (ret < 0)
                {
                    allowedErrors--;
                    if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                    if (allowedErrors == 0) { Log.Error("Too many errors!"); Status = Status.Stopping; break; }

                    continue;
                }

                if (gotFrame == 0)
                {
                    av_packet_free(&packet);
                    continue;
                }

                long pts = subFrame.sub.pts != AV_NOPTS_VALUE ? subFrame.sub.pts /*mcs*/ * 10 : (packet->pts != AV_NOPTS_VALUE ? (long)(packet->pts * SubtitlesStream.Timebase) : AV_NOPTS_VALUE);
                av_packet_free(&packet);

                if (pts == AV_NOPTS_VALUE)
                    continue;

                pts += subFrame.sub.start_display_time /*ms*/ * 10000L;

                if (!filledFromCodec) // TODO: CodecChanged? And when findstreaminfo is disabled as it is an external demuxer will not know the main demuxer's start time
                {
                    filledFromCodec = true;
                    avcodec_parameters_from_context(Stream.AVStream->codecpar, codecCtx);
                    SubtitlesStream.Refresh();

                    CodecChanged?.Invoke(this);
                }

                if (subFrame.sub.num_rects < 1)
                {
                    if (SubtitlesStream.IsBitmap) // clear prev subs frame
                    {
                        subFrame.duration   = uint.MaxValue;
                        subFrame.timestamp  = pts - demuxer.StartTime + Config.Subtitles.Delay;
                        Frames.Enqueue(subFrame);
                    }

                    fixed(AVSubtitle* subPtr = &subFrame.sub)
                        avsubtitle_free(subPtr);

                    continue;
                }

                subFrame.duration   = subFrame.sub.end_display_time;
                subFrame.timestamp  = pts - demuxer.StartTime + Config.Subtitles.Delay;

                if (subFrame.sub.rects[0]->type == AVSubtitleType.Ass)
                {
                    subFrame.text = Utils.BytePtrToStringUTF8(subFrame.sub.rects[0]->ass);
                    Config.Subtitles.Parser(subFrame);

                    fixed(AVSubtitle* subPtr = &subFrame.sub)
                        avsubtitle_free(subPtr);

                    if (string.IsNullOrEmpty(subFrame.text))
                        continue;
                }
                else if (subFrame.sub.rects[0]->type == AVSubtitleType.Text)
                {
                    subFrame.text = Utils.BytePtrToStringUTF8(subFrame.sub.rects[0]->text);

                    fixed(AVSubtitle* subPtr = &subFrame.sub)
                        avsubtitle_free(subPtr);

                    if (string.IsNullOrEmpty(subFrame.text))
                        continue;
                }

                if (CanTrace) Log.Trace($"Processes {Utils.TicksToTime(subFrame.timestamp)}");

                Frames.Enqueue(subFrame);
            }
        } while (Status == Status.Running);
    }

    public static void DisposeFrame(SubtitlesFrame frame)
    {
        if (frame.sub.num_rects > 0)
            fixed(AVSubtitle* ptr = &frame.sub)
                avsubtitle_free(ptr);
    }

    public void DisposeFrames()
    {
        if (!SubtitlesStream.IsBitmap)
            Frames = new ConcurrentQueue<SubtitlesFrame>();
        else
        {
            while (!Frames.IsEmpty)
            {
                Frames.TryDequeue(out var frame);
                DisposeFrame(frame);
            }
        }
    }
}

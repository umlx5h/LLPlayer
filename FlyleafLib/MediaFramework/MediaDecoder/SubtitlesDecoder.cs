using System.Diagnostics;

using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaDecoder;

public unsafe class SubtitlesDecoder : DecoderBase
{
    public SubtitlesStream  SubtitlesStream     => (SubtitlesStream) Stream;

    public ConcurrentQueue<SubtitlesFrame>
                            Frames              { get; protected set; } = [];

    public PacketQueue      SubtitlesPackets;

    public SubtitlesDecoder(Config config, int uniqueId = -1, int subIndex = 0) : base(config, uniqueId)
    {
        this.subIndex = subIndex;
    }

    private readonly int subIndex;

    protected override unsafe bool Setup()
    {
        AVCodec* codec = string.IsNullOrEmpty(Config.Decoder._SubtitlesCodec) ? avcodec_find_decoder(Stream.CodecID) : avcodec_find_decoder_by_name(Config.Decoder._SubtitlesCodec);
        if (codec == null)
        {
            Log.Error($"Codec not found ({(string.IsNullOrEmpty(Config.Decoder._SubtitlesCodec) ? Stream.CodecID : Config.Decoder._SubtitlesCodec)})");
            return false;
        }

        if (CanDebug) Log.Debug($"Using {avcodec_get_name(codec->id)} codec");

        codecCtx = avcodec_alloc_context3(codec); // Pass codec to use default settings
        if (codecCtx == null)
        {
            Log.Error($"Failed to allocate context");
            return false;
        }

        int ret = avcodec_parameters_to_context(codecCtx, Stream.AVStream->codecpar);
        if (ret < 0)
        {
            Log.Error($"Failed to pass parameters to context - {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");
            return false;
        }

        codecCtx->pkt_timebase  = Stream.AVStream->time_base;
        codecCtx->codec_id      = codec->id; // avcodec_parameters_to_context will change this we need to set Stream's Codec Id (eg we change mp2 to mp3)

        if (demuxer.avioCtx != null)
        {
            // Disable check since already converted to UTF-8
            codecCtx->sub_charenc_mode = SubCharencModeFlags.Ignore;
        }

        var codecOpts = Config.Decoder.SubtitlesCodecOpt;
        AVDictionary* avopt = null;
        foreach(var optKV in codecOpts)
            _ = av_dict_set(&avopt, optKV.Key, optKV.Value, 0);

        ret = avcodec_open2(codecCtx, null, avopt == null ? null : &avopt);
        if (ret < 0)
        {
            if (avopt != null) av_dict_free(&avopt);
            Log.Error($"Failed to open codec - {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");
            return false;
        }

        if (avopt != null)
        {
            AVDictionaryEntry *t = null;
            while ((t = av_dict_get(avopt, "", t, DictReadFlags.IgnoreSuffix)) != null)
                Log.Debug($"Ignoring codec option {BytePtrToStringUTF8(t->key)}");

            av_dict_free(&avopt);
        }

        return true;
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

        SubtitlesPackets = demuxer.SubtitlesPackets[subIndex];

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
            if (SubtitlesPackets.Count == 0)
            {
                CriticalArea = true;

                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueEmpty;

                while (SubtitlesPackets.Count == 0 && Status == Status.QueueEmpty)
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
                if (Status == Status.Stopped || SubtitlesPackets.Count == 0) continue;
                packet = SubtitlesPackets.Dequeue();

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
                    SubtitlesStream.Refresh(this);
                    CodecChanged?.Invoke(this);
                }

                if (subFrame.sub.num_rects < 1)
                {
                    if (SubtitlesStream.IsBitmap) // clear prev subs frame
                    {
                        subFrame.duration   = uint.MaxValue;
                        subFrame.Timestamp  = pts - demuxer.StartTime + Config.Subtitles[subIndex].Delay;
                        subFrame.isBitmap   = true;
                        Frames.Enqueue(subFrame);
                    }

                    fixed(AVSubtitle* subPtr = &subFrame.sub)
                        avsubtitle_free(subPtr);

                    continue;
                }

                subFrame.duration   = subFrame.sub.end_display_time;
                subFrame.Timestamp  = pts - demuxer.StartTime + Config.Subtitles[subIndex].Delay;

                if (subFrame.sub.rects[0]->type == AVSubtitleType.Ass)
                {
                    subFrame.text = BytePtrToStringUTF8(subFrame.sub.rects[0]->ass).Trim();
                    Config.Subtitles.Parser(subFrame);

                    fixed(AVSubtitle* subPtr = &subFrame.sub)
                        avsubtitle_free(subPtr);

                    if (string.IsNullOrEmpty(subFrame.text))
                        continue;
                }
                else if (subFrame.sub.rects[0]->type == AVSubtitleType.Text)
                {
                    subFrame.text = BytePtrToStringUTF8(subFrame.sub.rects[0]->text).Trim();

                    fixed(AVSubtitle* subPtr = &subFrame.sub)
                        avsubtitle_free(subPtr);

                    if (string.IsNullOrEmpty(subFrame.text))
                        continue;
                }
                else if (subFrame.sub.rects[0]->type == AVSubtitleType.Bitmap)
                {
                    var rect = subFrame.sub.rects[0];
                    byte[] data = ConvertBitmapSub(subFrame.sub, false);

                    subFrame.isBitmap = true;
                    subFrame.bitmap = new SubtitlesFrameBitmap()
                    {
                        data = data,
                        width = rect->w,
                        height = rect->h,
                        x = rect->x,
                        y = rect->y,
                    };
                }

                if (CanTrace) Log.Trace($"Processes {TicksToTime(subFrame.Timestamp)}");

                Frames.Enqueue(subFrame);
            }
        } while (Status == Status.Running);
    }

    public static void DisposeFrame(SubtitlesFrame frame)
    {
        Debug.Assert(frame != null, "frame is already disposed (race condition)");
        if (frame != null && frame.sub.num_rects > 0)
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

    // copy from old Flyleaf code
    public static byte[] ConvertBitmapSub(AVSubtitle sub, bool grey)
    {
        var rect = sub.rects[0];
        var stride = rect->linesize[0] * 4;

        byte[] data = new byte[rect->w * rect->h * 4];
        Span<uint> colors = stackalloc uint[256];

        fixed (byte* ptr = data)
        {
            var colorsData = new Span<uint>((byte*)rect->data[1], rect->nb_colors);

            for (int i = 0; i < colorsData.Length; i++)
                colors[i] = colorsData[i];

            ConvertPal(colors, 256, grey);

            for (int y = 0; y < rect->h; y++)
            {
                uint* xout = (uint*)(ptr + y * stride);
                byte* xin = ((byte*)rect->data[0]) + y * rect->linesize[0];

                for (int x = 0; x < rect->w; x++)
                    *xout++ = colors[*xin++];
            }
        }

        return data;
    }

    private static void ConvertPal(Span<uint> colors, int count, bool gray) // subs bitmap (source: mpv)
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
}

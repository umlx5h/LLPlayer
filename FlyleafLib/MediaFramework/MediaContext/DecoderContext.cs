using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaRemuxer;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaPlayer;
using FlyleafLib.Plugins;

using static FlyleafLib.Logger;
using static FlyleafLib.Utils;

namespace FlyleafLib.MediaFramework.MediaContext;

public unsafe partial class DecoderContext : PluginHandler
{
    /* TODO
     *
     * 1) Lock delay on demuxers' Format Context (for network streams)
     *      Ensure we interrupt if we are planning to seek
     *      Merge Seek witih GetVideoFrame (To seek accurate or to ensure keyframe)
     *      Long delay on Enable/Disable demuxer's streams (lock might not required)
     *
     * 2) Resync implementation / CurTime
     *      Transfer player's resync implementation here
     *      Ensure we can trust CurTime on lower level (eg. on decoders - demuxers using dts)
     *
     * 3) Timestamps / Memory leak
     *      If we have embedded audio/video and the audio decoder will stop/fail for some reason the demuxer will keep filling audio packets
     *      Should also check at lower level (demuxer) to prevent wrong packet timestamps (too early or too late)
     *      This is normal if it happens on live HLS (probably an ffmpeg bug)
     */

    #region Properties
    public object               Tag                 { get; set; } // Upper Layer Object (eg. Player, Downloader) - mainly for plugins to access it
    public bool                 EnableDecoding      { get; set; }
    public new bool             Interrupt
    {
        get => base.Interrupt;
        set
        {
            base.Interrupt = value;

            if (value)
            {
                VideoDemuxer.Interrupter.ForceInterrupt = 1;
                AudioDemuxer.Interrupter.ForceInterrupt = 1;

                for (int i = 0; i < subNum; i++)
                {
                    SubtitlesDemuxers[i].Interrupter.ForceInterrupt = 1;
                }
                DataDemuxer.Interrupter.ForceInterrupt = 1;
            }
            else
            {
                VideoDemuxer.Interrupter.ForceInterrupt = 0;
                AudioDemuxer.Interrupter.ForceInterrupt = 0;

                for (int i = 0; i < subNum; i++)
                {
                    SubtitlesDemuxers[i].Interrupter.ForceInterrupt = 0;
                }
                DataDemuxer.Interrupter.ForceInterrupt = 0;
            }
        }
    }

    /// <summary>
    /// It will not resync by itself. Requires manual call to ReSync()
    /// </summary>
    public bool                 RequiresResync      { get; set; }

    public string               Extension           => VideoDemuxer.Disposed ? AudioDemuxer.Extension : VideoDemuxer.Extension;

    // Demuxers
    public Demuxer              MainDemuxer         { get; private set; }
    public Demuxer              AudioDemuxer        { get; private set; }
    public Demuxer              VideoDemuxer        { get; private set; }
    // Demuxer for external subtitles, currently not used for subtitles, just used for stream info
    // Subtitles are displayed using SubtitlesManager
    public Demuxer[]            SubtitlesDemuxers   { get; private set; }
    public SubtitlesManager     SubtitlesManager    { get; private set; }

    public SubtitlesOCR         SubtitlesOCR        { get; private set; }
    public SubtitlesASR         SubtitlesASR        { get; private set; }

    public Demuxer              DataDemuxer         { get; private set; }

    // Decoders
    public AudioDecoder         AudioDecoder        { get; private set; }
    public VideoDecoder         VideoDecoder        { get; internal set;}
    public SubtitlesDecoder[]   SubtitlesDecoders   { get; private set; }
    public DataDecoder          DataDecoder         { get; private set; }
    public DecoderBase GetDecoderPtr(StreamBase stream)
    {
        switch (stream.Type)
        {
            case MediaType.Audio:
                return AudioDecoder;
            case MediaType.Video:
                return VideoDecoder;
            case MediaType.Data:
                return DataDecoder;
            case MediaType.Subs:
                return SubtitlesDecoders[SubtitlesSelectedHelper.CurIndex];
            default:
                throw new InvalidOperationException();
        }
    }
    // Streams
    public AudioStream          AudioStream         => (VideoDemuxer?.AudioStream) ?? AudioDemuxer.AudioStream;
    public VideoStream          VideoStream         => VideoDemuxer?.VideoStream;
    public SubtitlesStream[]    SubtitlesStreams
    {
        get
        {
            SubtitlesStream[] streams = new SubtitlesStream[subNum];

            for (int i = 0; i < subNum; i++)
            {
                if (VideoDemuxer?.SubtitlesStreams?[i] != null)
                {
                    streams[i] = VideoDemuxer.SubtitlesStreams[i];
                }
                else if (SubtitlesDemuxers[i]?.SubtitlesStreams?[i] != null)
                {
                    streams[i] = SubtitlesDemuxers[i].SubtitlesStreams[i];
                }
            }

            return streams;
        }
    }
    public DataStream           DataStream          => (VideoDemuxer?.DataStream) ?? DataDemuxer.DataStream;

    public Tuple<ExternalAudioStream, int>      ClosedAudioStream       { get; private set; }
    public Tuple<ExternalVideoStream, int>      ClosedVideoStream       { get; private set; }
    #endregion

    #region Initialize
    LogHandler Log;
    bool shouldDispose;
    int subNum => Config.Subtitles.Max;

    public DecoderContext(Config config = null, int uniqueId = -1, bool enableDecoding = true) : base(config, uniqueId)
    {
        Log = new LogHandler(("[#" + UniqueId + "]").PadRight(8, ' ') + " [DecoderContext] ");
        Playlist.decoder    = this;

        EnableDecoding      = enableDecoding;

        AudioDemuxer        = new Demuxer(Config.Demuxer, MediaType.Audio, UniqueId, EnableDecoding);
        VideoDemuxer        = new Demuxer(Config.Demuxer, MediaType.Video, UniqueId, EnableDecoding);
        SubtitlesDemuxers   = new Demuxer[subNum];
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDemuxers[i] = new Demuxer(Config.Demuxer, MediaType.Subs, UniqueId, EnableDecoding);
        }
        DataDemuxer         = new Demuxer(Config.Demuxer, MediaType.Data, UniqueId, EnableDecoding);

        SubtitlesManager    = new SubtitlesManager(Config, subNum);
        SubtitlesOCR        = new SubtitlesOCR(Config.Subtitles, subNum);
        SubtitlesASR        = new SubtitlesASR(SubtitlesManager, Config);

        Recorder            = new Remuxer(UniqueId);

        VideoDecoder        = new VideoDecoder(Config, UniqueId);
        AudioDecoder        = new AudioDecoder(Config, UniqueId, VideoDecoder);
        SubtitlesDecoders   = new SubtitlesDecoder[subNum];
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDecoders[i] = new SubtitlesDecoder(Config, UniqueId, i);
        }
        DataDecoder         = new DataDecoder(Config, UniqueId);

        if (EnableDecoding && config.Player.Usage != MediaPlayer.Usage.Audio)
            VideoDecoder.CreateRenderer();

        VideoDecoder.recCompleted = RecordCompleted;
        AudioDecoder.recCompleted = RecordCompleted;
    }

    public void Initialize()
    {
        VideoDecoder.Renderer?.ClearScreen();
        RequiresResync = false;

        OnInitializing();
        Stop();
        OnInitialized();
    }
    public void InitializeSwitch()
    {
        VideoDecoder.Renderer?.ClearScreen();
        RequiresResync = false;
        ClosedAudioStream = null;
        ClosedVideoStream = null;

        OnInitializingSwitch();
        Stop();
        OnInitializedSwitch();
    }
    #endregion

    #region Seek
    public int Seek(long ms = -1, bool forward = false, bool seekInQueue = true)
    {
        int ret = 0;

        if (ms == -1) ms = GetCurTimeMs();

        // Review decoder locks (lockAction should be added to avoid dead locks with flush mainly before lockCodecCtx)
        AudioDecoder.resyncWithVideoRequired = false; // Temporary to avoid dead lock on AudioDecoder.lockCodecCtx
        lock (VideoDecoder.lockCodecCtx)
        lock (AudioDecoder.lockCodecCtx)
        lock (SubtitlesDecoders[0].lockCodecCtx)
        lock (SubtitlesDecoders[1].lockCodecCtx)
        lock (DataDecoder.lockCodecCtx)
        {
            long seekTimestamp = CalcSeekTimestamp(VideoDemuxer, ms, ref forward);

            // Should exclude seek in queue for all "local/fast" files
            lock (VideoDemuxer.lockActions)
            if (Playlist.InputType == InputType.Torrent || ms == 0 || !seekInQueue || VideoDemuxer.SeekInQueue(seekTimestamp, forward) != 0)
            {
                VideoDemuxer.Interrupter.ForceInterrupt = 1;
                OpenedPlugin.OnBuffering();
                lock (VideoDemuxer.lockFmtCtx)
                {
                    if (VideoDemuxer.Disposed) { VideoDemuxer.Interrupter.ForceInterrupt = 0; return -1; }
                    ret = VideoDemuxer.Seek(seekTimestamp, forward);
                }
            }

            VideoDecoder.Flush();
            if (ms == 0)
                VideoDecoder.keyPacketRequired = false; // TBR

            if (AudioStream != null && AudioDecoder.OnVideoDemuxer)
            {
                AudioDecoder.Flush();
                if (ms == 0)
                    AudioDecoder.nextPts = AudioDecoder.Stream.StartTimePts;
            }

            for (int i = 0; i < subNum; i++)
            {
                if (SubtitlesStreams[i] != null && SubtitlesDecoders[i].OnVideoDemuxer)
                {
                    SubtitlesDecoders[i].Flush();
                }
            }

            if (DataStream != null && DataDecoder.OnVideoDemuxer)
                DataDecoder.Flush();
        }

        if (AudioStream != null && !AudioDecoder.OnVideoDemuxer)
        {
            AudioDecoder.Pause();
            AudioDecoder.Flush();
            AudioDemuxer.PauseOnQueueFull = true;
            RequiresResync = true;
        }

        //for (int i = 0; i < subNum; i++)
        //{
        //    if (SubtitlesStreams[i] != null && !SubtitlesDecoders[i].OnVideoDemuxer)
        //    {
        //        SubtitlesDecoders[i].Pause();
        //        SubtitlesDecoders[i].Flush();
        //        SubtitlesDemuxers[i].PauseOnQueueFull = true;
        //        RequiresResync = true;
        //    }
        //}

        if (DataStream != null && !DataDecoder.OnVideoDemuxer)
        {
            DataDecoder.Pause();
            DataDecoder.Flush();
            DataDemuxer.PauseOnQueueFull = true;
            RequiresResync = true;
        }

        return ret;
    }
    public int SeekAudio(long ms = -1, bool forward = false)
    {
        int ret = 0;

        if (AudioDemuxer.Disposed || AudioDecoder.OnVideoDemuxer || !Config.Audio.Enabled) return -1;

        if (ms == -1) ms = GetCurTimeMs();

        long seekTimestamp = CalcSeekTimestamp(AudioDemuxer, ms, ref forward);

        AudioDecoder.resyncWithVideoRequired = false; // Temporary to avoid dead lock on AudioDecoder.lockCodecCtx
        lock (AudioDecoder.lockActions)
        lock (AudioDecoder.lockCodecCtx)
        {
            lock (AudioDemuxer.lockActions)
                if (AudioDemuxer.SeekInQueue(seekTimestamp, forward) != 0)
                    ret = AudioDemuxer.Seek(seekTimestamp, forward);

            AudioDecoder.Flush();
            if (VideoDecoder.IsRunning)
            {
                AudioDemuxer.Start();
                AudioDecoder.Start();
            }
        }

        return ret;
    }

    //public int SeekSubtitles(int subIndex = -1, long ms = -1, bool forward = false)
    //{
    //    int ret = -1;

    //    if (!Config.Subtitles.Enabled)
    //        return ret;

    //    // all sub streams
    //    int startIndex = 0;
    //    int endIndex = subNum - 1;
    //    if (subIndex != -1)
    //    {
    //        // specific sub stream
    //        startIndex = subIndex;
    //        endIndex = subIndex;
    //    }

    //    for (int i = startIndex; i <= endIndex; i++)
    //    {
    //        // Perform seek only for external subtitles
    //        if (SubtitlesDemuxers[i].Disposed || SubtitlesDecoders[i].OnVideoDemuxer)
    //        {
    //            continue;
    //        }

    //        long localMs = ms;
    //        if (localMs == -1)
    //        {
    //            localMs = GetCurTimeMs();
    //        }

    //        long seekTimestamp = CalcSeekTimestamp(SubtitlesDemuxers[i], localMs, ref forward);

    //        lock (SubtitlesDecoders[i].lockActions)
    //            lock (SubtitlesDecoders[i].lockCodecCtx)
    //            {
    //                // Currently disabled as it will fail to seek within the queue the most of the times
    //                //lock (SubtitlesDemuxer.lockActions)
    //                //if (SubtitlesDemuxer.SeekInQueue(seekTimestamp, forward) != 0)
    //                ret = SubtitlesDemuxers[i].Seek(seekTimestamp, forward);

    //                SubtitlesDecoders[i].Flush();
    //                if (VideoDecoder.IsRunning)
    //                {
    //                    SubtitlesDemuxers[i].Start();
    //                    SubtitlesDecoders[i].Start();
    //                }
    //            }
    //    }
    //    return ret;
    //}

    public int SeekData(long ms = -1, bool forward = false)
    {
        int ret = 0;

        if (DataDemuxer.Disposed || DataDecoder.OnVideoDemuxer)
            return -1;

        if (ms == -1)
            ms = GetCurTimeMs();

        long seekTimestamp = CalcSeekTimestamp(DataDemuxer, ms, ref forward);

        lock (DataDecoder.lockActions)
            lock (DataDecoder.lockCodecCtx)
            {
                ret = DataDemuxer.Seek(seekTimestamp, forward);

                DataDecoder.Flush();
                if (VideoDecoder.IsRunning)
                {
                    DataDemuxer.Start();
                    DataDecoder.Start();
                }
            }

        return ret;
    }

    public long GetCurTime()    => !VideoDemuxer.Disposed ? VideoDemuxer.CurTime : !AudioDemuxer.Disposed ? AudioDemuxer.CurTime : 0;
    public int GetCurTimeMs()   => !VideoDemuxer.Disposed ? (int)(VideoDemuxer.CurTime / 10000) : (!AudioDemuxer.Disposed ? (int)(AudioDemuxer.CurTime / 10000) : 0);

    private long CalcSeekTimestamp(Demuxer demuxer, long ms, ref bool forward)
    {
        long startTime = demuxer.hlsCtx == null ? demuxer.StartTime : demuxer.hlsCtx->first_timestamp * 10;
        long ticks = (ms * 10000) + startTime;

        if (demuxer.Type == MediaType.Audio) ticks -= Config.Audio.Delay;
        //if (demuxer.Type == MediaType.Subs)
        //{
        //    int i = SubtitlesDemuxers[0] == demuxer ? 0 : 1;
        //    ticks -= Config.Subtitles[i].Delay + (2 * 1000 * 10000); // We even want the previous subtitles
        //}

        if (ticks < startTime)
        {
            ticks = startTime;
            forward = true;
        }
        else if (ticks > startTime + (!VideoDemuxer.Disposed ? VideoDemuxer.Duration : AudioDemuxer.Duration) - (50 * 10000))
        {
            ticks = Math.Max(startTime, startTime + demuxer.Duration - (50 * 10000));
            forward = false;
        }

        return ticks;
    }
    #endregion

    #region Start/Pause/Stop
    public void Pause()
    {
        VideoDecoder.Pause();
        AudioDecoder.Pause();
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDecoders[i].Pause();
        }
        DataDecoder.Pause();

        VideoDemuxer.Pause();
        AudioDemuxer.Pause();
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDemuxers[i].Pause();
        }
        DataDemuxer.Pause();
    }
    public void PauseDecoders()
    {
        VideoDecoder.Pause();
        AudioDecoder.Pause();
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDecoders[i].Pause();
        }
        DataDecoder.Pause();
    }
    public void PauseOnQueueFull()
    {
        VideoDemuxer.PauseOnQueueFull = true;
        AudioDemuxer.PauseOnQueueFull = true;
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDemuxers[i].PauseOnQueueFull = true;
        }
        DataDecoder.PauseOnQueueFull = true;
    }
    public void Start()
    {
        //if (RequiresResync) Resync();

        if (Config.Audio.Enabled)
        {
            AudioDemuxer.Start();
            AudioDecoder.Start();
        }

        if (Config.Video.Enabled)
        {
            VideoDemuxer.Start();
            VideoDecoder.Start();
        }

        if (Config.Subtitles.Enabled)
        {
            for (int i = 0; i < subNum; i++)
            {
                //if (SubtitlesStreams[i] != null && !SubtitlesDecoders[i].OnVideoDemuxer)
                //    SubtitlesDemuxers[i].Start();

                //if (SubtitlesStreams[i] != null)
                if (SubtitlesStreams[i] != null && SubtitlesDecoders[i].OnVideoDemuxer)
                    SubtitlesDecoders[i].Start();
            }
        }

        if (Config.Data.Enabled)
        {
            DataDemuxer.Start();
            DataDecoder.Start();
        }
    }
    public void Stop()
    {
        Interrupt = true;

        VideoDecoder.Dispose();
        AudioDecoder.Dispose();
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDecoders[i].Dispose();
        }

        DataDecoder.Dispose();
        AudioDemuxer.Dispose();
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDemuxers[i].Dispose();
        }

        DataDemuxer.Dispose();
        VideoDemuxer.Dispose();

        Interrupt = false;
    }
    public void StopThreads()
    {
        Interrupt = true;

        VideoDecoder.Stop();
        AudioDecoder.Stop();
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDecoders[i].Stop();
        }

        DataDecoder.Stop();
        AudioDemuxer.Stop();
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDemuxers[i].Stop();
        }
        DataDemuxer.Stop();
        VideoDemuxer.Stop();

        Interrupt = false;
    }
    #endregion

    public void Resync(long timestamp = -1)
    {
        bool isRunning = VideoDemuxer.IsRunning;

        if (AudioStream != null && AudioStream.Demuxer.Type != MediaType.Video && Config.Audio.Enabled)
        {
            if (timestamp == -1) timestamp = VideoDemuxer.CurTime;
            if (CanInfo) Log.Info($"Resync audio to {TicksToTime(timestamp)}");

            SeekAudio(timestamp / 10000);
            if (isRunning)
            {
                AudioDemuxer.Start();
                AudioDecoder.Start();
            }
        }

        //for (int i = 0; i < subNum; i++)
        //{
        //    if (SubtitlesStreams[i] != null && SubtitlesStreams[i].Demuxer.Type != MediaType.Video && Config.Subtitles.Enabled)
        //    {
        //        if (timestamp == -1)
        //            timestamp = VideoDemuxer.CurTime;
        //        if (CanInfo)
        //            Log.Info($"Resync subs:{i} to {TicksToTime(timestamp)}");

        //        SeekSubtitles(i, timestamp / 10000, false);
        //        if (isRunning)
        //        {
        //            SubtitlesDemuxers[i].Start();
        //            SubtitlesDecoders[i].Start();
        //        }
        //    }
        //}

        if (DataStream != null && Config.Data.Enabled) // Should check if it actually an external (not embedded) stream DataStream.Demuxer.Type != MediaType.Video ?
        {
            if (timestamp == -1)
                timestamp = VideoDemuxer.CurTime;
            if (CanInfo)
                Log.Info($"Resync data to {TicksToTime(timestamp)}");

            SeekData(timestamp / 10000);
            if (isRunning)
            {
                DataDemuxer.Start();
                DataDecoder.Start();
            }
        }

        RequiresResync = false;
    }

    //public void ResyncSubtitles(long timestamp = -1)
    //{
    //    if (SubtitlesStream != null && Config.Subtitles.Enabled)
    //    {
    //        if (timestamp == -1) timestamp = VideoDemuxer.CurTime;
    //        if (CanInfo) Log.Info($"Resync subs to {TicksToTime(timestamp)}");

    //        if (SubtitlesStream.Demuxer.Type != MediaType.Video)
    //            SeekSubtitles(timestamp / 10000);
    //        else

    //        if (VideoDemuxer.IsRunning)
    //        {
    //            SubtitlesDemuxer.Start();
    //            SubtitlesDecoder.Start();
    //        }
    //    }
    //}
    public void Flush()
    {
        VideoDemuxer.DisposePackets();
        AudioDemuxer.DisposePackets();
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDemuxers[i].DisposePackets();
        }
        DataDemuxer.DisposePackets();

        VideoDecoder.Flush();
        AudioDecoder.Flush();
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesDecoders[i].Flush();
        }
        DataDecoder.Flush();
    }

    // !!! NEEDS RECODING
    public long GetVideoFrame(long timestamp = -1)
    {
        // TBR: Between seek and GetVideoFrame lockCodecCtx is lost and if VideoDecoder is running will already have decoded some frames (Currently ensure you pause VideDecoder before seek)

        int ret;
        int allowedErrors = Config.Decoder.MaxErrors;
        AVPacket* packet;

        lock (VideoDemuxer.lockFmtCtx)
        lock (VideoDecoder.lockCodecCtx)
        while (VideoDemuxer.VideoStream != null && !Interrupt)
        {
            if (VideoDemuxer.VideoPackets.IsEmpty)
            {
                packet = av_packet_alloc();
                VideoDemuxer.Interrupter.ReadRequest();
                ret = av_read_frame(VideoDemuxer.FormatContext, packet);
                if (ret != 0)
                {
                    av_packet_free(&packet);
                    return -1;
                }
            }
            else
                packet = VideoDemuxer.VideoPackets.Dequeue();

            if (!VideoDemuxer.EnabledStreams.Contains(packet->stream_index)) { av_packet_free(&packet); continue; }

            if (CanTrace)
            {
                var stream = VideoDemuxer.AVStreamToStream[packet->stream_index];
                long dts = packet->dts == AV_NOPTS_VALUE ? -1 : (long)(packet->dts * stream.Timebase);
                long pts = packet->pts == AV_NOPTS_VALUE ? -1 : (long)(packet->pts * stream.Timebase);
                Log.Trace($"[{stream.Type}] DTS: {(dts == -1 ? "-" : TicksToTime(dts))} PTS: {(pts == -1 ? "-" : TicksToTime(pts))} | FLPTS: {(pts == -1 ? "-" : TicksToTime(pts - VideoDemuxer.StartTime))} | CurTime: {TicksToTime(VideoDemuxer.CurTime)} | Buffered: {TicksToTime(VideoDemuxer.BufferedDuration)}");
            }

            var codecType = VideoDemuxer.FormatContext->streams[packet->stream_index]->codecpar->codec_type;

            if (codecType == AVMediaType.Video && VideoDecoder.keyPacketRequired)
            {
                if (packet->flags.HasFlag(PktFlags.Key))
                    VideoDecoder.keyPacketRequired = false;
                else
                {
                    if (CanWarn) Log.Warn("Ignoring non-key packet");
                    av_packet_free(&packet);
                    continue;
                }
            }

            if (VideoDemuxer.IsHLSLive)
                VideoDemuxer.UpdateHLSTime();

            switch (codecType)
            {
                case AVMediaType.Audio:
                    if (timestamp == -1 || (long)(packet->pts * AudioStream.Timebase) - VideoDemuxer.StartTime + (VideoStream.FrameDuration / 2) > timestamp)
                        VideoDemuxer.AudioPackets.Enqueue(packet);
                    else
                        av_packet_free(&packet);

                    continue;

                case AVMediaType.Subtitle:
                    for (int i = 0; i < subNum; i++)
                    {
                        if (SubtitlesStreams[i]?.StreamIndex != packet->stream_index)
                        {
                            continue;
                        }

                        if (timestamp == -1 ||
                            (long)(packet->pts * SubtitlesStreams[i].Timebase) - VideoDemuxer.StartTime +
                            (VideoStream.FrameDuration / 2) > timestamp)
                        {
                            // Clone packets to support simultaneous display of the same subtitle
                            VideoDemuxer.SubtitlesPackets[i].Enqueue(av_packet_clone(packet));
                        }
                    }

                    // cloned, so free packet
                    av_packet_free(&packet);

                    continue;

                case AVMediaType.Data: // this should catch the data stream packets until we have a valid vidoe keyframe (it should fill the pts if NOPTS with lastVideoPacketPts similarly to the demuxer)
                    if ((timestamp == -1 && VideoDecoder.StartTime != NoTs) || (long)(packet->pts * DataStream.Timebase) - VideoDemuxer.StartTime + (VideoStream.FrameDuration / 2) > timestamp)
                        VideoDemuxer.DataPackets.Enqueue(packet);

                    packet = av_packet_alloc();

                    continue;

                case AVMediaType.Video:
                    ret = avcodec_send_packet(VideoDecoder.CodecCtx, packet);

                    if (VideoDecoder.swFallback)
                    {
                        VideoDecoder.SWFallback();
                        ret = avcodec_send_packet(VideoDecoder.CodecCtx, packet);
                    }

                    av_packet_free(&packet);

                    if (ret != 0)
                    {
                        allowedErrors--;
                        if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                        if (allowedErrors == 0) { Log.Error("Too many errors!");  return -1; }

                        continue;
                    }

                    //VideoDemuxer.UpdateCurTime();

                    var frame = av_frame_alloc();
                    while (VideoDemuxer.VideoStream != null && !Interrupt)
                    {
                        ret = avcodec_receive_frame(VideoDecoder.CodecCtx, frame);
                        if (ret != 0) { av_frame_unref(frame); break; }

                        if (frame->best_effort_timestamp != AV_NOPTS_VALUE)
                            frame->pts = frame->best_effort_timestamp;
                        else if (frame->pts == AV_NOPTS_VALUE)
                        {
                            if (!VideoStream.FixTimestamps)
                            {
                                av_frame_unref(frame);
                                continue;
                            }

                            frame->pts = VideoDecoder.lastFixedPts + VideoStream.StartTimePts;
                            VideoDecoder.lastFixedPts += av_rescale_q(VideoStream.FrameDuration / 10, Engine.FFmpeg.AV_TIMEBASE_Q, VideoStream.AVStream->time_base);
                        }

                        if (!VideoDecoder.filledFromCodec)
                        {
                            ret = VideoDecoder.FillFromCodec(frame);

                            if (ret == -1234)
                            {
                                av_frame_free(&frame);
                                return -1;
                            }
                        }

                        // Accurate seek with +- half frame distance
                        if (timestamp != -1 && (long)(frame->pts * VideoStream.Timebase) - VideoDemuxer.StartTime + (VideoStream.FrameDuration / 2) < timestamp)
                        {
                            av_frame_unref(frame);
                            continue;
                        }

                        //if (CanInfo) Info($"Asked for {Utils.TicksToTime(timestamp)} and got {Utils.TicksToTime((long)(frame->pts * VideoStream.Timebase) - VideoDemuxer.StartTime)} | Diff {Utils.TicksToTime(timestamp - ((long)(frame->pts * VideoStream.Timebase) - VideoDemuxer.StartTime))}");
                        VideoDecoder.StartTime = (long)(frame->pts * VideoStream.Timebase) - VideoDemuxer.StartTime;

                        var mFrame = VideoDecoder.Renderer.FillPlanes(frame);
                        if (mFrame != null) VideoDecoder.Frames.Enqueue(mFrame);

                        do
                        {
                            ret = avcodec_receive_frame(VideoDecoder.CodecCtx, frame);
                            if (ret != 0) break;
                            mFrame = VideoDecoder.Renderer.FillPlanes(frame);
                            if (mFrame != null) VideoDecoder.Frames.Enqueue(mFrame);
                        } while (!VideoDemuxer.Disposed && !Interrupt);

                        av_frame_free(&frame);
                        return mFrame.timestamp;
                    }

                    av_frame_free(&frame);

                    break; // Switch break

                default:
                    av_packet_free(&packet);
                    continue;

            } // Switch

        } // While

        return -1;
    }
    public new void Dispose()
    {
        shouldDispose = true;
        Stop();
        Interrupt = true;
        VideoDecoder.DestroyRenderer();
        base.Dispose();
    }

    public void PrintStats()
    {
        string dump = "\r\n-===== Streams / Packets / Frames =====-\r\n";
        dump += $"\r\n AudioPackets      ({VideoDemuxer.AudioStreams.Count}): {VideoDemuxer.AudioPackets.Count}";
        dump += $"\r\n VideoPackets      ({VideoDemuxer.VideoStreams.Count}): {VideoDemuxer.VideoPackets.Count}";
        for (int i = 0; i < subNum; i++)
        {
            dump += $"\r\n SubtitlesPackets{i+1}  ({VideoDemuxer.SubtitlesStreamsAll.Count}): {VideoDemuxer.SubtitlesPackets[i].Count}";
        }

        dump += $"\r\n AudioPackets      ({AudioDemuxer.AudioStreams.Count}): {AudioDemuxer.AudioPackets.Count} (AudioDemuxer)";

        for (int i = 0; i < subNum; i++)
        {
            dump += $"\r\n SubtitlesPackets{i+1}  ({SubtitlesDemuxers[i].SubtitlesStreamsAll.Count}): {SubtitlesDemuxers[i].SubtitlesPackets[0].Count} (SubtitlesDemuxer)";
        }

        dump += $"\r\n Video Frames         : {VideoDecoder.Frames.Count}";
        dump += $"\r\n Audio Frames         : {AudioDecoder.Frames.Count}";
        for (int i = 0; i < subNum; i++)
        {
            dump += $"\r\n Subtitles Frames{i+1}     : {SubtitlesDecoders[i].Frames.Count}";
        }

        if (CanInfo) Log.Info(dump);
    }

    #region Recorder
    Remuxer Recorder;
    public event EventHandler RecordingCompleted;
    public bool IsRecording => VideoDecoder.isRecording || AudioDecoder.isRecording;
    int oldMaxAudioFrames;
    bool recHasVideo;
    public void StartRecording(ref string filename, bool useRecommendedExtension = true)
    {
        if (IsRecording) StopRecording();

        oldMaxAudioFrames = -1;
        recHasVideo = false;

        if (CanInfo) Log.Info("Record Start");

        recHasVideo = !VideoDecoder.Disposed && VideoDecoder.Stream != null;

        if (useRecommendedExtension)
            filename = $"{filename}.{(recHasVideo ? VideoDecoder.Stream.Demuxer.Extension : AudioDecoder.Stream.Demuxer.Extension)}";

        Recorder.Open(filename);

        bool failed;

        if (recHasVideo)
        {
            failed = Recorder.AddStream(VideoDecoder.Stream.AVStream) != 0;
            if (CanInfo) Log.Info(failed ? "Failed to add video stream" : "Video stream added to the recorder");
        }

        if (!AudioDecoder.Disposed && AudioDecoder.Stream != null)
        {
            failed = Recorder.AddStream(AudioDecoder.Stream.AVStream, !AudioDecoder.OnVideoDemuxer) != 0;
            if (CanInfo) Log.Info(failed ? "Failed to add audio stream" : "Audio stream added to the recorder");
        }

        if (!Recorder.HasStreams || Recorder.WriteHeader() != 0) return; //throw new Exception("Invalid remuxer configuration");

        // Check also buffering and possible Diff of first audio/video timestamp to remuxer to ensure sync between each other (shouldn't be more than 30-50ms)
        oldMaxAudioFrames = Config.Decoder.MaxAudioFrames;
        //long timestamp = Math.Max(VideoDemuxer.CurTime + VideoDemuxer.BufferedDuration, AudioDemuxer.CurTime + AudioDemuxer.BufferedDuration) + 1500 * 10000;
        Config.Decoder.MaxAudioFrames = Config.Decoder.MaxVideoFrames;

        VideoDecoder.StartRecording(Recorder);
        AudioDecoder.StartRecording(Recorder);
    }
    public void StopRecording()
    {
        if (oldMaxAudioFrames != -1) Config.Decoder.MaxAudioFrames = oldMaxAudioFrames;

        VideoDecoder.StopRecording();
        AudioDecoder.StopRecording();
        Recorder.Dispose();
        oldMaxAudioFrames = -1;
        if (CanInfo) Log.Info("Record Completed");
    }
    internal void RecordCompleted(MediaType type)
    {
        if (!recHasVideo || (recHasVideo && type == MediaType.Video))
        {
            StopRecording();
            RecordingCompleted?.Invoke(this, new EventArgs());
        }
    }
    #endregion
}

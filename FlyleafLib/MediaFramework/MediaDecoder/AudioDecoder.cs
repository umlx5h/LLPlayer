using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRemuxer;

namespace FlyleafLib.MediaFramework.MediaDecoder;

/* TODO
 *
 * Sample Rate Pre-Convert (Output)
 * Change to fixed 48KHz and let ffmpeg filters do the convert if required (xaudio2 might cause latency while doing it)
 *
 * Circular Buffer
 * - Safe re-allocation and check also actual frames in queue (as now during draining we can overwrite them)
 * - Locking with Audio.AddSamples during re-allocation and re-write old data to the new buffer and update the pointers in queue
 *
 * Filters
 * - Note: Performance issue (for seek/speed change). We can't drain the buffersrc and re-use the filtergraph without re-initializing it, is not supported
 * - Check if av_buffersrc_get_nb_failed_requests required
 * - Add Config for filter threads?
 * - Review Access Violation issue with dynaudnorm/loudnorm filters in combination with atempo (when changing speed to fast?)
 * - Use multiple atempo for better quality (for < 0.5 and > 2, use eg. 2 of sqrt(X) * sqrt(X) to achive this)
 * - Review locks / recode RunInternal to be able to continue from where it stopped (eg. ProcessFilter)
 *
 * Custom Frames Queue to notify when the queue is not full anymore (to avoid thread sleep which can cause delays)
 * Support more output formats/channels/sampleRates (and currently output to 32-bit, sample rate to 48Khz and not the same as input? - should calculate possible delays)
 */

public unsafe partial class AudioDecoder : DecoderBase
{
    // Sink's Format (after Decoder + Filter)
    public AVChannelLayout  ChannelLayout       { get => channelLayout; private set { channelLayout = value; } }
    AVChannelLayout channelLayout;
    public string           ChannelLayoutStr    { get; private set; }
    public AVSampleFormat   SampleFormat        { get; private set; } = AVSampleFormat.None;
    public int              SampleRate          { get; private set; }
    public int              SampleBytes         { get; private set; }
    public double           SampleRateTimebase  { get; private set; } // Ticks

    public Action<AudioDecoder>
                            FormatChanged       { get; set; }

    public AudioStream      AudioStream         => (AudioStream) Stream;
    public readonly
            VideoDecoder    VideoDecoder;
    public ConcurrentQueue<AudioFrame>
                            Frames              { get; protected set; } = new();

    static readonly int     cBufTimesSize       = 4; // Extra for draining / filters (speed)
    int                     cBufTimesCur        = 1;
    byte[]                  cBuf;
    int                     cBufPos;
    int                     cBufSamples;
    internal bool           resyncWithVideoRequired;

    public AudioDecoder(Config config, int uniqueId = -1, VideoDecoder syncDecoder = null) : base(config, uniqueId)
        => VideoDecoder = syncDecoder;

    protected override bool Setup()
    {
        AVCodec* codec = string.IsNullOrEmpty(Config.Decoder._AudioCodec) ? avcodec_find_decoder(Stream.CodecID) : avcodec_find_decoder_by_name(Config.Decoder._AudioCodec);
        if (codec == null)
        {
            Log.Error($"Codec not found ({(string.IsNullOrEmpty(Config.Decoder._AudioCodec) ? Stream.CodecID : Config.Decoder._AudioCodec)})");
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

        var codecOpts = Config.Decoder.AudioCodecOpt;
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
    {
        DisposeFrames();
        DisposeFilters();

        filledFromCodec = false;
        expectingPts    = AV_NOPTS_VALUE;
    }
    public void DisposeFrames() => Frames = new();
    public void Flush()
    {
        lock (lockActions)
            lock (lockCodecCtx)
            {
                if (Disposed)
                    return;

                if (Status == Status.Ended)
                    Status = Status.Stopped;
                else if (Status == Status.Draining)
                    Status = Status.Stopping;

                resyncWithVideoRequired = !VideoDecoder.Disposed;
                expectingPts = AV_NOPTS_VALUE;
                DisposeFrames();
                avcodec_flush_buffers(codecCtx);
                if (filterGraph != null)
                    SetupFilters();
            }
    }

    protected override void RunInternal()
    {
        int allowedErrors   = Config.Decoder.MaxErrors;
        int sleepMs         = Config.Decoder.MaxAudioFrames > 5 && Config.Player.MaxLatency == 0 ? 10 : 4;
        int ret;
        AVPacket *packet;

        do
        {
            // Wait until Queue not Full or Stopped
            if (Frames.Count >= Config.Decoder.MaxAudioFrames)
            {
                lock (lockStatus)
                    if (Status == Status.Running)
                        Status = Status.QueueFull;

                while (Frames.Count >= Config.Decoder.MaxAudioFrames && Status == Status.QueueFull)
                    Thread.Sleep(sleepMs);

                lock (lockStatus)
                {
                    if (Status != Status.QueueFull)
                        break;

                    Status = Status.Running;
                }
            }

            // While Packets Queue Empty (Ended | Quit if Demuxer stopped | Wait until we get packets)
            if (demuxer.AudioPackets.Count == 0)
            {
                CriticalArea = true;

                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueEmpty;

                while (demuxer.AudioPackets.Count == 0 && Status == Status.QueueEmpty)
                {
                    if (demuxer.Status == Status.Ended)
                    {
                        lock (lockStatus)
                        {
                            // TODO: let the demuxer push the draining packet
                            Log.Debug("Draining");
                            Status = Status.Draining;
                            var drainPacket = av_packet_alloc();
                            drainPacket->data = null;
                            drainPacket->size = 0;
                            demuxer.AudioPackets.Enqueue(drainPacket);
                        }

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

                    Thread.Sleep(sleepMs);
                }

                lock (lockStatus)
                {
                    CriticalArea = false;
                    if (Status != Status.QueueEmpty && Status != Status.Draining) break;
                    if (Status != Status.Draining) Status = Status.Running;
                }
            }

            Monitor.Enter(lockCodecCtx); // restore the old lock / add interrupters similar to the demuxer
            try
            {
                if (Status == Status.Stopped)
                    { Monitor.Exit(lockCodecCtx); continue; }

                packet = demuxer.AudioPackets.Dequeue();

                if (packet == null)
                    { Monitor.Exit(lockCodecCtx); continue; }

                if (isRecording)
                {
                    if (!recGotKeyframe && VideoDecoder.StartRecordTime != AV_NOPTS_VALUE && (long)(packet->pts * AudioStream.Timebase) - demuxer.StartTime > VideoDecoder.StartRecordTime)
                        recGotKeyframe = true;

                    if (recGotKeyframe)
                        curRecorder.Write(av_packet_clone(packet), !OnVideoDemuxer);
                }

                ret = avcodec_send_packet(codecCtx, packet);
                av_packet_free(&packet);

                if (ret != 0 && ret != AVERROR(EAGAIN))
                {
                    if (ret == AVERROR_EOF)
                    {
                        Status = Status.Ended;
                        break;
                    }
                    else
                    {
                        allowedErrors--;
                        if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                        if (allowedErrors == 0) { Log.Error("Too many errors!"); Status = Status.Stopping; break; }

                        Monitor.Exit(lockCodecCtx); continue;
                    }
                }

                while (true)
                {
                    ret = avcodec_receive_frame(codecCtx, frame);
                    if (ret != 0)
                    {
                        av_frame_unref(frame);

                        if (ret == AVERROR_EOF && filterGraph != null)
                        {
                            lock (lockSpeed)
                            {
                                DrainFilters();
                                Status = Status.Ended;
                            }
                        }

                        break;
                    }

                    if (frame->best_effort_timestamp != AV_NOPTS_VALUE)
                        frame->pts = frame->best_effort_timestamp;
                    else if (frame->pts == AV_NOPTS_VALUE)
                    {
                        if (expectingPts == AV_NOPTS_VALUE)
                        {
                            av_frame_unref(frame);
                            continue;
                        }

                        frame->pts = expectingPts;
                    }

                    codecChanged = AudioStream.SampleFormat != codecCtx->sample_fmt || AudioStream.SampleRate != codecCtx->sample_rate || AudioStream.ChannelLayout.u.mask != codecCtx->ch_layout.u.mask;

                    if (!filledFromCodec || codecChanged)
                    {
                        if (codecChanged && filledFromCodec)
                        {
                            Log.Warn($"Codec changed {AudioStream.SampleRate / 1000:g} kHz / {AudioStream.ChannelLayoutStr} / {AudioStream.SampleFormat.ToString().ToLower()} => {codecCtx->sample_rate / 1000:g} kHz / {GetChannelLayoutStr(codecCtx->ch_layout)} / {codecCtx->sample_fmt.ToString().ToLower()}");
                            DisposeFrames();    // as we reset XAudio we need to avoid feeding those
                        }

                        DisposeFilters();

                        filledFromCodec         = true;
                        AudioStream.Refresh(this, frame);
                        codecChanged            = false;
                        resyncWithVideoRequired = !VideoDecoder.Disposed;
                        streamSampleRateTimebase= new() { Num = 1, Den = codecCtx->sample_rate};
                        gapOffsetTb             = av_rescale_q((long)TimeSpan.FromMilliseconds(40).TotalMicroseconds, TIME_BASE_Q, AudioStream.AVStream->time_base);

                        lock (lockSpeed)
                            ret = SetupFilters();

                        CodecChanged?.Invoke(this);

                        if (ret != 0)
                        {
                            Status = Status.Stopping;
                            av_frame_unref(frame);
                            break;
                        }
                    }

                    if (resyncWithVideoRequired)
                    {
                        // TODO: in case of long distance will spin (CPU issue), possible reseek?
                        while (VideoDecoder.StartTime == AV_NOPTS_VALUE && VideoDecoder.IsRunning && resyncWithVideoRequired)
                            Thread.Sleep(10);

                        long ts = (long)((frame->pts + frame->duration) * AudioStream.Timebase) - demuxer.StartTime + Config.Audio.Delay;

                        if (ts < VideoDecoder.StartTime)
                        {
                            if (CanTrace) Log.Trace($"Drops {TicksToTime(ts)} (< V: {TicksToTime(VideoDecoder.StartTime)})");
                            av_frame_unref(frame);
                            continue;
                        }
                        else
                            resyncWithVideoRequired = false;
                    }

                    lock (lockSpeed)
                        ProcessFilters();

                    av_frame_unref(frame);
                }
            } catch { }

            Monitor.Exit(lockCodecCtx);

        } while (Status == Status.Running);

        if (isRecording) { StopRecording(); recCompleted(MediaType.Audio); }

        if (Status == Status.Draining) Status = Status.Ended;
    }
    
    Queue<byte[]> cBufHistory = [];
    private void AllocateCircularBuffer(int samples)
    {
        /* TBR
        * 1. If we change to different in/out sample rates we need to calculate delay
        * 2. By destorying the cBuf can create critical issues while the audio decoder reads the data? (add lock) | we need to copy the lost data and change the pointers
        * 3. Recalculate on Config.Decoder.MaxAudioFrames change (greater)
        * 4. cBufTimesSize cause filters can pass the limit when we need to use lockSpeed
        */

        samples     = Math.Max(10000, samples); // 10K samples to ensure that currently we will not re-allocate?
        int size    = Config.Decoder.MaxAudioFrames * samples * SampleBytes * cBufTimesSize;
        
        if (cBuf != null)
        {
            if (CanDebug) Log.Debug($"Re-allocating circular buffer ({samples} > {cBufSamples}) with {size}bytes");

            cBufHistory.Enqueue(cBuf);
            if (cBufHistory.Count > 3)
                cBufHistory.Dequeue();
        }
        
        cBuf        = new byte[size];
        cBufPos     = 0;
        cBufSamples = samples;

    }

    #region Recording
    internal Action<MediaType>
            recCompleted;
    Remuxer curRecorder;
    bool    recGotKeyframe;
    internal bool isRecording;
    internal void StartRecording(Remuxer remuxer)
    {
        if (Disposed || isRecording) return;

        curRecorder     = remuxer;
        isRecording     = true;
        recGotKeyframe  = VideoDecoder.Disposed || VideoDecoder.Stream == null;
    }
    internal void StopRecording() => isRecording = false;
    #endregion
}

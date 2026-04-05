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
    static readonly AVSampleFormat   AOutSampleFormat    = AVSampleFormat.S16;
    static readonly string           AOutSampleFormatStr = av_get_sample_fmt_name(AOutSampleFormat);
    static readonly AVChannelLayout  AOutChannelLayout   = AV_CHANNEL_LAYOUT_STEREO;
    static readonly int              AOutChannels        = AOutChannelLayout.nb_channels;
    static readonly int              ASampleBytes        = av_get_bytes_per_sample(AOutSampleFormat) * AOutChannels;

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
    SwrContext*             swrCtx;

    internal long           nextPts;
    double                  sampleRateTimebase;

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
    private int SetupSwr()
    {
        int ret;

        DisposeSwr();
        swrCtx = swr_alloc();

        _= av_opt_set_chlayout(swrCtx,      "in_chlayout",          &codecCtx->ch_layout,   0);
        _= av_opt_set_int(swrCtx,           "in_sample_rate",       codecCtx->sample_rate,  0);
        _= av_opt_set_sample_fmt(swrCtx,    "in_sample_fmt",        codecCtx->sample_fmt,   0);

        fixed(AVChannelLayout* ptr = &AOutChannelLayout)
        _= av_opt_set_chlayout(swrCtx,      "out_chlayout",         ptr, 0);
        _= av_opt_set_int(swrCtx,           "out_sample_rate",      codecCtx->sample_rate,  0);
        _= av_opt_set_sample_fmt(swrCtx,    "out_sample_fmt",       AOutSampleFormat,       0);

        ret = swr_init(swrCtx);
        if (ret < 0)
            Log.Error($"Swr setup failed {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

        return ret;
    }
    private void DisposeSwr()
    {
        if (swrCtx == null)
            return;

        swr_close(swrCtx);

        fixed(SwrContext** ptr = &swrCtx)
            swr_free(ptr);

        swrCtx = null;
    }

    protected override void DisposeInternal()
    {
        DisposeFrames();
        DisposeSwr();
        DisposeFilters();

        filledFromCodec = false;
        nextPts         = AV_NOPTS_VALUE;
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
                nextPts = AV_NOPTS_VALUE;
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
                        if (nextPts == AV_NOPTS_VALUE && filledFromCodec) // Possible after seek (maybe set based on pkt_pos?)
                        {
                            av_frame_unref(frame);
                            continue;
                        }

                        frame->pts = nextPts;
                    }

                    // We could fix it down to the demuxer based on size?
                    if (frame->duration <= 0)
                        frame->duration = av_rescale_q((long)(frame->nb_samples * sampleRateTimebase), Engine.FFmpeg.AV_TIMEBASE_Q, Stream.AVStream->time_base);

                    codecChanged = AudioStream.SampleFormat != codecCtx->sample_fmt || AudioStream.SampleRate != codecCtx->sample_rate || AudioStream.ChannelLayout != codecCtx->ch_layout.u.mask;

                    if (!filledFromCodec || codecChanged)
                    {
                        if (codecChanged && filledFromCodec)
                        {
                            byte[] buf = new byte[50];
                            fixed (byte* bufPtr = buf)
                            {
                                _ = av_channel_layout_describe(&codecCtx->ch_layout, bufPtr, (nuint)buf.Length);
                                Log.Warn($"Codec changed {AudioStream.CodecIDOrig} {AudioStream.SampleFormat} {AudioStream.SampleRate} {AudioStream.ChannelLayoutStr} => {codecCtx->codec_id} {codecCtx->sample_fmt} {codecCtx->sample_rate} {BytePtrToStringUTF8(bufPtr)}");
                            }
                        }

                        // Dispose (mini)
                        //DisposeFrames();
                        DisposeSwr();
                        DisposeFilters();

                        filledFromCodec         = true;
                        AudioStream.Refresh(this, frame);
                        codecChanged            = false;
                        resyncWithVideoRequired = !VideoDecoder.Disposed;
                        sampleRateTimebase      = 1000 * 1000.0 / codecCtx->sample_rate;
                        nextPts                 = AudioStream.StartTimePts;

                        if (frame->pts == AV_NOPTS_VALUE)
                            frame->pts = nextPts;

                        ret = SetupFiltersOrSwr();

                        CodecChanged?.Invoke(this);

                        if (ret != 0)
                        {
                            Status = Status.Stopping;
                            av_frame_unref(frame);
                            break;
                        }

                        if (nextPts == AV_NOPTS_VALUE)
                        {
                            av_frame_unref(frame);
                            continue;
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
                    {
                        if (filterGraph != null)
                            ProcessFilters();
                        else
                            Process();

                        av_frame_unref(frame);
                    }
                }
            } catch { }

            Monitor.Exit(lockCodecCtx);

        } while (Status == Status.Running);

        if (isRecording) { StopRecording(); recCompleted(MediaType.Audio); }

        if (Status == Status.Draining) Status = Status.Ended;
    }
    private void Process()
    {
        try
        {
            nextPts = frame->pts + frame->duration;

            var dataLen     = frame->nb_samples * ASampleBytes;
            var speedDataLen= Align((int)(dataLen / speed), ASampleBytes);

            AudioFrame mFrame = new()
            {
                Timestamp   = (long)(frame->pts * AudioStream.Timebase) - demuxer.StartTime + Config.Audio.Delay,
                dataLen     = speedDataLen,
                speed       = speed
            };
            if (CanTrace) Log.Trace($"Processes {TicksToTime(mFrame.Timestamp)}");

            if (frame->nb_samples > cBufSamples)
                AllocateCircularBuffer(frame->nb_samples);
            else if (cBufPos + Math.Max(dataLen, speedDataLen) >= cBuf.Length)
                cBufPos = 0;

            fixed (byte *circularBufferPosPtr = &cBuf[cBufPos])
            {
                int ret = swr_convert(swrCtx, &circularBufferPosPtr, frame->nb_samples, (byte**)&frame->data, frame->nb_samples);
                if (ret < 0)
                    return;

                mFrame.dataPtr = (IntPtr)circularBufferPosPtr;
            }

            // Fill silence
            if (speed < 1)
                for (int p = dataLen; p < speedDataLen; p++)
                    cBuf[cBufPos + p] = 0;

            cBufPos += Math.Max(dataLen, speedDataLen);
            Frames.Enqueue(mFrame);

            // Wait until Queue not Full or Stopped
            if (Frames.Count >= Config.Decoder.MaxAudioFrames * cBufTimesCur)
            {
                Monitor.Exit(lockCodecCtx);
                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueFull;

                while (Frames.Count >= Config.Decoder.MaxAudioFrames * cBufTimesCur && Status == Status.QueueFull)
                    Thread.Sleep(20);

                Monitor.Enter(lockCodecCtx);

                lock (lockStatus)
                {
                    if (Status != Status.QueueFull)
                        return;

                    Status = Status.Running;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to process frame ({e.Message})");
        }
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
        int size    = Config.Decoder.MaxAudioFrames * samples * ASampleBytes * cBufTimesSize;
        
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

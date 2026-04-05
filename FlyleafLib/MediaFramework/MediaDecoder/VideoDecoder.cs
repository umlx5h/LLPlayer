using SharpGen.Runtime;

using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRemuxer;
using FlyleafLib.MediaFramework.MediaRenderer;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaPlayer;

namespace FlyleafLib.MediaFramework.MediaDecoder;

/* TBR
 * Missing locks (e.g. GetFrameNext) / Missing checks after locks (e.g. disposed) / Mixing locks (actions/demuxer/codecCtx/renderer)
 *  Currently no issues as we use locks at higher level
 *  
 * GetFrameNumberX: Still issues mainly with Prev, e.g. jumps from 279 to 281 frame | VFR / Timebase / FrameDuration / FPS inaccuracy
 *  Should use just GetFramePrev/Next and work with pts (but we currenlty work with Player.CurTime)
 *  
 * Open/Open2: Merge and review quick Setup/Full Dispose
 */

public unsafe class VideoDecoder : DecoderBase
{
    public Action           OpeningCodec;
    public Renderer         Renderer            { get; private set; }
    public bool             VideoAccelerated    { get; internal set; }

    public VideoStream      VideoStream         => (VideoStream) Stream;

    public long             StartTime           { get; internal set; } = AV_NOPTS_VALUE;
    public long             StartRecordTime     { get; internal set; } = AV_NOPTS_VALUE;

    internal bool           keyPacketRequired;
    internal bool           keyFrameRequired;   // Broken formats even with key packet don't return key frame
    internal bool           isIntraOnly;
    bool                    checkKeyFrame;
    bool                    swFallback;
    long                    startPts;
    long                    lastFixedPts;

    bool                    checkExtraFrames; // DecodeFrameNext
    int                     curFrameWidth, curFrameHeight; // To catch 'codec changed'

    // Hot paths / Same instance
    VideoCache              Frames;
    PacketQueue             vPackets;

    // Reverse Playback
    ConcurrentStack<List<nint>>
                            curReverseVideoStack    = [];
    List<nint>              curReverseVideoPackets  = [];
    List<VideoFrame>        curReverseVideoFrames   = [];
    int                     curReversePacketPos     = 0;

    // Drop frames if FPS is higher than allowed
    int                     curSpeedFrame           = 9999; // don't skip first frame (on start/after seek-flush)
    double                  skipSpeedFrames         = 0;

    // Fixes Seek Backwards failure on broken formats
    long                    curFixSeekDelta         = 0;
    const long              FIX_SEEK_DELTA_MCS      = 2_100_000;

    public VideoDecoder(Config config, int uniqueId = -1, bool createRenderer = true, Player player = null) : base(config, uniqueId)
    {
        getHWformat = new(GetFormat);

        if (createRenderer)
        {
            Renderer= new(this, UniqueId, player);
            Frames  = Renderer.Frames;
        }
    }

    #region Video Acceleration (Should be disposed seperately)
    public CodecSpec CurCodecSpec;
    AVCodecContext_get_format getHWformat;

    internal const AVPixelFormat    HW_PIX_FMT  = AVPixelFormat.D3d11;
    internal const AVHWDeviceType   HW_DEVICE   = AVHWDeviceType.D3d11va;

    public class CodecSpec
    {
        public string   Name;
        public AVCodec* Codec;
        public bool     IsHW;
        public bool     IsEmpty => Codec == null;

        internal static CodecSpec Empty = new();
    }
    static ConcurrentDictionary<AVCodecID,  CodecSpec> hwSpecs  = [];
    static ConcurrentDictionary<AVCodecID,  CodecSpec> swSpecs  = [];
    static ConcurrentDictionary<string,     CodecSpec> specs    = [];
    static CodecSpec FindHWDecoder(AVCodecID id)
    {
        if (hwSpecs.TryGetValue(id, out CodecSpec spec))
            return spec;

        AVCodec* codec, found = null;
        void* opaque = null;
        while ((codec = av_codec_iterate(ref opaque)) != null)
        {
            if (codec->id != id || av_codec_is_decoder(codec) == 0)
                continue;

            int i = 0;
            AVCodecHWConfig* config;
            while((config = avcodec_get_hw_config(codec, i++)) != null)
                if (config->pix_fmt == HW_PIX_FMT && config->methods.HasFlag(AVCodecHwConfigMethod.HwDeviceCtx))
                {
                    spec = new() { Codec = codec, Name = BytePtrToStringUTF8(codec->name), IsHW = true};
                    hwSpecs[codec->id] = spec;
                    return spec;
                }
        }

        hwSpecs[id] = CodecSpec.Empty;
        return CodecSpec.Empty;
    }
    static CodecSpec FindSWDecoder(AVCodecID id)
    {
        if (swSpecs.TryGetValue(id, out CodecSpec spec))
            return spec;

        AVCodec* codec = avcodec_find_decoder(id);
        spec = codec != null ? new() { Codec = codec, Name = BytePtrToStringUTF8(codec->name) } : CodecSpec.Empty;
        swSpecs[codec->id] = spec;
        return spec;
    }
    static CodecSpec FindDecoder(string name)
    {
        if (specs.TryGetValue(name, out CodecSpec spec))
            return spec;

        AVCodec* codec = avcodec_find_decoder_by_name(name);
        if (codec == null)
        {
            specs[name] = CodecSpec.Empty;
            return CodecSpec.Empty;
        }

        bool isHW = false;
        int i = 0;
        AVCodecHWConfig* config;
        while((config = avcodec_get_hw_config(codec, i++)) != null)
            if (config->pix_fmt == HW_PIX_FMT && config->methods.HasFlag(AVCodecHwConfigMethod.HwDeviceCtx))
            {
                isHW = true;
                break;
            }

        spec = new() { Codec = codec, Name = BytePtrToStringUTF8(codec->name), IsHW = isHW };
        specs[name] = spec;
        return spec;
    }

    private AVPixelFormat GetFormat(AVCodecContext* avctx, AVPixelFormat* pix_fmts)
    {
        if (CanDebug)
        {
            Log.Debug($"Codec profile '{avcodec_profile_name(codecCtx->codec_id, codecCtx->profile)}'");

            if (CanTrace)
            {
                var save = pix_fmts;
                while (*pix_fmts != AVPixelFormat.None)
                {
                    Log.Trace($"{*pix_fmts}");
                    pix_fmts++;
                }
                pix_fmts = save;
            }
        }

        bool foundHWformat = false;

        while (*pix_fmts != AVPixelFormat.None)
        {
            if ((*pix_fmts) == HW_PIX_FMT)
            {
                foundHWformat = true;
                break;
            }

            pix_fmts++;
        }

        if (codecCtx->hw_frames_ctx != null)
            av_buffer_unref(&codecCtx->hw_frames_ctx);

        if (!foundHWformat || !Renderer.ConfigHWFrames())
        {
            Log.Info("HW decoding failed");
            swFallback = true;
            return avcodec_default_get_format(avctx, pix_fmts);
        }

        codecCtx->hw_frames_ctx = av_buffer_ref(Renderer.ffFrames);


        return HW_PIX_FMT;
    }
    #endregion

    protected override bool Setup()
    {
        if (Renderer.Disposed)
            Renderer.Setup();

        VideoAccelerated = !swFallback && Renderer.ffDevice != null && Config.Video.VideoAcceleration;

        OpeningCodec?.Invoke();

        if (!string.IsNullOrEmpty(Config.Decoder._VideoCodec))
            CurCodecSpec = FindDecoder(Config.Decoder._VideoCodec);
        else if (VideoAccelerated)
        {
            CurCodecSpec = FindHWDecoder(Stream.CodecID);
            if (CurCodecSpec.IsEmpty)
            {
                if (CanDebug) Log.Debug($"HW decoding not supported for {Stream.CodecID}");
                CurCodecSpec = FindSWDecoder(Stream.CodecID);
            }
        }
        else
            CurCodecSpec = FindSWDecoder(Stream.CodecID);

        if (CurCodecSpec.IsEmpty)
        {
            Log.Error($"Decoder not found ({(!string.IsNullOrEmpty(Config.Decoder._VideoCodec) ? Config.Decoder._VideoCodec : Stream.CodecID)})");
            return false;
        }

        codecCtx = avcodec_alloc_context3(CurCodecSpec.Codec); // Pass codec to use default settings
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
        codecCtx->codec_id      = CurCodecSpec.Codec->id; // avcodec_parameters_to_context will change this we need to set Stream's Codec Id (eg we change mp2 to mp3)
        codecCtx->apply_cropping= 0;

        if (Config.Decoder.ShowCorrupted)
            codecCtx->flags |= CodecFlags.OutputCorrupt;

        if (Config.Decoder.LowDelay)
        {
            if (Config.Decoder.AllowDropFrames)
                codecCtx->flags |= CodecFlags.LowDelay;
            else
            {
                codecCtx->skip_frame = AVDiscard.None;
                codecCtx->flags2 |= CodecFlags2.Fast;
            }
        }
        else if (!Config.Decoder.AllowDropFrames)
            codecCtx->skip_frame = AVDiscard.None;

        var codecOpts = Config.Decoder.VideoCodecOpt;
        AVDictionary* avopt = null;
        foreach(var optKV in codecOpts)
            _ = av_dict_set(&avopt, optKV.Key, optKV.Value, 0);

        VideoAccelerated = VideoAccelerated && CurCodecSpec.IsHW;

        if (VideoAccelerated)
        {
            /* TODO: Frame threading [codecCtx->thread_type = ThreadTypeFlags.Frame]
             * Possible requires patching FFmpeg to pass BindFlags.ShaderResource to new allocated textures (get_buffer maybe?)
             * Seems to work fine with D3D11VP (if we pass the right texture from frame->data[0])
             */

            codecCtx->thread_count      = 1;
            codecCtx->hwaccel_flags    |= HWAccelFlags.IgnoreLevel;
            if (Config.Decoder.AllowProfileMismatch)
                codecCtx->hwaccel_flags|= HWAccelFlags.AllowProfileMismatch;
            codecCtx->get_format        = getHWformat;
            codecCtx->hw_device_ctx     = av_buffer_ref(Renderer.ffDevice);
            codecCtx->extra_hw_frames   = Config.Decoder.MaxVideoFrames + 1; // 1 extra for Renderer's LastFrame
        }
        else
            codecCtx->thread_count      = Math.Min(Config.Decoder.VideoThreads, codecCtx->codec_id == AVCodecID.Hevc ? 32 : 16);

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

        if (codecCtx->codec_descriptor != null)
            isIntraOnly = codecCtx->codec_descriptor->props.HasFlag(CodecPropFlags.IntraOnly);

        vPackets            = demuxer.VideoPackets;
        keyFrameRequired    = keyPacketRequired = false; // allow no key packet after open (lot of videos missing this)
        filledFromCodec     = false;
        isDraining          = false;
        lastFixedPts        = 0; // TBR: might need to set this to first known pts/dts
        startPts            = VideoStream.StartTimePts;
        allowedErrors       = Config.Decoder.MaxErrors;

        // Not all codecs fill key frame flag | https://github.com/SuRGeoNix/Flyleaf/issues/638 | Old MOV/MP4 container marking packets loosely as key
        checkKeyFrame       = codecCtx->codec_id != AVCodecID.Av1 &&
                             (VideoAccelerated ||
                              codecCtx->codec_id != AVCodecID.Vp8 && codecCtx->codec_id != AVCodecID.Vp9 && codecCtx->codec_id != AVCodecID.Qtrle);

        if (CanDebug) Log.Debug($"Using {CurCodecSpec.Name} {(VideoAccelerated ? "(HW)" : "(SW)")}");

        return true;
    }

    internal void Flush()
    {
        lock (lockActions)
            lock (lockCodecCtx)
            {
                if (Disposed)
                    return;

                if (Status == Status.Ended)
                    Status = Status.Stopped;

                DisposeFrames();
                avcodec_flush_buffers(codecCtx);

                isDraining          = false;
                keyFrameRequired    = false;
                keyPacketRequired   = !isIntraOnly;
                StartTime           = AV_NOPTS_VALUE;
                curSpeedFrame       = 9999;
            }
    }

    #region Run Loop
    int allowedErrors;
    bool isDraining;
    protected override void RunInternal()
    {
        if (demuxer.IsReversePlayback)
        {
            RunInternalReverse();
            return;
        }

        int sleepMs = Config.Player.MaxLatency == 0 ? 10 : 2;
        int ret;
        AVPacket *packet;

        do
        {
            // Wait until Queue not Full or Stopped
            if (Frames.Count >= Config.Decoder.MaxVideoFrames)
            {
                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueFull;

                while (Frames.Count >= Config.Decoder.MaxVideoFrames && Status == Status.QueueFull)
                    Thread.Sleep(sleepMs);

                lock (lockStatus)
                {
                    if (Status != Status.QueueFull) break;
                    Status = Status.Running;
                }
            }

            // While Packets Queue Empty (Drain | Quit if Demuxer stopped | Wait until we get packets)
            if (vPackets.IsEmpty && !isDraining)
            {
                CriticalArea = true;

                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueEmpty;

                while (vPackets.IsEmpty && Status == Status.QueueEmpty)
                {
                    if (demuxer.Status == Status.Ended)
                    {
                        lock (lockStatus)
                        {
                            Log.Debug("Draining");
                            isDraining          = true;
                            var drainPacket     = av_packet_alloc();
                            drainPacket->data   = null;
                            drainPacket->size   = 0;
                            vPackets.Enqueue(drainPacket);
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
                    if (Status != Status.QueueEmpty) break;
                    Status = Status.Running;
                }
            }

            // RecvFrame | GetPacket | SendPacket
            lock (lockCodecCtx)
            {
                if (Status == Status.Stopped)
                    continue;

                if (!keyPacketRequired)
                {
                    ret = RecvAVFrame();
                    if (ret == 0)
                    {
                        if (FillEnqueueAVFrame() == -1234)
                        {
                            Status = Status.Stopping;
                            break;
                        }

                        continue;
                    }
                    else if (ret != AVERROR_EAGAIN)
                    {
                        if (ret == -1234)
                            Status = Status.Stopping;

                        break; // else EOF
                    }
                }
                
                packet = vPackets.Dequeue();

                if (packet == null)
                    continue;

                if (isRecording)
                {
                    if (!recKeyPacketRequired && (packet->flags & PktFlags.Key) != 0)
                    {
                        recKeyPacketRequired = true;
                        StartRecordTime = (long)(packet->pts * VideoStream.Timebase) - demuxer.StartTime;
                    }

                    if (recKeyPacketRequired)
                        curRecorder.Write(av_packet_clone(packet));
                }

                ret = SendAVPacket(packet);
                if (ret != 0)
                {
                    if (ret == AVERROR_EAGAIN)
                    {   // Fast retry => Legitimate decoding errors | Waiting for key packet
                        while (Status == Status.Running && ret == AVERROR_EAGAIN && (packet = vPackets.Dequeue()) != null)
                            ret = SendAVPacket(packet); // TBR: Should record those?

                        if (ret == 0 || packet == null)
                            continue;
                    }

                    if (ret == -1234)
                        Status = Status.Stopping;

                    break; // else EOF
                }
            }

        } while (Status == Status.Running);

        if (isRecording)
        {
            StopRecording();
            recCompleted(MediaType.Video);
        }
    }
    internal int SendAVPacket(AVPacket* packet)
    {   /* Sends the provided packet to the decoder (avcodec_send_packet) | Should be used as tied to Run Loop (vPackets[] / Frames[])
         * - Key Packet / Frame Validations
         * - Software Fallback
         * - Global Errors Counter
         * 
         * Returns
         *  0       : Call RecvAVFrame  (Success | HasMoreOutput)   * Ideally we should not dipose packet when more output (but should not happen with current design)
         *  EAGAIN  : Call SendAVPacket (Ignored)                   * Invalid Packet, send next one
         *  EOF     : Quit Loop
         *  -1234   : Quit Loop         (Critical)                  * E.g. Status = Stopping
         */

        if (keyPacketRequired)
        {
            if (!packet->flags.HasFlag(PktFlags.Key) && packet->pts != startPts)
            {   // https://trac.ffmpeg.org/ticket/9412 | HEVC fails to seek at key packet (Fixed?) | Don't treat as error (?)
                if (CanDebug) Log.Debug("Ignoring non-key packet");
                av_packet_free(&packet);
                return AVERROR_EAGAIN;
                
            }

            keyFrameRequired  = checkKeyFrame && packet->pts != startPts;
            keyPacketRequired = false;
        }

        // TBR: AVERROR(EAGAIN) ideally we keep the packet and resend it after recv (it shouldn't happen at all as we keep track)
        int ret = avcodec_send_packet(codecCtx, packet);

        if (swFallback)
        {   // Could happen during (VA) GetFormat (called by avcodec_send_packet)
            SWFallback();
            ret = avcodec_send_packet(codecCtx, packet);
        }

        av_packet_free(&packet);

        if (ret == 0 || ret == AVERROR_EAGAIN)
            return 0;

        // TBR: Possible check for VA failed here (normally this will happen during get_format)

        if (ret == AVERROR_EOF)
        {
            if (!vPackets.IsEmpty) { avcodec_flush_buffers(codecCtx); return AVERROR_EAGAIN; } // TBR: Happens on HLS while switching video streams
            Status = Status.Ended;
            return AVERROR_EOF;
        }

        if (ret == AVERROR_ENOMEM) { Log.Error($"{FFmpegEngine.ErrorCodeToMsg(ret)}"); return -1234; }

        allowedErrors--;
        if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

        if (allowedErrors == 0) { Log.Error("Too many errors!"); return -1234; }

        return AVERROR_EAGAIN;
    }
    internal int RecvAVFrame()
    {   /* Receives frame from the decoder (avcodec_receive_frame) | Should be used as tied to Run Loop (vPackets[] / Frames[])
         * - Key Frame Validation
         * - Codec Change
         * - Fix Timestamps
         * - Fill Stream From Codec
         * - Skip Frames
         * - Global Errors Counter
         * 
         * Returns
         *  0       : Call RecvAVFrame  (Success)           * Try for more output
         *  EAGAIN  : Call SendAVPacket (NeedsMoreInput)    * Invalid Packet, send next one
         *  EOF     : Quit Loop         (Ended)             * Drained
         *  -1234   : Quit Loop         (Critical)          * E.g. Status = Stopping
         */
        int ret = avcodec_receive_frame(codecCtx, frame);
        if (ret != 0)
        {
            if (ret == AVERROR_EAGAIN)
                return AVERROR_EAGAIN;

            if (ret == AVERROR_EOF)
            {
                if (!vPackets.IsEmpty) { avcodec_flush_buffers(codecCtx); return AVERROR_EAGAIN; } // TBR: Happens on HLS while switching video streams
                Status = Status.Ended;
                return AVERROR_EOF;
            }

            if (ret == AVERROR_ENOMEM || ret == AVERROR_EINVAL)
                { Log.Error($"{FFmpegEngine.ErrorCodeToMsg(ret)}"); return -1234; }

            allowedErrors--;
            if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

            if (allowedErrors == 0) { Log.Error("Too many errors!"); return -1234; }

            return RecvAVFrame(); // TBR maybe try another packet EAGAIN
        }

        if (keyFrameRequired)
        {
            if (!frame->flags.HasFlag(FrameFlags.Key))
            {
                if (CanInfo) Log.Info("Ignoring non-key frame");
                av_frame_unref(frame);
                return RecvAVFrame();
            }
            
            keyFrameRequired = false;
        }

        if ((frame->height != curFrameHeight || frame->width != curFrameWidth) && filledFromCodec)
        {
            filledFromCodec = false;
            Log.Warn($"Codec changed {VideoStream.CodecID} {curFrameWidth}x{curFrameHeight} => {codecCtx->codec_id} {frame->width}x{frame->height}");
        }

        if (frame->best_effort_timestamp != AV_NOPTS_VALUE)
            frame->pts = frame->best_effort_timestamp;

        else if (frame->pts == AV_NOPTS_VALUE)
        {
            if (!VideoStream.FixTimestamps && VideoStream.Duration > TimeSpan.FromSeconds(1).Ticks)
            {
                // TBR: it is possible to have a single frame / image with no dts/pts which actually means pts = 0 ? (ticket_3449.264) - GenPts will not affect it
                // TBR: first frame might no have dts/pts which probably means pts = 0 (and not start time!)
                av_frame_unref(frame);
                return RecvAVFrame();
            }

            // Create timestamps for h264/hevc raw streams (Needs also to handle this with the remuxer / no recording currently supported!)
            frame->pts = lastFixedPts + VideoStream.StartTimePts;
            lastFixedPts += av_rescale_q(VideoStream.FrameDuration / 10, Engine.FFmpeg.AV_TIMEBASE_Q, VideoStream.AVStream->time_base);
        }

        if (!filledFromCodec) // Ensures we have a proper frame before filling from codec
        {
            ret = FillFromCodec(frame);
            if (ret == -1234)
                return -1234;
        }

        if (skipSpeedFrames > 1)
        {
            curSpeedFrame++;
            if (curSpeedFrame < skipSpeedFrames)
            {
                av_frame_unref(frame);
                return RecvAVFrame();
            }
            curSpeedFrame = 0;
        }

        return 0;
    }
    VideoFrame FillAVFrame()
    {   // Renderer.FillPlanes with error handling
        VideoFrame mFrame = null;

        try
        {
            mFrame = Renderer.FillPlanes(ref frame);
        }
        catch(SharpGenException e)
        {
            Log.Error($"FillAVFrame failed ({e.ResultCode.NativeApiCode} | {Renderer.Device.DeviceRemovedReason.NativeApiCode} | {e.Message})");
            ResetLocal();
        }
        catch (Exception ex)
        {
            Log.Error($"FillAVFrame failed ({ex.Message})");
            av_frame_unref(frame);
        }

        return mFrame;
    }
    internal int FillEnqueueAVFrame()
    {
        VideoFrame mFrame = FillAVFrame();

        if (mFrame != null)
        {
            allowedErrors = Config.Decoder.MaxErrors;

            if (StartTime == NoTs)
                StartTime = mFrame.Timestamp;

            Frames.Enqueue(mFrame);

            return 0;
        }

        allowedErrors--;
        if (allowedErrors == 0) { Log.Error("Too many errors!"); return -1234; }

        return AVERROR_EAGAIN; // currently same as 0
    }
    void ResetLocal()
    {   // Silent Dispose + Renderer Reset + Reopen (TBR: locks / can't pasuse player from here)
        DisposeInternal();
        if (codecCtx != null)
        {
            fixed (AVCodecContext** ptr = &codecCtx)
                avcodec_free_context(ptr);

            codecCtx = null;
        }
        Renderer.Reset(pausePlayer: false, fromDecoder: true);
        Open2(Stream, null, false);
        keyPacketRequired   = !isIntraOnly;
        keyFrameRequired    = false;
    }
    #endregion

    internal int FillFromCodec(AVFrame* frame)
    {
        filledFromCodec = true;
        curFixSeekDelta = 0;
        curFrameWidth   = frame->width;
        curFrameHeight  = frame->height;

        VideoStream.Refresh(this, frame);
        startPts        = VideoStream.StartTimePts;
        skipSpeedFrames = speed * VideoStream.FPS / (Config.Video.MaxOutputFps + 1);

        int ret = 0;

        if (VideoStream.PixelFormat == AVPixelFormat.None)
        {
            Log.Error("PixelFormat unknown");
            ret = -1234;
        }
        else
        {
            try
            {
                Renderer.VPConfig(VideoStream, frame);
            }
            catch (Exception ex)
            {
                Log.Error($"VPConfig failed ({ex.Message})");
                ret = -1234;
            }
        }

        CodecChanged?.Invoke(this);

        return ret;
    }

    internal bool SWFallback()
    {
        bool ret;

        DisposeInternal();
        if (codecCtx != null)
            fixed (AVCodecContext** ptr = &codecCtx)
                avcodec_free_context(ptr);

        codecCtx            = null;
        swFallback          = true;
        bool keyRequiredOld = keyPacketRequired;
        ret = Open2(Stream, null, false); // TBR:  Dispose() on failure could cause a deadlock
        keyPacketRequired   = keyRequiredOld;
        keyFrameRequired    = false;
        swFallback          = false;
        filledFromCodec     = false;

        return ret;
    }

    private void RunInternalReverse()
    {   // BUG: with B-frames, we should not remove the ref packets (we miss frames each time we restart decoding the gop)
        int ret = 0;
        int allowedErrors = Config.Decoder.MaxErrors;
        AVPacket *packet;

        do
        {
            // While Packets Queue Empty (Drain | Quit if Demuxer stopped | Wait until we get packets)
            if (demuxer.VideoPacketsReverse.IsEmpty && curReverseVideoStack.IsEmpty && curReverseVideoPackets.Count == 0)
            {
                CriticalArea = true;

                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueEmpty;

                while (demuxer.VideoPacketsReverse.IsEmpty && Status == Status.QueueEmpty)
                {
                    if (demuxer.Status == Status.Ended) // TODO
                    {
                        lock (lockStatus) Status = Status.Ended;

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

            if (curReverseVideoPackets.Count == 0)
            {
                if (curReverseVideoStack.IsEmpty)
                    demuxer.VideoPacketsReverse.TryDequeue(out curReverseVideoStack);

                curReverseVideoStack.TryPop(out curReverseVideoPackets);
                curReversePacketPos = 0;
            }

            while (curReverseVideoPackets.Count > 0 && Status == Status.Running)
            {
                // Wait until Queue not Full or Stopped
                if (Frames.Count + curReverseVideoFrames.Count >= Config.Decoder.MaxVideoFrames)
                {
                    lock (lockStatus)
                        if (Status == Status.Running) Status = Status.QueueFull;

                    while (Frames.Count + curReverseVideoFrames.Count >= Config.Decoder.MaxVideoFrames && Status == Status.QueueFull) Thread.Sleep(20);

                    lock (lockStatus)
                    {
                        if (Status != Status.QueueFull) break;
                        Status = Status.Running;
                    }
                }

                lock (lockCodecCtx)
                {
                    if (keyPacketRequired)
                    {
                        keyPacketRequired = false;
                        curReversePacketPos = 0;
                        break;
                    }

                    packet = (AVPacket*)curReverseVideoPackets[curReversePacketPos++];
                    ret = avcodec_send_packet(codecCtx, packet);

                    if (ret != 0 && ret != AVERROR(EAGAIN))
                    {
                        if (ret == AVERROR_EOF) { Status = Status.Ended; break; }

                        if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                        allowedErrors--;
                        if (allowedErrors == 0) { Log.Error("Too many errors!"); Status = Status.Stopping; break; }

                        for (int i = curReverseVideoPackets.Count - 1; i >= curReversePacketPos - 1; i--)
                        {
                            packet = (AVPacket*)curReverseVideoPackets[i];
                            av_packet_free(&packet);
                            curReverseVideoPackets[curReversePacketPos - 1] = 0;
                            curReverseVideoPackets.RemoveAt(i);
                        }

                        avcodec_flush_buffers(codecCtx);
                        curReversePacketPos = 0;

                        for (int i = curReverseVideoFrames.Count - 1; i >= 0; i--)
                            Frames.Enqueue(curReverseVideoFrames[i]);

                        curReverseVideoFrames.Clear();

                        continue;
                    }

                    while (true)
                    {
                        ret = avcodec_receive_frame(codecCtx, frame);
                        if (ret != 0) { av_frame_unref(frame); break; }

                        if (frame->best_effort_timestamp != AV_NOPTS_VALUE)
                            frame->pts = frame->best_effort_timestamp;
                        else if (frame->pts == AV_NOPTS_VALUE)
                            { av_frame_unref(frame); continue; }

                        bool shouldProcess = curReverseVideoPackets.Count - curReversePacketPos < Config.Decoder.MaxVideoFrames - Config.Decoder.MaxVideoFramesPrev; // TBR: Back Cache* (probably should add this somewhere else too

                        if (shouldProcess)
                        {
                            av_packet_free(&packet);
                            curReverseVideoPackets[curReversePacketPos - 1] = 0;
                            var mFrame = FillAVFrame();
                            if (mFrame != null)
                                curReverseVideoFrames.Add(mFrame);
                            else
                            {
                                allowedErrors--;
                                if (allowedErrors == 0) { Log.Error("Too many errors!"); Status = Status.Stopping; break; }
                            }
                        }
                        else
                            av_frame_unref(frame);
                    }

                    if (curReversePacketPos == curReverseVideoPackets.Count)
                    {
                        curReverseVideoPackets.RemoveRange(Math.Max(0, Config.Decoder.MaxVideoFramesPrev + curReverseVideoPackets.Count - Config.Decoder.MaxVideoFrames), Math.Min(curReverseVideoPackets.Count, Config.Decoder.MaxVideoFrames - Config.Decoder.MaxVideoFramesPrev) );
                        avcodec_flush_buffers(codecCtx);
                        curReversePacketPos = 0;

                        for (int i = curReverseVideoFrames.Count - 1; i >= 0; i--)
                            Frames.Enqueue(curReverseVideoFrames[i]);

                        curReverseVideoFrames.Clear();

                        break; // force recheck for max queues etc...
                    }

                } // Lock CodecCtx

            } // while curReverseVideoPackets.Count > 0

        } while (Status == Status.Running);

        if (Status != Status.Pausing && Status != Status.Paused)
            curReversePacketPos = 0;
    }

    public void RefreshMaxVideoFrames() // TODO: Transfer (all from Player?*) to renderer remove locks/check
    {
        lock (lockActions)
        {
            if (VideoStream == null)
                return;

            bool wasRunning = IsRunning;
            Renderer.ffFramesInfo.CodecId = AVCodecID.None; // TBR: force re-allocation
            Open(Stream);
            if (wasRunning)
                Start();
        }
    }

    protected override void OnSpeedChanged(double value)
    {
        if (VideoStream == null) return;
        speed = value;
        skipSpeedFrames = speed * VideoStream.FPS / (Config.Video.MaxOutputFps + 1); // Give 1 fps breath as some streams can be 60.x fps instead - cp->framerate vs av_guess_frame_rate- which one is right?)
    }

    /// <summary>
    /// Prevents to get the first frame after seek/flush
    /// </summary>
    public void ResetSpeedFrame()
        => curSpeedFrame = 0;

    /// <summary>
    /// Gets the frame number of a VideoFrame timestamp
    /// </summary>
    /// <param name="timestamp"></param>
    /// <returns></returns>
    public int GetFrameNumber(long timestamp)
        => Math.Max(0, (int)((timestamp + 2_0000 - VideoStream.StartTime + demuxer.StartTime) / VideoStream.FrameDuration));

    /// <summary>
    /// Gets the frame number of an AVFrame timestamp
    /// </summary>
    /// <param name="timestamp"></param>
    /// <returns></returns>
    public int GetFrameNumber2(long timestamp)
        => Math.Max(0, (int)((timestamp + 2_0000 - VideoStream.StartTime) / VideoStream.FrameDuration));

    /// <summary>
    /// Gets the VideoFrame timestamp from the frame number
    /// </summary>
    /// <param name="frameNumber"></param>
    /// <returns></returns>
    public long GetFrameTimestamp(int frameNumber)
        => VideoStream.StartTime + (frameNumber * VideoStream.FrameDuration);

    /// <summary>
    /// Performs accurate seeking to the requested VideoFrame and returns it
    /// </summary>
    /// <param name="frameNumber">Zero based frame index</param>
    /// <param name="backwards">Workaround for VFR for backwards frame stepping</param>
    /// <returns>The requested VideoFrame or null on failure</returns>
    public VideoFrame GetFrame(int frameNumber, bool backwards = false)
    {
        frameNumber = Math.Max(0, frameNumber);
        long requiredTimestamp = GetFrameTimestamp(frameNumber);
        long curSeekMcs = requiredTimestamp / 10;
        int curFrameNumber;

        do
        {
            demuxer.Pause();
            Pause();
            demuxer.Interrupter.SeekRequest();
            int ret = av_seek_frame(demuxer.FormatContext, -1, curSeekMcs - curFixSeekDelta, SeekFlags.Frame | SeekFlags.Backward);

            if (ret < 0)
                ret = av_seek_frame(demuxer.FormatContext, -1, Math.Max((curSeekMcs - (long)TimeSpan.FromSeconds(1).TotalMicroseconds) - curFixSeekDelta, demuxer.StartTime / 10), SeekFlags.Frame);

            demuxer.DisposePackets();

            if (demuxer.Status == Status.Ended)
                demuxer.Status = Status.Stopped;

            if (ret < 0)
                return null;

            Flush();
            checkExtraFrames = false;

            if (DecodeFrameNext() != 0)
                return null;

            curFrameNumber = GetFrameNumber2((long)(frame->pts * VideoStream.Timebase));

            if (curFrameNumber > frameNumber)
            {
                curFixSeekDelta += FIX_SEEK_DELTA_MCS;
                continue;
            }

            do
            {
                if (curFrameNumber >= frameNumber ||
                    (backwards && curFrameNumber + 2 >= frameNumber && GetFrameNumber2((long)(frame->pts * VideoStream.Timebase) + VideoStream.FrameDuration + (VideoStream.FrameDuration / 2)) - curFrameNumber > 1))
                {   // At least return a previous frame in case of Tb inaccuracy and don't stuck at the same frame
                    var mFrame = FillAVFrame();
                    if (mFrame != null)
                        return mFrame;
                }

                av_frame_unref(frame);
                if (DecodeFrameNext() != 0)
                    break;

                curFrameNumber = GetFrameNumber2((long)(frame->pts * VideoStream.Timebase));

            } while (true);

            return null;
        } while (true);
    }

    /// <summary>
    /// Gets next VideoFrame (Decoder/Demuxer must not be running)
    /// </summary>
    /// <returns>The next VideoFrame</returns>
    public VideoFrame GetFrameNext()
    {
        checkExtraFrames = true;

        if (DecodeFrameNext() == 0)
        {
            var mFrame = FillAVFrame();
            if (mFrame != null)
                return mFrame;
        }

        return null;
    }

    /// <summary>
    /// Pushes the decoder to the next available VideoFrame (Decoder/Demuxer must not be running)
    /// </summary>
    /// <returns></returns>
    public int DecodeFrameNext()
    {
        int ret;
        int allowedErrors = Config.Decoder.MaxErrors;

        if (checkExtraFrames)
        {
            if (Status == Status.Ended)
                return AVERROR_EOF;

            if (DecodeFrameNextInternal() == 0)
                return 0;

            if (demuxer.Status == Status.Ended && vPackets.IsEmpty && Frames.IsEmpty)
            {
                Stop(); // NOTE: Could be paused and will cause dead lock with Status ended
                Status = Status.Ended;
                return AVERROR_EOF;
            }

            checkExtraFrames = false;
        }

        while (true)
        {
            ret = demuxer.GetNextVideoPacket();
            if (ret != 0)
            {
                if (demuxer.Status != Status.Ended)
                    return ret;

                // Drain (TBR: probably only first drained working here)
                ret = avcodec_send_packet(codecCtx, demuxer.packet);
                av_packet_unref(demuxer.packet);

                if (ret != 0)
                    return AVERROR_EOF;

                checkExtraFrames = true;
                return DecodeFrameNext();
            }

            if (keyPacketRequired)
            {
                if (!demuxer.packet->flags.HasFlag(PktFlags.Key) && demuxer.packet->pts != startPts)
                {
                    if (CanDebug) Log.Debug("Ignoring non-key packet");
                    av_packet_unref(demuxer.packet);
                    continue;
                }

                keyFrameRequired  = checkKeyFrame && demuxer.packet->pts != startPts;
                keyPacketRequired = false;
            }

            ret = avcodec_send_packet(codecCtx, demuxer.packet);

            if (swFallback) // Should use 'global' packet to reset it in get_format (same packet should use also from DecoderContext)
            {
                SWFallback();
                ret = avcodec_send_packet(codecCtx, demuxer.packet);
            }

            av_packet_unref(demuxer.packet);

            if (ret != 0 && ret != AVERROR(EAGAIN))
            {
                if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                if (allowedErrors-- < 1)
                    { Log.Error("Too many errors!"); return ret; }

                continue;
            }

            if (DecodeFrameNextInternal() == 0)
            {
                checkExtraFrames = true;
                return 0;
            }
        }

    }
    private int DecodeFrameNextInternal()
    {
        int ret = avcodec_receive_frame(codecCtx, frame);
        if (ret != 0) { av_frame_unref(frame); return ret; }

        if (keyFrameRequired)
        {
            if (!frame->flags.HasFlag(FrameFlags.Key)) { av_frame_unref(frame); DecodeFrameNextInternal(); }
            keyFrameRequired = false;
        }

        if (frame->best_effort_timestamp != AV_NOPTS_VALUE)
            frame->pts = frame->best_effort_timestamp;

        else if (frame->pts == AV_NOPTS_VALUE)
        {
            if (!VideoStream.FixTimestamps)
            {
                av_frame_unref(frame);

                return DecodeFrameNextInternal();
            }

            frame->pts = lastFixedPts + VideoStream.StartTimePts;
            lastFixedPts += av_rescale_q(VideoStream.FrameDuration / 10, Engine.FFmpeg.AV_TIMEBASE_Q, VideoStream.AVStream->time_base);
        }

        if (StartTime == NoTs)
            StartTime = (long)(frame->pts * VideoStream.Timebase) - demuxer.StartTime;

        if (!filledFromCodec) // Ensures we have a proper frame before filling from codec
        {
            ret = FillFromCodec(frame);
            if (ret == -1234)
                return -1;
        }

        return 0;
    }

    #region Dispose
    // TODO: try to handle all from renderer* (requires reverse to embed in Frames)
    public void DisposeFrames()
    {
        Frames?.Reset();
        DisposeFramesReverse();
    }
    private void DisposeFramesReverse()
    {
        while (!curReverseVideoStack.IsEmpty)
        {
            curReverseVideoStack.TryPop(out var t2);
            for (int i = 0; i < t2.Count; i++)
            {
                if (t2[i] == 0) continue;
                AVPacket* packet = (AVPacket*)t2[i];
                av_packet_free(&packet);
            }
        }

        for (int i = 0; i < curReverseVideoPackets.Count; i++)
        {
            if (curReverseVideoPackets[i] == 0) continue;
            AVPacket* packet = (AVPacket*)curReverseVideoPackets[i];
            av_packet_free(&packet);
        }

        curReverseVideoPackets.Clear();

        for (int i = 0; i < curReverseVideoFrames.Count; i++)
            curReverseVideoFrames[i].Dispose();

        curReverseVideoFrames.Clear();
    }
    protected override void DisposeInternal()
    {   // Called by Dispose (lockActions) | TBR: lock (lockCodecCtx)?
        DisposeFrames();
        StartTime       = AV_NOPTS_VALUE;
        swFallback      = false;
        curSpeedFrame   = 9999;
    }
    #endregion

    #region Recording
    internal Action<MediaType> recCompleted;
    Remuxer curRecorder;
    bool recKeyPacketRequired;
    internal bool isRecording;

    internal void StartRecording(Remuxer remuxer)
    {
        if (Disposed || isRecording) return;

        StartRecordTime     = AV_NOPTS_VALUE;
        curRecorder         = remuxer;
        recKeyPacketRequired= false;
        isRecording         = true;
    }
    internal void StopRecording() => isRecording = false;
    #endregion
}

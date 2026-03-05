using System.Runtime.InteropServices;

using Vortice.DXGI;
using Vortice.Direct3D11;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRenderer;
using FlyleafLib.MediaFramework.MediaRemuxer;
using FlyleafLib.MediaFramework.MediaDemuxer;

namespace FlyleafLib.MediaFramework.MediaDecoder;

/* TBR
 * HWFrames should be handled by renderer (we keep ref on hwframes and on avframe) | We keep avframe ref (in LastFrame) but we unref the hwframes (might cause issues)
 * 
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

    public ConcurrentQueue<VideoFrame>
                            Frames              { get; protected set; } = [];
    public Renderer         Renderer            { get; private set; }
    public bool             VideoAccelerated    { get; internal set; }

    public VideoStream      VideoStream         => (VideoStream) Stream;

    public long             StartTime           { get; internal set; } = AV_NOPTS_VALUE;
    public long             StartRecordTime     { get; internal set; } = AV_NOPTS_VALUE;

    internal SwsContext*    swsCtx;
    nint                    swsBufferPtr;
    internal byte_ptrArray4 swsData;
    internal int_array4     swsLineSize;

    internal bool           swFallback;
    internal bool           keyPacketRequired;
    internal bool           keyFrameRequired;   // Broken formats even with key packet don't return key frame
    bool                    checkKeyFrame;
    internal bool           isIntraOnly;
    internal long           startPts;
    internal long           lastFixedPts;

    bool                    checkExtraFrames; // DecodeFrameNext
    int                     curFrameWidth, curFrameHeight; // To catch 'codec changed'

    // Hot paths / Same instance
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

    public VideoDecoder(Config config, int uniqueId = -1) : base(config, uniqueId)
        => getHWformat = new AVCodecContext_get_format(GetFormat);

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

    public void EnsureRenderer() // TBR: It should be in the constructor but DecoderContext will not work with null VideoDecoder for AudioOnly
    {
        if (Renderer == null)
            Renderer = new Renderer(this, 0, UniqueId);
        else if (Renderer.Disposed)
            Renderer.Initialize();
    }
    public void DestroyRenderer() => Renderer?.Dispose();
    public void CreateSwapChain(nint handle)
    {
        EnsureRenderer();
        Renderer.InitializeSwapChain(handle);
    }
    public void CreateSwapChain(Action<IDXGISwapChain2> swapChainWinUIClbk)
    {
        EnsureRenderer();
        Renderer.SwapChainWinUIClbk = swapChainWinUIClbk;
        if (Renderer.SwapChainWinUIClbk != null)
            Renderer.InitializeWinUISwapChain();

    }
    public void DestroySwapChain() => Renderer?.DisposeSwapChain();

    #region Video Acceleration (Should be disposed seperately)
    public CodecSpec            CurCodecSpec;
    const AVPixelFormat         PIX_FMT_HWACCEL = AVPixelFormat.D3d11;
    const AVHWDeviceType        HW_DEVICE       = AVHWDeviceType.D3d11va;
    internal ID3D11Texture2D    textureFFmpeg;
    AVCodecContext_get_format   getHWformat;
    AVBufferRef*                hw_device_ctx, hwframes;

    public class CodecSpec
    {
        public string   Name;
        public AVCodec* Codec;
        public bool     IsHW;
        public bool     IsEmpty => Codec == null;

        public static CodecSpec Empty = new();
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
                if (config->pix_fmt == PIX_FMT_HWACCEL && config->methods.HasFlag(AVCodecHwConfigMethod.HwDeviceCtx))
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
            if (config->pix_fmt == PIX_FMT_HWACCEL && config->methods.HasFlag(AVCodecHwConfigMethod.HwDeviceCtx))
            {
                isHW = true;
                break;
            }

        spec = new() { Codec = codec, Name = BytePtrToStringUTF8(codec->name), IsHW = isHW };
        specs[name] = spec;
        return spec;
    }

    bool InitVA()
    {
        AVHWDeviceContext*          device_ctx;
        AVD3D11VADeviceContext*     d3d11va_device_ctx;

        hw_device_ctx               = av_hwdevice_ctx_alloc(HW_DEVICE);
        device_ctx                  = (AVHWDeviceContext*) hw_device_ctx->data;
        d3d11va_device_ctx          = (AVD3D11VADeviceContext*) device_ctx->hwctx;
        d3d11va_device_ctx->device  = (Flyleaf.FFmpeg.ID3D11Device*) Renderer.Device.NativePointer;
        int ret                     = av_hwdevice_ctx_init(hw_device_ctx);

        if (ret != 0)
        {
            Log.Error($"VA Failed - {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

            fixed(AVBufferRef** ptr = &hw_device_ctx)
                av_buffer_unref(ptr);

            hw_device_ctx = null;
            return false;
        }

        Renderer.Device.AddRef(); // Important to give another reference for FFmpeg so we can dispose without issues

        return true;
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
            if ((*pix_fmts) == PIX_FMT_HWACCEL)
            {
                foundHWformat = true;
                break;
            }

            pix_fmts++;
        }

        int ret = ShouldAllocateNew();

        if (foundHWformat && ret == 0)
        {
            if (codecCtx->hw_frames_ctx == null && hwframes != null)
                codecCtx->hw_frames_ctx = av_buffer_ref(hwframes);

            return PIX_FMT_HWACCEL;
        }

        if (!foundHWformat || !VideoAccelerated || AllocateHWFrames() != 0)
        {
            // TBR: Do we keep ref of texture array? do we dispose it on all refs? otherwise make sure we inform to dispose frames before re-alloc
            Log.Info("HW decoding failed");
            swFallback = true;
            return avcodec_default_get_format(avctx, pix_fmts);
        }

        if (CanDebug)
            Log.Debug("HW frame allocation completed");

        if (ret == 2)                    
        {   // TBR: It seems that codecCtx changes but upcoming frame still has previous configuration (this will fire FillFromCodec twice, could cause issues?)
            filledFromCodec = false;
            codecChanged    = true;
            Log.Warn($"Codec changed {VideoStream.CodecID} {curFrameWidth}x{curFrameHeight} => {codecCtx->codec_id} {frame->width}x{frame->height}");
        }

        return PIX_FMT_HWACCEL;
    }
    private int ShouldAllocateNew() // 0: No, 1: Yes, 2: Yes+Codec Changed
    {
        if (hwframes == null)
            return 1;

        AVHWFramesContext* t2 = (AVHWFramesContext*) hwframes->data;

        if (codecCtx->coded_width != t2->width)
            return 2;

        if (codecCtx->coded_height != t2->height)
            return 2;

        return 0;
    }
    private int AllocateHWFrames()
    {
        if (hwframes != null)
            fixed(AVBufferRef** ptr = &hwframes)
                av_buffer_unref(ptr);

        hwframes = null;

        if (codecCtx->hw_frames_ctx != null)
            av_buffer_unref(&codecCtx->hw_frames_ctx);

        if (avcodec_get_hw_frames_parameters(codecCtx, codecCtx->hw_device_ctx, PIX_FMT_HWACCEL, &codecCtx->hw_frames_ctx) != 0)
            return -1;

        AVHWFramesContext* hw_frames_ctx = (AVHWFramesContext*)codecCtx->hw_frames_ctx->data;
        var requestedSize = hw_frames_ctx->initial_pool_size;
        AVD3D11VAFramesContext *va_frames_ctx = (AVD3D11VAFramesContext *)hw_frames_ctx->hwctx;
        va_frames_ctx->BindFlags  |= (uint)BindFlags.Decoder | (uint)BindFlags.ShaderResource;

        hwframes = av_buffer_ref(codecCtx->hw_frames_ctx);

        int ret = av_hwframe_ctx_init(codecCtx->hw_frames_ctx);
        if (ret == 0)
        {
            if (requestedSize != hw_frames_ctx->initial_pool_size)
            {
                codecCtx->extra_hw_frames = codecCtx->extra_hw_frames - Math.Abs(requestedSize - hw_frames_ctx->initial_pool_size); // should update this?*
                Log.Warn($"Allocated HW surfaces changed from {Config.Decoder.MaxVideoFrames} to {codecCtx->extra_hw_frames - 1}");
                Config.Decoder.SetMaxVideoFrames(codecCtx->extra_hw_frames - 1);
            }

            lock (Renderer.lockDevice)
            {
                textureFFmpeg = new((nint) va_frames_ctx->texture);
                filledFromCodec = false;
            }
        }

        return ret;
    }
    #endregion

    protected override bool Setup()
    {
        EnsureRenderer();
        //if (Renderer == null || Renderer.Device == null || hw_device_ctx != null) return false; // Should not happen*

        VideoAccelerated = !swFallback &&
                           !Config.Video.SwsForce &&
                            Config.Video.VideoAcceleration &&
                            Renderer.FeatureLevel >= Vortice.Direct3D.FeatureLevel.Level_10_0;

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

        VideoAccelerated = VideoAccelerated && CurCodecSpec.IsHW && InitVA();

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
            codecCtx->hw_device_ctx     = av_buffer_ref(hw_device_ctx);
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
                              codecCtx->codec_id != AVCodecID.Vp8 && codecCtx->codec_id != AVCodecID.Vp9);

        if (CanDebug) Log.Debug($"Using {CurCodecSpec.Name} {(VideoAccelerated ? "(HW)" : "(SW)")}");

        return true;
    }
    internal bool SetupSws()
    {
        Marshal.FreeHGlobal(swsBufferPtr);
        var fmt         = AVPixelFormat.Rgba;
        swsData         = new byte_ptrArray4();
        swsLineSize     = new int_array4();
        int outBufferSize
                        = av_image_get_buffer_size(fmt, codecCtx->width, codecCtx->height, 1);
        swsBufferPtr    = Marshal.AllocHGlobal(outBufferSize);
        _ = av_image_fill_arrays(ref swsData, ref swsLineSize, (byte*) swsBufferPtr, fmt, codecCtx->width, codecCtx->height, 1);
        swsCtx          = sws_getContext(codecCtx->coded_width, codecCtx->coded_height, codecCtx->pix_fmt, codecCtx->width, codecCtx->height, fmt, SwsFlags.None, null, null, null);

        if (swsCtx == null)
        {
            Log.Error($"Failed to allocate SwsContext");
            return false;
        }

        return true;
    }
    internal void Flush()
    {
        lock (lockActions)
            lock (lockCodecCtx)
            {
                if (Disposed) return;

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
                        if (FillAVFrame() == -1234)
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

        // GetFormat checks already for this but only for hardware accelerated (should also check for codec/fps* and possible reset sws if required)
        // Might use AVERROR_INPUT_CHANGED to let ffmpeg check for those (requires a flag to be set*)
        if ((frame->height != curFrameHeight || frame->width != curFrameWidth) && filledFromCodec) // could be already changed on getformat
        {
            codecChanged    = true;
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
    internal int FillAVFrame()
    {
        var mFrame = Renderer.FillPlanes(frame);
        if (mFrame != null)
        {
            allowedErrors = Config.Decoder.MaxErrors;

            if (StartTime == NoTs)
                StartTime = mFrame.timestamp;

            Frames.Enqueue(mFrame);

            return 0;
        }

        allowedErrors--;
        if (allowedErrors == 0) { Log.Error("Too many errors!"); return -1234; }

        HandleDeviceReset();

        return AVERROR_EAGAIN;
    }
    #endregion

    internal int FillFromCodec(AVFrame* frame)
    {
        lock (Renderer.lockDevice)
        {
            int ret = 0;

            filledFromCodec = true;
            curFixSeekDelta = 0;
            curFrameWidth   = frame->width;
            curFrameHeight  = frame->height;

            VideoStream.Refresh(this, frame);
            codecChanged    = false;
            startPts        = VideoStream.StartTimePts;
            skipSpeedFrames = speed * VideoStream.FPS / (Config.Video.MaxOutputFps + 1);
            
            if (VideoStream.PixelFormat == AVPixelFormat.None || !Renderer.ConfigPlanes(frame))
            {
                Log.Error("[Pixel Format] Unknown");
                CodecChanged?.Invoke(this);
                return -1234;
            }

            CodecChanged?.Invoke(this);
            return ret;
        }
    }

    internal bool handleDeviceReset; // Let Renderer decide when we reset (within RunInternal)
    internal void HandleDeviceReset()
    {
        if (!handleDeviceReset)
            return;

        handleDeviceReset = false;
        DisposeInternal();
        if (codecCtx != null)
        {
            fixed (AVCodecContext** ptr = &codecCtx)
                avcodec_free_context(ptr);

            codecCtx = null;
        }
        Renderer.Flush();
        Open2(Stream, null, false);
        keyPacketRequired   = !isIntraOnly;
        keyFrameRequired    = false;
    }

    internal bool SWFallback()
    {
        lock (Renderer.lockDevice)
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

                        bool shouldProcess = curReverseVideoPackets.Count - curReversePacketPos < Config.Decoder.MaxVideoFrames;

                        if (shouldProcess)
                        {
                            av_packet_free(&packet);
                            curReverseVideoPackets[curReversePacketPos - 1] = 0;
                            var mFrame = Renderer.FillPlanes(frame);
                            if (mFrame != null)
                                curReverseVideoFrames.Add(mFrame);
                            else if (handleDeviceReset)
                            {
                                HandleDeviceReset();
                                continue;
                            }
                        }
                        else
                            av_frame_unref(frame);
                    }

                    if (curReversePacketPos == curReverseVideoPackets.Count)
                    {
                        curReverseVideoPackets.RemoveRange(Math.Max(0, curReverseVideoPackets.Count - Config.Decoder.MaxVideoFrames), Math.Min(curReverseVideoPackets.Count, Config.Decoder.MaxVideoFrames) );
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

    public void RefreshMaxVideoFrames()
    {
        lock (lockActions)
        {
            if (VideoStream == null)
                return;

            bool wasRunning = IsRunning;
            Open(Stream);
            if (wasRunning)
                Start();
        }
    }

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
                    var mFrame = Renderer.FillPlanes(frame);
                    if (mFrame != null)
                        return mFrame;

                    HandleDeviceReset();
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
            var mFrame = Renderer.FillPlanes(frame);
            if (mFrame != null)
                return mFrame;
            
            HandleDeviceReset();
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
    public void DisposeFrames()
    {
        while (!Frames.IsEmpty)
        {
            Frames.TryDequeue(out var frame);
            DisposeFrame(frame);
        }

        DisposeFramesReverse();
    }
    private void DisposeFramesReverse()
    {
        while (!curReverseVideoStack.IsEmpty)
        {
            curReverseVideoStack.TryPop(out var t2);
            for (int i = 0; i<t2.Count; i++)
            {
                if (t2[i] == 0) continue;
                AVPacket* packet = (AVPacket*)t2[i];
                av_packet_free(&packet);
            }
        }

        for (int i = 0; i<curReverseVideoPackets.Count; i++)
        {
            if (curReverseVideoPackets[i] == 0) continue;
            AVPacket* packet = (AVPacket*)curReverseVideoPackets[i];
            av_packet_free(&packet);
        }

        curReverseVideoPackets.Clear();

        for (int i=0; i<curReverseVideoFrames.Count; i++)
            DisposeFrame(curReverseVideoFrames[i]);

        curReverseVideoFrames.Clear();
    }
    public static void DisposeFrame(VideoFrame frame)
    {
        if (frame == null)
            return;

        if (frame.textures != null)
        {
            for (int i=0; i<frame.textures.Length; i++)
                frame.textures[i].Dispose();

            frame.textures = null;
        }

        if (frame.srvs != null)
        {
            for (int i=0; i<frame.srvs.Length; i++)
                frame.srvs[i].Dispose();

            frame.srvs = null;
        }

        if (frame.avFrame != null)
            fixed(AVFrame** ptr = &frame.avFrame)
            av_frame_free(ptr);
    }
    protected override void DisposeInternal()
    {
        lock (lockCodecCtx)
        {
            DisposeFrames();

            if (hwframes != null)
                fixed(AVBufferRef** ptr = &hwframes)
                    av_buffer_unref(ptr);

            if (hw_device_ctx != null)
                fixed(AVBufferRef** ptr = &hw_device_ctx)
                    av_buffer_unref(ptr);

            if (swsCtx != null)
                sws_freeContext(swsCtx);

            hwframes    = null;
            hw_device_ctx
                        = null;
            swsCtx      = null;
            StartTime   = AV_NOPTS_VALUE;
            swFallback  = false;
            curSpeedFrame= 9999;
        }
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

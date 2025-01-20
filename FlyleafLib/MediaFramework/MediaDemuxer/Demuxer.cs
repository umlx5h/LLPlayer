using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;

using static Flyleaf.FFmpeg.ffmpegEx;

using FlyleafLib.MediaFramework.MediaProgram;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaPlayer;
using static FlyleafLib.Config;
using static FlyleafLib.Logger;

namespace FlyleafLib.MediaFramework.MediaDemuxer;

public unsafe class Demuxer : RunThreadBase
{
    /* TODO
     * 1) Review lockFmtCtx on Enable/Disable Streams causes delay and is not fully required
     * 2) Include AV_DISPOSITION_ATTACHED_PIC images in streams as VideoStream with flag
     *      Possible introduce ImageStream and include also video streams with < 1 sec duration (eg. mjpeg etc)
     */

    #region Properties
    public MediaType                Type            { get; private set; }
    public DemuxerConfig            Config          { get; set; }

    // Format Info
    public string                   Url             { get; private set; }
    public string                   Name            { get; private set; }
    public string                   LongName        { get; private set; }
    public string                   Extensions      { get; private set; }
    public string                   Extension       { get; private set; }
    public long                     StartTime       { get; private set; }
    public long                     StartRealTime   { get; private set; }
    public long                     Duration        { get; internal set; }
    public void ForceDuration(long duration) { Duration = duration; IsLive = duration != 0; }

    public Dictionary<string, string>
                                    Metadata        { get; internal set; } = new Dictionary<string, string>();

    /// <summary>
    /// The time of first packet in the queue (zero based, substracts start time)
    /// </summary>
    public long                     CurTime         => CurPackets.CurTime != 0 ? CurPackets.CurTime : lastSeekTime;

    /// <summary>
    /// The buffered time in the queue (last packet time - first packet time)
    /// </summary>
    public long                     BufferedDuration=> CurPackets.BufferedDuration;

    public bool                     IsLive          { get; private set; }
    public bool                     IsHLSLive       { get; private set; }

    public AVFormatContext*         FormatContext   => fmtCtx;
    public CustomIOContext          CustomIOContext { get; private set; }

    // Media Programs
    public ObservableCollection<Program>
                                    Programs        { get; private set; } = new ObservableCollection<Program>();

    // Media Streams
    public ObservableCollection<AudioStream>
                                    AudioStreams    { get; private set; } = new ObservableCollection<AudioStream>();
    public ObservableCollection<VideoStream>
                                    VideoStreams    { get; private set; } = new ObservableCollection<VideoStream>();
    public ObservableCollection<SubtitlesStream>
                                    SubtitlesStreamsAll
                                                    { get; private set; } = new ObservableCollection<SubtitlesStream>();
    public ObservableCollection<DataStream>
                                    DataStreams     { get; private set; } = new ObservableCollection<DataStream>();
    readonly object lockStreams = new();

    public List<int>                EnabledStreams  { get; private set; } = new List<int>();
    public Dictionary<int, StreamBase>
                                    AVStreamToStream{ get; private set; }

    public AudioStream              AudioStream     { get; private set; }
    public VideoStream              VideoStream     { get; private set; }
    // In the case of the secondary external subtitle, there is only one stream, but it goes into index=1 and index = 0 is null.
    public SubtitlesStream[]        SubtitlesStreams
                                                    { get; private set; }
    public DataStream               DataStream      { get; private set; }

    // Audio/Video Stream's HLSPlaylist
    internal HLSPlaylist*           HLSPlaylist     { get; private set; }

    // Media Packets
    public PacketQueue              Packets         { get; private set; }
    public PacketQueue              AudioPackets    { get; private set; }
    public PacketQueue              VideoPackets    { get; private set; }
    public PacketQueue[]            SubtitlesPackets
                                                    { get; private set; }
    public PacketQueue              DataPackets     { get; private set; }
    public PacketQueue              CurPackets      { get; private set; }

    public bool                     UseAVSPackets   { get; private set; }

    public PacketQueue GetPacketsPtr(StreamBase stream)
    {
        if (!UseAVSPackets)
        {
            return Packets;
        }

        switch (stream.Type)
        {
            case MediaType.Audio:
                return AudioPackets;
            case MediaType.Video:
                return VideoPackets;
            case MediaType.Subs:
                return SubtitlesPackets[SubtitlesSelectedHelper.CurIndex];
            default:
                return DataPackets;
        }
    }

    public ConcurrentQueue<ConcurrentStack<List<IntPtr>>>
                                    VideoPacketsReverse
                                                    { get; private set; } = new ConcurrentQueue<ConcurrentStack<List<IntPtr>>>();

    public bool                     IsReversePlayback
                                                    { get; private set; }

    public long                     TotalBytes      { get; private set; } = 0;

    // Interrupt
    public Interrupter              Interrupter     { get; private set; }

    public ObservableCollection<Chapter>
                                    Chapters        { get; private set; } = new ObservableCollection<Chapter>();
    public class Chapter
    {
        public long     StartTime   { get; set; }
        public long     EndTime     { get; set; }
        public string   Title       { get; set; }
    }

    public event EventHandler AudioLimit;
    bool audioBufferLimitFired;
    void OnAudioLimit()
        => Task.Run(() => AudioLimit?.Invoke(this, new EventArgs()));

    public event EventHandler TimedOut;
    internal void OnTimedOut()
        => Task.Run(() => TimedOut?.Invoke(this, new EventArgs()));
    #endregion

    #region Constructor / Declaration
    public AVPacket*        packet;
    AVFormatContext*        fmtCtx;
    internal HLSContext*    hlsCtx;

    long                    hlsPrevSeqNo            = AV_NOPTS_VALUE;   // Identifies the change of the m3u8 playlist (wraped)
    internal long           hlsStartTime            = AV_NOPTS_VALUE;   // Calculation of first timestamp (lastPacketTs - hlsCurDuration)
    long                    hlsCurDuration;                             // Duration until the start of the current segment
    long                    lastSeekTime;                               // To set CurTime while no packets are available

    public object           lockFmtCtx              = new();
    internal bool           allowReadInterrupts;

    /* Reverse Playback
     *
     * Video Packets Queue (FIFO)                       ConcurrentQueue<ConcurrentStack<List<IntPtr>>>
     *      Video Packets Seek Stacks (FILO)            ConcurrentStack<List<IntPtr>>
     *          Video Packets List Keyframe (List)      List<IntPtr>
     */

    long                    curReverseStopPts       = AV_NOPTS_VALUE;
    long                    curReverseStopRequestedPts
                                                    = AV_NOPTS_VALUE;
    long                    curReverseStartPts      = AV_NOPTS_VALUE;
    List<IntPtr>            curReverseVideoPackets  = new();
    ConcurrentStack<List<IntPtr>>
                            curReverseVideoStack    = new();
    long                    curReverseSeekOffset;

    // Required for passing AV Options and HTTP Query params to the underlying contexts
    AVFormatContext_io_open ioopen;
    AVFormatContext_io_open ioopenDefault;
    AVDictionary*           avoptCopy;
    Dictionary<string, string>
                            queryParams;
    byte[]                  queryCachedBytes;

    // for TEXT subtitles buffer
    byte*                   inputData;
    int                     inputDataSize;
    internal AVIOContext*   avioCtx;

    int subNum => Config.config.Subtitles.Max;

    public Demuxer(DemuxerConfig config, MediaType type = MediaType.Video, int uniqueId = -1, bool useAVSPackets = true) : base(uniqueId)
    {
        Config          = config;
        Type            = type;
        UseAVSPackets   = useAVSPackets;
        Interrupter     = new Interrupter(this);
        CustomIOContext = new CustomIOContext(this);

        Packets         = new PacketQueue(this);
        AudioPackets    = new PacketQueue(this);
        VideoPackets    = new PacketQueue(this);
        SubtitlesPackets = new PacketQueue[subNum];
        for (int i = 0; i < subNum; i++)
        {
            SubtitlesPackets[i] = new PacketQueue(this);
        }
        SubtitlesStreams = new SubtitlesStream[subNum];

        DataPackets     = new PacketQueue(this);
        CurPackets      = Packets; // Will be updated on stream switch in case of AVS

        string typeStr = Type == MediaType.Video ? "Main" : Type.ToString();
        threadName = $"Demuxer: {typeStr,5}";

        Utils.UIInvokeIfRequired(() =>
        {
            BindingOperations.EnableCollectionSynchronization(Programs,         lockStreams);
            BindingOperations.EnableCollectionSynchronization(AudioStreams,     lockStreams);
            BindingOperations.EnableCollectionSynchronization(VideoStreams,     lockStreams);
            BindingOperations.EnableCollectionSynchronization(SubtitlesStreamsAll, lockStreams);
            BindingOperations.EnableCollectionSynchronization(DataStreams,      lockStreams);

            BindingOperations.EnableCollectionSynchronization(Chapters,         lockStreams);
        });

        ioopen = IOOpen;
    }
    #endregion

    #region Dispose / Dispose Packets
    public void DisposePackets()
    {
        if (UseAVSPackets)
        {
            AudioPackets.Clear();
            VideoPackets.Clear();
            for (int i = 0; i < subNum; i++)
            {
                SubtitlesPackets[i].Clear();
            }
            DataPackets.Clear();

            DisposePacketsReverse();
        }
        else
            Packets.Clear();

        hlsStartTime = AV_NOPTS_VALUE;
    }

    public void DisposePacketsReverse()
    {
        while (!VideoPacketsReverse.IsEmpty)
        {
            VideoPacketsReverse.TryDequeue(out var t1);
            while (!t1.IsEmpty)
            {
                t1.TryPop(out var t2);
                for (int i = 0; i < t2.Count; i++)
                {
                    if (t2[i] == IntPtr.Zero) continue;
                    AVPacket* packet = (AVPacket*)t2[i];
                    av_packet_free(&packet);
                }
            }
        }

        while (!curReverseVideoStack.IsEmpty)
        {
            curReverseVideoStack.TryPop(out var t2);
            for (int i = 0; i < t2.Count; i++)
            {
                if (t2[i] == IntPtr.Zero) continue;
                AVPacket* packet = (AVPacket*)t2[i];
                av_packet_free(&packet);
            }
        }
    }
    public void Dispose()
    {
        if (Disposed)
            return;

        lock (lockActions)
        {
            if (Disposed)
                return;

            Stop();

            Url = null;
            hlsCtx = null;

            IsReversePlayback   = false;
            curReverseStopPts   = AV_NOPTS_VALUE;
            curReverseStartPts  = AV_NOPTS_VALUE;
            hlsPrevSeqNo        = AV_NOPTS_VALUE;
            lastSeekTime        = 0;

            // Free Streams
            lock (lockStreams)
            {
                AudioStreams.Clear();
                VideoStreams.Clear();
                SubtitlesStreamsAll.Clear();
                DataStreams.Clear();
                Programs.Clear();

                Chapters.Clear();
            }
            EnabledStreams.Clear();
            AudioStream         = null;
            VideoStream         = null;
            for (int i = 0; i < subNum; i++)
            {
                SubtitlesStreams[i] = null;
            }
            DataStream          = null;
            queryParams         = null;
            queryCachedBytes    = null;

            DisposePackets();

            if (fmtCtx != null)
            {
                Interrupter.CloseRequest();
                fixed (AVFormatContext** ptr = &fmtCtx) { avformat_close_input(ptr); fmtCtx = null; }
            }

            if (avoptCopy != null) fixed (AVDictionary** ptr = &avoptCopy) av_dict_free(ptr);
            if (packet != null) fixed (AVPacket** ptr = &packet) av_packet_free(ptr);

            CustomIOContext.Dispose();

            if (avioCtx != null)
            {
                av_free(avioCtx->buffer);
                fixed (AVIOContext** ptr = &avioCtx)
                {
                    avio_context_free(ptr);
                }

                inputData = null;
                inputDataSize = 0;
            }

            TotalBytes = 0;
            Status = Status.Stopped;
            Disposed = true;

            Log.Info("Disposed");
        }
    }
    #endregion

    #region Open / Seek / Run
    public string Open(string url)      => Open(url, null);
    public string Open(Stream stream)   => Open(null, stream);
    public string Open(string url, Stream stream)
    {
        bool    gotLockActions  = false;
        bool    gotLockFmtCtx   = false;
        string  error           = null;

        try
        {
            Monitor.Enter(lockActions,ref gotLockActions);
            Dispose();
            Monitor.Enter(lockFmtCtx, ref gotLockFmtCtx);
            Url = url;

            if (string.IsNullOrEmpty(url) && stream == null)
                return "Invalid empty/null input";

            Dictionary<string, string>
                            fmtOptExtra = null;
            AVInputFormat*  inFmt       = null;
            int             ret         = -1;

            Disposed = false;
            Status   = Status.Opening;

            // Allocate / Prepare Format Context
            fmtCtx = avformat_alloc_context();
            if (Config.AllowInterrupts)
                fmtCtx->interrupt_callback.callback = Interrupter.interruptClbk;

            fmtCtx->flags |= (FmtFlags2)Config.FormatFlags;

            // Force Format (url as input and Config.FormatOpt for options)
            if (Config.ForceFormat != null)
            {
                inFmt = av_find_input_format(Config.ForceFormat);
                if (inFmt == null)
                    return error = $"[av_find_input_format] {Config.ForceFormat} not found";
            }

            // Force Custom IO Stream Context (url should be null)
            if (stream != null)
            {
                CustomIOContext.Initialize(stream);
                stream.Seek(0, SeekOrigin.Begin);
                url = null;
            }

            if (Type == MediaType.Subs &&
                Utils.ExtensionsSubtitlesText.Contains(Utils.GetUrlExtention(url)) &&
                File.Exists(url))
            {
                // If the files can be read with text subtitles, load them all into memory and convert them to UTF8.
                // Because ffmpeg expects UTF8 text.
                try
                {
                    FileInfo file = new(url);
                    if (file.Length >= 10 * 1024 * 1024)
                    {
                        throw new InvalidOperationException($"TEXT subtitle is too big (>=10MB) to load: {file.Length}");
                    }

                    // Detects character encoding, reads text and converts to UTF8
                    Encoding encoding = Encoding.Default;

                    Encoding detected = TextEncodings.DetectEncoding(url);
                    if (detected != null)
                    {
                        encoding = detected;
                    }

                    string content = File.ReadAllText(url, encoding);
                    byte[] contentBytes = Encoding.UTF8.GetBytes(content);

                    inputDataSize = contentBytes.Length;
                    inputData = (byte*)av_malloc((nuint)inputDataSize);

                    Span<byte> src = new(contentBytes);
                    Span<byte> dst = new(inputData, inputDataSize);
                    src.CopyTo(dst);

                    avioCtx = avio_alloc_context(inputData, inputDataSize, 0, null, null, null, null);
                    if (avioCtx == null)
                    {
                        throw new InvalidOperationException("avio_alloc_context");
                    }

                    // Pass to ffmpeg by on-memory
                    fmtCtx->pb = avioCtx;
                    fmtCtx->flags |= FmtFlags2.CustomIo;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not load text subtitles to memory: {ex.Message}");
                }
            }

            /* Force Format with Url syntax to support format, url and options within the input url
                *
                * fmt://$format$[/]?$input$&$options$
                *
                * deprecate support for device://
                *
                * Examples:
                *  See: https://ffmpeg.org/ffmpeg-devices.html for devices formats and options
                *
                * 1. fmt://gdigrab?title=Command Prompt&framerate=2
                * 2. fmt://gdigrab?desktop
                * 3. fmt://dshow?audio=Microphone (Relatek):video=Lenovo Camera
                * 4. fmt://rawvideo?C:\root\dev\Flyleaf\VideoSamples\rawfile.raw&pixel_format=uyvy422&video_size=1920x1080&framerate=60
                *
                */
            else if (url.StartsWith("fmt://") || url.StartsWith("device://"))
            {
                string  urlFromUrl  = null;
                string  fmtStr      = "";
                int     fmtStarts   = url.IndexOf('/') + 2;
                int     queryStarts = url.IndexOf('?');

                if (queryStarts == -1)
                    fmtStr = url[fmtStarts..];
                else
                {
                    fmtStr = url[fmtStarts..queryStarts];

                    string  query       = url[(queryStarts + 1)..];
                    int     inputEnds   = query.IndexOf('&');

                    if (inputEnds == -1)
                        urlFromUrl  = query;
                    else
                    {
                        urlFromUrl  = query[..inputEnds];
                        query       = query[(inputEnds + 1)..];

                        fmtOptExtra = Utils.ParseQueryString(query);
                    }
                }

                url     = urlFromUrl;
                fmtStr  = fmtStr.Replace("/", "");
                inFmt   = av_find_input_format(fmtStr);
                if (inFmt == null)
                    return error = $"[av_find_input_format] {fmtStr} not found";
            }
            else if (url.StartsWith("srt://"))
            {
                ReadOnlySpan<char> urlSpan = url.AsSpan();
                int queryPos = urlSpan.IndexOf('?');

                if (queryPos != -1)
                {
                    fmtOptExtra = Utils.ParseQueryString(urlSpan.Slice(queryPos + 1));
                    url = urlSpan[..queryPos].ToString();
                }
            }

            if (Config.FormatOptToUnderlying && url != null && (url.StartsWith("http://") || url.StartsWith("https://")))
            {
                queryParams = [];
                if (Config.DefaultHTTPQueryToUnderlying)
                {
                    int queryStarts = url.IndexOf('?');
                    if (queryStarts != -1)
                    {
                        var qp = Utils.ParseQueryString(url.AsSpan()[(queryStarts + 1)..]);
                        foreach (var kv in qp)
                            queryParams[kv.Key] = kv.Value;
                    }
                }

                foreach (var kv in Config.ExtraHTTPQueryParamsToUnderlying)
                    queryParams[kv.Key] = kv.Value;

                if (queryParams.Count > 0)
                {
                    var queryCachedStr = "?";
                    foreach (var kv in queryParams)
                        queryCachedStr += kv.Value == null ? $"{kv.Key}&" : $"{kv.Key}={kv.Value}&";

                    queryCachedStr = queryCachedStr[..^1];
                    queryCachedBytes = Encoding.UTF8.GetBytes(queryCachedStr);
                }
                else
                    queryParams = null;
            }

            // Some devices required to be opened from a UI or STA thread | after 20-40 sec. of demuxing -> [gdigrab @ 0000019affe3f2c0] Failed to capture image (error 6) or (error 8)
            bool isDevice = inFmt != null && inFmt->priv_class != null && (
                    inFmt->priv_class->category.HasFlag(AVClassCategory.DeviceAudioInput) ||
                    inFmt->priv_class->category.HasFlag(AVClassCategory.DeviceAudioOutput) ||
                    inFmt->priv_class->category.HasFlag(AVClassCategory.DeviceInput) ||
                    inFmt->priv_class->category.HasFlag(AVClassCategory.DeviceOutput) ||
                    inFmt->priv_class->category.HasFlag(AVClassCategory.DeviceVideoInput) ||
                    inFmt->priv_class->category.HasFlag(AVClassCategory.DeviceVideoOutput)
                    );

            // Open Format Context
            allowReadInterrupts = true; // allow Open interrupts always
            Interrupter.OpenRequest();

            // Nesting the io_open (to pass the options to the underlying formats)
            if (Config.FormatOptToUnderlying)
            {
                ioopenDefault = (AVFormatContext_io_open)Marshal.GetDelegateForFunctionPointer(fmtCtx->io_open.Pointer, typeof(AVFormatContext_io_open));
                fmtCtx->io_open = ioopen;
            }

            if (isDevice)
            {
                string fmtName = Utils.BytePtrToStringUTF8(inFmt->name);

                if (fmtName == "decklink") // avoid using UI thread for decklink (STA should be enough for CoInitialize/CoCreateInstance)
                    Utils.STAInvoke(() => OpenFormat(url, inFmt, fmtOptExtra, out ret));
                else
                    Utils.UIInvoke(() => OpenFormat(url, inFmt, fmtOptExtra, out ret));
            }
            else
                OpenFormat(url, inFmt, fmtOptExtra, out ret);

            if ((ret == AVERROR_EXIT && !Interrupter.Timedout) || Status != Status.Opening || Interrupter.ForceInterrupt == 1) { if (ret < 0) fmtCtx = null; return error = "Cancelled"; }
            if (ret < 0) { fmtCtx = null; return error = Interrupter.Timedout ? "[avformat_open_input] Timeout" : $"[avformat_open_input] {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})"; }

            // Find Streams Info
            if (Config.AllowFindStreamInfo)
            {
                // TBR: Tested and even if it requires more analyze duration it does not actually use it
                bool requiresMoreAnalyse = false;
                for (int i = 0; i < fmtCtx->nb_streams; i++)
                    if (fmtCtx->streams[i]->codecpar->codec_id == AVCodecID.HdmvPgsSubtitle ||
                        fmtCtx->streams[i]->codecpar->codec_id == AVCodecID.DvdSubtitle
                        )
                        { requiresMoreAnalyse = true; break; }

                if (requiresMoreAnalyse)
                {
                    fmtCtx->probesize = Math.Max(fmtCtx->probesize, 5000 * (long)1024 * 1024); // Bytes
                    fmtCtx->max_analyze_duration = Math.Max(fmtCtx->max_analyze_duration, 1000 * (long)1000 * 1000); // Mcs
                }

                ret = avformat_find_stream_info(fmtCtx, null);
                if (ret == AVERROR_EXIT || Status != Status.Opening || Interrupter.ForceInterrupt == 1) return error = "Cancelled";
                if (ret < 0) return error = $"[avformat_find_stream_info] {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})";
            }

            // Prevent Multiple Immediate exit requested on eof (maybe should not use avio_feof() to test for the end)
            if (fmtCtx->pb != null)
                fmtCtx->pb->eof_reached = 0;

            bool hasVideo = FillInfo();

            if (Type == MediaType.Video && !hasVideo && AudioStreams.Count == 0)
                return error = $"No audio / video stream found";
            else if (Type == MediaType.Audio && AudioStreams.Count == 0)
                return error = $"No audio stream found";
            else if (Type == MediaType.Subs && SubtitlesStreamsAll.Count == 0)
                return error = $"No subtitles stream found";

            packet = av_packet_alloc();
            Status = Status.Stopped;
            allowReadInterrupts = Config.AllowReadInterrupts && !Config.ExcludeInterruptFmts.Contains(Name);

            return error = null;
        }
        catch (Exception ex)
        {
            return error = $"Unknown: {ex.Message}";
        }
        finally
        {
            if (error != null)
                Dispose();

            if (gotLockFmtCtx)  Monitor.Exit(lockFmtCtx);
            if (gotLockActions) Monitor.Exit(lockActions);
        }
    }

    int IOOpen(AVFormatContext* s, AVIOContext** pb, byte* urlb, IOFlags flags, AVDictionary** avFmtOpts)
    {
        int ret;
        AVDictionaryEntry *t = null;

        if (avoptCopy != null)
        {
            while ((t = av_dict_get(avoptCopy, "", t, DictReadFlags.IgnoreSuffix)) != null)
                _ = av_dict_set(avFmtOpts, Utils.BytePtrToStringUTF8(t->key), Utils.BytePtrToStringUTF8(t->value), 0);
        }

        if (queryParams == null)
            ret = ioopenDefault(s, pb, urlb, flags, avFmtOpts);
        else
        {
            int urlLength   =  0;
            int queryPos    = -1;
            while (urlb[urlLength] != '\0')
            {
                if (urlb[urlLength] == '?' && queryPos == -1 && urlb[urlLength + 1] != '\0')
                    queryPos = urlLength;

                urlLength++;
            }

            // urlNoQuery + ? + queryCachedBytes
            if (queryPos == -1)
            {
                ReadOnlySpan<byte> urlNoQuery = new(urlb, urlLength);
                int         newLength   = urlLength + queryCachedBytes.Length + 1;
                Span<byte>  urlSpan     = newLength < 1024 ? stackalloc byte[newLength] : new byte[newLength];// new(urlPtr, newLength);
                urlNoQuery.CopyTo(urlSpan);
                queryCachedBytes.AsSpan().CopyTo(urlSpan[urlNoQuery.Length..]);

                fixed(byte* urlPtr =  urlSpan)
                    ret = ioopenDefault(s, pb, urlPtr, flags, avFmtOpts);
            }

            // urlNoQuery + ? + existingParams/queryParams combined
            else
            {
                ReadOnlySpan<byte> urlNoQuery   = new(urlb, queryPos);
                ReadOnlySpan<byte> urlQuery     = new(urlb + queryPos + 1, urlLength - queryPos - 1);
                var qps = Utils.ParseQueryString(Encoding.UTF8.GetString(urlQuery));

                foreach (var kv in queryParams)
                    if (!qps.ContainsKey(kv.Key))
                        qps[kv.Key] = kv.Value;

                string newQuery = "?";
                foreach (var kv in qps)
                    newQuery += kv.Value == null ? $"{kv.Key}&" : $"{kv.Key}={kv.Value}&";

                int         newLength   = urlNoQuery.Length + newQuery.Length + 1;
                Span<byte>  urlSpan     = newLength < 1024 ? stackalloc byte[newLength] : new byte[newLength];// new(urlPtr, newLength);
                urlNoQuery.CopyTo(urlSpan);
                Encoding.UTF8.GetBytes(newQuery).AsSpan().CopyTo(urlSpan[urlNoQuery.Length..]);

                fixed(byte* urlPtr =  urlSpan)
                    ret = ioopenDefault(s, pb, urlPtr, flags, avFmtOpts);
            }
        }

        return ret;
    }

    private void OpenFormat(string url, AVInputFormat* inFmt, Dictionary<string, string> opt, out int ret)
    {
        AVDictionary* avopt = null;
        var curOpt = Type == MediaType.Video ? Config.FormatOpt : (Type == MediaType.Audio ? Config.AudioFormatOpt : Config.SubtitlesFormatOpt);

        if (curOpt != null)
            foreach (var optKV in curOpt)
                av_dict_set(&avopt, optKV.Key, optKV.Value, 0);

        if (opt != null)
            foreach (var optKV in opt)
                av_dict_set(&avopt, optKV.Key, optKV.Value, 0);

        if (Config.FormatOptToUnderlying)
            fixed(AVDictionary** ptr = &avoptCopy)
                av_dict_copy(ptr, avopt, 0);

        fixed(AVFormatContext** fmtCtxPtr = &fmtCtx)
            ret = avformat_open_input(fmtCtxPtr, url, inFmt, avopt == null ? null : &avopt);

        if (avopt != null)
        {
            if (ret >= 0)
            {
                AVDictionaryEntry *t = null;

                while ((t = av_dict_get(avopt, "", t, DictReadFlags.IgnoreSuffix)) != null)
                    Log.Debug($"Ignoring format option {Utils.BytePtrToStringUTF8(t->key)}");
            }

            av_dict_free(&avopt);
        }
    }
    private bool FillInfo()
    {
        Name        = Utils.BytePtrToStringUTF8(fmtCtx->iformat->name);
        LongName    = Utils.BytePtrToStringUTF8(fmtCtx->iformat->long_name);
        Extensions  = Utils.BytePtrToStringUTF8(fmtCtx->iformat->extensions);

        StartTime       = fmtCtx->start_time != AV_NOPTS_VALUE ? fmtCtx->start_time * 10 : 0;
        StartRealTime   = fmtCtx->start_time_realtime != AV_NOPTS_VALUE ? fmtCtx->start_time_realtime * 10 : 0;
        Duration        = fmtCtx->duration > 0 ? fmtCtx->duration * 10 : 0;

        // TBR: Possible we can get Apple HTTP Live Streaming/hls with HLSPlaylist->finished with Duration != 0
        if (Engine.Config.FFmpegHLSLiveSeek && Duration == 0 && Name == "hls" && Environment.Is64BitProcess) // HLSContext cast is not safe
        {
            hlsCtx = (HLSContext*) fmtCtx->priv_data;
            StartTime = 0;
            //UpdateHLSTime(); Maybe with default 0 playlist
        }

        if (fmtCtx->nb_streams == 1 && fmtCtx->streams[0]->codecpar->codec_type == AVMediaType.Subtitle) // External Streams (mainly for .sub will have as start time the first subs timestamp)
            StartTime = 0;

        IsLive = Duration == 0 || hlsCtx != null;

        bool hasVideo = false;
        AVStreamToStream = new Dictionary<int, StreamBase>();

        Metadata.Clear();
        AVDictionaryEntry* b = null;
        while (true)
        {
            b = av_dict_get(fmtCtx->metadata, "", b, DictReadFlags.IgnoreSuffix);
            if (b == null) break;
            Metadata.Add(Utils.BytePtrToStringUTF8(b->key), Utils.BytePtrToStringUTF8(b->value));
        }

        Chapters.Clear();
        string dump = "";
        for (int i=0; i<fmtCtx->nb_chapters; i++)
        {
            var chp = fmtCtx->chapters[i];
            double tb = av_q2d(chp->time_base) * 10000.0 * 1000.0;
            string title = "";

            b = null;
            while (true)
            {
                b = av_dict_get(chp->metadata, "", b, DictReadFlags.IgnoreSuffix);
                if (b == null) break;
                if (Utils.BytePtrToStringUTF8(b->key).ToLower() == "title")
                    title = Utils.BytePtrToStringUTF8(b->value);
            }

            if (CanDebug)
                dump += $"[Chapter {i+1,-2}] {Utils.TicksToTime((long)(chp->start * tb) - StartTime)} - {Utils.TicksToTime((long)(chp->end * tb) - StartTime)} | {title}\r\n";

            Chapters.Add(new Chapter()
            {
                StartTime   = (long)((chp->start * tb) - StartTime),
                EndTime     = (long)((chp->end * tb) - StartTime),
                Title       = title
            });
        }

        if (CanDebug && dump != "") Log.Debug($"Chapters\r\n\r\n{dump}");

        bool audioHasEng = false;
        bool subsHasEng = false;

        lock (lockStreams)
        {
            for (int i=0; i<fmtCtx->nb_streams; i++)
            {
                fmtCtx->streams[i]->discard = AVDiscard.All;

                switch (fmtCtx->streams[i]->codecpar->codec_type)
                {
                    case AVMediaType.Audio:
                        AudioStreams.Add(new AudioStream(this, fmtCtx->streams[i]));
                        AVStreamToStream.Add(fmtCtx->streams[i]->index, AudioStreams[^1]);
                        audioHasEng = AudioStreams[^1].Language == Language.English;

                        break;

                    case AVMediaType.Video:
                        if ((fmtCtx->streams[i]->disposition & DispositionFlags.AttachedPic) != 0)
                            { Log.Info($"Excluding image stream #{i}"); continue; }

                        // TBR: When AllowFindStreamInfo = false we can get valid pixel format during decoding (in case of remuxing only this might crash, possible check if usedecoders?)
                        if (((AVPixelFormat)fmtCtx->streams[i]->codecpar->format) == AVPixelFormat.None && Config.AllowFindStreamInfo)
                        {
                            Log.Info($"Excluding invalid video stream #{i}");
                            continue;
                        }
                        VideoStreams.Add(new VideoStream(this, fmtCtx->streams[i]));
                        AVStreamToStream.Add(fmtCtx->streams[i]->index, VideoStreams[^1]);
                        hasVideo |= !Config.AllowFindStreamInfo || VideoStreams[^1].PixelFormat != AVPixelFormat.None;

                        break;

                    case AVMediaType.Subtitle:
                        SubtitlesStreamsAll.Add(new SubtitlesStream(this, fmtCtx->streams[i]));
                        AVStreamToStream.Add(fmtCtx->streams[i]->index, SubtitlesStreamsAll[^1]);
                        subsHasEng = SubtitlesStreamsAll[^1].Language == Language.English;
                        break;

                    case AVMediaType.Data:
                        DataStreams.Add(new DataStream(this, fmtCtx->streams[i]));
                        AVStreamToStream.Add(fmtCtx->streams[i]->index, DataStreams[^1]);

                        break;

                    default:
                        Log.Info($"#[Unknown #{i}] {fmtCtx->streams[i]->codecpar->codec_type}");
                        break;
                }
            }

            if (!audioHasEng)
                for (int i=0; i<AudioStreams.Count; i++)
                    if (AudioStreams[i].Language.Culture == null && AudioStreams[i].Language.OriginalInput == null)
                        AudioStreams[i].Language = Language.English;

            if (!subsHasEng && Type == MediaType.Video)
                for (int i=0; i<SubtitlesStreamsAll.Count; i++)
                    if (SubtitlesStreamsAll[i].Language.Culture == null && SubtitlesStreamsAll[i].Language.OriginalInput == null)
                        SubtitlesStreamsAll[i].Language = Language.English;

            if (fmtCtx->nb_programs > 0)
            {
                for (int i = 0; i < fmtCtx->nb_programs; i++)
                {
                    fmtCtx->programs[i]->discard = AVDiscard.All;
                    Program program = new(fmtCtx->programs[i], this);
                    Programs.Add(program);
                }
            }

            Extension = GetValidExtension();
        }

        PrintDump();
        return hasVideo;
    }

    public int SeekInQueue(long ticks, bool forward = false)
    {
        lock (lockActions)
        {
            if (Disposed) return -1;

            /* Seek within current bufffered queue
             *
             * 10 seconds because of video keyframe distances or 1 seconds for other (can be fixed also with CurTime+X seek instead of timestamps)
             * For subtitles it should keep (prev packet) the last presented as it can have a lot of distance with CurTime (cur packet)
             * It doesn't work for HLS live streams
             * It doesn't work for decoders buffered queue (which is small only subs might be an issue if we have large decoder queue)
             */

            long startTime = StartTime;

            if (hlsCtx != null)
            {
                ticks    += hlsStartTime - (hlsCtx->first_timestamp * 10);
                startTime = hlsStartTime;
            }

            if (ticks + (VideoStream != null && forward ? (10000 * 10000) : 1000 * 10000) > CurTime + startTime && ticks < CurTime + startTime + BufferedDuration)
            {
                bool found = false;
                while (VideoPackets.Count > 0)
                {
                    var packet = VideoPackets.Peek();
                    if (packet->pts != AV_NOPTS_VALUE && ticks < packet->pts * VideoStream.Timebase && (packet->flags & PktFlags.Key) != 0)
                    {
                        found = true;
                        ticks = (long) (packet->pts * VideoStream.Timebase);

                        break;
                    }

                    VideoPackets.Dequeue();
                    av_packet_free(&packet);
                }

                while (AudioPackets.Count > 0)
                {
                    var packet = AudioPackets.Peek();
                    if (packet->pts != AV_NOPTS_VALUE && (packet->pts + packet->duration) * AudioStream.Timebase >= ticks)
                    {
                        if (Type == MediaType.Audio || VideoStream == null)
                            found = true;

                        break;
                    }

                    AudioPackets.Dequeue();
                    av_packet_free(&packet);
                }

                for (int i = 0; i < subNum; i++)
                {
                    while (SubtitlesPackets[i].Count > 0)
                    {
                        var packet = SubtitlesPackets[i].Peek();
                        if (packet->pts != AV_NOPTS_VALUE && ticks < (packet->pts + packet->duration) * SubtitlesStreams[i].Timebase)
                        {
                            if (Type == MediaType.Subs)
                            {
                                found = true;
                            }

                            break;
                        }

                        SubtitlesPackets[i].Dequeue();
                        av_packet_free(&packet);
                    }
                }

                while (DataPackets.Count > 0)
                {
                    var packet = DataPackets.Peek();
                    if (packet->pts != AV_NOPTS_VALUE && ticks < (packet->pts + packet->duration) * DataStream.Timebase)
                    {
                        if (Type == MediaType.Data)
                            found = true;

                        break;
                    }

                    DataPackets.Dequeue();
                    av_packet_free(&packet);
                }

                if (found)
                {
                    Log.Debug("[Seek] Found in Queue");
                    return 0;
                }
            }

            return -1;
        }
    }
    public int Seek(long ticks, bool forward = false)
    {
        /* Current Issues
         *
         * HEVC/MPEG-TS: Fails to seek to keyframe https://blog.csdn.net/Annie_heyeqq/article/details/113649501 | https://trac.ffmpeg.org/ticket/9412
         * AVSEEK_FLAG_BACKWARD will not work on .dav even if it returns 0 (it will work after it fills the index table)
         * Strange delay (could be 200ms!) after seek on HEVC/yuv420p10le (10-bits) while trying to Present on swapchain (possible recreates texturearray?)
         * AVFMT_NOTIMESTAMPS unknown duration (can be calculated?) should perform byte seek instead (percentage based on total pb size)
         */

        lock (lockActions)
        {
            if (Disposed) return -1;

            int ret;
            long savedPbPos = 0;

            Interrupter.ForceInterrupt = 1;
            lock (lockFmtCtx)
            {
                Interrupter.ForceInterrupt = 0;

                // Flush required because of the interrupt
                if (fmtCtx->pb != null)
                {
                    savedPbPos = fmtCtx->pb->pos;
                    avio_flush(fmtCtx->pb);
                    fmtCtx->pb->error = 0; // AVERROR_EXIT will stay forever and will cause the demuxer to go in Status Stopped instead of Ended (after interrupted seeks)
                    fmtCtx->pb->eof_reached = 0;
                }
                avformat_flush(fmtCtx);

                // Forces seekable HLS
                if (hlsCtx != null)
                    fmtCtx->ctx_flags &= ~FmtCtxFlags.Unseekable;

                Interrupter.SeekRequest();
                if (VideoStream != null)
                {
                    if (CanDebug) Log.Debug($"[Seek({(forward ? "->" : "<-")})] Requested at {new TimeSpan(ticks)}");

                    // TODO: After proper calculation of Duration
                    //if (VideoStream.FixTimestamps && Duration > 0)
                        //ret = av_seek_frame(fmtCtx, -1, (long)((ticks/(double)Duration) * avio_size(fmtCtx->pb)), AVSEEK_FLAG_BYTE);
                    //else
                    ret = ticks == StartTime
                        ? avformat_seek_file(fmtCtx, -1, 0, 0, 0, 0)
                        : av_seek_frame(fmtCtx, -1, ticks / 10, forward ? SeekFlags.Frame : SeekFlags.Backward);

                    curReverseStopPts = AV_NOPTS_VALUE;
                    curReverseStartPts= AV_NOPTS_VALUE;
                }
                else
                {
                    if (CanDebug) Log.Debug($"[Seek({(forward ? "->" : "<-")})] Requested at {new TimeSpan(ticks)} | ANY");
                    ret = forward ?
                        avformat_seek_file(fmtCtx, -1, ticks / 10   , ticks / 10, long.MaxValue , SeekFlags.Any):
                        avformat_seek_file(fmtCtx, -1, long.MinValue, ticks / 10, ticks / 10    , SeekFlags.Any);
                }

                if (ret < 0)
                {
                    if (hlsCtx != null) fmtCtx->ctx_flags &= ~FmtCtxFlags.Unseekable;
                    Log.Info($"Seek failed 1/2 (retrying) {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                    ret = VideoStream != null
                        ? av_seek_frame(fmtCtx, -1, ticks / 10, forward ? SeekFlags.Backward : SeekFlags.Frame)
                        : forward ?
                            avformat_seek_file(fmtCtx, -1, long.MinValue, ticks / 10, ticks / 10    , SeekFlags.Any):
                            avformat_seek_file(fmtCtx, -1, ticks / 10   , ticks / 10, long.MaxValue , SeekFlags.Any);

                    if (ret < 0)
                    {
                        Log.Warn($"Seek failed 2/2 {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                        // Flush required because of seek failure (reset pb to last pos otherwise will be eof) - Mainly for NoTimestamps (TODO: byte seek/calc dur/percentage)
                        if (fmtCtx->pb != null)
                        {
                            avio_flush(fmtCtx->pb);
                            fmtCtx->pb->error = 0;
                            fmtCtx->pb->eof_reached = 0;
                            avio_seek(fmtCtx->pb, savedPbPos, 0);
                        }
                        avformat_flush(fmtCtx);
                    }
                    else
                        lastSeekTime = ticks - StartTime - (hlsCtx != null ? hlsStartTime : 0);
                }
                else
                    lastSeekTime = ticks - StartTime - (hlsCtx != null ? hlsStartTime : 0);

                DisposePackets();
                lock (lockStatus) if (Status == Status.Ended) Status = Status.Stopped;
            }

            return ret; // >= 0 for success
        }
    }

    protected override void RunInternal()
    {
        if (IsReversePlayback)
        {
            RunInternalReverse();
            return;
        }

        int ret = 0;
        int allowedErrors = Config.MaxErrors;
        bool gotAVERROR_EXIT = false;
        audioBufferLimitFired = false;
        long lastVideoPacketPts = 0;

        do
        {
            // Wait until not QueueFull
            if (BufferedDuration > Config.BufferDuration || (Config.BufferPackets != 0 && CurPackets.Count > Config.BufferPackets))
            {
                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueFull;

                while (!PauseOnQueueFull && (BufferedDuration > Config.BufferDuration || (Config.BufferPackets != 0 && CurPackets.Count > Config.BufferPackets)) && Status == Status.QueueFull)
                    Thread.Sleep(20);

                lock (lockStatus)
                {
                    if (PauseOnQueueFull) { PauseOnQueueFull = false; Status = Status.Pausing; }
                    if (Status != Status.QueueFull) break;
                    Status = Status.Running;
                }
            }

            // Wait possible someone asks for lockFmtCtx
            else if (gotAVERROR_EXIT)
            {
                gotAVERROR_EXIT = false;
                Thread.Sleep(5);
            }

            // Demux Packet
            lock (lockFmtCtx)
            {
                Interrupter.ReadRequest();
                ret = av_read_frame(fmtCtx, packet);
                if (Interrupter.ForceInterrupt != 0)
                {
                    av_packet_unref(packet);
                    gotAVERROR_EXIT = true;
                    continue;
                }

                // Possible check if interrupt/timeout and we dont seek to reset the backend pb->pos = 0?
                if (ret != 0)
                {
                    av_packet_unref(packet);

                    if (ret == AVERROR_EOF)
                    {
                        Status = Status.Ended;
                        break;
                    }

                    if (Interrupter.Timedout)
                    {
                        Status = Status.Stopping;
                        break;
                    }

                    allowedErrors--;
                    if (CanWarn) Log.Warn($"{FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                    if (allowedErrors == 0) { Log.Error("Too many errors!"); Status = Status.Stopping; break; }

                    gotAVERROR_EXIT = true;
                    continue;
                }

                TotalBytes += packet->size;

                // Skip Disabled Streams | TODO: It's possible that the streams will changed (add/remove or even update/change of codecs)
                if (!EnabledStreams.Contains(packet->stream_index)) { av_packet_unref(packet); continue; }

                if (IsHLSLive)
                    UpdateHLSTime();

                if (CanTrace)
                {
                    var stream = AVStreamToStream[packet->stream_index];
                    long dts = packet->dts == AV_NOPTS_VALUE ? -1 : (long)(packet->dts * stream.Timebase);
                    long pts = packet->pts == AV_NOPTS_VALUE ? -1 : (long)(packet->pts * stream.Timebase);
                    Log.Trace($"[{stream.Type}] DTS: {(dts == -1 ? "-" : Utils.TicksToTime(dts))} PTS: {(pts == -1 ? "-" : Utils.TicksToTime(pts))} | FLPTS: {(pts == -1 ? "-" : Utils.TicksToTime(pts - StartTime))} | CurTime: {Utils.TicksToTime(CurTime)} | Buffered: {Utils.TicksToTime(BufferedDuration)}");
                }

                // Enqueue Packet (AVS Queue or Single Queue)
                if (UseAVSPackets)
                {
                    switch (fmtCtx->streams[packet->stream_index]->codecpar->codec_type)
                    {
                        case AVMediaType.Audio:
                            //Log($"Audio => {Utils.TicksToTime((long)(packet->pts * AudioStream.Timebase))} | {Utils.TicksToTime(CurTime)}");

                            // Handles A/V de-sync and ffmpeg bug with 2^33 timestamps wrapping
                            if (Config.MaxAudioPackets != 0 && AudioPackets.Count > Config.MaxAudioPackets)
                            {
                                av_packet_unref(packet);
                                packet = av_packet_alloc();

                                if (!audioBufferLimitFired)
                                {
                                    audioBufferLimitFired = true;
                                    OnAudioLimit();
                                }

                                break;
                            }

                            AudioPackets.Enqueue(packet);
                            packet = av_packet_alloc();

                            break;

                        case AVMediaType.Video:
                            //Log($"Video => {Utils.TicksToTime((long)(packet->pts * VideoStream.Timebase))} | {Utils.TicksToTime(CurTime)}");
                            lastVideoPacketPts = packet->pts;
                            VideoPackets.Enqueue(packet);
                            packet = av_packet_alloc();

                            break;

                        case AVMediaType.Subtitle:
                            for (int i = 0; i < subNum; i++)
                            {
                                // Clone packets to support simultaneous display of the same subtitle
                                if (packet->stream_index == SubtitlesStreams[i]?.StreamIndex)
                                {
                                    SubtitlesPackets[i].Enqueue(av_packet_clone(packet));
                                }
                            }

                            // cloned, so free packet
                            av_packet_unref(packet);

                            break;

                        case AVMediaType.Data:
                            // Some data streams only have nopts, set pts to last video packet pts
                            if (packet->pts == AV_NOPTS_VALUE)
                                packet->pts = lastVideoPacketPts;
                            DataPackets.Enqueue(packet);
                            packet = av_packet_alloc();

                            break;

                        default:
                            av_packet_unref(packet);
                            break;
                    }
                }
                else
                {
                    Packets.Enqueue(packet);
                    packet = av_packet_alloc();
                }
            }
        } while (Status == Status.Running);
    }
    private void RunInternalReverse()
    {
        int ret = 0;
        int allowedErrors = Config.MaxErrors;
        bool gotAVERROR_EXIT = false;

        // To demux further for buffering (related to BufferDuration)
        int maxQueueSize = 2;
        curReverseSeekOffset = av_rescale_q(3 * 1000 * 10000 / 10, Engine.FFmpeg.AV_TIMEBASE_Q, VideoStream.AVStream->time_base);

        do
        {
            // Wait until not QueueFull
            if (VideoPacketsReverse.Count > maxQueueSize)
            {
                lock (lockStatus)
                    if (Status == Status.Running) Status = Status.QueueFull;

                while (!PauseOnQueueFull && VideoPacketsReverse.Count > maxQueueSize && Status == Status.QueueFull) { Thread.Sleep(20); }

                lock (lockStatus)
                {
                    if (PauseOnQueueFull) { PauseOnQueueFull = false; Status = Status.Pausing; }
                    if (Status != Status.QueueFull) break;
                    Status = Status.Running;
                }
            }

            // Wait possible someone asks for lockFmtCtx
            else if (gotAVERROR_EXIT)
            {
                gotAVERROR_EXIT = false;
                Thread.Sleep(5);
            }

            // Demux Packet
            lock (lockFmtCtx)
            {
                Interrupter.ReadRequest();
                ret = av_read_frame(fmtCtx, packet);
                if (Interrupter.ForceInterrupt != 0)
                {
                    av_packet_unref(packet); gotAVERROR_EXIT = true;
                    continue;
                }

                // Possible check if interrupt/timeout and we dont seek to reset the backend pb->pos = 0?
                if (ret != 0)
                {
                    av_packet_unref(packet);

                    if (ret == AVERROR_EOF)
                    {
                        if (curReverseVideoPackets.Count > 0)
                        {
                            var drainPacket = av_packet_alloc();
                            drainPacket->data = null;
                            drainPacket->size = 0;
                            curReverseVideoPackets.Add((IntPtr)drainPacket);
                            curReverseVideoStack.Push(curReverseVideoPackets);
                            curReverseVideoPackets = new List<IntPtr>();
                        }

                        if (!curReverseVideoStack.IsEmpty)
                        {
                            VideoPacketsReverse.Enqueue(curReverseVideoStack);
                            curReverseVideoStack = new ConcurrentStack<List<IntPtr>>();
                        }

                        if (curReverseStartPts != AV_NOPTS_VALUE && curReverseStartPts <= VideoStream.StartTimePts)
                        {
                            Status = Status.Ended;
                            break;
                        }

                        //Log($"[][][SEEK END] {curReverseStartPts} | {Utils.TicksToTime((long) (curReverseStartPts * VideoStream.Timebase))}");
                        Interrupter.SeekRequest();
                        ret = av_seek_frame(fmtCtx, VideoStream.StreamIndex, Math.Max(curReverseStartPts - curReverseSeekOffset, VideoStream.StartTimePts), SeekFlags.Backward);

                        if (ret != 0)
                        {
                            Status = Status.Stopping;
                            break;
                        }

                        curReverseStopPts = curReverseStartPts;
                        curReverseStartPts = AV_NOPTS_VALUE;
                        continue;
                    }

                    allowedErrors--;
                    if (CanWarn) Log.Warn($"{ FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                    if (allowedErrors == 0) { Log.Error("Too many errors!"); Status = Status.Stopping; break; }

                    gotAVERROR_EXIT = true;
                    continue;
                }

                if (VideoStream.StreamIndex != packet->stream_index) { av_packet_unref(packet); continue; }

                if ((packet->flags & PktFlags.Key) != 0)
                {
                    if (curReverseStartPts == AV_NOPTS_VALUE)
                        curReverseStartPts = packet->pts;

                    if (curReverseVideoPackets.Count > 0)
                    {
                        var drainPacket = av_packet_alloc();
                        drainPacket->data = null;
                        drainPacket->size = 0;
                        curReverseVideoPackets.Add((IntPtr)drainPacket);
                        curReverseVideoStack.Push(curReverseVideoPackets);
                        curReverseVideoPackets = new List<IntPtr>();
                    }
                }

                if (packet->pts != AV_NOPTS_VALUE && (
                    (curReverseStopRequestedPts != AV_NOPTS_VALUE && curReverseStopRequestedPts <= packet->pts)  ||
                    (curReverseStopPts == AV_NOPTS_VALUE && (packet->flags & PktFlags.Key) != 0 && packet->pts != curReverseStartPts)     ||
                    (packet->pts == curReverseStopPts)
                    ))
                {
                    if (curReverseStartPts == AV_NOPTS_VALUE || curReverseStopPts == curReverseStartPts)
                    {
                        curReverseSeekOffset *= 2;
                        if (curReverseStartPts == AV_NOPTS_VALUE) curReverseStartPts = curReverseStopPts;
                        if (curReverseStartPts == AV_NOPTS_VALUE) curReverseStartPts = curReverseStopRequestedPts;
                    }

                    curReverseStopRequestedPts = AV_NOPTS_VALUE;

                    if ((packet->flags & PktFlags.Key) == 0 && curReverseVideoPackets.Count > 0)
                    {
                        var drainPacket = av_packet_alloc();
                        drainPacket->data = null;
                        drainPacket->size = 0;
                        curReverseVideoPackets.Add((IntPtr)drainPacket);
                        curReverseVideoStack.Push(curReverseVideoPackets);
                        curReverseVideoPackets = new List<IntPtr>();
                    }

                    if (!curReverseVideoStack.IsEmpty)
                    {
                        VideoPacketsReverse.Enqueue(curReverseVideoStack);
                        curReverseVideoStack = new ConcurrentStack<List<IntPtr>>();
                    }

                    av_packet_unref(packet);

                    if (curReverseStartPts != AV_NOPTS_VALUE && curReverseStartPts <= VideoStream.StartTimePts)
                    {
                        Status = Status.Ended;
                        break;
                    }

                    //Log($"[][][SEEK] {curReverseStartPts} | {Utils.TicksToTime((long) (curReverseStartPts * VideoStream.Timebase))}");
                    Interrupter.SeekRequest();
                    ret = av_seek_frame(fmtCtx, VideoStream.StreamIndex, Math.Max(curReverseStartPts - curReverseSeekOffset, 0), SeekFlags.Backward);

                    if (ret != 0)
                    {
                        Status = Status.Stopping;
                        break;
                    }

                    curReverseStopPts = curReverseStartPts;
                    curReverseStartPts = AV_NOPTS_VALUE;
                }
                else
                {
                    if (curReverseStartPts != AV_NOPTS_VALUE)
                    {
                        curReverseVideoPackets.Add((IntPtr)packet);
                        packet = av_packet_alloc();
                    }
                    else
                        av_packet_unref(packet);
                }
            }

        } while (Status == Status.Running);
    }

    public void EnableReversePlayback(long timestamp)
    {
        IsReversePlayback = true;
        Seek(StartTime + timestamp);
        curReverseStopRequestedPts = av_rescale_q((StartTime + timestamp) / 10, Engine.FFmpeg.AV_TIMEBASE_Q, VideoStream.AVStream->time_base);
    }
    public void DisableReversePlayback() => IsReversePlayback = false;
    #endregion

    #region Switch Programs / Streams
    public bool IsProgramEnabled(StreamBase stream)
    {
        for (int i=0; i<Programs.Count; i++)
            for (int l=0; l<Programs[i].Streams.Count; l++)
                if (Programs[i].Streams[l].StreamIndex == stream.StreamIndex && fmtCtx->programs[i]->discard != AVDiscard.All)
                    return true;

        return false;
    }
    public void EnableProgram(StreamBase stream)
    {
        if (IsProgramEnabled(stream))
        {
            if (CanDebug) Log.Debug($"[Stream #{stream.StreamIndex}] Program already enabled");
            return;
        }

        for (int i=0; i<Programs.Count; i++)
            for (int l=0; l<Programs[i].Streams.Count; l++)
                if (Programs[i].Streams[l].StreamIndex == stream.StreamIndex)
                {
                    if (CanDebug) Log.Debug($"[Stream #{stream.StreamIndex}] Enables program #{i}");
                    fmtCtx->programs[i]->discard = AVDiscard.Default;
                    return;
                }
    }
    public void DisableProgram(StreamBase stream)
    {
        for (int i=0; i<Programs.Count; i++)
            for (int l=0; l<Programs[i].Streams.Count; l++)
                if (Programs[i].Streams[l].StreamIndex == stream.StreamIndex && fmtCtx->programs[i]->discard != AVDiscard.All)
                {
                    bool isNeeded = false;
                    for (int l2=0; l2<Programs[i].Streams.Count; l2++)
                    {
                        if (Programs[i].Streams[l2].StreamIndex != stream.StreamIndex && EnabledStreams.Contains(Programs[i].Streams[l2].StreamIndex))
                            {isNeeded = true; break; }
                    }

                    if (!isNeeded)
                    {
                        if (CanDebug) Log.Debug($"[Stream #{stream.StreamIndex}] Disables program #{i}");
                        fmtCtx->programs[i]->discard = AVDiscard.All;
                    }
                    else if (CanDebug)
                        Log.Debug($"[Stream #{stream.StreamIndex}] Program #{i} is needed");
                }
    }

    public void EnableStream(StreamBase stream)
    {
        lock (lockFmtCtx)
        {
            if (Disposed || stream == null || EnabledStreams.Contains(stream.StreamIndex))
            {
                // If it detects that the primary and secondary are trying to select the same subtitle
                // Put the same instance in SubtitlesStreams and return.
                if (stream != null && stream.Type == MediaType.Subs && EnabledStreams.Contains(stream.StreamIndex))
                {
                    SubtitlesStreams[SubtitlesSelectedHelper.CurIndex] = (SubtitlesStream)stream;
                    // Necessary to update UI immediately
                    stream.Enabled = stream.Enabled;
                }
                return;
            };

            EnabledStreams.Add(stream.StreamIndex);
            fmtCtx->streams[stream.StreamIndex]->discard = AVDiscard.Default;
            stream.Enabled = true;
            EnableProgram(stream);

            switch (stream.Type)
            {
                case MediaType.Audio:
                    AudioStream = (AudioStream) stream;
                    if (VideoStream == null)
                    {
                        if (AudioStream.HLSPlaylist != null)
                        {
                            IsHLSLive = true;
                            HLSPlaylist = AudioStream.HLSPlaylist;
                            UpdateHLSTime();
                        }
                        else
                        {
                            HLSPlaylist = null;
                            IsHLSLive = false;
                        }
                    }

                    break;

                case MediaType.Video:
                    VideoStream = (VideoStream) stream;
                    VideoPackets.frameDuration = VideoStream.FrameDuration > 0 ? VideoStream.FrameDuration : 30 * 1000 * 10000;
                    if (VideoStream.HLSPlaylist != null)
                    {
                        IsHLSLive = true;
                        HLSPlaylist = VideoStream.HLSPlaylist;
                        UpdateHLSTime();
                    }
                    else
                    {
                        HLSPlaylist = null;
                        IsHLSLive = false;
                    }

                    break;

                case MediaType.Subs:
                    SubtitlesStreams[SubtitlesSelectedHelper.CurIndex] = (SubtitlesStream)stream;

                    break;

                case MediaType.Data:
                    DataStream = (DataStream) stream;

                    break;
            }

            if (UseAVSPackets)
                CurPackets = VideoStream != null ? VideoPackets : (AudioStream != null ? AudioPackets : (SubtitlesStreams[0] != null ? SubtitlesPackets[0] : DataPackets));

            if (CanInfo) Log.Info($"[{stream.Type} #{stream.StreamIndex}] Enabled");
        }
    }
    public void DisableStream(StreamBase stream)
    {
        lock (lockFmtCtx)
        {
            if (Disposed || stream == null || !EnabledStreams.Contains(stream.StreamIndex)) return;

            // If it is the same subtitle, do not disable it.
            if (stream.Type == MediaType.Subs && SubtitlesStreams[0] != null && SubtitlesStreams[0] == SubtitlesStreams[1])
            {
                // clear only what is needed
                SubtitlesStreams[SubtitlesSelectedHelper.CurIndex] = null;
                SubtitlesPackets[SubtitlesSelectedHelper.CurIndex].Clear();
                // Necessary to update UI immediately
                stream.Enabled = stream.Enabled;
                return;
            }

            /* AVDISCARD_ALL causes syncing issues between streams (TBR: bandwidth?)
             * 1) While switching video streams will not switch at the same timestamp
             * 2) By disabling video stream after a seek, audio will not seek properly
             * 3) HLS needs to update hlsCtx->first_time and read at least on package before seek to be accurate
             */

            fmtCtx->streams[stream.StreamIndex]->discard = AVDiscard.All;
            EnabledStreams.Remove(stream.StreamIndex);
            stream.Enabled = false;
            DisableProgram(stream);

            switch (stream.Type)
            {
                case MediaType.Audio:
                    AudioStream = null;
                    if (VideoStream != null)
                    {
                        if (VideoStream.HLSPlaylist != null)
                        {
                            IsHLSLive = true;
                            HLSPlaylist = VideoStream.HLSPlaylist;
                            UpdateHLSTime();
                        }
                        else
                        {
                            HLSPlaylist = null;
                            IsHLSLive = false;
                        }
                    }

                    AudioPackets.Clear();

                    break;

                case MediaType.Video:
                    VideoStream = null;
                    if (AudioStream != null)
                    {
                        if (AudioStream.HLSPlaylist != null)
                        {
                            IsHLSLive = true;
                            HLSPlaylist = AudioStream.HLSPlaylist;
                            UpdateHLSTime();
                        }
                        else
                        {
                            HLSPlaylist = null;
                            IsHLSLive = false;
                        }
                    }

                    VideoPackets.Clear();
                    break;

                case MediaType.Subs:
                    SubtitlesStreams[SubtitlesSelectedHelper.CurIndex] = null;
                    SubtitlesPackets[SubtitlesSelectedHelper.CurIndex].Clear();

                    break;

                case MediaType.Data:
                    DataStream = null;
                    DataPackets.Clear();

                    break;
            }

            if (UseAVSPackets)
                CurPackets = VideoStream != null ? VideoPackets : (AudioStream != null ? AudioPackets : SubtitlesPackets[0]);

            if (CanInfo) Log.Info($"[{stream.Type} #{stream.StreamIndex}] Disabled");
        }
    }
    public void SwitchStream(StreamBase stream)
    {
        lock (lockFmtCtx)
        {
            if (stream.Type == MediaType.Audio)
                DisableStream(AudioStream);
            else if (stream.Type == MediaType.Video)
                DisableStream(VideoStream);
            else if (stream.Type == MediaType.Subs)
                DisableStream(SubtitlesStreams[SubtitlesSelectedHelper.CurIndex]);
            else
                DisableStream(DataStream);

            EnableStream(stream);
        }
    }
    #endregion

    #region Misc
    internal void UpdateHLSTime()
    {
        // TBR: Access Violation
        // [hls @ 00000269f9cdb400] Media sequence changed unexpectedly: 150070 -> 0

        if (hlsPrevSeqNo != HLSPlaylist->cur_seq_no)
        {
            hlsPrevSeqNo = HLSPlaylist->cur_seq_no;
            hlsStartTime = AV_NOPTS_VALUE;

            hlsCurDuration = 0;
            long duration = 0;

            for (long i=0; i<HLSPlaylist->cur_seq_no - HLSPlaylist->start_seq_no; i++)
            {
                hlsCurDuration += HLSPlaylist->segments[i]->duration;
                duration += HLSPlaylist->segments[i]->duration;
            }

            for (long i=HLSPlaylist->cur_seq_no - HLSPlaylist->start_seq_no; i<HLSPlaylist->n_segments; i++)
                duration += HLSPlaylist->segments[i]->duration;

            hlsCurDuration *= 10;
            duration *= 10;
            Duration = duration;
        }

        if (hlsStartTime == AV_NOPTS_VALUE && CurPackets.LastTimestamp != AV_NOPTS_VALUE)
        {
            hlsStartTime = CurPackets.LastTimestamp - hlsCurDuration;
            CurPackets.UpdateCurTime();
        }
    }

    private string GetValidExtension()
    {
        // TODO
        // Should check for all supported output formats (there is no list in ffmpeg.autogen ?)
        // Should check for inner input format (not outer protocol eg. hls/rtsp)
        // Should check for raw codecs it can be mp4/mov but it will not work in mp4 only in mov (or avi for raw)

        if (Name == "mpegts")
            return "ts";
        else if (Name == "mpeg")
            return "mpeg";

        List<string> supportedOutput = new() { "mp4", "avi", "flv", "flac", "mpeg", "mpegts", "mkv", "ogg", "ts"};
        string defaultExtenstion = "mp4";
        bool hasPcm = false;
        bool isRaw = false;

        foreach (var stream in AudioStreams)
            if (stream.Codec.Contains("pcm")) hasPcm = true;

        foreach (var stream in VideoStreams)
            if (stream.Codec.Contains("raw")) isRaw = true;

        if (isRaw) defaultExtenstion = "avi";

        // MP4 container doesn't support PCM
        if (hasPcm) defaultExtenstion = "mkv";

        // TODO
        // Check also shortnames
        //if (Name == "mpegts") return "ts";
        //if ((fmtCtx->iformat->flags & AVFMT_TS_DISCONT) != 0) should be mp4 or container that supports segments

        if (string.IsNullOrEmpty(Extensions)) return defaultExtenstion;
        string[] extensions = Extensions.Split(',');
        if (extensions == null || extensions.Length < 1) return defaultExtenstion;

        // Try to set the output container same as input
        for (int i=0; i<extensions.Length; i++)
            if (supportedOutput.Contains(extensions[i]))
                return extensions[i] == "mp4" && isRaw ? "mov" : extensions[i];

        return defaultExtenstion;
    }
    private void PrintDump()
    {
        string dump = $"\r\n[Format  ] {LongName}/{Name} | {Extensions} | {Utils.TicksToTime(fmtCtx->start_time * 10)}/{Utils.TicksToTime(fmtCtx->duration * 10)} | {Utils.TicksToTime(StartTime)}/{Utils.TicksToTime(Duration)}";

        foreach(var stream in VideoStreams)     dump += "\r\n" + stream.GetDump();
        foreach(var stream in AudioStreams)     dump += "\r\n" + stream.GetDump();
        foreach(var stream in SubtitlesStreamsAll) dump += "\r\n" + stream.GetDump();

        if (fmtCtx->nb_programs > 0)
            dump += $"\r\n[Programs] {fmtCtx->nb_programs}";

        for (int i=0; i<fmtCtx->nb_programs; i++)
        {
            dump += $"\r\n\tProgram #{i}";

            if (fmtCtx->programs[i]->nb_stream_indexes > 0)
                dump += $"\r\n\t\tStreams [{fmtCtx->programs[i]->nb_stream_indexes}]: ";

            for (int l=0; l<fmtCtx->programs[i]->nb_stream_indexes; l++)
                dump += $"{fmtCtx->programs[i]->stream_index[l]},";

            if (fmtCtx->programs[i]->nb_stream_indexes > 0)
                dump = dump[..^1];
        }

        if (CanInfo) Log.Info($"Format Context Info {dump}\r\n");
    }

    /// <summary>
    /// Gets next VideoPacket from the existing queue or demuxes it if required (Demuxer must not be running)
    /// </summary>
    /// <returns>0 on success</returns>
    public int GetNextVideoPacket()
    {
        if (VideoPackets.Count > 0)
        {
            packet = VideoPackets.Dequeue();
            return 0;
        }
        else
            return GetNextPacket(VideoStream.StreamIndex);
    }

    /// <summary>
    /// Pushes the demuxer to the next available packet (Demuxer must not be running)
    /// </summary>
    /// <param name="streamIndex">Packet's stream index</param>
    /// <returns>0 on success</returns>
    public int GetNextPacket(int streamIndex = -1)
    {
        int ret;

        while (true)
        {
            Interrupter.ReadRequest();
            ret = av_read_frame(fmtCtx, packet);

            if (ret != 0)
            {
                av_packet_unref(packet);

                if ((ret == AVERROR_EXIT && fmtCtx->pb != null && fmtCtx->pb->eof_reached != 0) || ret == AVERROR_EOF)
                {
                    packet = av_packet_alloc();
                    packet->data = null;
                    packet->size = 0;

                    Stop();
                    Status = Status.Ended;
                }

                return ret;
            }

            if (streamIndex != -1)
            {
                if (packet->stream_index == streamIndex)
                    return 0;
            }
            else if (EnabledStreams.Contains(packet->stream_index))
                return 0;

            av_packet_unref(packet);
        }
    }
    #endregion
}

public unsafe class PacketQueue
{
    // TODO: DTS might not be available without avformat_find_stream_info (should changed based on packet->duration and fallback should be removed)
    readonly Demuxer demuxer;
    readonly ConcurrentQueue<IntPtr> packets = new();
    public long frameDuration = 30 * 1000 * 10000; // in case of negative buffer duration calculate it based on packets count / FPS

    public long Bytes               { get; private set; }
    public long BufferedDuration    { get; private set; }
    public long CurTime             { get; private set; }
    public int  Count               => packets.Count;
    public bool IsEmpty             => packets.IsEmpty;

    public long FirstTimestamp      { get; private set; } = AV_NOPTS_VALUE;
    public long LastTimestamp       { get; private set; } = AV_NOPTS_VALUE;

    public PacketQueue(Demuxer demuxer)
        => this.demuxer = demuxer;

    public void Clear()
    {
        lock(packets)
        {
            while (!packets.IsEmpty)
            {
                packets.TryDequeue(out IntPtr packetPtr);
                if (packetPtr == IntPtr.Zero) continue;
                AVPacket* packet = (AVPacket*)packetPtr;
                av_packet_free(&packet);
            }

            FirstTimestamp = AV_NOPTS_VALUE;
            LastTimestamp = AV_NOPTS_VALUE;
            Bytes = 0;
            BufferedDuration = 0;
            CurTime = 0;
        }
    }

    public void Enqueue(AVPacket* packet)
    {
        lock (packets)
        {
            packets.Enqueue((IntPtr)packet);

            if (packet->dts != AV_NOPTS_VALUE || packet->pts != AV_NOPTS_VALUE)
            {
                LastTimestamp = packet->dts != AV_NOPTS_VALUE ?
                    (long)(packet->dts * demuxer.AVStreamToStream[packet->stream_index].Timebase):
                    (long)(packet->pts * demuxer.AVStreamToStream[packet->stream_index].Timebase);

                if (FirstTimestamp == AV_NOPTS_VALUE)
                {
                    FirstTimestamp = LastTimestamp;
                    UpdateCurTime();
                }
                else
                {
                    BufferedDuration = LastTimestamp - FirstTimestamp;
                    if (BufferedDuration < 0)
                        BufferedDuration = packets.Count * frameDuration;
                }
            }
            else
                BufferedDuration = packets.Count * frameDuration;

            Bytes += packet->size;
        }
    }

    public AVPacket* Dequeue()
    {
        lock(packets)
            if (packets.TryDequeue(out IntPtr packetPtr))
            {
                AVPacket* packet = (AVPacket*)packetPtr;

                if (packet->dts != AV_NOPTS_VALUE || packet->pts != AV_NOPTS_VALUE)
                {
                    FirstTimestamp = packet->dts != AV_NOPTS_VALUE ?
                        (long)(packet->dts * demuxer.AVStreamToStream[packet->stream_index].Timebase):
                        (long)(packet->pts * demuxer.AVStreamToStream[packet->stream_index].Timebase);
                    UpdateCurTime();
                }
                else
                    BufferedDuration = packets.Count * frameDuration;

                return (AVPacket*)packetPtr;
            }

        return null;
    }

    public AVPacket* Peek()
        => packets.TryPeek(out IntPtr packetPtr)
        ? (AVPacket*)packetPtr
        : (AVPacket*)null;

    internal void UpdateCurTime()
    {
        if (demuxer.hlsCtx != null)
        {
            if (demuxer.hlsStartTime != AV_NOPTS_VALUE)
            {
                if (FirstTimestamp < demuxer.hlsStartTime)
                {
                    demuxer.Duration += demuxer.hlsStartTime - FirstTimestamp;
                    demuxer.hlsStartTime = FirstTimestamp;
                    CurTime = 0;
                }
                else
                    CurTime = FirstTimestamp - demuxer.hlsStartTime;
            }
        }
        else
            CurTime = FirstTimestamp - demuxer.StartTime;

        if (CurTime < 0)
            CurTime = 0;

        BufferedDuration = LastTimestamp - FirstTimestamp;

        if (BufferedDuration < 0)
            BufferedDuration = packets.Count * frameDuration;
    }
}

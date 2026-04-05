using System.Runtime.InteropServices;

using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaDecoder;

public unsafe partial class AudioDecoder
{
    // TODO: Check locks (lockSpeed) - during seek and speed change (also in xaudio submit samples - we change the data len and we lose sync with submited samples vs played)

    AVFilterContext*        abufferCtx;
    AVFilterContext*        abufferSinkCtx;
    AVFilterGraph*          filterGraph;
    bool                    abufferDrained;
    long                    curSamples;
    double                  missedSamples;
    long                    filterFirstPts;
    bool                    setFirstPts;
    object                  lockSpeed = new();
    AVRational              sinkTimebase;
    AVFrame*                filtframe;

    static AVFilter* ATEMPO     = avfilter_get_by_name("atempo");
    static AVFilter* ABUFFER    = avfilter_get_by_name("abuffer");
    static AVFilter* ABUFFERSINK= avfilter_get_by_name("abuffersink");

    private AVFilterContext* CreateFilter(string name, string args, AVFilterContext* prevCtx = null, string id = null)
        => CreateFilter(avfilter_get_by_name(name), args, prevCtx, id ?? name);

    private AVFilterContext* CreateFilter(AVFilter* filter, string args, AVFilterContext* prevCtx = null, string id = null)
    {
        int ret;
        AVFilterContext*    filterCtx;

        if (filter == null)
            throw new Exception($"[Filter {BytePtrToStringUTF8(filter->name)}] not found");
        
        ret = avfilter_graph_create_filter(&filterCtx, filter, id, args, null, filterGraph);
        if (ret < 0)
            throw new Exception($"[Filter {BytePtrToStringUTF8(filter->name)}] avfilter_graph_create_filter failed ({FFmpegEngine.ErrorCodeToMsg(ret)})");

        if (prevCtx == null)
            return filterCtx;

        ret = avfilter_link(prevCtx, 0, filterCtx, 0);

        return ret != 0
            ? throw new Exception($"[Filter {BytePtrToStringUTF8(filter->name)}] avfilter_link failed ({FFmpegEngine.ErrorCodeToMsg(ret)})")
            : filterCtx;
    }

    private int Set<T>(AVFilterContext* fltCtx, string name, T[] value, AVOptionType type, OptSearchFlags searchFlags = OptSearchFlags.Children, uint startElement = 0) where T : unmanaged
    {
        fixed(T* ptr = value)
            return av_opt_set_array(fltCtx, name, searchFlags, startElement, (uint)value.Length, type, ptr);
    }
    private int SetupFilters()
    {
        int ret = -1;

        try
        {
            DisposeFilters();

            AVFilterContext* linkCtx;

            sinkTimebase    = new() { Num = 1, Den = codecCtx->sample_rate};
            filtframe       = av_frame_alloc();
            filterGraph     = avfilter_graph_alloc();
            setFirstPts     = true;
            abufferDrained  = false;

            // IN (abuffersrc)
            linkCtx = abufferCtx = CreateFilter(ABUFFER,
                $"channel_layout={AudioStream.ChannelLayoutStr}:sample_fmt={AudioStream.SampleFormatStr}:sample_rate={codecCtx->sample_rate}:time_base={sinkTimebase.Num}/{sinkTimebase.Den}");

            // USER DEFINED
            if (Config.Audio.Filters != null)
                foreach (var filter in Config.Audio.Filters)
                    try
                    {
                        linkCtx = CreateFilter(filter.Name, filter.Args, linkCtx, filter.Id);
                    }
                    catch (Exception e) { Log.Error($"{e.Message}"); }

            // SPEED (atempo up to 3) | [0.125 - 0.25](3), [0.25 - 0.5](2), [0.5 - 2.0](1), [2.0 - 4.0](2), [4.0 - X](3)
            if (speed != 1)
            {
                if (speed >= 0.5 && speed <= 2)
                    linkCtx = CreateFilter(ATEMPO, $"tempo={speed.ToString("0.0000000000", System.Globalization.CultureInfo.InvariantCulture)}", linkCtx);
                else if ((speed > 2 & speed <= 4) || (speed >= 0.25 && speed < 0.5))
                {
                    var singleAtempoSpeed = Math.Sqrt(speed);
                    linkCtx = CreateFilter(ATEMPO, $"tempo={singleAtempoSpeed.ToString("0.0000000000", System.Globalization.CultureInfo.InvariantCulture)}", linkCtx);
                    linkCtx = CreateFilter(ATEMPO, $"tempo={singleAtempoSpeed.ToString("0.0000000000", System.Globalization.CultureInfo.InvariantCulture)}", linkCtx);
                }
                else if (speed > 4 || speed >= 0.125 && speed < 0.25)
                {
                    var singleAtempoSpeed = Math.Pow(speed, 1.0 / 3);
                    linkCtx = CreateFilter(ATEMPO, $"tempo={singleAtempoSpeed.ToString("0.0000000000", System.Globalization.CultureInfo.InvariantCulture)}", linkCtx);
                    linkCtx = CreateFilter(ATEMPO, $"tempo={singleAtempoSpeed.ToString("0.0000000000", System.Globalization.CultureInfo.InvariantCulture)}", linkCtx);
                    linkCtx = CreateFilter(ATEMPO, $"tempo={singleAtempoSpeed.ToString("0.0000000000", System.Globalization.CultureInfo.InvariantCulture)}", linkCtx);
                }
            }

            // OUT (abuffersink)
            if (Engine.FFmpeg.Ver8OrGreater)
            {
                abufferSinkCtx = avfilter_graph_alloc_filter(filterGraph, ABUFFERSINK, null);
                Set(abufferSinkCtx, "sample_formats",  [AOutSampleFormat],         AVOptionType.SampleFmt);
                Set(abufferSinkCtx, "samplerates",     [AudioStream.SampleRate],   AVOptionType.Int);
                Set(abufferSinkCtx, "channel_layouts", [AV_CHANNEL_LAYOUT_STEREO], AVOptionType.Chlayout);
                ret = avfilter_init_dict(abufferSinkCtx, null);
            }
            else
            {
                abufferSinkCtx = CreateFilter(ABUFFERSINK, null, null);
                int tmpSampleRate = AudioStream.SampleRate;
                fixed (AVSampleFormat* ptr = &AOutSampleFormat)
                    ret = av_opt_set_bin(abufferSinkCtx , "sample_fmts"         , (byte*)ptr,            sizeof(AVSampleFormat) , OptSearchFlags.Children);
                ret = av_opt_set_bin(abufferSinkCtx     , "sample_rates"        , (byte*)&tmpSampleRate, sizeof(int)            , OptSearchFlags.Children);
                ret = av_opt_set_int(abufferSinkCtx     , "all_channel_counts"  , 0                                             , OptSearchFlags.Children);
                ret = av_opt_set(abufferSinkCtx         , "ch_layouts"          , "stereo"                                      , OptSearchFlags.Children);
            }

            _ = avfilter_link(linkCtx, 0, abufferSinkCtx, 0);

            // GRAPH CONFIG
            ret = avfilter_graph_config(filterGraph, null);

            // CRIT TBR:!!!
            var tb = 1000 * 10000.0 / sinkTimebase.Den; // TBR: DONT CHANGE values will affect Screamer | Ensures we have at least 20-70ms samples to avoid audio crackling and av sync issues
            ((FilterLink*)abufferSinkCtx->inputs[0])->min_samples = (int) (20 * 10000 / tb);
            ((FilterLink*)abufferSinkCtx->inputs[0])->max_samples = (int) (70 * 10000 / tb);

            return ret < 0
                ? throw new Exception($"[FilterGraph] {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})")
                : 0;
        }
        catch (Exception e)
        {
            fixed(AVFilterGraph** filterGraphPtr = &filterGraph)
                avfilter_graph_free(filterGraphPtr);

            Log.Error($"{e.Message}");

            return ret;
        }
    }

    private void DisposeFilters()
    {
        if (filterGraph == null)
            return;

        fixed(AVFilterGraph** filterGraphPtr = &filterGraph)
            avfilter_graph_free(filterGraphPtr);

        if (filtframe != null)
        {
            fixed (AVFrame** ptr = &filtframe) av_frame_free(ptr);
            filtframe = null;
        }
            

        abufferCtx      = null;
        abufferSinkCtx  = null;
        filterGraph     = null;
    }
    protected override void OnSpeedChanged(double value)
    {
        // Possible Task to avoid locking UI thread as lockAtempo can wait for the Frames queue to be freed (will cause other issues and couldnt reproduce the possible dead lock)
        cBufTimesCur = cBufTimesSize;
        lock (lockSpeed)
        {
            if (filterGraph != null)
                DrainFilters();

            cBufTimesCur= 1;
            speed       = value;

            var frames = Frames.ToArray();
            for (int i = 0; i < frames.Length; i++)
                FixSample(frames[i], speed);

            if (filterGraph != null)
                SetupFilters();
        }
    }
    internal void FixSample(AudioFrame frame, double newSpeed)
    {
        var oldSpeed    = frame.speed;
        var oldDataLen  = frame.dataLen;
        frame.dataLen   = Align((int) (oldDataLen * oldSpeed / newSpeed), ASampleBytes);
        frame.speed     = newSpeed;
        fixed (byte* cBufStartPosPtr = &cBuf[0])
        {
            var curOffset = (long)frame.dataPtr - (long)cBufStartPosPtr;

            if (newSpeed < oldSpeed)
            {
                if (curOffset + frame.dataLen >= cBuf.Length)
                {
                    frame.dataPtr = (IntPtr)cBufStartPosPtr;
                    curOffset  = 0;
                    oldDataLen = 0;
                }

                // fill silence
                for (int p = oldDataLen; p < frame.dataLen; p++)
                    cBuf[curOffset + p] = 0;
            }
        }
    }
    private int UpdateFilterInternal(string filterId, string key, string value)
    {
        int ret = avfilter_graph_send_command(filterGraph, filterId, key, value, null, 0, 0);
        Log.Info($"[{filterId}] {key}={value} {(ret >=0 ? "success" : "failed")}");

        return ret;
    }
    internal int SetupFiltersOrSwr()
    {
        lock (lockSpeed)
        {
            int ret = -1;

            if (Disposed)
                return ret;

            if (Config.Audio.FiltersEnabled)
            {
                ret = SetupFilters();

                if (ret != 0)
                {
                    Log.Error($"Setup filters failed. Fallback to Swr.");
                    ret = SetupSwr();
                }
                else
                    DisposeSwr();
            }
            else
            {
                DisposeFilters();
                ret = SetupSwr();
            }

            return ret;
        }
    }

    public int UpdateFilter(string filterId, string key, string value)
    {
        lock (lockCodecCtx)
            return filterGraph != null ? UpdateFilterInternal(filterId, key, value) : -1;
    }
    public int ReloadFilters()
    {
        if (!Config.Audio.FiltersEnabled)
            return -1;

        lock (lockActions)
            lock (lockCodecCtx)
                return SetupFilters();
    }

    private void ProcessFilters()
    {
        if (setFirstPts)
        {
            setFirstPts     = false;
            filterFirstPts  = frame->pts;
            curSamples      = 0;
            missedSamples   = 0;
        }
        else if (Math.Abs(frame->pts - nextPts) > 10 * 10000) // 10ms distance should resync filters (TBR: it should be 0ms however we might get 0 pkt_duration for unknown?)
        {
            DrainFilters();
            Log.Warn($"Resync filters! ({TicksToTime((long)((frame->pts - nextPts) * AudioStream.Timebase))} distance)");
            //resyncWithVideoRequired = !VideoDecoder.Disposed;
            DisposeFrames();
            avcodec_flush_buffers(codecCtx);
            if (filterGraph != null)
                SetupFilters();
            return;
        }

        nextPts = frame->pts + frame->duration;

        int ret;

        if ((ret = av_buffersrc_add_frame_flags(abufferCtx, frame, AVBuffersrcFlag.KeepRef | AVBuffersrcFlag.NoCheckFormat)) < 0) // We check format change manually before here
        {
            Log.Warn($"[buffersrc] {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");
            Status = Status.Stopping;
            return;
        }

        while (true)
        {
            if ((ret = av_buffersink_get_frame_flags(abufferSinkCtx, filtframe, 0)) < 0) // Sometimes we get AccessViolationException while we UpdateFilter (possible related with .NET7 debug only bug)
                return; // EAGAIN (Some filters will send EAGAIN even if EOF currently we handled cause our Status will be Draining)

            if (filtframe->pts == AV_NOPTS_VALUE) // we might desync here (we dont count frames->nb_samples) ?
            {
                av_frame_unref(filtframe);
                continue;
            }

            ProcessFilter();

            // Wait until Queue not Full or Stopped
            if (Frames.Count >= Config.Decoder.MaxAudioFrames * cBufTimesCur)
            {
                Monitor.Exit(lockCodecCtx);
                lock (lockStatus)
                    if (Status == Status.Running)
                        Status = Status.QueueFull;

                while (Frames.Count >= Config.Decoder.MaxAudioFrames * cBufTimesCur && (Status == Status.QueueFull || Status == Status.Draining))
                    Thread.Sleep(20);

                Monitor.Enter(lockCodecCtx);

                lock (lockStatus)
                {
                    if (Status == Status.QueueFull)
                        Status = Status.Running;
                    else if (Status != Status.Draining)
                        return;
                }
            }
        }
    }
    private void DrainFilters()
    {
        if (abufferDrained)
            return;

        abufferDrained = true;

        int ret;

        if ((ret = av_buffersrc_add_frame(abufferCtx, null)) < 0)
        {
            Log.Warn($"[buffersrc] {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");
            return;
        }

        while (true)
        {
            if ((ret = av_buffersink_get_frame_flags(abufferSinkCtx, filtframe, 0)) < 0)
                return;

            if (filtframe->pts == AV_NOPTS_VALUE)
            {
                av_frame_unref(filtframe);
                return;
            }

            ProcessFilter();
        }
    }
    private void ProcessFilter()
    {
        var curLen = filtframe->nb_samples * ASampleBytes;

        if (filtframe->nb_samples > cBufSamples) // (min 10000)
            AllocateCircularBuffer(filtframe->nb_samples);
        else if (cBufPos + curLen >= cBuf.Length)
            cBufPos = 0;

        long newPts         = filterFirstPts + av_rescale_q((long)(curSamples + missedSamples), sinkTimebase, AudioStream.AVStream->time_base);
        var samplesSpeed1   = filtframe->nb_samples * speed;
        missedSamples      += samplesSpeed1 - (int)samplesSpeed1;
        curSamples         += (int)samplesSpeed1;

        AudioFrame mFrame = new()
        {
            dataLen     = curLen,
            Timestamp   = (long)((newPts * AudioStream.Timebase) - demuxer.StartTime + Config.Audio.Delay),
            speed       = speed
        };

        if (CanTrace) Log.Trace($"Processes {TicksToTime(mFrame.Timestamp)}");

        fixed (byte* circularBufferPosPtr = &cBuf[cBufPos])
            mFrame.dataPtr = (IntPtr)circularBufferPosPtr;

        Marshal.Copy(filtframe->data[0], cBuf, cBufPos, mFrame.dataLen);
        cBufPos += curLen;

        Frames.Enqueue(mFrame);
        av_frame_unref(filtframe);
    }
}

/// <summary>
/// FFmpeg Filter
/// </summary>
public class Filter
{
    /// <summary>
    /// <para>
    /// FFmpeg valid filter id
    /// (Required only to send commands)
    /// </para>
    /// </summary>
    public string Id    { get; set; }

    /// <summary>
    /// FFmpeg valid filter name
    /// </summary>
    public string Name  { get; set; }

    /// <summary>
    /// FFmpeg valid filter args
    /// </summary>
    public string Args  { get; set; }
}

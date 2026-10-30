using System.Runtime.InteropServices;
using static System.Globalization.CultureInfo;

using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaStream;

namespace FlyleafLib.MediaFramework.MediaDecoder;

public unsafe partial class AudioDecoder
{   // TODO: Check locks (lockSpeed) - during seek and speed change (also in xaudio submit samples - we change the data len and we lose sync with submited samples vs played)

    static AVFilter* ATEMPO     = avfilter_get_by_name("atempo");
    static AVFilter* ABUFFER    = avfilter_get_by_name("abuffer");
    static AVFilter* ABUFFERSINK= avfilter_get_by_name("abuffersink");

    AVFilterContext*        abufferCtx;
    AVFilterContext*        abufferSinkCtx;
    AVFilterGraph*          filterGraph;
    bool                    abufferDrained;
    AVRational              streamSampleRateTimebase;
    AVFrame*                filtframe;
    object                  lockSpeed = new();

    long                    firstPts;                   // First valid pts of the current continues session
    long                    gapOffsetTb;                // Offset that we create an actual gap-discontinuity
    long                    decodedSamples;             // Continues decoded amples to calculate expectingPts (based on firstPts)
    internal long           expectingPts;               // Expected next continues decoded pts
    long                    filtSamples;                // Continues filtered samples to calculate frame's pts-timestamp
    double                  filtMissedSamples;          // Fixes rounding issues

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
        /* NOTES
         * 
         * We don't use sink's timebase / timestamps. Instead we manually calculated it based on samples.
         * We pass sample rate as timebase for input (could cause issues with filters that use pts as we don't rescale from original?)
         * We cannot currently handle planar (we handle single buffer packed/interleaved with dataLen etc.)
         */
        int ret = -1;

        try
        {
            DisposeFilters();

            AVFilterContext* linkCtx;

            filtframe       = av_frame_alloc();
            filterGraph     = avfilter_graph_alloc();
            firstPts        = NoTs;
            abufferDrained  = false;

            // IN (abuffersrc)
            linkCtx = abufferCtx = CreateFilter(ABUFFER,
                $"channel_layout={AudioStream.ChannelLayoutStr}:sample_fmt={AudioStream.SampleFormatStr}:sample_rate={codecCtx->sample_rate}:time_base=1/{streamSampleRateTimebase.Den}");

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
                    linkCtx = CreateFilter(ATEMPO, $"tempo={speed.ToString("0.0000000000", InvariantCulture)}", linkCtx);
                else if ((speed > 2 & speed <= 4) || (speed >= 0.25 && speed < 0.5))
                {
                    var singleAtempoSpeed = Math.Sqrt(speed);
                    linkCtx = CreateFilter(ATEMPO, $"tempo={singleAtempoSpeed.ToString("0.0000000000", InvariantCulture)}", linkCtx);
                    linkCtx = CreateFilter(ATEMPO, $"tempo={singleAtempoSpeed.ToString("0.0000000000", InvariantCulture)}", linkCtx);
                }
                else if (speed > 4 || speed >= 0.125 && speed < 0.25)
                {
                    var singleAtempoSpeed = Math.Pow(speed, 1.0 / 3);
                    linkCtx = CreateFilter(ATEMPO, $"tempo={singleAtempoSpeed.ToString("0.0000000000", InvariantCulture)}", linkCtx);
                    linkCtx = CreateFilter(ATEMPO, $"tempo={singleAtempoSpeed.ToString("0.0000000000", InvariantCulture)}", linkCtx);
                    linkCtx = CreateFilter(ATEMPO, $"tempo={singleAtempoSpeed.ToString("0.0000000000", InvariantCulture)}", linkCtx);
                }
            }

            // OUT (abuffersink)
            abufferSinkCtx = avfilter_graph_alloc_filter(filterGraph, ABUFFERSINK, null);

            // Xaudio supported formats (Packed/Interleaved)
            Set(abufferSinkCtx, "sample_formats", [AVSampleFormat.U8, AVSampleFormat.S16, AVSampleFormat.S32, AVSampleFormat.Flt], AVOptionType.SampleFmt);

            // XAudio supported layouts (Native)
            if (AudioStream.ChannelLayout.order == AVChannelOrder.Native && (AudioStream.ChannelLayout.u.mask & ~0x0003FFFFU) == 0)
                Set(abufferSinkCtx, "channel_layouts", [AudioStream.ChannelLayout], AVOptionType.Chlayout);
            else
                Set(abufferSinkCtx, "channel_layouts", [AV_CHANNEL_LAYOUT_STEREO],  AVOptionType.Chlayout);

            ret = avfilter_init_dict(abufferSinkCtx, null);

            _ = avfilter_link(linkCtx, 0, abufferSinkCtx, 0);

            // GRAPH CONFIG
            ret = avfilter_graph_config(filterGraph, null);
            if (ret < 0)
                throw new Exception($"[FilterGraph] {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

            // SINK CONFIG
            var input0 = abufferSinkCtx->inputs[0];

            bool changed = false;
            if (input0->format != (int)SampleFormat)
            {
                SampleFormat = (AVSampleFormat)input0->format;
                changed = true;
            }

            fixed(AVChannelLayout* ptr = &channelLayout)
                if (av_channel_layout_compare(&input0->ch_layout, ptr) != 0)
                {
                    channelLayout = input0->ch_layout;
                    ChannelLayoutStr = GetChannelLayoutStr(channelLayout);
                    changed = true;
                }

            if (input0->sample_rate != SampleRate)
            {
                SampleRate = input0->sample_rate;
                SampleRateTimebase = 1000 * 10000.0 / SampleRate;
                changed = true;
            }

            SampleBytes = av_get_bytes_per_sample(SampleFormat) * channelLayout.nb_channels;

            // TBR: DONT CHANGE values will affect Screamer | Ensures we have at least 20-70ms samples to avoid audio crackling and av sync issues
            ((FilterLink*)input0)->min_samples = SampleRate * 20 / 1000;
            ((FilterLink*)input0)->max_samples = SampleRate * 70 / 1000;

            if (changed)
                FormatChanged?.Invoke(this);

            return 0;
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
    {   // Possible Task to avoid locking UI thread as lockAtempo can wait for the Frames queue to be freed (will cause other issues and couldnt reproduce the possible dead lock)
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
    void FixSample(AudioFrame frame, double newSpeed)
    {
        var oldSpeed    = frame.speed;
        var oldDataLen  = frame.dataLen;
        frame.dataLen   = Align((int) (oldDataLen * oldSpeed / newSpeed), SampleBytes);
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
                if (SampleFormat == AVSampleFormat.U8)
                    for (int p = oldDataLen; p < frame.dataLen; p++)
                        cBuf[curOffset + p] = 0x80;
                else
                    for (int p = oldDataLen; p < frame.dataLen; p++)
                        cBuf[curOffset + p] = 0;
            }
        }
    }
    
    public int UpdateFilter(string filterId, string key, string value)
    {
        lock (lockCodecCtx)
        {
            if (filterGraph == null)
                return -1;

            int ret = avfilter_graph_send_command(filterGraph, filterId, key, value, null, 0, 0);
            Log.Info($"[{filterId}] {key}={value} {(ret >=0 ? "success" : "failed")}");

            return ret;
        }
    }
    public int ReloadFilters()
    {
        lock (lockActions)
            lock (lockCodecCtx)
                return SetupFilters();
    }

    private void ProcessFilters()
    {
        /* NOTES
         * We can't trust pts/duration. Even pts can have a small gap that will be corrected on the next pts (decoder's issue).
         * So we calculte expecting pts based on samples only and when the gap is large enough we reset.
         * Filtered frames are always continues timestamps (decoded frames will control the gaps *setFirstPts)
         */

        if (firstPts == NoTs)
        {
            firstPts            = frame->pts;
            decodedSamples      = 0;
            filtSamples         = 0;
            filtMissedSamples   = 0;
        }
        else if (Math.Abs(frame->pts - expectingPts) > gapOffsetTb)
        {
            Log.Warn($"Resync filters! ({TicksToTime((long)((frame->pts - expectingPts) * AudioStream.Timebase))} distance)");

            DrainFilters();
            SetupFilters();
            ProcessFilters(); // Recursion (don't drop the gap frame)

            return;
        }

        decodedSamples += frame->nb_samples;
        expectingPts    = firstPts + av_rescale_q(decodedSamples, streamSampleRateTimebase, AudioStream.AVStream->time_base);
        
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
            if (av_buffersink_get_frame_flags(abufferSinkCtx, filtframe, 0) < 0)
                return;

            ProcessFilter();
        }
    }
    private void ProcessFilter()
    {
        var curLen = filtframe->nb_samples * SampleBytes;

        if (filtframe->nb_samples > cBufSamples) // (min 10000)
            AllocateCircularBuffer(filtframe->nb_samples);
        else if (cBufPos + curLen >= cBuf.Length)
            cBufPos = 0;

        long newPts         = firstPts + av_rescale_q((long)(filtSamples + filtMissedSamples), streamSampleRateTimebase, AudioStream.AVStream->time_base);
        var samplesSpeed1   = filtframe->nb_samples * speed;
        filtMissedSamples  += samplesSpeed1 - (int)samplesSpeed1;
        filtSamples        += (int)samplesSpeed1;

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

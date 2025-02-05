using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Whisper.net;
using Whisper.net.LibraryLoader;
using static FlyleafLib.Logger;

namespace FlyleafLib.MediaPlayer;

#nullable enable

// TODO: L: Pause and resume ASR
// TODO: L: Enable simultaneous use of primary and secondary (in conjunction with automatic translation)
// TODO: L: Support ASR (Translate) so that it can run in parallel with ASR (Transcribe)

/// <summary>
/// Running ASR from a media file
/// </summary>
/// <remarks>
/// Read in a separate thread from the video playback.
/// Note that multiple threads cannot seek to multiple locations for a single AVFormatContext,
/// so it is necessary to open it with another avformat_open_input for the same video.
/// </remarks>
public class SubtitlesASR
{
    private readonly SubtitlesManager _subtitlesManager;
    private readonly Config _config;
    private readonly Lock _locker = new();
    private CancellationTokenSource? _cts = null;
    private List<SubtitleData>? _prevSubs;
    private (string, int)? _openedStream;

    private int? _subIndex;
    public int? SubIndex => _subIndex;

    private readonly LogHandler Log;

    public SubtitlesASR(SubtitlesManager subtitlesManager, Config config)
    {
        _subtitlesManager = subtitlesManager;
        _config = config;

        Log = new LogHandler(("[#1]").PadRight(8, ' ') + " [SubtitlesASR  ] ");
    }

    /// <summary>
    /// Check that ASR is executable
    /// </summary>
    /// <param name="err">error information</param>
    /// <returns></returns>
    public bool CanExecute(out string err)
    {
        if (_config.Subtitles.WhisperModel == null)
        {
            err = "The whisper model is not set. Please download it from the settings.";
            return false;
        }

        if (!File.Exists(_config.Subtitles.WhisperModel.ModelFilePath))
        {
            err = $"The whisper model file '{_config.Subtitles.WhisperModel.ModelFileName}' does not exist in the folder. Please download it from the settings.";
            return false;
        }

        err = "";

        return true;
    }

    /// <summary>
    /// Open madi file and read all subtitle data from audio
    /// </summary>
    /// <param name="subIndex">0: Primary, 1: Secondary</param>
    /// <param name="url">media file path</param>
    /// <param name="streamIndex">Audio streamIndex</param>
    /// <param name="curTime">Current playback timestamp, from which whisper is run</param>
    public void Open(int subIndex, string url, int streamIndex, TimeSpan curTime)
    {
        // Cancel if already executed
        TryCancel(true);

        lock (_locker)
        {
            _cts = new CancellationTokenSource();
            _subIndex = subIndex;

            (string url, int streamIndex) openStream = (url, streamIndex);
            if (_openedStream == openStream && _prevSubs != null)
            {
                // Restore Subs data for the same audio
                _subtitlesManager[subIndex].Load(_prevSubs);
            }
            else
            {
                _openedStream = openStream;
                // Clear stored subs when a different audio is opened (to prevent memory leaks)
                _prevSubs = null;
            }

            using AudioReader reader = new(_config);

            bool isFirst = true;
            reader.Open(url, streamIndex);
            reader.ReadAll(curTime, (data) =>
            {
                if (_cts.Token.IsCancellationRequested)
                {
                    return;
                }

                if (isFirst)
                {
                    // Delete subtitles after the first subtitle to be added (leave the previous one)
                    _subtitlesManager[subIndex].DeleteAfter(data.StartTime);

                    // Set language
                    // Can currently only be set for the whole, not per subtitle
                    _subtitlesManager[subIndex].LanguageSource = Language.Get(data.Language);

                    isFirst = false;
                }

                SubtitleData sub = new()
                {
                    Text = data.Text,
                    StartTime = data.StartTime,
                    EndTime = data.EndTime,
#if DEBUG
                    StartTimeChunk = data.StartTimeChunk,
                    EndTimeChunk = data.EndTimeChunk,
#endif
                };

                _subtitlesManager[subIndex].Add(sub);
            }, _cts.Token);
        }

        if (!_cts.Token.IsCancellationRequested)
        {
            // TODO: L: Notify, express completion in some way
            Utils.PlayCompletionSound();
        }
    }

    public void TryCancel(bool isWait)
    {
        var cts = _cts;
        if (cts != null)
        {
            // Save current Subs before canceling.
            // TODO: L: locking?
            if (!cts.IsCancellationRequested)
            {
                var prevSubs = _subtitlesManager[_subIndex!.Value].Subs;
                if (prevSubs.Count > 0)
                {
                    _prevSubs = prevSubs.ToList();
                    // clear
                    _subtitlesManager[_subIndex!.Value].Clear();
                }

                cts.Cancel();
                _subIndex = null;
            }
            else
            {
                Log.Info("Already cancel requested");
            }

            if (!isWait)
            {
                return;
            }

            lock (_locker)
            {
                // dispose after it is no longer used.
                cts.Dispose();
                _cts = null;
            }
        }
    }

    public void Reset(int subIndex)
    {
        if (_subIndex == subIndex)
        {
            // cancel asynchronously as it takes time to cancel.
            TryCancel(false);
        }
    }
}

public class WhisperExecuter
{
    private readonly Config _config;

    private readonly LogHandler Log;

    public WhisperExecuter(Config config)
    {
        _config = config;
        Log = new LogHandler(("[#1]").PadRight(8, ' ') + " [WhisperExecute] ");
    }

    public async IAsyncEnumerable<SubtitleASRData> Do(MemoryStream waveStream, TimeSpan chunkStart, TimeSpan chunkEnd, int chunkCnt, CancellationToken token = default)
    {
        // Output wav file for debugging
        //using (FileStream fs = new($"subtitlewhisper-{chunkCnt}.wav", FileMode.Create, FileAccess.Write))
        //{
        //    waveStream.WriteTo(fs);
        //    waveStream.Position = 0;
        //}


        if (_config.Subtitles.WhisperRuntimeLibraries.Count >= 1)
        {
            RuntimeOptions.RuntimeLibraryOrder = [.. _config.Subtitles.WhisperRuntimeLibraries];
        }
        else
        {
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]; // fallback to default
        }

        using WhisperFactory whisperFactory = WhisperFactory.FromPath(_config.Subtitles.WhisperModel.ModelFilePath);

        WhisperProcessorBuilder whisperBuilder = whisperFactory.CreateBuilder();
        await using WhisperProcessor processor = _config.Subtitles.WhisperParameters.ConfigureBuilder(whisperBuilder).Build();

        await foreach (var result in processor.ProcessAsync(waveStream, token))
        {
            token.ThrowIfCancellationRequested();

            TimeSpan start = chunkStart.Add(result.Start);
            TimeSpan end = chunkStart.Add(result.End);
            if (end > chunkEnd)
            {
                // Shorten by 20 ms to prevent the next subtitle from being covered
                end = chunkEnd.Subtract(TimeSpan.FromMilliseconds(20));
            }

            SubtitleASRData sub = new()
            {
                Text = result.Text,
                StartTime = start,
                EndTime = end,
#if DEBUG
                StartTimeChunk = result.Start,
                EndTimeChunk = result.End,
#endif
                Language = result.Language
            };

            Log.Debug(string.Format("{0}->{1} ({2}->{3}): {4}",
                start, end,
                result.Start, result.End,
                result.Text));

            yield return sub;
        }
    }
}

public unsafe class AudioReader : IDisposable
{
    private readonly Config _config;
    private AVFormatContext* _fmtCtx = null;
    private AVStream* _stream = null;
    private AVCodec* _codec = null;
    private AVCodecContext* _codecCtx = null;
    private SwrContext* _swrContext = null;
    private AVPacket* _packet = null;
    private AVFrame* _frame = null;
    private readonly LogHandler Log;

    public AudioReader(Config config)
    {
        _config = config;
        Log = new LogHandler(("[#1]").PadRight(8, ' ') + " [AudioReader   ] ");
    }

    public void Open(string url, int streamIndex)
    {
        fixed (AVFormatContext** pFmtCtx = &_fmtCtx)
            avformat_open_input(pFmtCtx, url, null, null)
                .ThrowExceptionIfError("avformat_open_input");

        avformat_find_stream_info(_fmtCtx, null)
            .ThrowExceptionIfError("avformat_find_stream_info");

        // Select an audio stream for a given index
        bool streamFound = _fmtCtx->nb_streams >= streamIndex + 1 &&
                           _fmtCtx->streams[streamIndex]->codecpar->codec_type == AVMediaType.Audio;
        if (!streamFound)
        {
            throw new InvalidOperationException($"No audio stream was found for the streamIndex:{streamIndex}");
        }

        _stream = _fmtCtx->streams[streamIndex];

        _codec = avcodec_find_decoder(_stream->codecpar->codec_id);
        if (_codec is null)
        {
            throw new InvalidOperationException("avcodec_find_decoder");
        }

        _codecCtx = avcodec_alloc_context3(_codec);
        if (_codecCtx is null)
        {
            throw new InvalidOperationException("avcodec_alloc_context3");
        }

        avcodec_parameters_to_context(_codecCtx, _stream->codecpar)
            .ThrowExceptionIfError("avcodec_parameters_to_context");

        _codecCtx->pkt_timebase = _fmtCtx->streams[_stream->index]->time_base;

        avcodec_open2(_codecCtx, _codec, null)
            .ThrowExceptionIfError("avcodec_open2");
    }

    /// <summary>
    /// Extract audio files in WAV format and run Whisper
    /// </summary>
    /// <param name="curTime">Current playback timestamp, from which whisper is run</param>
    /// <param name="addSub">Action to process one result</param>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="OperationCanceledException"></exception>
    public void ReadAll(TimeSpan curTime, Action<SubtitleASRData> addSub, CancellationToken token = default)
    {
        int ret = -1;

        _packet = av_packet_alloc();
        _frame = av_frame_alloc();

        // When passing the audio file to Whisper, it must be converted to a 16000 sample rate WAV file.
        // For this purpose, the ffmpeg API is used to perform the conversion.
        // Audio files are divided by a certain size, stored in memory, and passed by memory stream.
        int targetSampleRate = 16000;
        int targetChannel = 1;
        MemoryStream waveStream = new();

        // Stream processing is performed by dividing the audio by a certain size and passing it to whisper.
        // TODO: L: Review this process as subtitles may be cut off in the middle.
        long chunkSize = _config.Subtitles.ASRChunkSize;
        // Also split by elapsed seconds for live
        TimeSpan chunkElapsed = TimeSpan.FromSeconds(_config.Subtitles.ASRChunkSeconds);
        Stopwatch chunkSw = new();
        chunkSw.Start();

        int chunkCnt = 0;
        long framePts = long.MinValue;
        TimeSpan? chunkStart = null;

        WriteWavHeader(waveStream, targetSampleRate, targetChannel);

        // Whisper from the current playback position
        // TODO: L: Fold back and allow the first half to run as well.
        if (curTime > TimeSpan.FromSeconds(30))
        {
            TimeSpan seekTime = curTime.Subtract(TimeSpan.FromSeconds(10));
            // Seek if later than 30 seconds (Seek before 10 seconds)
            // TODO: L: m2ts seek problem?
            av_seek_frame(_fmtCtx, -1, seekTime.Ticks / 10, 0)
                .ThrowExceptionIfError("av_seek_frame");
        }

        long startTime = 0;
        if (_fmtCtx->start_time != AV_NOPTS_VALUE)
        {
            // 0.1us (tick)
            startTime = _fmtCtx->start_time * 10;
        }
        // 0.1us (tick)
        double timebase = av_q2d(_stream->time_base) * 10000.0 * 1000.0;

        int demuxErrors = 0;
        int decodeErrors = 0;

        while (true)
        {
            ret = av_read_frame(_fmtCtx, _packet);
            if (ret == AVERROR_EOF)
            {
                break;
            }

            if (ret != 0)
            {
                // demux error
                av_packet_unref(_packet);
                if (CanWarn) Log.Warn($"av_read_frame: {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                if (++demuxErrors == _config.Demuxer.MaxErrors)
                {
                    ret.ThrowExceptionIfError("av_read_frame");
                }
            }

            if (token.IsCancellationRequested)
            {
                av_packet_unref(_packet);
                break;
            }

            // Discard all but the selected audio stream.
            if (_packet->stream_index != _stream->index)
            {
                av_packet_unref(_packet);
                continue;
            }

            ret = avcodec_send_packet(_codecCtx, _packet);
            if (ret == AVERROR(EAGAIN))
            {
                continue;
            }

            if (ret != 0)
            {
                // decoder error
                av_packet_unref(_packet);
                if (CanWarn) Log.Warn($"avcodec_send_packet: {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                if (++decodeErrors == _config.Decoder.MaxErrors)
                {
                    ret.ThrowExceptionIfError("avcodec_send_packet");
                }

                continue;
            }

            av_packet_unref(_packet);

            while (ret >= 0)
            {
                ret = avcodec_receive_frame(_codecCtx, _frame);
                if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF)
                {
                    break;
                }
                ret.ThrowExceptionIfError("avcodec_receive_frame");

                framePts = _frame->pts;

                if (chunkStart == null)
                {
                    chunkStart = new TimeSpan((long)(framePts * timebase) - startTime);
                    if (chunkStart.Value.Ticks < 0)
                    {
                        // Correct to 0 if negative
                        chunkStart = new TimeSpan(0);
                    }
                }

                ResampleTo(waveStream, _frame, targetSampleRate, targetChannel);

                // TODO: L: want it to split at the silent part
                if (waveStream.Length >= chunkSize || chunkSw.Elapsed >= chunkElapsed)
                {
                    ProcessChunk(waveStream, framePts);

                    waveStream = new MemoryStream();
                    WriteWavHeader(waveStream, targetSampleRate, targetChannel);

                    chunkStart = null;
                    chunkSw.Restart();
                }
            }
        }

        if (!token.IsCancellationRequested)
        {
            // Process remaining
            if (waveStream.Length > 0 && framePts != long.MinValue)
            {
                ProcessChunk(waveStream, framePts);
            }
        }

        waveStream.Dispose();

        return;

        void ProcessChunk(MemoryStream stream, long endPts)
        {
            chunkCnt++;

            UpdateWavHeader(stream);
            stream.Position = 0;

            TimeSpan chunkEnd = TimeSpan.FromSeconds(endPts * av_q2d(_stream->time_base));
            //Debug.WriteLine("chunk: " + chunkCnt);
            //Debug.WriteLine("duration: " + chunkEnd.Subtract(chunkStart.Value));
            //Debug.WriteLine("duration sec: " + chunkEnd.Subtract(chunkStart.Value).TotalSeconds);
            //Debug.WriteLine($"range: {chunkStart} - {chunkEnd}");

            try
            {
                // TODO: L: slightly leaking memory?
                WhisperExecuter whisperExecuter = new(_config);
                IAsyncEnumerator<SubtitleASRData> result = whisperExecuter
                    .Do(stream, chunkStart!.Value, chunkEnd, chunkCnt, token).GetAsyncEnumerator(token);
                while (result.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    addSub(result.Current);
                }
            }
            catch (OperationCanceledException)
            {
                Log.Debug("Whisper canceled");
            }

            stream.Dispose();
        }
    }

    private static void WriteWavHeader(Stream stream, int sampleRate, int channels)
    {
        BinaryWriter writer = new(stream);
        writer.Write(['R', 'I', 'F', 'F']);
        writer.Write(0); // placeholder for file size
        writer.Write(['W', 'A', 'V', 'E']);
        writer.Write(['f', 'm', 't', ' ']);
        writer.Write(16); // PCM header size
        writer.Write((short)1); // PCM format
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2); // Byte rate
        writer.Write((short)(channels * 2)); // Block align
        writer.Write((short)16); // Bits per sample
        writer.Write(['d', 'a', 't', 'a']);
        writer.Write(0); // placeholder for data size
    }

    private static void UpdateWavHeader(Stream stream)
    {
        long fileSize = stream.Length;
        stream.Seek(4, SeekOrigin.Begin);
        stream.Write(BitConverter.GetBytes((int)(fileSize - 8)), 0, 4);
        stream.Seek(40, SeekOrigin.Begin);
        stream.Write(BitConverter.GetBytes((int)(fileSize - 44)), 0, 4);
    }

    private void ResampleTo(Stream toStream, AVFrame* frame, int targetSampleRate, int targetChannel)
    {
        AVChannelLayout outLayout;
        av_channel_layout_default(&outLayout, targetChannel);

        if (_swrContext == null)
        {
            // NOTE: important to reuse this context
            fixed (SwrContext** ptr = &_swrContext)
            {
                swr_alloc_set_opts2(
                    ptr,
                    &outLayout,
                    AVSampleFormat.S16,
                    targetSampleRate,
                    &frame->ch_layout,
                    (AVSampleFormat)frame->format,
                    frame->sample_rate,
                    0, null)
                    .ThrowExceptionIfError("swr_alloc_set_opts2");

                swr_init(_swrContext)
                    .ThrowExceptionIfError("swr_init");
            }
        }

        // ffmpeg ref: https://github.com/FFmpeg/FFmpeg/blob/504df09c34607967e4109b7b114ee084cf15a3ae/libavfilter/af_aresample.c#L171-L227
        double ratio = targetSampleRate * 1.0 / frame->sample_rate; // 16000:44100=0.36281179138321995
        int n_out = (int)(frame->nb_samples * ratio) + 32;

        long delay = swr_get_delay(_swrContext, targetSampleRate);
        if (delay > 0)
        {
            n_out += (int)Math.Min(delay, Math.Max(4096, n_out));
        }

        byte* sampledBuf = stackalloc byte[n_out * outLayout.nb_channels * sizeof(ushort)];
        int samplesPerChannel = swr_convert(
                _swrContext,
                &sampledBuf,
                n_out,
                frame->extended_data,
                frame->nb_samples);
        samplesPerChannel.ThrowExceptionIfError("swr_convert");

        int resampledDataSize = samplesPerChannel * outLayout.nb_channels * sizeof(ushort);

        toStream.Write(new Span<byte>(sampledBuf, resampledDataSize));
    }

    private bool _isDisposed;

    public void Dispose()
    {
        if (_isDisposed)
            return;

        // av_frame_alloc
        if (_frame != null)
        {
            fixed (AVFrame** ptr = &_frame)
            {
                av_frame_free(ptr);
            }
        }

        // av_packet_alloc
        if (_packet != null)
        {
            fixed (AVPacket** ptr = &_packet)
            {
                av_packet_free(ptr);
            }
        }

        // swr_init
        if (_swrContext != null)
        {
            fixed (SwrContext** ptr = &_swrContext)
            {
                swr_free(ptr);
            }
        }

        // avcodec_alloc_context3
        if (_codecCtx != null)
        {
            fixed (AVCodecContext** ptr = &_codecCtx)
            {
                avcodec_free_context(ptr);
            }
        }

        // avformat_open_input
        if (_fmtCtx != null)
        {
            fixed (AVFormatContext** ptr = &_fmtCtx)
            {
                avformat_close_input(ptr);
            }
        }

        _isDisposed = true;
    }
}

public class SubtitleASRData
{
    public string Text { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }

#if DEBUG
    public TimeSpan StartTimeChunk { get; set; }
    public TimeSpan EndTimeChunk { get; set; }
#endif

    public TimeSpan Duration => EndTime - StartTime;

    // ISO6391
    // ref: https://github.com/openai/whisper/blob/main/whisper/tokenizer.py#L10
    public string Language { get; set; }
}

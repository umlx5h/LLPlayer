using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;
using static FlyleafLib.Logger;

namespace FlyleafLib.MediaPlayer;

#nullable enable

// TODO: L: Pause and resume ASR

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
    private readonly Lock _lockerSubs = new();
    private CancellationTokenSource? _cts = null;
    public HashSet<int> SubIndexSet { get; } = new();

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
    /// Open media file and read all subtitle data from audio
    /// </summary>
    /// <param name="subIndex">0: Primary, 1: Secondary</param>
    /// <param name="url">media file path</param>
    /// <param name="streamIndex">Audio streamIndex</param>
    /// <param name="curTime">Current playback timestamp, from which whisper is run</param>
    /// <returns>true: process completed, false: run in progress</returns>
    public bool Open(int subIndex, string url, int streamIndex, TimeSpan curTime)
    {
        // When Dual ASR: Copy the other ASR result and return early
        if (SubIndexSet.Count > 0 && !SubIndexSet.Contains(subIndex))
        {
            lock (_lockerSubs)
            {
                SubIndexSet.Add(subIndex);
                int otherIndex = (subIndex + 1) % 2;

                if (_subtitlesManager[otherIndex].Subs.Count > 0)
                {
                    bool enableTranslated = _config.Subtitles[subIndex].EnabledTranslated;

                    // Copy other ASR result
                    _subtitlesManager[subIndex]
                        .Load(_subtitlesManager[otherIndex].Subs.Select(s =>
                        {
                            var clone = (SubtitleData)s.Clone();

                            if (!enableTranslated)
                            {
                                clone.TranslatedText = null;
                                clone.EnabledTranslated = true;
                            }

                            return clone;
                        }));

                    if (!_subtitlesManager[otherIndex].IsLoading)
                    {
                        // Copy the language source if one of them is already done.
                        _subtitlesManager[subIndex].LanguageSource = _subtitlesManager[otherIndex].LanguageSource;
                    }
                }
            }

            // return early
            return false;
        }

        // If it has already been executed, cancel it to start over from the current playback position.
        if (SubIndexSet.Contains(subIndex))
        {
            Dictionary<int, List<SubtitleData>> prevSubs = new();
            HashSet<int> prevSubIndexSet = [.. SubIndexSet];
            lock (_lockerSubs)
            {
                // backup current result
                foreach (int i in SubIndexSet)
                {
                    prevSubs[i] = _subtitlesManager[i].Subs.ToList();
                }
            }
            // Cancel preceding execution and wait
            TryCancel(true);

            // restore previous result
            lock (_lockerSubs)
            {
                foreach (int i in prevSubIndexSet)
                {
                    _subtitlesManager[i].Load(prevSubs[i]);
                    // Re-enable spinner
                    _subtitlesManager[i].StartLoading();

                    SubIndexSet.Add(i);
                }
            }
        }

        lock (_locker)
        {
            _cts = new CancellationTokenSource();
            SubIndexSet.Add(subIndex);

            using AudioReader reader = new(_config);

            reader.Open(url, streamIndex);
            reader.ReadAll(curTime, data =>
            {
                if (_cts.Token.IsCancellationRequested)
                {
                    return;
                }

                lock (_lockerSubs)
                {
                    foreach (int i in SubIndexSet)
                    {
                        bool isInit = false;
                        if (_subtitlesManager[i].LanguageSource == null)
                        {
                            isInit = true;

                            // Delete subtitles after the first subtitle to be added (leave the previous one)
                            _subtitlesManager[i].DeleteAfter(data.StartTime);

                            // Set language
                            // Can currently only be set for the whole, not per subtitle
                            _subtitlesManager[i].LanguageSource = Language.Get(data.Language);
                        }

                        SubtitleData sub = new()
                        {
                            Text = data.Text,
                            StartTime = data.StartTime,
                            EndTime = data.EndTime,
#if DEBUG
                            ChunkNo = data.ChunkNo,
                            StartTimeChunk = data.StartTimeChunk,
                            EndTimeChunk = data.EndTimeChunk,
#endif
                        };

                        _subtitlesManager[i].Add(sub);
                        if (isInit)
                        {
                            _subtitlesManager[i].SetCurrentTime(new TimeSpan(_config.Subtitles.player.CurTime));
                        }
                    }
                }
            }, _cts.Token);

            if (!_cts.Token.IsCancellationRequested)
            {
                // TODO: L: Notify, express completion in some way
                Utils.PlayCompletionSound();
            }

            foreach (int i in SubIndexSet)
            {
                lock (_lockerSubs)
                {
                    // Stop spinner (required when dual ASR)
                    _subtitlesManager[i].StartLoading().Dispose();
                }
            }
        }

        return true;
    }

    public void TryCancel(bool isWait)
    {
        var cts = _cts;
        if (cts != null)
        {
            if (!cts.IsCancellationRequested)
            {
                lock (_lockerSubs)
                {
                    foreach (var i in SubIndexSet)
                    {
                        _subtitlesManager[i].Clear();
                    }
                }

                cts.Cancel();
                lock (_lockerSubs)
                {
                    SubIndexSet.Clear();
                }
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
        if (!SubIndexSet.Contains(subIndex))
            return;

        if (SubIndexSet.Count == 2)
        {
            lock (_lockerSubs)
            {
                // When Dual ASR: only the state is cleared without stopping ASR execution.
                SubIndexSet.Remove(subIndex);
                _subtitlesManager[subIndex].Clear();
            }

            return;
        }

        // cancel asynchronously as it takes time to cancel.
        TryCancel(false);
    }
}

public class AudioReader : IDisposable
{
    private readonly Config _config;
    private unsafe AVFormatContext* _fmtCtx = null;
    private unsafe AVStream* _stream = null;
    private unsafe AVCodec* _codec = null;
    private unsafe AVCodecContext* _codecCtx = null;
    private unsafe SwrContext* _swrContext = null;
    private unsafe AVPacket* _packet = null;
    private unsafe AVFrame* _frame = null;

    private bool _isFile;

    private readonly LogHandler Log;

    public AudioReader(Config config)
    {
        _config = config;
        Log = new LogHandler(("[#1]").PadRight(8, ' ') + " [AudioReader   ] ");
    }

    public unsafe void Open(string url, int streamIndex)
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

        _isFile = File.Exists(url);
    }

    private record struct AudioChunk(MemoryStream Stream, int ChunkNumber, TimeSpan Start, TimeSpan End);

    /// <summary>
    /// Extract audio files in WAV format and run Whisper
    /// </summary>
    /// <param name="curTime">Current playback timestamp, from which whisper is run</param>
    /// <param name="addSub">Action to process one result</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="OperationCanceledException"></exception>
    public void ReadAll(TimeSpan curTime, Action<SubtitleASRData> addSub, CancellationToken cancellationToken)
    {
        unsafe
        {
            _packet = av_packet_alloc();
            _frame = av_frame_alloc();

            // Whisper from the current playback position
            // TODO: L: Fold back and allow the first half to run as well.
            if (curTime > TimeSpan.FromSeconds(30))
            {
                long savedPbPos = _fmtCtx->pb != null ? _fmtCtx->pb->pos : 0;

                long ticks = curTime.Subtract(TimeSpan.FromSeconds(10)).Ticks;
                // Seek if later than 30 seconds (Seek before 10 seconds)
                // TODO: L: m2ts seek problem?
                int ret = avformat_seek_file(_fmtCtx, -1, long.MinValue, ticks / 10, ticks / 10, SeekFlags.Any);
                if (ret < 0)
                {
                    Log.Info($"Seek failed 1/2 (retrying) {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");
                    ret = avformat_seek_file(_fmtCtx, -1, ticks / 10, ticks / 10, long.MaxValue, SeekFlags.Any);
                    if (ret < 0)
                    {
                        Log.Warn($"Seek failed 2/2 {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                        // (from Demuxer) Flush required because of seek failure
                        if (_fmtCtx->pb != null)
                        {
                            avio_flush(_fmtCtx->pb);
                            _fmtCtx->pb->error = 0;
                            _fmtCtx->pb->eof_reached = 0;
                            avio_seek(_fmtCtx->pb, savedPbPos, 0);
                        }
                        avformat_flush(_fmtCtx);

                        // proceed even if seek failed
                    }
                }
            }
        }

        // Assume a network stream and parallelize the reading of packets and the execution of whisper.
        // For network video, increase capacity as downloads may take longer.
        // (concern that memory usage will increase by three times the chunk size)
        int capacity = _isFile ? 1 : 2;
        BoundedChannelOptions channelOptions = new(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
        };
        Channel<AudioChunk> channel = Channel.CreateBounded<AudioChunk>(channelOptions);

        // own cancellation for producer/consumer
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = cts.Token;

        ConcurrentStack<MemoryStream> memoryStreamPool = new();

        // Consumer: Run whisper
        Task consumerTask = Task.Run(DoConsumer, token);

        // Producer: Extract WAV and pass to consumer
        Task producerTask = Task.Run(DoProducer, token);

        // complete channel
        producerTask.ContinueWith(t =>
            channel.Writer.Complete(), token);

        // When an exception occurs in both consumer and producer, the other is canceled.
        consumerTask.ContinueWith(t =>
            cts.Cancel(), TaskContinuationOptions.OnlyOnFaulted);
        producerTask.ContinueWith(t =>
            cts.Cancel(), TaskContinuationOptions.OnlyOnFaulted);

        try
        {
            Task.WhenAll(consumerTask, producerTask).Wait();
        }
        catch (AggregateException ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // canceled by caller
                if (CanDebug) Log.Debug("Whisper canceled");
                return;
            }

            // canceled because of exceptions
            throw;
        }

        return;

        async Task DoConsumer()
        {
            await using WhisperExecuter whisperExecuter = new(_config);

            while (await channel.Reader.WaitToReadAsync(token))
            {
                // Use TryPeek() to reduce the channel capacity by one.
                if (!channel.Reader.TryPeek(out AudioChunk chunk))
                    throw new InvalidOperationException("can not peek AudioChunk from channel");

                try
                {
                    if (CanDebug) Log.Debug(
                            $"Reading chunk from channel (chunkNo: {chunk.ChunkNumber}, start: {chunk.Start}, end: {chunk.End})");

                    var iter = whisperExecuter.Do(chunk.Stream, chunk.Start, chunk.End, chunk.ChunkNumber, token);
                    await foreach (var result in iter)
                    {
                        addSub(result);
                    }
                }
                finally
                {
                    chunk.Stream.SetLength(0);
                    memoryStreamPool.Push(chunk.Stream);

                    if (!channel.Reader.TryRead(out _))
                        throw new InvalidOperationException("can not discard AudioChunk from channel");
                }
            }
        }

        unsafe void DoProducer()
        {
            // When passing the audio file to Whisper, it must be converted to a 16000 sample rate WAV file.
            // For this purpose, the ffmpeg API is used to perform the conversion.
            // Audio files are divided by a certain size, stored in memory, and passed by memory stream.
            int targetSampleRate = 16000;
            int targetChannel = 1;
            MemoryStream waveStream = new(); // MemoryStream does not need to be disposed for releasing memory
            TimeSpan waveDuration = TimeSpan.Zero; // for logging

            const int waveHeaderSize = 44;

            // Stream processing is performed by dividing the audio by a certain size and passing it to whisper.
            long chunkSize = _config.Subtitles.ASRChunkSize;
            // Also split by elapsed seconds for live
            TimeSpan chunkElapsed = TimeSpan.FromSeconds(_config.Subtitles.ASRChunkSeconds);
            Stopwatch chunkSw = new();
            chunkSw.Start();

            WriteWavHeader(waveStream, targetSampleRate, targetChannel);

            int chunkCnt = 0;
            TimeSpan? chunkStart = null;
            long framePts = AV_NOPTS_VALUE;

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

            int ret = -1;

            while (true)
            {
                token.ThrowIfCancellationRequested();

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

                    if (_frame->best_effort_timestamp != AV_NOPTS_VALUE)
                    {
                        framePts = _frame->best_effort_timestamp;
                    }
                    else if (_frame->pts != AV_NOPTS_VALUE)
                    {
                        framePts = _frame->pts;
                    }
                    else
                    {
                        // Certain encoders sometimes cannot get pts (APE, Musepack)
                        framePts += _frame->duration;
                    }

                    waveDuration = waveDuration.Add(new TimeSpan((long)(_frame->duration * timebase)));

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
                        TimeSpan chunkEnd = TimeSpan.FromSeconds(framePts * av_q2d(_stream->time_base));
                        chunkCnt++;

                        if (CanInfo) Log.Info(
                            $"Process chunk (chunkNo: {chunkCnt}, sizeMB: {waveStream.Length / 1024 / 1024}, duration: {waveDuration}, elapsed: {chunkSw.Elapsed})");

                        UpdateWavHeader(waveStream);

                        AudioChunk chunk = new(waveStream, chunkCnt, chunkStart.Value, chunkEnd);

                        if (CanDebug) Log.Debug($"Writing chunk to channel ({chunkCnt})");
                        // if channel capacity reached, it will be waited
                        channel.Writer.WriteAsync(chunk, token).AsTask().Wait(token);
                        if (CanDebug) Log.Debug($"Done writing chunk to channel ({chunkCnt})");

                        if (memoryStreamPool.TryPop(out var stream))
                            waveStream = stream;
                        else
                            waveStream = new MemoryStream();

                        WriteWavHeader(waveStream, targetSampleRate, targetChannel);
                        waveDuration = TimeSpan.Zero;

                        chunkStart = null;
                        chunkSw.Restart();
                        framePts = AV_NOPTS_VALUE;
                    }
                }
            }

            // Process remaining
            if (waveStream.Length > waveHeaderSize && framePts != AV_NOPTS_VALUE)
            {
                TimeSpan chunkEnd = TimeSpan.FromSeconds(framePts * av_q2d(_stream->time_base));
                chunkCnt++;

                if (CanInfo) Log.Info(
                    $"Process last chunk (chunkNo: {chunkCnt}, sizeMB: {waveStream.Length / 1024 / 1024}, duration: {waveDuration}, elapsed: {chunkSw.Elapsed})");

                UpdateWavHeader(waveStream);

                AudioChunk chunk = new(waveStream, chunkCnt, chunkStart!.Value, chunkEnd);

                if (CanDebug) Log.Debug($"Writing last chunk to channel ({chunkCnt})");
                channel.Writer.WriteAsync(chunk, token).AsTask().Wait(token);
                if (CanDebug) Log.Debug($"Done writing last chunk to channel ({chunkCnt})");
            }
        }
    }

    private static void WriteWavHeader(Stream stream, int sampleRate, int channels)
    {
        using BinaryWriter writer = new(stream, Encoding.UTF8, true);
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
        stream.Position = 0;
    }

    private unsafe void ResampleTo(Stream toStream, AVFrame* frame, int targetSampleRate, int targetChannel)
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

    public unsafe void Dispose()
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

public class WhisperExecuter : IAsyncDisposable
{
    private readonly Config _config;

    private readonly LogHandler Log;
    private readonly IDisposable _logger;
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;

    public WhisperExecuter(Config config)
    {
        _config = config;
        Log = new LogHandler(("[#1]").PadRight(8, ' ') + " [WhisperExecute] ");

        if (_config.Subtitles.WhisperRuntimeLibraries.Count >= 1)
        {
            RuntimeOptions.RuntimeLibraryOrder = [.. _config.Subtitles.WhisperRuntimeLibraries];
        }
        else
        {
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]; // fallback to default
        }

        _logger = CanDebug
            ? LogProvider.AddLogger((level, s) => Log.Debug($"[Whisper.net] [{level.ToString()}] {s}"))
            : Disposable.Empty;

        if (CanDebug) Log.Debug($"Selecting whisper runtime libraries from ({string.Join(",", RuntimeOptions.RuntimeLibraryOrder)})");

        _factory = WhisperFactory.FromPath(_config.Subtitles.WhisperModel.ModelFilePath);

        if (CanDebug) Log.Debug($"Selected whisper runtime library '{RuntimeOptions.LoadedLibrary}'");

        WhisperProcessorBuilder whisperBuilder = _factory.CreateBuilder();
        _processor = _config.Subtitles.WhisperParameters.ConfigureBuilder(whisperBuilder).Build();
    }

    public async ValueTask DisposeAsync()
    {
        await _processor.DisposeAsync();
        _factory.Dispose();
        _logger.Dispose();
    }

    public async IAsyncEnumerable<SubtitleASRData> Do(MemoryStream waveStream, TimeSpan chunkStart, TimeSpan chunkEnd, int chunkCnt, [EnumeratorCancellation] CancellationToken token)
    {
        // Output wav file for debugging
        //using (FileStream fs = new($"subtitlewhisper-{chunkCnt}.wav", FileMode.Create, FileAccess.Write))
        //{
        //    waveStream.WriteTo(fs);
        //    waveStream.Position = 0;
        //}

        await foreach (var result in _processor.ProcessAsync(waveStream, token).ConfigureAwait(false))
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
                Text = result.Text.Trim(), // remove leading whitespace
                StartTime = start,
                EndTime = end,
#if DEBUG
                ChunkNo = chunkCnt,
                StartTimeChunk = result.Start,
                EndTimeChunk = result.End,
#endif
                Language = result.Language
            };

            if (CanDebug) Log.Debug(string.Format("{0}->{1} ({2}->{3}): {4}",
                start, end,
                result.Start, result.End,
                result.Text));

            yield return sub;
        }
    }
}

public class SubtitleASRData
{
    public string Text { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }

#if DEBUG
    public int ChunkNo { get; set; }
    public TimeSpan StartTimeChunk { get; set; }
    public TimeSpan EndTimeChunk { get; set; }
#endif

    public TimeSpan Duration => EndTime - StartTime;

    // ISO6391
    // ref: https://github.com/openai/whisper/blob/main/whisper/tokenizer.py#L10
    public string Language { get; set; }
}

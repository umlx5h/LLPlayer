using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Builders;
using CliWrap.EventStream;
using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaStream;
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
public class SubtitlesASR : NotifyPropertyChanged
{
    private readonly SubtitlesManager _subtitlesManager;
    private readonly Config _config;
    private readonly Lock _locker = new();
    private readonly Lock _lockerSubs = new();
    private CancellationTokenSource? _cts = null;
    public HashSet<int> SubIndexSet { get; } = new();

    // Track the latest processed subtitle time
    private long _latestSubtitleTime = 0;
    
    // Property to expose the latest subtitle time processed
    public long LatestSubtitleTime
    {
        get
        {
            lock (_lockerSubs)
            {
                return _latestSubtitleTime;
            }
        }
        private set
        {
            lock (_lockerSubs)
            {
                if (_latestSubtitleTime != value)
                {
                    _latestSubtitleTime = value;
                    Utils.UI(() => Raise(nameof(LatestSubtitleTime)));
                }
            }
        }
    }

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
        if (_config.Subtitles.ASREngine == SubASREngineType.WhisperCpp)
        {
            if (_config.Subtitles.WhisperCppConfig.Model == null)
            {
                err = "whisper.cpp model is not set. Please download it from the settings.";
                return false;
            }

            if (!File.Exists(_config.Subtitles.WhisperCppConfig.Model.ModelFilePath))
            {
                err = $"whisper.cpp model file '{_config.Subtitles.WhisperCppConfig.Model.ModelFileName}' does not exist in the folder. Please download it from the settings.";
                return false;
            }
        }
        else if (_config.Subtitles.ASREngine == SubASREngineType.FasterWhisper)
        {
            if (_config.Subtitles.FasterWhisperConfig.UseManualEngine)
            {
                if (!File.Exists(_config.Subtitles.FasterWhisperConfig.ManualEnginePath))
                {
                    err = "faster-whisper engine does not exist in the manual path.";
                    return false;
                }
            }
            else
            {
                if (!File.Exists(FasterWhisperConfig.DefaultEnginePath))
                {
                    err = "faster-whisper engine is not downloaded. Please download it from the settings.";
                    return false;
                }
            }

            if (_config.Subtitles.FasterWhisperConfig.UseManualModel)
            {
                if (!Directory.Exists(_config.Subtitles.FasterWhisperConfig.ManualModelDir))
                {
                    err = "faster-whisper manual model directory does not exist.";
                    return false;
                }
            }
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
    /// <param name="type">Demuxer type</param>
    /// <param name="curTime">Current playback timestamp, from which whisper is run</param>
    /// <returns>true: process completed, false: run in progress</returns>
    public bool Execute(int subIndex, string url, int streamIndex, MediaType type, TimeSpan curTime)
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
            SubIndexSet.Add(subIndex);

            _cts = new CancellationTokenSource();
            using AudioReader reader = new(_config, subIndex);
            reader.Open(url, streamIndex, type, _cts.Token);

            if (_cts.Token.IsCancellationRequested)
            {
                return true;
            }

            reader.ReadAll(curTime, data =>
            {
                if (_cts.Token.IsCancellationRequested)
                {
                    return;
                }

                lock (_lockerSubs)
                {
                    // Update the latest subtitle time if this one is later
                    if (data.EndTime.Ticks > _latestSubtitleTime)
                    {
                        LatestSubtitleTime = data.EndTime.Ticks;
                    }
                    
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
                    
                    // Reset the latest subtitle time when canceling
                    LatestSubtitleTime = 0;
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
    private readonly int _subIndex;

    private Demuxer? _demuxer;
    private AudioDecoder? _decoder;
    private AudioStream? _stream;

    private unsafe AVPacket* _packet = null;
    private unsafe AVFrame* _frame = null;
    private unsafe SwrContext* _swrContext = null;

    private bool _isFile;

    private readonly LogHandler Log;

    public AudioReader(Config config, int subIndex)
    {
        _config = config;
        _subIndex = subIndex;
        Log = new LogHandler(("[#1]").PadRight(8, ' ') + " [AudioReader   ] ");
    }

    public void Open(string url, int streamIndex, MediaType type, CancellationToken token)
    {
        _demuxer = new Demuxer(_config.Demuxer, type, _subIndex + 1, false);

        token.Register(() =>
        {
            if (_demuxer != null)
                _demuxer.Interrupter.ForceInterrupt = 1;
        });

        _demuxer.Log.Prefix = _demuxer.Log.Prefix.Replace("Demuxer: ", "DemuxerA:");
        string? error = _demuxer.Open(url);

        if (error != null)
        {
            if (token.IsCancellationRequested)
                return;

            throw new InvalidOperationException($"demuxer open error: {error}");
        }

        _stream = (AudioStream)_demuxer.AVStreamToStream[streamIndex];

        _decoder = new AudioDecoder(_config, _subIndex + 1);
        _decoder.Log.Prefix = _decoder.Log.Prefix.Replace("Decoder: ", "DecoderA:");

        error = _decoder.Open(_stream);

        if (error != null)
        {
            if (token.IsCancellationRequested)
                return;

            throw new InvalidOperationException($"decoder open error: {error}");
        }

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
        if (_demuxer == null || _decoder == null || _stream == null)
            throw new InvalidOperationException("Open() is not called");

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
            await using IASRService asrService = _config.Subtitles.ASREngine switch
            {
                SubASREngineType.WhisperCpp => new WhisperCppASRService(_config),
                SubASREngineType.FasterWhisper => new FasterWhisperASRService(_config),
                _ => throw new InvalidOperationException()
            };

            while (await channel.Reader.WaitToReadAsync(token))
            {
                // Use TryPeek() to reduce the channel capacity by one.
                if (!channel.Reader.TryPeek(out AudioChunk chunk))
                    throw new InvalidOperationException("can not peek AudioChunk from channel");

                try
                {
                    if (CanDebug) Log.Debug(
                            $"Reading chunk from channel (chunkNo: {chunk.ChunkNumber}, start: {chunk.Start}, end: {chunk.End})");

                    //// Output wav file for debugging
                    //await using (FileStream fs = new($"subtitlewhisper-{chunk.ChunkNumber}.wav", FileMode.Create, FileAccess.Write))
                    //{
                    //    chunk.Stream.WriteTo(fs);
                    //    chunk.Stream.Position = 0;
                    //}

                    await foreach (var data in asrService.Do(chunk.Stream, token))
                    {
                        TimeSpan start = chunk.Start.Add(data.start);
                        TimeSpan end = chunk.Start.Add(data.end);
                        if (end > chunk.End)
                        {
                            // Shorten by 20 ms to prevent the next subtitle from being covered
                            end = chunk.End.Subtract(TimeSpan.FromMilliseconds(20));
                        }

                        SubtitleASRData subData = new()
                        {
                            Text = data.text,
                            Language = data.language,
                            StartTime = start,
                            EndTime = end,
#if DEBUG
                            ChunkNo = chunk.ChunkNumber,
                            StartTimeChunk = chunk.Start,
                            EndTimeChunk = chunk.End
#endif
                        };

                        if (CanDebug) Log.Debug(string.Format("{0}->{1} ({2}->{3}): {4}",
                            start, end,
                            chunk.Start, chunk.End,
                            data.text));

                        addSub(subData);
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
            _packet = av_packet_alloc();
            _frame = av_frame_alloc();

            // Whisper from the current playback position
            // TODO: L: Fold back and allow the first half to run as well.
            if (curTime > TimeSpan.FromSeconds(30))
            {
                // copy from DecoderContext.CalcSeekTimestamp()
                long startTime = _demuxer.hlsCtx == null ? _demuxer.StartTime : _demuxer.hlsCtx->first_timestamp * 10;
                long ticks = curTime.Ticks + startTime;

                bool forward = false;

                if (_demuxer.Type == MediaType.Audio) ticks -= _config.Audio.Delay;

                if (ticks < startTime)
                {
                    ticks = startTime;
                    forward = true;
                }
                else if (ticks > startTime + _demuxer.Duration - (50 * 10000))
                {
                    ticks = Math.Max(startTime, startTime + _demuxer.Duration - (50 * 10000));
                    forward = false;
                }

                _ = _demuxer.Seek(ticks, forward);
            }

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

            int demuxErrors = 0;
            int decodeErrors = 0;

            while (!token.IsCancellationRequested)
            {
                _demuxer.Interrupter.ReadRequest();
                int ret = av_read_frame(_demuxer.fmtCtx, _packet);

                if (ret != 0)
                {
                    av_packet_unref(_packet);

                    if (_demuxer.Interrupter.Timedout)
                    {
                        if (token.IsCancellationRequested)
                            break;

                        ret.ThrowExceptionIfError("av_read_frame (timed out)");
                    }

                    if (ret == AVERROR_EOF || token.IsCancellationRequested)
                    {
                        break;
                    }

                    // demux error
                    if (CanWarn) Log.Warn($"av_read_frame: {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                    if (++demuxErrors == _config.Demuxer.MaxErrors)
                    {
                        ret.ThrowExceptionIfError("av_read_frame");
                    }
                    continue;
                }

                // Discard all but the selected audio stream.
                if (_packet->stream_index != _stream.StreamIndex)
                {
                    av_packet_unref(_packet);
                    continue;
                }

                ret = avcodec_send_packet(_decoder.CodecCtx, _packet);
                av_packet_unref(_packet);

                if (ret != 0)
                {
                    if (ret == AVERROR(EAGAIN))
                    {
                        // Receive_frame and send_packet both returned EAGAIN, which is an API violation.
                        ret.ThrowExceptionIfError("avcodec_send_packet (EAGAIN)");
                    }

                    // decoder error
                    if (CanWarn) Log.Warn($"avcodec_send_packet: {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");

                    if (++decodeErrors == _config.Decoder.MaxErrors)
                    {
                        ret.ThrowExceptionIfError("avcodec_send_packet");
                    }

                    continue;
                }

                while (ret >= 0)
                {
                    ret = avcodec_receive_frame(_decoder.CodecCtx, _frame);
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

                    waveDuration = waveDuration.Add(new TimeSpan((long)(_frame->duration * _stream.Timebase)));

                    if (chunkStart == null)
                    {
                        chunkStart = new TimeSpan((long)(framePts * _stream.Timebase) - _demuxer.StartTime);
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
                        TimeSpan chunkEnd = new TimeSpan((long)(framePts * _stream.Timebase) - _demuxer.StartTime);
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

            token.ThrowIfCancellationRequested();

            // Process remaining
            if (waveStream.Length > waveHeaderSize && framePts != AV_NOPTS_VALUE)
            {
                TimeSpan chunkEnd = new TimeSpan((long)(framePts * _stream.Timebase) - _demuxer.StartTime);

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

    private byte[] _sampledBuf;
    private int _sampledBufSize;

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
        int nOut = (int)(frame->nb_samples * ratio) + 32;

        long delay = swr_get_delay(_swrContext, targetSampleRate);
        if (delay > 0)
        {
            nOut += (int)Math.Min(delay, Math.Max(4096, nOut));
        }
        int needed = nOut * outLayout.nb_channels * sizeof(ushort);

        if (_sampledBufSize < needed)
        {
            _sampledBuf = new byte[needed];
            _sampledBufSize = needed;
        }

        int samplesPerChannel;

        fixed (byte* dst = _sampledBuf)
        {
            samplesPerChannel = swr_convert(
                 _swrContext,
                 &dst,
                 nOut,
                 frame->extended_data,
                 frame->nb_samples);
        }
        samplesPerChannel.ThrowExceptionIfError("swr_convert");

        int resampledDataSize = samplesPerChannel * outLayout.nb_channels * sizeof(ushort);

        toStream.Write(_sampledBuf, 0, resampledDataSize);
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

        _decoder?.Dispose();
        if (_demuxer != null)
        {
            _demuxer.Interrupter.ForceInterrupt = 0;
            _demuxer.Dispose();
        }

        _isDisposed = true;
    }
}

public interface IASRService : IAsyncDisposable
{
    public IAsyncEnumerable<(string text, TimeSpan start, TimeSpan end, string language)> Do(MemoryStream waveStream, CancellationToken token);
}

// https://github.com/sandrohanea/whisper.net
// https://github.com/ggerganov/whisper.cpp
public class WhisperCppASRService : IASRService
{
    private readonly Config _config;

    private readonly LogHandler Log;
    private readonly IDisposable _logger;
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;

    private readonly bool _isLanguageDetect;
    private string? _detectedLanguage;

    public WhisperCppASRService(Config config)
    {
        _config = config;
        Log = new LogHandler(("[#1]").PadRight(8, ' ') + " [WhisperCpp    ] ");

        if (_config.Subtitles.WhisperCppConfig.RuntimeLibraries.Count >= 1)
        {
            RuntimeOptions.RuntimeLibraryOrder = [.. _config.Subtitles.WhisperCppConfig.RuntimeLibraries];
        }
        else
        {
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]; // fallback to default
        }

        _logger = CanDebug
            ? LogProvider.AddLogger((level, s) => Log.Debug($"[Whisper.net] [{level.ToString()}] {s}"))
            : Disposable.Empty;

        if (CanDebug) Log.Debug($"Selecting whisper runtime libraries from ({string.Join(",", RuntimeOptions.RuntimeLibraryOrder)})");

        _factory = WhisperFactory.FromPath(_config.Subtitles.WhisperCppConfig.Model!.ModelFilePath, _config.Subtitles.WhisperCppConfig.GetFactoryOptions());

        if (CanDebug) Log.Debug($"Selected whisper runtime library '{RuntimeOptions.LoadedLibrary}'");

        WhisperProcessorBuilder whisperBuilder = _factory.CreateBuilder();
        _processor = _config.Subtitles.WhisperCppConfig.ConfigureBuilder(_config.Subtitles.WhisperConfig, whisperBuilder).Build();

        _isLanguageDetect = _config.Subtitles.WhisperConfig.LanguageDetection;
    }

    public async ValueTask DisposeAsync()
    {
        await _processor.DisposeAsync();
        _factory.Dispose();
        _logger.Dispose();
    }

    public async IAsyncEnumerable<(string text, TimeSpan start, TimeSpan end, string language)> Do(MemoryStream waveStream, [EnumeratorCancellation] CancellationToken token)
    {
        // If language detection is on, set detected language manually
        // TODO: L: Currently this is set because language information is managed for the entire subtitle,
        // but if language information is maintained for each subtitle, it should not be set.
        if (_isLanguageDetect && _detectedLanguage is not null)
        {
            _processor.ChangeLanguage(_detectedLanguage);
        }

        await foreach (var result in _processor.ProcessAsync(waveStream, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();

            if (_detectedLanguage is null && !string.IsNullOrEmpty(result.Language))
            {
                _detectedLanguage = result.Language;
            }

            string text = result.Text.Trim(); // remove leading whitespace

            yield return (text, result.Start, result.End, result.Language);
        }
    }
}

// https://github.com/Purfview/whisper-standalone-win
// Purfview's Stand-alone Faster-Whisper-XXL & Faster-Whisper
// Do not support official OpenAI Whisper version
public class FasterWhisperASRService : IASRService
{
    private readonly Config _config;

    public FasterWhisperASRService(Config config)
    {
        _config = config;

        _isLanguageDetect = _config.Subtitles.WhisperConfig.LanguageDetection;
        _manualLanguage = _config.Subtitles.WhisperConfig.Language;
        _cmdBase = BuildCommand(_config.Subtitles.FasterWhisperConfig, _config.Subtitles.WhisperConfig);

        if (!_config.Subtitles.FasterWhisperConfig.UseManualModel)
        {
            WhisperConfig.EnsureModelsDirectory();
        }
    }

    private readonly Command _cmdBase;
    private readonly bool _isLanguageDetect;
    private readonly string _manualLanguage;
    private string? _detectedLanguage;

    private static readonly Regex LanguageReg = new("^Detected language '(.+)' with probability");
    // [08:15.050 --> 08:16.450] Text
    private static readonly Regex SubShortReg = new(@"^\[\d{2}:\d{2}\.\d{3} --> \d{2}:\d{2}\.\d{3}\] ", RegexOptions.Compiled);
    // [02:08:15.050 --> 02:08:16.450] Text
    private static readonly Regex SubLongReg = new(@"^\[\d{2}:\d{2}:\d{2}\.\d{3} --> \d{2}:\d{2}:\d{2}\.\d{3}\] ", RegexOptions.Compiled);
    private static readonly Regex EndReg = new("^Operation finished in:");

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public static Command BuildCommand(FasterWhisperConfig config, WhisperConfig commonConfig)
    {
        string tempFolder = Path.GetTempPath();
        string enginePath = config.UseManualEngine ? config.ManualEnginePath! : FasterWhisperConfig.DefaultEnginePath;

        ArgumentsBuilder args = new();
        args.Add("--output_dir").Add(tempFolder);
        args.Add("--output_format").Add("srt");
        args.Add("--verbose").Add("True");
        args.Add("--beep_off");
        args.Add("--model").Add(config.Model);
        args.Add("--model_dir")
            .Add(config.UseManualModel ? config.ManualModelDir! : WhisperConfig.ModelsDirectory);

        if (commonConfig.Translate)
            args.Add("--task").Add("translate");

        if (!commonConfig.LanguageDetection)
            args.Add("--language").Add(commonConfig.Language);

        string arguments = args.Build();

        if (!string.IsNullOrWhiteSpace(config.ExtraArguments))
        {
            arguments += $" {config.ExtraArguments}";
        }

        Command cmd = Cli.Wrap(enginePath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None);

        if (config.ProcessPriority != ProcessPriorityClass.Normal)
        {
            cmd = cmd.WithResourcePolicy(builder =>
                builder.SetPriority(config.ProcessPriority));
        }

        return cmd;
    }

    private static TimeSpan ParseTime(ReadOnlySpan<char> time, bool isLong)
    {
        if (isLong)
        {
            // 01:28:02.130
            // hh:mm:ss.fff
            int hours = int.Parse(time[..2]);
            int minutes = int.Parse(time[3..5]);
            int seconds = int.Parse(time[6..8]);
            int milliseconds = int.Parse(time[9..12]);
            return new TimeSpan(0, hours, minutes, seconds, milliseconds);
        }
        else
        {
            // 28:02.130
            // mm:ss.fff
            int minutes = int.Parse(time[..2]);
            int seconds = int.Parse(time[3..5]);
            int milliseconds = int.Parse(time[6..9]);
            return new TimeSpan(0, 0, minutes, seconds, milliseconds);
        }
    }

    public async IAsyncEnumerable<(string text, TimeSpan start, TimeSpan end, string language)> Do(MemoryStream waveStream, [EnumeratorCancellation] CancellationToken token)
    {
        string tempFilePath = Path.GetTempFileName();
        // because no output option
        string outputFilePath = Path.ChangeExtension(tempFilePath, "srt");

        // write WAV to tmp folder
        await using (FileStream fileStream = new(tempFilePath, FileMode.Create, FileAccess.Write))
        {
            waveStream.WriteTo(fileStream);
        }

        CancellationTokenSource forceCts = new();
        token.Register(() =>
        {
            // force kill if not exited when sending interrupt
            forceCts.CancelAfter(5000);
        });

        try
        {
            string? lastLine = null;
            StringBuilder output = new(); // for error output
            Lock outputLock = new();
            bool oneSuccess = false;

            ArgumentsBuilder args = new();
            if (_isLanguageDetect && !string.IsNullOrEmpty(_detectedLanguage))
            {
                args.Add("--language").Add(_detectedLanguage);
            }
            args.Add(tempFilePath);
            string addedArgs = args.Build();

            Command cmd = _cmdBase.WithArguments($"{_cmdBase.Arguments} {addedArgs}");

            await foreach (var cmdEvent in cmd.ListenAsync(Encoding.Default, Encoding.Default, forceCts.Token, token))
            {
                token.ThrowIfCancellationRequested();

                if (cmdEvent is StandardErrorCommandEvent stdErr)
                {
                    lock (outputLock)
                    {
                        output.AppendLine(stdErr.Text);
                    }

                    continue;
                }

                if (cmdEvent is not StandardOutputCommandEvent stdOut)
                {
                    continue;
                }

                string line = stdOut.Text;

                // process stdout
                if (!oneSuccess)
                {
                    lock (outputLock)
                    {
                        output.AppendLine(line);
                    }

                }
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                lastLine = line;

                if (_isLanguageDetect && _detectedLanguage == null)
                {
                    var match = LanguageReg.Match(line);
                    if (match.Success)
                    {
                        string languageName = match.Groups[1].Value;
                        _detectedLanguage = WhisperLanguage.LanguageToCode[languageName];
                    }

                    continue;
                }

                bool isLong = false;

                Match subtitleMatch = SubShortReg.Match(line);
                if (!subtitleMatch.Success)
                {
                    subtitleMatch = SubLongReg.Match(line);
                    if (!subtitleMatch.Success)
                    {
                        continue;
                    }

                    isLong = true;
                }

                ReadOnlySpan<char> lineSpan = line.AsSpan();

                Range startRange = 1..10;
                Range endRange = 15..24;
                Range textRange = 26..;

                if (isLong)
                {
                    startRange = 1..13;
                    endRange = 18..30;
                    textRange = 32..;
                }

                TimeSpan start = ParseTime(lineSpan[startRange], isLong);
                TimeSpan end = ParseTime(lineSpan[endRange], isLong);
                // because some languages have leading spaces
                string text = lineSpan[textRange].Trim().ToString();

                yield return (text, start, end, _isLanguageDetect ? _detectedLanguage! : _manualLanguage);

                if (!oneSuccess)
                {
                    oneSuccess = true;
                }
            }

            // validate if success
            if (lastLine == null || !EndReg.Match(lastLine).Success)
            {
                throw new InvalidOperationException("Failed to execute faster-whisper")
                {
                    Data =
                    {
                        ["whisper_command"] = cmd.CommandToText(),
                        ["whisper_output"] = output.ToString()
                    }
                };
            }
        }
        finally
        {
            // delete tmp wave
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
            // delete output srt
            if (File.Exists(outputFilePath))
            {
                File.Delete(outputFilePath);
            }
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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaRenderer;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaPlayer.Translation;
using static FlyleafLib.Logger;

namespace FlyleafLib.MediaPlayer;

#nullable enable

public class SubtitlesManager
{
    private readonly SubManager[] _subManagers;
    public SubManager this[int subIndex] => _subManagers[subIndex];
    private readonly int _subNum;

    public SubtitlesManager(Config config, int subNum)
    {
        _subNum = subNum;
        _subManagers = new SubManager[subNum];
        for (int i = 0; i < subNum; i++)
        {
            _subManagers[i] = new SubManager(config, i);
        }
    }

    /// <summary>
    /// Open a file and read all subtitle data by streaming
    /// </summary>
    /// <param name="subIndex">0: Primary, 1: Secondary</param>
    /// <param name="url">subtitle file path or video file path</param>
    /// <param name="streamIndex">streamIndex of subtitle</param>
    /// <param name="type">demuxer media type</param>
    /// <param name="useBitmap">Use bitmap subtitles or immediately release bitmap if not used</param>
    /// <param name="lang">subtitle language</param>
    public void Open(int subIndex, string url, int streamIndex, MediaType type, bool useBitmap, Language lang)
    {
        // TODO: L: Add caching subtitle data for the same stream and URL?
        this[subIndex].Open(url, streamIndex, type, useBitmap, lang);
    }

    public void SetCurrentTime(TimeSpan currentTime)
    {
        for (int i = 0; i < _subNum; i++)
        {
            this[i].SetCurrentTime(currentTime);
        }
    }
}

public class SubManager : INotifyPropertyChanged
{
    private readonly Lock _locker = new();
    private CancellationTokenSource? _cts;
    public SubtitleData? SelectedSub { get; set => Set(ref field, value); } = null;
    public int CurrentIndex { get; private set => Set(ref field, value); } = -1;

    public PositionState State
    {
        get;
        private set
        {
            bool prevIsDisplaying = IsDisplaying;
            if (Set(ref field, value) && prevIsDisplaying != IsDisplaying)
            {
                OnPropertyChanged(nameof(IsDisplaying));
            }
        }
    } = PositionState.First;

    public bool IsDisplaying => State == PositionState.Showing;

    /// <summary>
    /// List of subtitles that can be bound to ItemsControl
    /// Must be sorted with timestamp to perform binary search.
    /// </summary>
    public BulkObservableCollection<SubtitleData> Subs { get; } = new();

    /// <summary>
    /// True when addition to Subs is running... (Reading all subtitles, OCR, ASR)
    /// </summary>
    public bool IsLoading { get; private set => Set(ref field, value); } = false;

    // LanguageSource with fallback
    public Language? Language
    {
        get
        {
            if (LanguageSource == Language.Unknown)
            {
                // fallback to user set language
                return _subIndex == 0 ? _config.Subtitles.LanguageFallbackPrimary : _config.Subtitles.LanguageFallbackSecondary;
            }

            return LanguageSource;
        }
    }

    public Language? LanguageSource
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(Language));
            }
        }
    }

    private readonly object _subsLocker = new();
    private readonly Config _config;
    private readonly int _subIndex;
    private readonly SubTranslator _subTranslator;
    private readonly LogHandler Log;

    public SubManager(Config config, int subIndex, bool enableSync = true)
    {
        _config = config;
        _subIndex = subIndex;
        // TODO: L: Review whether to initialize it here.
        _subTranslator = new SubTranslator(this, config.Subtitles, subIndex);
        Log = new LogHandler(("[#1]").PadRight(8, ' ') + $" [SubManager{subIndex + 1}   ] ");

        if (enableSync)
        {
            // Enable binding to ItemsControl
            Utils.UIInvokeIfRequired(() =>
            {
                BindingOperations.EnableCollectionSynchronization(Subs, _subsLocker);
            });
        }
    }

    public enum PositionState
    {
        First,   // still haven't reached the first subtitle
        Showing, // currently displaying
        Around,  // not displayed and can seek before and after
        Last     // After the last subtitle
    }

    /// <summary>
    /// Force UI refresh
    /// </summary>
    internal void Refresh()
    {
        // NOTE: If it is not executed in the main thread, the following error occurs.
        // System.NotSupportedException: 'This type of CollectionView does not support'
        Utils.UI(() =>
        {
            CollectionViewSource.GetDefaultView(Subs).Refresh();
            OnPropertyChanged(nameof(CurrentIndex)); // required for translating current sub
        });
    }

    /// <summary>
    /// This must be called when doing heavy operation
    /// </summary>
    /// <returns></returns>
    internal IDisposable StartLoading()
    {
        IsLoading = true;

        return Disposable.Create(() =>
        {
            IsLoading = false;
        });
    }

    public void Load(IEnumerable<SubtitleData> items)
    {
        lock (_subsLocker)
        {
            CurrentIndex = -1;
            SelectedSub = null;
            Subs.Clear();
            Subs.AddRange(items);
        }
    }

    public void Add(SubtitleData sub)
    {
        lock (_subsLocker)
        {
            sub.Index = Subs.Count;
            Subs.Add(sub);
        }
    }

    public void AddRange(IEnumerable<SubtitleData> items)
    {
        lock (_subsLocker)
        {
            Subs.AddRange(items);
        }
    }

    public SubtitleData? GetCurrent()
    {
        lock (_subsLocker)
        {
            if (Subs.Count == 0 || CurrentIndex == -1)
            {
                return null;
            }

            Debug.Assert(CurrentIndex < Subs.Count);

            if (State == PositionState.Showing)
            {
                return Subs[CurrentIndex];
            }

            return null;
        }
    }

    public SubtitleData? GetNext()
    {
        lock (_subsLocker)
        {
            if (Subs.Count == 0)
            {
                return null;
            }

            switch (State)
            {
                case PositionState.First:
                    return Subs[0];

                case PositionState.Showing:
                    if (CurrentIndex < Subs.Count - 1)
                        return Subs[CurrentIndex + 1];
                    break;

                case PositionState.Around:
                    if (CurrentIndex < Subs.Count - 1)
                        return Subs[CurrentIndex + 1];
                    break;
            }

            return null;
        }
    }

    public SubtitleData? GetPrev()
    {
        lock (_subsLocker)
        {
            if (Subs.Count == 0 || CurrentIndex == -1)
                return null;

            switch (State)
            {
                case PositionState.Showing:
                    if (CurrentIndex > 0)
                        return Subs[CurrentIndex - 1];
                    break;

                case PositionState.Around:
                    if (CurrentIndex >= 0)
                        return Subs[CurrentIndex];
                    break;

                case PositionState.Last:
                    return Subs[^1];
            }
        }

        return null;
    }

    private readonly SubtitleData _searchSub = new();

    public SubManager SetCurrentTime(TimeSpan currentTime)
    {
        // Adjust the display timing of subtitles by adjusting the timestamp of the video
        currentTime = currentTime.Subtract(new TimeSpan(_config.Subtitles[_subIndex].Delay));

        lock (_subsLocker)
        {
            // If no subtitle data is loaded, nothing is done.
            if (Subs.Count == 0)
                return this;

            // If it is a subtitle that is displaying, it does nothing.
            var curSub = GetCurrent();
            if (curSub != null && curSub.StartTime < currentTime && curSub.EndTime > currentTime)
            {
                return this;
            }

            _searchSub.StartTime = currentTime;

            int ret = Subs.BinarySearch(_searchSub, SubtitleTimeStartComparer.Instance);
            int cur = -1;

            if (~ret == 0)
            {
                CurrentIndex = -1;
                SelectedSub = null;
                State = PositionState.First;
                return this;
            }

            if (ret < 0)
            {
                // The reason subtracting 1 is that the result of the binary search is the next big index.
                cur = (~ret) - 1;
            }
            else
            {
                // If the starting position is matched, it is unlikely
                cur = ret;
            }

            Debug.Assert(cur >= 0, "negative index detected");
            Debug.Assert(cur < Subs.Count, "out of bounds detected");

            if (cur == Subs.Count - 1)
            {
                if (Subs[cur].EndTime < currentTime)
                {
                    CurrentIndex = cur;
                    SelectedSub = Subs[cur];
                    State = PositionState.Last;
                }
                else
                {
                    CurrentIndex = cur;
                    SelectedSub = Subs[cur];
                    State = PositionState.Showing;
                }
            }
            else
            {
                if (Subs[cur].StartTime <= currentTime && Subs[cur].EndTime >= currentTime)
                {
                    // Show subtitles
                    CurrentIndex = cur;
                    SelectedSub = Subs[cur];
                    State = PositionState.Showing;
                }
                else if (Subs[cur].StartTime <= currentTime)
                {
                    // Almost there to display in currentIndex.
                    CurrentIndex = cur;
                    SelectedSub = Subs[cur];
                    State = PositionState.Around;
                }
            }
        }

        return this;
    }

    public void Sort()
    {
        lock (_subsLocker)
        {
            if (Subs.Count == 0)
                return;

            Subs.Sort(SubtitleTimeStartComparer.Instance);
        }
    }

    public void DeleteAfter(TimeSpan time)
    {
        lock (_subsLocker)
        {
            if (Subs.Count == 0)
                return;

            int index = Subs.BinarySearch(new SubtitleData { EndTime = time }, new SubtitleTimeEndComparer());

            if (index < 0)
            {
                index = ~index;
            }

            if (index < Subs.Count)
            {
                var newSubs = Subs.GetRange(0, index).ToList();
                var deleteSubs = Subs.GetRange(index, Subs.Count - index).ToList();
                Load(newSubs);

                foreach (var sub in deleteSubs)
                {
                    sub.Dispose();
                }
            }
        }
    }

    public void Open(string url, int streamIndex, MediaType type, bool useBitmap, Language lang)
    {
        // Asynchronously read subtitle timestamps and text

        // Cancel if already executed
        TryCancelWait();

        lock (_locker)
        {
            using var loading = StartLoading();

            List<SubtitleData> subChunk = new();

            try
            {
                _cts = new CancellationTokenSource();
                using SubtitleReader reader = new(_config, _subIndex);
                reader.Open(url, streamIndex, type, _cts.Token);

                _cts.Token.ThrowIfCancellationRequested();

                bool isFirst = true;
                int subCnt = 0;

                Stopwatch refreshSw = new();
                refreshSw.Start();

                reader.ReadAll(useBitmap, data =>
                {
                    if (isFirst)
                    {
                        isFirst = false;
                        // Set the language at the timing of the first subtitle data set.
                        LanguageSource = lang;

                        Log.Info($"Start loading subs... (lang:{lang.TopEnglishName})");
                    }

                    data.Index = subCnt++;
                    subChunk.Add(data);

                    // Large files and network files take time to load to the end.
                    // To prevent frequent UI updates, use AddRange to group files to some extent before adding them.
                    if (subChunk.Count >= 2 && refreshSw.Elapsed > TimeSpan.FromMilliseconds(500))
                    {
                        AddRange(subChunk);
                        subChunk.Clear();
                        refreshSw.Restart();
                    }
                }, _cts.Token);

                // Process remaining
                if (subChunk.Count > 0)
                {
                    AddRange(subChunk);
                }
                refreshSw.Stop();
                Log.Info("End loading subs");
            }
            catch (OperationCanceledException)
            {
                foreach (var sub in subChunk)
                {
                    sub.Dispose();
                }

                Clear();
            }
        }
    }

    public void TryCancelWait()
    {
        if (_cts != null)
        {
            // If it has already been executed, cancel it and wait until the preceding process is finished.
            // (It waits because it has a lock)
            _cts.Cancel();
            lock (_locker)
            {
                // dispose after it is no longer used.
                _cts.Dispose();
                _cts = null;
            }
        }
    }

    public void Clear()
    {
        lock (_subsLocker)
        {
            CurrentIndex = -1;
            SelectedSub = null;
            foreach (var sub in Subs)
            {
                sub.Dispose();
            }
            Subs.Clear();
            State = PositionState.First;
            LanguageSource = null;
            IsLoading = false;
        }
    }

    public void Reset()
    {
        TryCancelWait();
        Clear();
    }

    #region INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    #endregion
}

public unsafe class SubtitleReader : IDisposable
{
    private readonly Config _config;
    private readonly LogHandler Log;
    private readonly int _subIndex;

    private Demuxer? _demuxer;
    private SubtitlesDecoder? _decoder;
    private SubtitlesStream? _stream;

    private AVPacket* _packet = null;

    public SubtitleReader(Config config, int subIndex)
    {
        _config = config;
        Log = new LogHandler(("[#1]").PadRight(8, ' ') + $" [SubReader{subIndex + 1}    ] ");

        _subIndex = subIndex;
    }

    public void Open(string url, int streamIndex, MediaType type, CancellationToken token)
    {
        _demuxer = new Demuxer(_config.Demuxer, type, _subIndex + 1, false);

        token.Register(() =>
        {
            if (_demuxer != null)
                _demuxer.Interrupter.ForceInterrupt = 1;
        });

        _demuxer.Log.Prefix = _demuxer.Log.Prefix.Replace("Demuxer: ", "DemuxerS:");
        string? error = _demuxer.Open(url);

        if (error != null)
        {
            token.ThrowIfCancellationRequested(); // if canceled

            throw new InvalidOperationException($"demuxer open error: {error}");
        }

        _stream = (SubtitlesStream)_demuxer.AVStreamToStream[streamIndex];

        if (type == MediaType.Subs)
        {

            _stream.ExternalStream = new ExternalSubtitlesStream()
            {
                Url = url,
                IsBitmap = _stream.IsBitmap
            };

            _stream.ExternalStreamAdded();
        }

        _decoder = new SubtitlesDecoder(_config, _subIndex + 1);
        _decoder.Log.Prefix = _decoder.Log.Prefix.Replace("Decoder: ", "DecoderS:");
        error = _decoder.Open(_stream);

        if (error != null)
        {
            token.ThrowIfCancellationRequested(); // if canceled

            throw new InvalidOperationException($"decoder open error: {error}");
        }
    }

    /// <summary>
    /// Read subtitle stream to the end and get all subtitle data
    /// </summary>
    /// <param name="useBitmap"></param>
    /// <param name="addSub"></param>
    /// <param name="token"></param>
    /// <exception cref="OperationCanceledException">The token has had cancellation requested.</exception>
    public void ReadAll(bool useBitmap, Action<SubtitleData> addSub, CancellationToken token)
    {
        if (_demuxer == null || _decoder == null || _stream == null)
            throw new InvalidOperationException("Open() is not called");

        SubtitleData? prevSub = null;

        _packet = av_packet_alloc();

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

            // Discard all but the subtitle stream.
            if (_packet->stream_index != _stream.StreamIndex)
            {
                av_packet_unref(_packet);
                continue;
            }

            SubtitleData subData = new();
            int gotSub = 0;
            AVSubtitle sub = default;

            ret = avcodec_decode_subtitle2(_decoder.CodecCtx, &sub, &gotSub, _packet);
            if (ret < 0)
            {
                // decode error
                av_packet_unref(_packet);
                if (CanWarn) Log.Warn($"avcodec_decode_subtitle2: {FFmpegEngine.ErrorCodeToMsg(ret)} ({ret})");
                if (++decodeErrors == _config.Decoder.MaxErrors)
                {
                    ret.ThrowExceptionIfError("avcodec_decode_subtitle2");
                }

                continue;
            }

            if (gotSub == 0)
            {
                av_packet_unref(_packet);
                continue;
            }

            long pts = AV_NOPTS_VALUE; // 0.1us
            if (sub.pts != AV_NOPTS_VALUE)
            {
                pts = sub.pts /*us*/ * 10;
            }
            else if (_packet->pts != AV_NOPTS_VALUE)
            {
                pts = (long)(_packet->pts * _stream.Timebase);
            }

            av_packet_unref(_packet);

            if (pts == AV_NOPTS_VALUE)
            {
                continue;
            }

            // Bitmap PGS has a special format.
            if (_stream.IsBitmap && prevSub != null
                /*&& _stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HDMV_PGS_SUBTITLE*/)
            {
                if (sub.num_rects < 1)
                {
                    // Support for special format bitmap subtitles.
                    // In the case of bitmap subtitles, num_rects = 0 and 1 may alternate.
                    // In this case sub->start_display_time and sub->end_display_time are always fixed at 0 and
                    // AVPacket->duration is also always 0.
                    // This indicates the end of the previous subtitle, and the time in pts is the end time of the previous subtitle.

                    // Note that not all bitmap subtitles have this behavior.

                    // Assign pts as the end time of the previous subtitle
                    prevSub.EndTime = new TimeSpan(pts - _demuxer.StartTime);
                    addSub(prevSub);
                    prevSub = null;

                    avsubtitle_free(&sub);
                    continue;
                }

                // There are cases where num_rects = 1 is consecutive.
                // In this case, the previous subtitle end time is corrected by pts, and a new subtitle is started with the same pts.
                if (prevSub.Bitmap?.Sub.end_display_time == uint.MaxValue) // 4294967295
                {
                    prevSub.EndTime = new TimeSpan(pts - _demuxer.StartTime);
                    addSub(prevSub);
                    prevSub = null;
                }
            }

            subData.StartTime = new TimeSpan(pts - _demuxer.StartTime);
            subData.EndTime = subData.StartTime.Add(TimeSpan.FromMilliseconds(sub.end_display_time));

            switch (sub.rects[0]->type)
            {
                case AVSubtitleType.Text:
                    subData.Text = Utils.BytePtrToStringUTF8(sub.rects[0]->text).Trim();
                    avsubtitle_free(&sub);

                    if (string.IsNullOrEmpty(subData.Text))
                    {
                        continue;
                    }

                    break;
                case AVSubtitleType.Ass:
                    string text = Utils.BytePtrToStringUTF8(sub.rects[0]->ass).Trim();
                    avsubtitle_free(&sub);

                    subData.Text = ParseSubtitles.SSAtoSubStyles(text, out var subStyles);
                    subData.SubStyles = subStyles;

                    if (string.IsNullOrEmpty(subData.Text))
                    {
                        continue;
                    }

                    break;

                case AVSubtitleType.Bitmap:
                    subData.IsBitmap = true;

                    if (useBitmap)
                    {
                        // Save subtitle data for (OCR or subtitle cache)
                        subData.Bitmap = new SubtitleBitmapData(sub);
                    }
                    else
                    {
                        // Only subtitle timestamp information is used, so bitmap is released
                        avsubtitle_free(&sub);
                    }

                    break;
            }

            if (prevSub != null)
            {
                addSub(prevSub);
            }

            prevSub = subData;
        }

        if (token.IsCancellationRequested)
        {
            prevSub?.Dispose();
            token.ThrowIfCancellationRequested();
        }

        // Process last
        if (prevSub != null)
        {
            addSub(prevSub);
        }
    }

    private bool _isDisposed;
    public void Dispose()
    {
        if (_isDisposed)
            return;

        // av_packet_alloc
        if (_packet != null)
        {
            fixed (AVPacket** ptr = &_packet)
            {
                av_packet_free(ptr);
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

public class SubtitleBitmapData : IDisposable
{
    public SubtitleBitmapData(AVSubtitle sub)
    {
        Sub = sub;
    }

    private readonly ReaderWriterLockSlim _rwLock = new();
    private bool _isDisposed;

    public AVSubtitle Sub;

    public WriteableBitmap SubToWritableBitmap(bool isGrey)
    {
        (byte[] data, AVSubtitleRect rect) = SubToBitmap(isGrey);

        WriteableBitmap wb = new(
            rect.w, rect.h,
            Utils.NativeMethods.DpiXSource, Utils.NativeMethods.DpiYSource,
            PixelFormats.Bgra32, null
        );
        Int32Rect dirtyRect = new(0, 0, rect.w, rect.h);
        wb.Lock();

        Marshal.Copy(data, 0, wb.BackBuffer, data.Length);

        wb.AddDirtyRect(dirtyRect);
        wb.Unlock();
        wb.Freeze();

        return wb;
    }

    public unsafe (byte[] data, AVSubtitleRect rect) SubToBitmap(bool isGrey)
    {
        if (_isDisposed)
            throw new InvalidOperationException("already disposed");

        try
        {
            // Prevent from disposing
            _rwLock.EnterReadLock();

            AVSubtitleRect rect = *Sub.rects[0];
            byte[] data = Renderer.ConvertBitmapSub(Sub, isGrey);

            return (data, rect);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _rwLock.EnterWriteLock();

        if (Sub.num_rects > 0)
        {
            unsafe
            {
                fixed (AVSubtitle* subPtr = &Sub)
                {
                    avsubtitle_free(subPtr);
                }
            }
        }

        _isDisposed = true;
        _rwLock.ExitWriteLock();

#if DEBUG
        GC.SuppressFinalize(this);
#endif
    }

#if DEBUG
    ~SubtitleBitmapData()
    {
        System.Diagnostics.Debug.Fail("Dispose is not called");
    }
#endif
}

public class SubtitleData : IDisposable, INotifyPropertyChanged
{
    public int Index { get; set; }

    public string? Text
    {
        get;
        set
        {
            var prevIsText = IsText;
            if (Set(ref field, value))
            {
                if (prevIsText != IsText)
                    OnPropertyChanged(nameof(IsText));
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string? TranslatedText
    {
        get;
        set
        {
            var prevUseTranslated = UseTranslated;
            if (Set(ref field, value))
            {
                if (prevUseTranslated != UseTranslated)
                {
                    OnPropertyChanged(nameof(UseTranslated));
                }
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public bool IsText => !string.IsNullOrEmpty(Text);

    public bool IsTranslated => TranslatedText != null;
    public bool UseTranslated => EnabledTranslated && IsTranslated;

    public bool EnabledTranslated = true;

    public string? DisplayText => UseTranslated ? TranslatedText : Text;

    public List<SubStyle>? SubStyles;
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
#if DEBUG
    public int ChunkNo { get; set => Set(ref field, value); }
    public TimeSpan StartTimeChunk { get; set => Set(ref field, value); }
    public TimeSpan EndTimeChunk { get; set => Set(ref field, value); }
#endif
    public TimeSpan Duration => EndTime - StartTime;

    public SubtitleBitmapData? Bitmap { get; set; }

    public bool IsBitmap { get; set; }

    private bool _isDisposed;

    public void Dispose()
    {
        if (_isDisposed)
            return;

        if (IsBitmap && Bitmap != null)
        {
            Bitmap.Dispose();
            Bitmap = null;
        }

        _isDisposed = true;
    }

    public SubtitleData Clone()
    {
        return new SubtitleData()
        {
            Index = Index,
            Text = Text,
            TranslatedText = TranslatedText,
            EnabledTranslated = EnabledTranslated,
            StartTime = StartTime,
            EndTime = EndTime,
#if DEBUG
            ChunkNo = ChunkNo,
            StartTimeChunk = StartTimeChunk,
            EndTimeChunk = EndTimeChunk,
#endif
            IsBitmap = IsBitmap,
            Bitmap = null,
        };
    }

    #region INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    #endregion
}

public class SubtitleTimeStartComparer : Comparer<SubtitleData>
{
    public static SubtitleTimeStartComparer Instance { get; } = new();
    private SubtitleTimeStartComparer() { }
    static SubtitleTimeStartComparer() { }

    public override int Compare(SubtitleData? x, SubtitleData? y)
    {
        if (object.Equals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        return x.StartTime.CompareTo(y.StartTime);
    }
}

public class SubtitleTimeEndComparer : Comparer<SubtitleData>
{
    public override int Compare(SubtitleData? x, SubtitleData? y)
    {
        if (object.Equals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        return x.EndTime.CompareTo(y.EndTime);
    }
}

internal static class WrapperHelper
{
    public static int ThrowExceptionIfError(this int error, string message)
    {
        if (error < 0)
        {
            string errStr = AvErrorStr(error);
            throw new InvalidOperationException($"{message}: {errStr} ({error})");
        }

        return error;
    }

    public static unsafe string AvErrorStr(this int error)
    {
        int bufSize = 1024;
        byte* buf = stackalloc byte[bufSize];

        if (av_strerror(error, buf, (nuint)bufSize) == 0)
        {
            string errStr = Marshal.PtrToStringAnsi((IntPtr)buf)!;
            return errStr;
        }

        return "unknown error";
    }
}

public static class ObservableCollectionExtensions
{
    public static int FindIndex<T>(this ObservableCollection<T> collection, Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(match);

        for (int i = 0; i < collection.Count; i++)
        {
            if (match(collection[i]))
                return i;
        }
        return -1;
    }

    public static int BinarySearch<T>(this ObservableCollection<T> collection, T item, IComparer<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(collection);

        //comparer ??= Comparer<T>.Default;
        int low = 0;
        int high = collection.Count - 1;

        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            int comparison = comparer.Compare(collection[mid], item);

            if (comparison == 0)
                return mid;
            if (comparison < 0)
                low = mid + 1;
            else
                high = mid - 1;
        }

        return ~low;
    }

    public static IEnumerable<T> GetRange<T>(this ObservableCollection<T> collection, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (index < 0 || count < 0 || (index + count) > collection.Count)
            throw new ArgumentOutOfRangeException();

        return collection.Skip(index).Take(count);
    }

    public static void Sort<T>(this ObservableCollection<T> collection, IComparer<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(comparer);

        List<T> sortedList = collection.ToList();
        sortedList.Sort(comparer);

        collection.Clear();
        foreach (var item in sortedList)
        {
            collection.Add(item);
        }
    }
}

public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnCollectionChanged(e);
    }

    public void AddRange(IEnumerable<T> list)
    {
        if (list == null)
            ArgumentNullException.ThrowIfNull(list);

        _suppressNotification = true;

        foreach (T item in list)
        {
            Add(item);
        }
        _suppressNotification = false;

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

using System.Collections.ObjectModel;
using FlyleafLib.MediaFramework.MediaContext;
using FlyleafLib.MediaFramework.MediaStream;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using static FlyleafLib.Utils;

namespace FlyleafLib.MediaPlayer;

public class SubsBitmap
{
    public WriteableBitmap Source { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public class SubsBitmapPosition : NotifyPropertyChanged
{
    private readonly Player _player;
    private readonly int _subIndex;

    public SubsBitmapPosition(Player player, int subIndex)
    {
        _player = player;
        _subIndex = subIndex;

        Calculate();
    }

    #region Config

    public decimal ConfScale
    {
        get;
        set
        {
            if (value <= 0)
            {
                return;
            }

            if (Set(ref field, value))
            {
                Calculate();
            }
        }
    } = 1.0M; // x 1.0

    public decimal ConfPos
    {
        get;
        set
        {
            if (value < 0M || value > 150.0M)
            {
                return;
            }

            if (Set(ref field, value))
            {
                Calculate();
            }
        }
    } = 100.0M; // 0 - 150.0

    #endregion

    #region WPF Property

    public Thickness? Margin { get; private set => Set(ref field, value); }

    public double ScaleX { get; private set => Set(ref field, value); } = 1.0;

    public double ScaleY { get; private set => Set(ref field, value); } = 1.0;

    /// <summary>
    /// True for horizontal subtitles
    /// Bitmap subtitles may be displayed vertically
    /// </summary>
    public bool IsHorizontal { get; private set; }

    #endregion

    public void Calculate()
    {
        if (_player.Subtitles == null ||
            _player.Subtitles[_subIndex].Data.Bitmap == null ||
            _player.Subtitles[_subIndex].Data.BitmapPosition == null ||
            _player.SubtitlesDecoders[_subIndex].SubtitlesStream == null)
        {
            return;
        }

        SubsBitmap bitmap = _player.Subtitles[_subIndex].Data.Bitmap;

        // Calculate the ratio of the current width of the window to the width of the video
        double renderWidth = _player.VideoDecoder.Renderer.GetViewport.Width;
        // double windowWidth = ActualWidth;
        double videoWidth = _player.SubtitlesDecoders[_subIndex].Width;

        // double videoHeight_ = (int)(videoWidth / Player.VideoDemuxer.VideoStream.AspectRatio.Value);
        double renderHeight = _player.VideoDecoder.Renderer.GetViewport.Height;
        // double windowHeight = ActualHeight;
        double videoHeight = _player.SubtitlesDecoders[_subIndex].Height;

        // In aspect ratio like a movie, a black background may be added to the top and bottom.
        // In this case, the subtitles should be placed based on the video display area, so the offset from the image rendering area excluding the black background should be taken into consideration.
        double yOffset = _player.renderer.GetViewport.Y;
        double xOffset = _player.renderer.GetViewport.X;

        double scaleFactorX = renderWidth / videoWidth;
        // double scaleFactorY = renderHeight / videoHeight;

        // Adjust subtitle size by the calculated ratio
        double scaleX = scaleFactorX;
        // double scaleY = scaleFactorY;

        // Note that if you are cropping videos with mkv in Handbrake, the subtitles will be crushed vertically if you don't use this one. vlc will crush them, but mpv will not have a problem.
        // It may be nice to be able to choose between the two.
        double scaleY = scaleX;

        double x = bitmap.X * scaleFactorX + xOffset;

        double yPosition = bitmap.Y / videoHeight;
        double y = renderHeight * yPosition + yOffset;

        // Adjust subtitle position(y - axis) by config.
        // However, if it detects that the subtitle is not a normal subtitle such as vertical subtitle, it will not be adjusted.
        // (Adjust only when it is below the center of the screen)
        // mpv: https://github.com/mpv-player/mpv/blob/df166c1333694cbfe70980dbded1984d48b0685a/sub/sd_lavc.c#L491-L494
        IsHorizontal = (bitmap.Y >= videoHeight / 2);
        if (IsHorizontal)
        {
            // mpv: https://github.com/mpv-player/mpv/blob/df166c1333694cbfe70980dbded1984d48b0685a/sub/sd_lavc.c#L486
            double offset = (double)((100.0M - ConfPos) / 100.0M * (decimal)videoHeight);
            y -= offset * scaleY;
        }

        // Adjust x and y axes when changing subtitle size(centering)
        if (ConfScale != 1.0M)
        {
            // mpv: https://github.com/mpv-player/mpv/blob/df166c1333694cbfe70980dbded1984d48b0685a/sub/sd_lavc.c#L508-L515
            double ratio = (double)((ConfScale - 1.0M) / 2);

            double dw = bitmap.Width * scaleX;
            double dy = bitmap.Height * scaleY;

            x -= dw * ratio;
            y -= dy * ratio;

            scaleX *= (double)ConfScale;
            scaleY *= (double)ConfScale;
        }

        Margin = new Thickness(x, y, 0, 0);
        ScaleX = scaleX;
        ScaleY = scaleY;
    }
}

public class SubsData : NotifyPropertyChanged
{
#nullable enable
    private readonly Player _player;
    private readonly int _subIndex;
    private Subtitles Subs => _player.Subtitles;
    private Config Config => _player.Config;

    public SubsData(Player player, int subIndex)
    {
        _player = player;
        _subIndex = subIndex;

        BitmapPosition = new SubsBitmapPosition(_player, _subIndex);

        Config.Subtitles[_subIndex].PropertyChanged += SubConfigOnPropertyChanged;
    }

    private void SubConfigOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Config.SubConfig.Visible))
        {
            Raise(nameof(IsVisible));
            Raise(nameof(IsAbsoluteVisible));
            Raise(nameof(IsDisplaying));
        }
    }

    /// <summary>
    /// Subtitles Position (Position & Scale)
    /// </summary>
    public SubsBitmapPosition? BitmapPosition { get; set => Set(ref field, value); }

    /// <summary>
    /// Subtitles Bitmap
    /// </summary>
    public SubsBitmap? Bitmap
    {
        get;
        internal set
        {
            if (!Set(ref field, value))
            {
                return;
            }

            BitmapPosition?.Calculate();

            if (Bitmap != null)
            {
                // TODO: L: When vertical and horizontal subtitles are displayed at the same time, the subtitles are displayed incorrectly
                // Absolute display of Primary sub
                // 1. If Primary is true and Secondary is false
                // 2. When a vertical subtitle is detected

                // Absolute display of Secondary sub
                // 1. When a vertical subtitle is detected
                bool isAbsolute = Subs[0].Enabled && !Subs[1].Enabled;
                if (BitmapPosition != null && !BitmapPosition.IsHorizontal)
                {
                    isAbsolute = true;
                }

                IsAbsolute = isAbsolute;
            }

            Raise(nameof(IsDisplaying));
        }
    }

    /// <summary>
    /// Whether subtitles are currently displayed or not
    /// (unless bitmap subtitles are displayed absolutely)
    /// </summary>
    public bool IsDisplaying => IsVisible && (Bitmap != null && !IsAbsolute // Bitmap subtitles, not absolute display
                                              ||
                                              !string.IsNullOrEmpty(Text)); // When text subtitles are available

    /// <summary>
    /// When vertical subtitle is detected in the case of bitmap sub, it becomes True and is displayed as absolute.
    /// </summary>
    public bool IsAbsolute
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(IsAbsoluteVisible));
                Raise(nameof(IsDisplaying));
            }
        }
    } = false;

    /// <summary>
    /// Bind target Used for switching Visibility
    /// </summary>
    public bool IsAbsoluteVisible => IsAbsolute && IsVisible;

    /// <summary>
    /// Bind target Used for switching Visibility
    /// </summary>
    public bool IsVisible => Config.Subtitles[_subIndex].Visible;

    /// <summary>
    /// Subtitles Text (updates dynamically while playing based on the duration that it should be displayed)
    /// </summary>
    public string Text
    {
        get;
        internal set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(IsDisplaying));
            }
        }
    } = "";

    public void Reset()
    {
        Clear();
        UIIfRequired(() =>
        {
            // Clear does not reset because there is a config in SubsBitmapPosition
            BitmapPosition = new SubsBitmapPosition(_player, _subIndex);
        });
    }

    public void Clear()
    {
        if (Text != "" || Bitmap != null)
        {
            UIIfRequired(() =>
            {
                Text = "";
                Bitmap = null;
            });
        }
    }
#nullable restore
}

/// <summary>
/// Preserve the state of streamIndex, URL, etc. of the currently selected subtitle in a global variable.
/// TODO: L: Cannot use multiple Players in one app, required to change this?
/// </summary>
public static class SubtitlesSelectedHelper
{
#nullable enable
    public static ValueTuple<int?, ExternalSubtitlesStream?>? Primary { get; set; }
    public static ValueTuple<int?, ExternalSubtitlesStream?>? Secondary { get; set; }

    public static SelectSubMethod PrimaryMethod { get; set; } = SelectSubMethod.Original;
    public static SelectSubMethod SecondaryMethod { get; set; } = SelectSubMethod.Original;

    /// <summary>
    /// Holds the index to switch (0: Primary, 1: Secondary)
    /// </summary>
    public static int CurIndex { get; set; } = 0;

    public static void Reset(int subIndex)
    {
        Debug.Assert(subIndex is 0 or 1);

        if (subIndex == 1)
        {
            Secondary = null;
            SecondaryMethod = SelectSubMethod.Original;
        }
        else
        {
            Primary = null;
            PrimaryMethod = SelectSubMethod.Original;
        }
    }

    public static SelectSubMethod GetMethod(int subIndex)
    {
        Debug.Assert(subIndex is 0 or 1);

        return subIndex == 1 ? SecondaryMethod : PrimaryMethod;
    }

    public static void SetMethod(int subIndex, SelectSubMethod method)
    {
        Debug.Assert(subIndex is 0 or 1);

        if (subIndex == 1)
        {
            SecondaryMethod = method;
        }
        else
        {
            PrimaryMethod = method;
        }
    }

    public static ValueTuple<int?, ExternalSubtitlesStream?>? Get(int subIndex)
    {
        Debug.Assert(subIndex is 0 or 1);
        return subIndex == 1 ? Secondary : Primary;
    }

    public static void Set(int subIndex, ValueTuple<int?, ExternalSubtitlesStream?>? tuple)
    {
        Debug.Assert(subIndex is 0 or 1);
        if (subIndex == 1)
        {
            Secondary = tuple;
        }
        else
        {
            Primary = tuple;
        }
    }

    public static bool GetSubEnabled(this StreamBase stream, int subIndex)
    {
        Debug.Assert(subIndex is 0 or 1);
        var tuple = subIndex == 1 ? Secondary : Primary;

        if (!tuple.HasValue)
        {
            return false;
        }

        if (tuple.Value.Item1.HasValue)
        {
            // internal sub
            return tuple.Value.Item1 == stream.StreamIndex;
        }

        if (tuple.Value.Item2 is not null)
        {
            // external sub
            return GetSubEnabled(stream.ExternalStream, subIndex);
        }

        return false;
    }

    public static bool GetSubEnabled(this ExternalStream stream, int subIndex)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return GetSubEnabled(stream.Url, subIndex);
    }

    public static bool GetSubEnabled(this string url, int subIndex)
    {
        Debug.Assert(subIndex is 0 or 1);
        var tuple = subIndex == 1 ? Secondary : Primary;

        if (!tuple.HasValue || tuple.Value.Item2 is null)
        {
            return false;
        }

        return tuple.Value.Item2.Url == url;
    }
#nullable restore
}

public class Subtitle : NotifyPropertyChanged
{
    private readonly Player _player;
    private readonly int _subIndex;

    private Config Config => _player.Config;
    private DecoderContext Decoder => _player?.decoder;

    public Subtitle(int subIndex, Player player)
    {
        _player = player;
        _subIndex = subIndex;

        Data = new SubsData(_player, _subIndex);
    }

    public bool EnabledASR
    {
        get;
        internal set
        {
            if (Set(ref field, value))
            {
                // Synchronize as it is updated from outside
                _enabledASR = value;
            }
        }
    }
    private bool _enabledASR;

    /// <summary>
    /// Whether the input has subtitles and it is configured
    /// </summary>
    public bool IsOpened { get; private set => Set(ref field, value); }
    private bool _isOpened;

    public string Codec { get; private set => Set(ref field, value); }
    private string _codec;

    public bool IsBitmap { get; private set => Set(ref field, value); }
    private bool _isBitmap;

    public bool Enabled => IsOpened || EnabledASR;

    public SubsData Data { get; }

    private void UpdateUI()
    {
        UIIfRequired(() =>
        {
            IsOpened = _isOpened;
            Codec = _codec;
            IsBitmap = _isBitmap;
            EnabledASR = _enabledASR;
        });
    }

    internal void Reset()
    {
        _codec = null;
        _isOpened = false;
        _isBitmap = false;
        _player.sFramesPrev[_subIndex] = null;
        // TODO: L: Here it may be null when switching subs while displaying.
        _player.sFrames[_subIndex] = null;
        _enabledASR = false;

        _player.SubtitlesASR.Reset(_subIndex);
        _player.SubtitlesOCR.Reset(_subIndex);
        _player.SubtitlesManager[_subIndex].Reset();

        _player.SubtitleClear(_subIndex);
        //_player.renderer?.ClearOverlayTexture();

        SubtitlesSelectedHelper.Reset(_subIndex);

        Data.Reset();
        UpdateUI();
    }

    internal void Refresh()
    {
        var subStream = Decoder.SubtitlesStreams[_subIndex];

        if (subStream == null)
        {
            Reset();
            return;
        }

        _codec = subStream.Codec;
        _isOpened = !Decoder.SubtitlesDecoders[_subIndex].Disposed;
        _isBitmap = subStream is { IsBitmap: true };

        // Update the selection state of automatically opened subtitles
        // (also manually updated but no problem as it is no change, necessary for primary subtitles)
        if (subStream.ExternalStream is ExternalSubtitlesStream extStream)
        {
            // external sub
            SubtitlesSelectedHelper.Set(_subIndex, (null, extStream));
        }
        else if (subStream.StreamIndex != -1)
        {
            // internal sub
            SubtitlesSelectedHelper.Set(_subIndex, (subStream.StreamIndex, null));
        }

        Data.Reset();
        _player.sFramesPrev[_subIndex] = null;
        _player.sFrames[_subIndex] = null;
        _player.SubtitleClear(_subIndex);
        //player.renderer?.ClearOverlayTexture();

        _enabledASR = false;
        _player.SubtitlesASR.Reset(_subIndex);
        _player.SubtitlesOCR.Reset(_subIndex);
        _player.SubtitlesManager[_subIndex].Reset();

        if (_player.renderer != null)
        {
            // Adjust bitmap subtitle size when resizing screen
            _player.renderer.ViewportChanged -= RendererOnViewportChanged;
            _player.renderer.ViewportChanged += RendererOnViewportChanged;
        }

        UpdateUI();

        // Create cache of the all subtitles in a separate thread
        Task.Run(Load);
    }

    internal void Load()
    {
        // Do not cache for original secondary subtitles
        if (!Config.Subtitles.EnabledCached && _subIndex > 0 && SubtitlesSelectedHelper.SecondaryMethod == SelectSubMethod.Original)
        {
            return;
        }

        SubtitlesStream stream = Decoder.SubtitlesStreams[_subIndex];
        bool isExternal = stream.ExternalStream != null;
        ExternalSubtitlesStream extStream = (ExternalSubtitlesStream)stream.ExternalStream;

        bool useBitmap = Config.Subtitles.EnabledCached || SubtitlesSelectedHelper.GetMethod(_subIndex) == SelectSubMethod.OCR;

        if (isExternal)
        {
            // external sub
            _player.SubtitlesManager.Open(_subIndex, extStream.Url, -1, useBitmap, extStream.Language);
        }
        else
        {
            // internal sub
            _player.SubtitlesManager.Open(_subIndex, Decoder.MainDemuxer.Url, stream.StreamIndex, useBitmap, stream.Language);
        }

        TimeSpan curTime = new(_player.CurTime);

        _player.SubtitlesManager[_subIndex].SetCurrentTime(curTime);

        // Do OCR
        // TODO: L: When OCR is performed while bitmap stream is loaded, the stream is reset and the bitmap is loaded again.
        // Is it better to reuse loaded bitmap?
        if (SubtitlesSelectedHelper.GetMethod(_subIndex) == SelectSubMethod.OCR)
        {
            if (isExternal || _player.SubtitlesOCR.CanDo(stream.Language))
            {
                _player.SubtitlesOCR.Do(_subIndex, _player.SubtitlesManager[_subIndex].Subs.ToList(), stream.Language, curTime);
            }
        }
    }

    internal void Enable()
    {
        if (!_player.CanPlay)
            return;

        // TODO: L: For some reason, there is a problem with subtitles temporarily not being
        // displayed, waiting for about 10 seconds will fix it.
        Decoder.OpenSuggestedSubtitles(_subIndex);

        _player.ReSync(Decoder.SubtitlesStreams[_subIndex], (int)(_player.CurTime / 10000), true);

        Refresh();
        UpdateUI();
    }
    internal void Disable()
    {
        if (!Enabled)
            return;

        Decoder.CloseSubtitles(_subIndex);
        Reset();
        UpdateUI();

        SubtitlesSelectedHelper.Reset(_subIndex);
    }

    private void RendererOnViewportChanged(object sender, EventArgs e)
    {
        Data.BitmapPosition?.Calculate();
    }

    public void EnableASR()
    {
        _enabledASR = true;

        var url = _player.AudioDecoder.Demuxer.Url;
        var streamIndex = _player.AudioDecoder.AudioStream.StreamIndex;

        TimeSpan curTime = new(_player.CurTime);

        Task.Factory.StartNew(() =>
        {
            _player.SubtitlesASR.Open(_subIndex, url, streamIndex, curTime);
        }, TaskCreationOptions.LongRunning);

        UpdateUI();
    }
}

public class Subtitles
{
    /// <summary>
    /// Embedded Streams
    /// </summary>
    public ObservableCollection<SubtitlesStream>
                    Streams         => _player.decoder?.VideoDemuxer.SubtitlesStreamsAll;

    private readonly Subtitle[] _subs;

    // indexer
    public Subtitle this[int i] => _subs[i];

    public Player Player => _player;

    private readonly Player _player;

    private Config Config => _player.Config;

    private int subNum => Config.Subtitles.Max;

    public Subtitles(Player player)
    {
        _player = player;
        _subs = new Subtitle[subNum];

        for (int i = 0; i < subNum; i++)
        {
            _subs[i] = new Subtitle(i, _player);
        }
    }

    internal void Enable()
    {
        for (int i = 0; i < subNum; i++)
        {
            this[i].Enable();
        }
    }

    internal void Disable()
    {
        for (int i = 0; i < subNum; i++)
        {
            this[i].Disable();
        }
    }

    internal void Reset()
    {
        for (int i = 0; i < subNum; i++)
        {
            this[i].Reset();
        }
    }

    internal void Refresh()
    {
        for (int i = 0; i < subNum; i++)
        {
            this[i].Refresh();
        }
    }

    // TODO: L: refactor
    public void DelayRemovePrimary()   => Config.Subtitles[0].Delay -= Config.Player.SubtitlesDelayOffset;
    public void DelayAddPrimary()      => Config.Subtitles[0].Delay += Config.Player.SubtitlesDelayOffset;
    public void DelayRemove2Primary()  => Config.Subtitles[0].Delay -= Config.Player.SubtitlesDelayOffset2;
    public void DelayAdd2Primary()     => Config.Subtitles[0].Delay += Config.Player.SubtitlesDelayOffset2;

    public void DelayRemoveSecondary()   => Config.Subtitles[1].Delay -= Config.Player.SubtitlesDelayOffset;
    public void DelayAddSecondary()      => Config.Subtitles[1].Delay += Config.Player.SubtitlesDelayOffset;
    public void DelayRemove2Secondary()  => Config.Subtitles[1].Delay -= Config.Player.SubtitlesDelayOffset2;
    public void DelayAdd2Secondary()     => Config.Subtitles[1].Delay += Config.Player.SubtitlesDelayOffset2;

    public void ToggleEnabled()        => Config.Subtitles.Enabled = !Config.Subtitles.Enabled;

    public void ToggleVisibility()
    {
        Config.Subtitles[0].Visible = !Config.Subtitles[0].Visible;
        Config.Subtitles[1].Visible = Config.Subtitles[0].Visible;
    }
    public void ToggleVisibilityPrimary()
    {
        Config.Subtitles[0].Visible = !Config.Subtitles[0].Visible;
    }

    public void ToggleVisibilitySecondary()
    {
        Config.Subtitles[1].Visible = !Config.Subtitles[1].Visible;
    }

    private bool _prevSeek()
    {
        var prev = _player.SubtitlesManager[0].GetPrev();
        if (prev is not null)
        {
            _player.SeekAccurate(prev.StartTime, 0);
            return true;
        }

        return false;
    }

    public void PrevSeek() => _prevSeek();

    public void PrevSeekFallback()
    {
        bool seeked = _prevSeek();
        if (!seeked)
        {
            _player.SeekBackward();
        }
    }

    public void CurSeek()
    {
        var cur = _player.SubtitlesManager[0].GetCurrent();
        if (cur is not null)
        {
            _player.SeekAccurate(cur.StartTime, 0);
        }
        else
        {
            // fallback to prevSeek (same as mpv)
            PrevSeek();
        }
    }

    private bool _nextSeek()
    {
        var next = _player.SubtitlesManager[0].GetNext();
        if (next is not null)
        {
            _player.SeekAccurate(next.StartTime, 0);
            return true;
        }
        return false;
    }

    public void NextSeek() => _nextSeek();

    public void NextSeekFallback()
    {
        bool seeked = _nextSeek();
        if (!seeked)
        {
            _player.SeekForward();
        }
    }
}

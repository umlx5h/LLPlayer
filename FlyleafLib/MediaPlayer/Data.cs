using FlyleafLib.MediaFramework.MediaContext;
using FlyleafLib.MediaFramework.MediaStream;
using System;
using System.Collections.ObjectModel;

namespace FlyleafLib.MediaPlayer;
public class Data : NotifyPropertyChanged
{
    /// <summary>
    /// Embedded Streams
    /// </summary>
    public ObservableCollection<DataStream>
                        Streams => decoder?.DataDemuxer.DataStreams;

    /// <summary>
    /// Whether the input has data and it is configured
    /// </summary>
    public bool IsOpened { get => isOpened; internal set => Set(ref _IsOpened, value); }
    internal bool   _IsOpened, isOpened;

    Action uiAction;
    Player player;
    DecoderContext decoder => player.decoder;
    Config Config => player.Config;

    public Data(Player player)
    {
        this.player = player;
        uiAction = () =>
        {
            IsOpened = IsOpened;
        };
    }
    internal void Reset()
    {
        isOpened = false;

        player.UIAdd(uiAction);
    }
    internal void Refresh()
    {
        if (decoder.DataStream == null)
        { Reset(); return; }

        isOpened = !decoder.DataDecoder.Disposed;

        player.UIAdd(uiAction);
    }
    internal void Enable()
    {
        if (!player.CanPlay)
            return;

        decoder.OpenSuggestedData();
        player.ReSync(decoder.DataStream, (int)(player.CurTime / 10000), true);

        Refresh();
        player.UIAll();
    }
    internal void Disable()
    {
        if (!IsOpened)
            return;

        decoder.CloseData();

        player.dFrame = null;
        Reset();
        player.UIAll();
    }
}

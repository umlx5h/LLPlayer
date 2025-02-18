using System.Collections.Generic;

using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaPlaylist;
using FlyleafLib.MediaPlayer;

namespace FlyleafLib.MediaFramework.MediaStream;

public class ExternalStream : DemuxerInput
{
    public string   PluginName      { get; set; }
    public PlaylistItem
                    PlaylistItem    { get; set; }
    public int      Index           { get; set; } = -1; // if we need it (already used to compare same type streams) we need to ensure we fix it in case of removing an item
    public string   Protocol        { get; set; }
    public string   Codec           { get; set; }
    public long     BitRate         { get; set; }
    public Dictionary<string, object>
                    Tag             { get; set; } = new Dictionary<string, object>();
    public void AddTag(object tag, string pluginName)
    {
        if (Tag.ContainsKey(pluginName))
            Tag[pluginName] = tag;
        else
            Tag.Add(pluginName, tag);
    }
    public object GetTag(string pluginName)
        => Tag.ContainsKey(pluginName) ? Tag[pluginName] : null;

    /// <summary>
    /// Whether the item is currently enabled or not
    /// </summary>
    public bool Enabled
    {
        get => _Enabled;
        set
        {
            Utils.UI(() =>
            {
                if (Set(ref _Enabled, value) && value)
                {
                    OpenedCounter++;
                }

                Raise(nameof(EnabledPrimarySubtitle));
                Raise(nameof(EnabledSecondarySubtitle));
                Raise(nameof(SubtitlesStream.SelectedSubMethods));
            });
        }
    }
    bool _Enabled;

    /// <summary>
    /// Times this item has been used/opened
    /// </summary>
    public int      OpenedCounter   { get; set; }

    public MediaType
                    Type => this is ExternalAudioStream ? MediaType.Audio : this is ExternalVideoStream ? MediaType.Video : MediaType.Subs;

    #region Subtitles
    // TODO: L: Used for subtitle streams only, but defined in the base class
    public bool EnabledPrimarySubtitle => Enabled && this.GetSubEnabled(0);
    public bool EnabledSecondarySubtitle => Enabled && this.GetSubEnabled(1);
    #endregion
}

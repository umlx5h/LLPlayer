using System.Collections.Generic;
using System.Collections.ObjectModel;

using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaStream;

namespace FlyleafLib.MediaFramework.MediaPlaylist;

public class PlaylistItem : DemuxerInput
{
    public int      Index                   { get; set; } = -1; // if we need it we need to ensure we fix it in case of removing an item

    /// <summary>
    /// While the Url can expire or be null DirectUrl can be used as a new input for re-opening
    /// </summary>
    public string   DirectUrl               { get; set; }

    /// <summary>
    /// Relative folder to playlist's folder base (can be empty, not null)
    /// Use Path.Combine(Playlist.FolderBase, Folder) to get absolute path for saving related files with the current selection item (such as subtitles)
    /// </summary>
    public string   Folder                  { get; set; } = "";

    public long     FileSize                { get; set; }

    /// <summary>
    /// Usually just the filename part of the provided Url
    /// </summary>
    public string   OriginalTitle           { get => _OriginalTitle;set => SetUI(ref _OriginalTitle, value ?? "", false); }
    string _OriginalTitle = "";

    /// <summary>
    /// Movie/TVShow Title
    /// </summary>
    public string   MediaTitle              { get => _MediaTitle;   set => SetUI(ref _MediaTitle, value ?? "", false); }
    string _MediaTitle = "";

    /// <summary>
    /// Movie/TVShow Title including Movie's Year or TVShow's Season/Episode
    /// </summary>
    public string   Title                   { get => _Title;        set { if (_Title == "") OriginalTitle = value; SetUI(ref _Title, value ?? "", false);} }
    string _Title = "";

    public List<Demuxer.Chapter>
                    Chapters                { get; set; } = new();

    public int      Season                  { get; set; }
    public int      Episode                 { get; set; }
    public int      Year                    { get; set; }

    public Dictionary<string, object>
                    Tag                     { get; set; } = [];
    public void AddTag(object tag, string pluginName)
    {
        if (!Tag.TryAdd(pluginName, tag))
            Tag[pluginName] = tag;
    }

    public object GetTag(string pluginName)
        => Tag.TryGetValue(pluginName, out object value) ? value : null;

    public bool     SearchedLocal           { get; set; }
    public bool     SearchedOnline          { get; set; }

    /// <summary>
    /// Whether the item is currently enabled or not
    /// </summary>
    public bool     Enabled                 { get => _Enabled; set { if (SetUI(ref _Enabled, value) && value == true) OpenedCounter++; } }
    bool _Enabled;
    public int      OpenedCounter           { get; set; }

    public ExternalVideoStream
                    ExternalVideoStream     { get; set; }
    public ExternalAudioStream
                    ExternalAudioStream     { get; set; }
    public ExternalSubtitlesStream[]
                    ExternalSubtitlesStreams
                                            { get; set; } = new ExternalSubtitlesStream[2];

    public ObservableCollection<ExternalVideoStream>
                    ExternalVideoStreams    { get; set; } = [];
    public ObservableCollection<ExternalAudioStream>
                    ExternalAudioStreams    { get; set; } = [];
    public ObservableCollection<ExternalSubtitlesStream>
                    ExternalSubtitlesStreamsAll
                                            { get; set; } = [];
    internal object lockExternalStreams = new();

    bool filled;
    public void FillMediaParts() // Called during OpenScrape (if file) & Open/Search Subtitles (to be able to search online and compare tvshow/movie properly)
    {
        if (filled)
            return;

        filled      = true;
        var mp      = Utils.GetMediaParts(OriginalTitle);
        Year        = mp.Year;
        Season      = mp.Season;
        Episode     = mp.Episode;
        MediaTitle  = mp.Title; // safe title to check with online subs

        if (mp.Season > 0 && mp.Episode > 0) // tvshow
        {
            var title = "S";
            title += Season > 9 ? Season : $"0{Season}";
            title += "E";
            title += Episode > 9 ? Episode : $"0{Episode}";

            Title = mp.Title == "" ? title : mp.Title + " (" + title + ")";
        }
        else if (mp.Year > 0) // movie
            Title = mp.Title + " (" + mp.Year + ")";
    }

    public static void AddExternalStream(ExternalStream extStream, PlaylistItem item, string pluginName, object tag = null)
    {
        lock (item.lockExternalStreams)
        {
            extStream.PlaylistItem = item;
            extStream.PluginName = pluginName;

            if (extStream is ExternalAudioStream astream)
            {
                item.ExternalAudioStreams.Add(astream);
                extStream.Index = item.ExternalAudioStreams.Count - 1;
            }
            else if (extStream is ExternalVideoStream vstream)
            {
                item.ExternalVideoStreams.Add(vstream);
                extStream.Index = item.ExternalVideoStreams.Count - 1;
            }
            else if (extStream is ExternalSubtitlesStream sstream)
            {
                item.ExternalSubtitlesStreamsAll.Add(sstream);
                extStream.Index = item.ExternalSubtitlesStreamsAll.Count - 1;
            }

            if (tag != null)
                extStream.AddTag(tag, pluginName);
        };
    }
}

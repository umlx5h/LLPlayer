using System.IO;
using FlyleafLib.MediaFramework.MediaStream;

namespace FlyleafLib.Plugins;

public class OpenSubtitles : PluginBase, IOpenSubtitles, ISearchLocalSubtitles
{
    public new int Priority { get; set; } = 3000;

    public OpenSubtitlesResults Open(string url)
    {
        /* TODO
         * 1) Identify language
         */

        foreach(var extStream in Selected.ExternalSubtitlesStreams)
            if (extStream.Url == url)
                return new OpenSubtitlesResults(extStream);

        string title;

        try
        {
            FileInfo fi = new(url);
            title = fi.Extension == null ? fi.Name : fi.Name[..^fi.Extension.Length];
        }
        catch { title = url; }

        ExternalSubtitlesStream newExtStream = new()
        {
            Url         = url,
            Title       = title,
            Downloaded  = true,
        };

        AddExternalStream(newExtStream);

        return new OpenSubtitlesResults(newExtStream);
    }

    public OpenSubtitlesResults Open(Stream iostream) => null;

    public void SearchLocalSubtitles()
    {
        /* TODO
         * 1) Identify language
        */

        try
        {
            // Checks for text subtitles with the same file name and reads them
            // TODO: L: Search for subtitles with filenames like video file.XXX.srt
            // TODO: L: Allow reading from specific folders as well.
            foreach (string ext in Utils.ExtensionsSubtitles)
            {
                string subPath = Path.ChangeExtension(Playlist.Url, ext);
                if (File.Exists(subPath))
                {
                    ExternalSubtitlesStream sub = new()
                    {
                        Url = subPath,
                        Title = Path.GetFileNameWithoutExtension(subPath),
                        Downloaded = true,
                        Language = Language.Unknown
                    };

                    Log.Debug($"Adding [{sub.Language.TopEnglishName}] {subPath}");

                    AddExternalStream(sub);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"SearchLocalSubtitles failed ({e.Message})");
        }
    }
}

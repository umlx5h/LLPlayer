using System;
using System.IO;

using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaFramework.MediaPlaylist;

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
        bool converted = false;

        try
        {
            FileInfo fi = new(url);
            title = fi.Extension == null ? fi.Name : fi.Name[..^fi.Extension.Length];
            converted = fi.Extension.Equals(".sub", StringComparison.OrdinalIgnoreCase);
        }
        catch { title = url; }

        ExternalSubtitlesStream newExtStream = new()
        {
            Url         = url,
            Title       = title,
            Downloaded  = true,
            Converted   = converted
        };

        AddExternalStream(newExtStream);

        return new OpenSubtitlesResults(newExtStream);
    }

    public OpenSubtitlesResults Open(Stream iostream) => null;

    public void SearchLocalSubtitles()
    {
        /* TODO
         * 1) Subs folder could exist under Season X (it will suggest another season's subtitle)
         * 2) Identify language
         * 3) Confidence
         */

        try
        {
            string folder = Path.Combine(Playlist.FolderBase, Selected.Folder, "Subs");
            if (!Directory.Exists(folder))
                return;

            string[] filesCur1 = Directory.GetFiles(folder, $"*.srt"); // We consider Subs/ folder has only subs for this movie/series
            string[] filesCur2 = Directory.GetFiles(folder, $"*.sub");

            string[] filesCur = new string[filesCur1.Length + filesCur2.Length];

            for (int i = 0; i < filesCur1.Length; i++)
                filesCur[i] = filesCur1[i];

            for (int i = 0; i < filesCur2.Length; i++)
                filesCur[i + filesCur1.Length] = filesCur2[i];

            foreach(string file in filesCur)
            {
                bool exists = false;
                foreach(var extStream in Selected.ExternalSubtitlesStreams)
                    if (extStream.Url == file)
                        { exists = true; break; }
                if (exists) continue;

                FileInfo fi = new(file);

                // We might have same Subs/ folder for more than one episode/season then filename requires to have season/episode
                var mp = Utils.GetMediaParts(fi.Name);
                if (mp.Episode != Selected.Episode || (mp.Season != Selected.Season && Selected.Season > 0 && mp.Season > 0))
                    continue;

                string title = null;

                // Until we analyze the actual text to identify the language we just use the filename
                bool converted = false;
                var lang = Language.Unknown;

                if (fi.Extension.Equals(".sub", StringComparison.OrdinalIgnoreCase))
                {
                    title = fi.Extension == null ? fi.Name : fi.Name[..^fi.Extension.Length];
                    converted = true;
                }
                if (fi.Name.IndexOf(".utf8", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    converted = true;

                    int pos = -1;
                    if ((pos = fi.Name.IndexOf($".{Language.Unknown.IdSubLanguage}.utf8", StringComparison.OrdinalIgnoreCase)) != -1)
                        title = fi.Name[..pos];
                    else
                        foreach (var lang2 in Config.Subtitles.Languages)
                            if ((pos = fi.Name.IndexOf($".{lang2.IdSubLanguage}.utf8", StringComparison.OrdinalIgnoreCase)) != -1)
                                { lang = lang2; title = fi.Name[..pos]; break; }

                    if (title == null)
                        title = fi.Extension == null ? fi.Name : fi.Name[..^fi.Extension.Length];
                }
                else
                {
                    title = fi.Extension == null ? fi.Name : fi.Name[..^fi.Extension.Length];

                    bool utf8Exists = false;

                    foreach(string file2 in filesCur)
                    {
                        FileInfo fi2 = new(file2);
                        if (fi2.Name.IndexOf(".utf8", StringComparison.OrdinalIgnoreCase) != -1 && fi2.Name.IndexOf(title, StringComparison.OrdinalIgnoreCase) != -1)
                            { utf8Exists = true; break; }
                    }

                    if (utf8Exists)
                        continue;
                }

                Log.Debug($"Adding [{lang}] {file}");

                AddExternalStream(new ExternalSubtitlesStream()
                {
                    Url         = file,
                    Title       = title,
                    Converted   = converted,
                    Downloaded  = true,
                    Language    = lang
                });
            }
        } catch (Exception e) { Log.Error($"SearchLocalSubtitles failed ({e.Message})"); }
    }
}

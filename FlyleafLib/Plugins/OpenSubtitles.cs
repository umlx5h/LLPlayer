using FlyleafLib.MediaFramework.MediaStream;
using Lingua;
using System.IO;
using System.Text;

namespace FlyleafLib.Plugins;

public class OpenSubtitles : PluginBase, IOpenSubtitles, ISearchLocalSubtitles
{
    public new int Priority { get; set; } = 3000;

    private readonly Lazy<LanguageDetector> _languageDetector = new(() =>
    {
        LanguageDetector detector = LanguageDetectorBuilder
            .FromAllLanguages()
            .Build();

        return detector;
    }, true);

    public OpenSubtitlesResults Open(string url)
    {
        foreach(var extStream in Selected.ExternalSubtitlesStreams)
            if (extStream.Url == url)
                return new OpenSubtitlesResults(extStream);

        string title;

        try
        {
            FileInfo fi = new(url);
            title = string.IsNullOrEmpty(fi.Extension) ? fi.Name : fi.Name[..^fi.Extension.Length];
        }
        catch { title = url; }

        ExternalSubtitlesStream newExtStream = new()
        {
            Url         = url,
            Title       = title,
            Downloaded  = true,
            IsBitmap    = IsSubtitleBitmap(url),
        };

        if (Config.Subtitles.LanguageAutoDetect && !newExtStream.IsBitmap)
        {
            newExtStream.Language = DetectLanguage(url);
            newExtStream.LanguageDetected = true;
        }

        AddExternalStream(newExtStream);

        return new OpenSubtitlesResults(newExtStream);
    }

    public OpenSubtitlesResults Open(Stream iostream) => null;

    public void SearchLocalSubtitles()
    {
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
                        IsBitmap = IsSubtitleBitmap(subPath),
                    };

                    if (Config.Subtitles.LanguageAutoDetect && !sub.IsBitmap)
                    {
                        sub.Language = DetectLanguage(subPath);
                        sub.LanguageDetected = true;
                    }

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

    // TODO: L: To check the contents of a file by determining the bitmap.
    private bool IsSubtitleBitmap(string path)
    {
        try
        {
            FileInfo fi = new(path);

            return Utils.ExtensionsSubtitlesBitmap.Contains(fi.Extension.TrimStart('.').ToLower());
        }
        catch
        {
            return false;
        }
    }

    // TODO: L: Would it be better to check with SubtitlesManager for network subtitles?
    private Language DetectLanguage(string path)
    {
        if (!File.Exists(path))
        {
            return Language.Unknown;
        }

        Encoding encoding = Encoding.Default;
        Encoding detected = TextEncodings.DetectEncoding(path);

        if (detected != null)
        {
            encoding = detected;
        }

        // TODO: L: refactor: use the code for reading subtitles in Demuxer
        byte[] data = new byte[100 * 1024];

        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read);
            int bytesRead = fs.Read(data, 0, data.Length);
            Array.Resize(ref data, bytesRead);
        }
        catch
        {
            return Language.Unknown;
        }

        string content = encoding.GetString(data);

        var detectedLanguage = _languageDetector.Value.DetectLanguageOf(content);

        if (detectedLanguage == Lingua.Language.Unknown)
        {
            return Language.Unknown;
        }

        return Language.Get(detectedLanguage.IsoCode6391().ToString().ToLower());
    }
}

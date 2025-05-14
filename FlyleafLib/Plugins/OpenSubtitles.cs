using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FlyleafLib.MediaFramework.MediaStream;
using Lingua;

namespace FlyleafLib.Plugins;

public class OpenSubtitles : PluginBase, IOpenSubtitles, ISearchLocalSubtitles
{
    public new int Priority { get; set; } = 3000;

    private static readonly Lazy<LanguageDetector> LanguageDetector = new(() =>
    {
        LanguageDetector detector = LanguageDetectorBuilder
            .FromAllLanguages()
            .Build();

        return detector;
    }, true);

    private static readonly HashSet<string> ExtSet = new(Utils.ExtensionsSubtitles, StringComparer.OrdinalIgnoreCase);

    public OpenSubtitlesResults Open(string url)
    {
        foreach (var extStream in Selected.ExternalSubtitlesStreamsAll)
            if (extStream.Url == url)
                return new(extStream);

        string title;

        if (File.Exists(url))
        {
            Selected.FillMediaParts();

            FileInfo fi = new(url);
            title       = fi.Name;
        }
        else
        {
            try
            {
                Uri uri = new(url);
                title = Path.GetFileName(uri.LocalPath);

                if (title == null || title.Trim().Length == 0)
                    title = url;

            } catch
            {
                title = url;
            }
        }

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

        return new(newExtStream);
    }

    public OpenSubtitlesResults Open(Stream iostream) => null;

    public void SearchLocalSubtitles()
    {
        try
        {
            string mediaDir = Path.GetDirectoryName(Playlist.Url);
            string mediaName = Path.GetFileNameWithoutExtension(Playlist.Url);

            OrderedDictionary<string, Language> result = new(StringComparer.OrdinalIgnoreCase);

            CollectFromDirectory(mediaDir, mediaName, result);

            // also search in subdirectories
            string paths = Config.Subtitles.SearchLocalPaths;
            if (!string.IsNullOrWhiteSpace(paths))
            {
                foreach (Range seg in paths.AsSpan().Split(';'))
                {
                    var path = paths.AsSpan(seg).Trim();
                    if (path.IsEmpty) continue;

                    string searchDir = !Path.IsPathRooted(path)
                        ? Path.Join(mediaDir, path)
                        : path.ToString();

                    if (Directory.Exists(searchDir))
                    {
                        CollectFromDirectory(searchDir, mediaName, result);
                    }
                }
            }

            if (result.Count == 0)
            {
                return;
            }

            Selected.FillMediaParts();

            foreach (var (path, lang) in result)
            {
                if (Selected.ExternalSubtitlesStreamsAll.Any(s => s.Url == path))
                {
                    continue;
                }

                FileInfo fi = new(path);
                string title = fi.Name;

                ExternalSubtitlesStream sub = new()
                {
                    Url = path,
                    Title = title,
                    Downloaded = true,
                    IsBitmap = IsSubtitleBitmap(path),
                    Language = lang
                };

                if (Config.Subtitles.LanguageAutoDetect && !sub.IsBitmap && lang == Language.Unknown)
                {
                    sub.Language = DetectLanguage(path);
                    sub.LanguageDetected = true;
                }

                Log.Debug($"Adding [{sub.Language.TopEnglishName}] {path}");

                AddExternalStream(sub);
            }
        }
        catch (Exception e)
        {
            Log.Error($"SearchLocalSubtitles failed ({e.Message})");
        }
    }

    private static void CollectFromDirectory(string searchDir, string filename, IDictionary<string, Language> result)
    {
        HashSet<int> added = null;

        // Get files starting with the same filename
        List<string> fileList;
        try
        {
            fileList = Directory.GetFiles(searchDir, $"{filename}.*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .ToList();
        }
        catch
        {
            return;
        }

        if (fileList.Count == 0)
        {
            return;
        }

        var files = fileList.Select(f => new
        {
            FullPath = f,
            FileName = Path.GetFileName(f)
        }).ToList();

        // full match with top priority (video.srt, video.ass)
        foreach (string ext in ExtSet)
        {
            string expect = $"{filename}.{ext}";
            int match = files.FindIndex(x => string.Equals(x.FileName, expect, StringComparison.OrdinalIgnoreCase));
            if (match != -1)
            {
                result.TryAdd(files[match].FullPath, Language.Unknown);
                added ??= new HashSet<int>();
                added.Add(match);
            }
        }

        // head match (video.*.srt, video.*.ass)
        var extSetLookup = ExtSet.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var (i, x) in files.Index())
        {
            // skip full match
            if (added != null && added.Contains(i))
            {
                continue;
            }

            var span = x.FileName.AsSpan();
            var fileExt = Path.GetExtension(span).TrimStart('.');

            // Check if the file is a subtitle file by its extension
            if (extSetLookup.Contains(fileExt))
            {
                var name = Path.GetFileNameWithoutExtension(span);

                if (!name.StartsWith(filename + '.', StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Language lang = Language.Unknown;

                var extraPart = name.Slice(filename.Length + 1); // Skip file name and dot
                if (extraPart.Length > 0)
                {
                    foreach (var codeSeg in extraPart.Split('.'))
                    {
                        var code = extraPart[codeSeg];
                        if (code.Length > 0)
                        {
                            Language parsed = Language.Get(code.ToString());
                            if (!string.IsNullOrEmpty(parsed.IdSubLanguage) && parsed.IdSubLanguage != "und")
                            {
                                lang = parsed;
                                break;
                            }
                        }
                    }
                }

                result.TryAdd(x.FullPath, lang);
            }
        }
    }

    // TODO: L: To check the contents of a file by determining the bitmap.
    private static bool IsSubtitleBitmap(string path)
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
    private static Language DetectLanguage(string path)
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

        var detectedLanguage = LanguageDetector.Value.DetectLanguageOf(content);

        if (detectedLanguage == Lingua.Language.Unknown)
        {
            return Language.Unknown;
        }

        return Language.Get(detectedLanguage.IsoCode6391().ToString().ToLower());
    }
}

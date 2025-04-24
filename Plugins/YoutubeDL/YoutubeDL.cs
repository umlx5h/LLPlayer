using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaPlaylist;
using FlyleafLib.MediaFramework.MediaStream;

using static FlyleafLib.Utils;

namespace FlyleafLib.Plugins
{
    public class YoutubeDL : PluginBase, IOpen, ISuggestExternalAudio, ISuggestExternalVideo
    {
        /* TODO
         * 1) Check Audio streams if we need to add also video streams with audio
         * 2) Check Best Audio bitrates/quality (mainly for audio only player)
         * 3) Dispose ytdl and not tag it to every item (use only format if required)
         * 4) Use playlist_index to set the default playlist item
         */

        public new int      Priority        { get; set; } = 1999;
        static string       plugin_path     = "yt-dlp.exe";
        static JsonSerializerOptions
                            jsonSettings    = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        FileSystemWatcher   watcher;
        string              workingDir;

        Process             proc;
        int                 procId = -1;
        object              procLocker = new();

        bool                addingItem;
        string              dynamicOptions = "";
        bool                errGenericImpersonate;
        long                sessionId = -1; // same for playlists
        int                 retries;

        static HashSet<string>
                            subsExt = new(StringComparer.OrdinalIgnoreCase) { "vtt", "srt", "ass", "ssa" };

        public override Dictionary<string, string> GetDefaultOptions()
            => new()
            {
                { "ExtraArguments", "" }, // TBR: Restore default functionality with --cookies-from-browser {defaultBrowser} || https://github.com/yt-dlp/yt-dlp/issues/7271
                { "MaxVideoHeight", "720" },
                { "PreferVideoWithAudio", "False" },
            };

        public override void OnInitializing()
            => DisposeInternal();

        public override void Dispose()
            => DisposeInternal();

        private Format GetAudioOnly(YoutubeDLJson ytdl)
        {
            // Prefer best with no video and protocol
            // Prioritize m3u8 protocol because https is very slow on YouTube
            var m3u8Formats = ytdl.formats.Where(f => f.protocol == "m3u8_native").ToList();
            for (int i = m3u8Formats.Count - 1; i >= 0; i--)
                if (HasAudio(m3u8Formats[i]) && !HasVideo(m3u8Formats[i]))
                    return m3u8Formats[i];

            // Prefer best with no video (dont waste bandwidth)
            for (int i = ytdl.formats.Count - 1; i >= 0; i--)
                if (HasAudio(ytdl.formats[i]) && !HasVideo(ytdl.formats[i]))
                    return ytdl.formats[i];

            // Prefer audio from worst video?
            for (int i = 0; i < ytdl.formats.Count; i++)
                if (HasAudio(ytdl.formats[i]))
                    return ytdl.formats[i];

            return null;
        }
        private Format GetBestMatch(YoutubeDLJson ytdl)
        {
            // TODO: Expose in settings (vCodecs Blacklist) || Create a HW decoding failed list dynamic (check also for whitelist)
            List<string> vCodecsBlacklist = [];

            int maxHeight;

            if (int.TryParse(Options["MaxVideoHeight"], out var height) && height > 0)
                maxHeight = Math.Min(Config.Video.MaxVerticalResolution, height);
            else
                maxHeight = Config.Video.MaxVerticalResolution;

            // Video Streams Order based on Screen Resolution
            var iresults =
                from    format in ytdl.formats
                where   HasVideo(format) && format.height <= maxHeight && (!Regex.IsMatch(format.protocol, "dash", RegexOptions.IgnoreCase) || format.vcodec.ToLower() == "vp9")
                orderby format.width    descending,
                        format.height   descending,
                        format.protocol descending, // prefer m3u8 over https (for performance)
                        format.vcodec   descending, // prefer vp09 over avc   (for performance)
                        format.tbr      descending,
                        format.fps      descending
                select  format;

            if (iresults == null || iresults.Count() == 0)
            {
                // Fall-back to any
                iresults =
                    from    format in ytdl.formats
                    where   HasVideo(format)
                    orderby format.width    descending,
                            format.height   descending,
                            format.protocol descending,
                            format.vcodec   descending,
                            format.tbr      descending,
                            format.fps      descending
                    select  format;

                if (iresults == null || iresults.Count() == 0) return null;
            }

            List<Format> results = iresults.ToList();

            // Best Resolution
            double bestWidth = results[0].width;
            double bestHeight = results[0].height;

            // Choose from the best resolution (0. with acodec and not blacklisted 1. not blacklisted 2. any)
            int priority = 1;
            if (bool.TryParse(Options["PreferVideoWithAudio"], out var v) && v)
            {
                priority = 0;
            }
            while (priority < 3)
            {
                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i].width != bestWidth || results[i].height != bestHeight)
                        break;

                    if (priority == 0 && !IsBlackListed(vCodecsBlacklist, results[i].vcodec) && results[i].acodec != "none")
                        return results[i];
                    else if (priority == 1 && !IsBlackListed(vCodecsBlacklist, results[i].vcodec))
                        return results[i];
                    else if (priority == 2)
                        return results[i];
                }

                priority++;
            }

            return results[results.Count - 1]; // Fall-back to any
        }
        private static bool IsBlackListed(List<string> blacklist, string codec)
        {
            foreach (string codec2 in blacklist)
                if (Regex.IsMatch(codec, codec2, RegexOptions.IgnoreCase))
                    return true;

            return false;
        }
        private static bool HasVideo(Format fmt)
        {
            if (fmt.height > 0 || fmt.vbr > 0 || fmt.vcodec != "none")
                return true;

            return false;
        }
        private static bool HasAudio(Format fmt)
        {
            if (fmt.abr > 0 || fmt.acodec != "none")
                return true;

            return false;
        }

        private static bool IsGeneratedSubtitle(string url)
        {
            if (url.Contains("youtube") && url.Contains("/api/timedtext"))
                return true;

            return false;
        }

        private void DisposeInternal()
        {
            lock (procLocker)
            {
                if (Disposed)
                    return;

                Log.Debug($"Disposing ({procId})");

                if (procId != -1)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = "taskkill",
                        Arguments       = $"/pid {procId} /f /t",
                        CreateNoWindow  = true,
                        UseShellExecute = false,
                        WindowStyle     = ProcessWindowStyle.Hidden,
                    }).WaitForExit();
                }

                retries         =  0;
                sessionId       = -1;
                dynamicOptions  = "";
                errGenericImpersonate = false;

                if (watcher != null)
                {
                    watcher.Dispose();
                    watcher = null;
                }

                if (workingDir != null)
                {
                    Log.Debug($"Folder deleted ({workingDir})");
                    Directory.Delete(workingDir, true);
                    workingDir = null;
                }

                Disposed = true;
                Log.Debug($"Disposed ({procId})");
            }
        }

        private void NewPlaylistItem(string path)
        {
            string json = null;

            // File Watcher informs us on rename but the process still accessing the file
            for (int i=0; i<3; i++)
            {
                Thread.Sleep(20);
                try { json = File.ReadAllText(path); } catch { if (sessionId != Handler.OpenCounter) return; continue; }
                break;
            }

            YoutubeDLJson ytdl = null;

            try
            {
                ytdl = JsonSerializer.Deserialize<YoutubeDLJson>(json, jsonSettings);
            } catch (Exception e)
            {
                Log.Error($"[JsonSerializer] {e.Message}");
            }

            if (sessionId != Handler.OpenCounter) return;

            if (ytdl == null)
                return;

            if (ytdl._type == "playlist")
                return;

            PlaylistItem item = new();

            if (Playlist.ExpectingItems == 0)
                Playlist.ExpectingItems = (int)ytdl.playlist_count;

            if (Playlist.Title == null)
            {
                if (!string.IsNullOrEmpty(ytdl.playlist_title))
                {
                    Playlist.Title = ytdl.playlist_title;
                    Log.Debug($"Playlist Title -> {Playlist.Title}");
                }
                else if (!string.IsNullOrEmpty(ytdl.playlist))
                {
                    Playlist.Title = ytdl.playlist;
                    Log.Debug($"Playlist Title -> {Playlist.Title}");
                }
            }

            item.Title = ytdl.title;
            Log.Debug($"Adding {item.Title}");

            item.DirectUrl = ytdl.webpage_url;

            if (ytdl.chapters != null && ytdl.chapters.Count > 0)
            {
                item.Chapters.AddRange(ytdl.chapters.Select(c => new Demuxer.Chapter()
                {
                    StartTime = TimeSpan.FromSeconds(c.start_time).Ticks,
                    EndTime = TimeSpan.FromSeconds(c.end_time).Ticks,
                    Title = c.title
                }));
            }

            // If no formats still could have a single format attched to the main root class
            if (ytdl.formats == null)
                ytdl.formats = [ytdl];

            // Audio / Video Streams
            for (int i=0; i<ytdl.formats.Count; i++)
            {
                if (sessionId != Handler.OpenCounter)
                    return;

                Format fmt = ytdl.formats[i];

                if (ytdl.formats[i].vcodec == null)
                    ytdl.formats[i].vcodec = "";

                if (ytdl.formats[i].acodec == null)
                    ytdl.formats[i].acodec = "";

                if (ytdl.formats[i].protocol == null)
                    ytdl.formats[i].protocol = "";

                bool hasAudio = HasAudio(fmt);
                bool hasVideo = HasVideo(fmt);

                if (!hasVideo && !hasAudio)
                    continue;

                ExternalStream extStream;

                if (hasVideo)
                {
                    extStream = new ExternalVideoStream()
                    {
                        Url         = fmt.url,
                        UrlFallback = string.IsNullOrEmpty(fmt.manifest_url) ? ytdl.manifest_url : fmt.manifest_url,
                        Protocol    = fmt.protocol,
                        HasAudio    = hasAudio,
                        BitRate     = (long)fmt.vbr,
                        Codec       = fmt.vcodec,
                        //Language = Language.Get(fmt.language),
                        Width       = (int)fmt.width,
                        Height      = (int)fmt.height,
                        FPS         = fmt.fps
                    };
                }
                else
                {
                    extStream = new ExternalAudioStream()
                    {
                        Url         = fmt.url,
                        UrlFallback = string.IsNullOrEmpty(fmt.manifest_url) ? ytdl.manifest_url : fmt.manifest_url,
                        Protocol    = fmt.protocol,
                        HasVideo    = hasVideo,
                        BitRate     = (long)fmt.abr,
                        Codec       = fmt.acodec,
                        Language    = Language.Get(fmt.language)
                    };
                }

                AddHeaders(extStream, fmt);
                AddExternalStream(extStream, fmt, item);
            }

            if (GetBestMatch(ytdl) == null && GetAudioOnly(ytdl) == null)
            {
                Log.Warn("No streams found");
                return;
            }

            // Subtitles Streams
            try
            {
                // May not be available
                Language videoLang = ytdl.language == null ? Language.Unknown : Language.Get(ytdl.language);
                HashSet<string> addedUrl = new(); // for de-duplication

                if (ytdl.automatic_captions != null)
                {
                    foreach (var subtitle1 in ytdl.automatic_captions)
                    {
                        if (sessionId != Handler.OpenCounter)
                            return;

                        // original (source) language has this suffix
                        const string suffix = "-orig";

                        string langCode = subtitle1.Key;
                        bool isOriginal = langCode.EndsWith(suffix);

                        if (isOriginal)
                        {
                            // remove -orig suffix
                            langCode = langCode[..^suffix.Length];
                        }

                        Language lang = Language.Get(langCode);

                        foreach (var subtitle in subtitle1.Value)
                        {
                            if (!subsExt.Contains(subtitle.ext))
                                continue;

                            bool autoGenerated = IsGeneratedSubtitle(subtitle.url);

                            if (!isOriginal && autoGenerated) // always load original subtitle
                            {
                                // Automatic subtitles are loaded under the following ORed conditions
                                // 1. If the language matches the set language
                                // 2. Subtitles in the same language as the video
                                if (!(Config.Subtitles.Languages.Contains(lang) || videoLang != Language.Unknown && videoLang == lang))
                                {
                                    continue;
                                }
                            }

                            // because -orig may be duplicated
                            if (!addedUrl.Add(subtitle.url))
                                continue;

                            AddExternalStream(new ExternalSubtitlesStream()
                            {
                                Downloaded    = true,
                                Protocol      = subtitle.ext,
                                Language      = lang,
                                Url           = subtitle.url,
                                AutoGenerated = autoGenerated
                            }, null, item);
                        }
                    }
                }
            } catch (Exception e) { Log.Warn($"Failed to add subtitles ({e.Message})"); }

            AddPlaylistItem(item, ytdl);
        }
        public void AddHeaders(ExternalStream extStream, Format fmt)
        {
            if (fmt.http_headers != null)
            {
                if (fmt.http_headers.TryGetValue("User-Agent", out string value))
                {
                    extStream.UserAgent = value;
                    fmt.http_headers.Remove("User-Agent");
                }

                if (fmt.http_headers.TryGetValue("Referer", out value))
                {
                    extStream.Referrer = value;
                    fmt.http_headers.Remove("Referer");
                }

                extStream.HTTPHeaders = fmt.http_headers;

                if (!string.IsNullOrEmpty(fmt.cookies))
                    extStream.HTTPHeaders.Add("Cookies", fmt.cookies);

            }
        }

        public bool CanOpen()
        {
            try
            {
                if (Playlist.IOStream != null)
                    return false;

                Uri uri = new Uri(Playlist.Url);
                string scheme = uri.Scheme.ToLower();

                if (scheme != "http" && scheme != "https")
                    return false;

                string ext = Utils.GetUrlExtention(uri.AbsolutePath);

                if (ext == "m3u8" || ext == "mp3" || ext == "m3u" || ext == "pls")
                    return false;

                // TBR: try to avoid processing radio stations
                if (string.IsNullOrEmpty(uri.PathAndQuery) || uri.PathAndQuery.Length < 5)
                    return false;

            } catch (Exception) { return false; }

            return true;
        }
        public OpenResults Open()
        {
            try
            {
                lock (procLocker)
                {
                    Disposed = false;
                    sessionId = Handler.OpenCounter;
                    Playlist.InputType = InputType.Web;

                    workingDir = Path.GetTempPath() + Guid.NewGuid().ToString();

                    Log.Debug($"Folder created ({workingDir})");
                    Directory.CreateDirectory(workingDir);
                    proc = new Process
                    {
                        EnableRaisingEvents = true,

                        StartInfo = new ProcessStartInfo
                        {
                            FileName        = Path.Combine(Engine.Plugins.Folder, Name, plugin_path),
                            Arguments       = $"{dynamicOptions}{Options["ExtraArguments"]} --no-check-certificate --skip-download --youtube-skip-dash-manifest --write-info-json -P \"{workingDir}\" \"{Playlist.Url}\" -o \"%(title).220B\"", // 418 max filename length
                            CreateNoWindow  = true,
                            UseShellExecute = false,
                            WindowStyle     = ProcessWindowStyle.Hidden,
                            RedirectStandardError   = true,
                            RedirectStandardOutput  = Logger.CanDebug,
                        }
                    };

                    proc.Exited += (o, e) =>
                    {
                        lock (procLocker)
                        {
                            if (Logger.CanDebug)
                                Log.Debug($"Process completed ({(procId == -1 ? "Killed" : $"{procId}")})");

                            proc.Close();
                            proc    = null;
                            procId  = -1;
                        }
                    };

                    proc.ErrorDataReceived += (o, e) =>
                    {
                        if (sessionId != Handler.OpenCounter || e.Data == null)
                            return;

                        Log.Debug($"[stderr] {e.Data}");

                        if (!errGenericImpersonate && e.Data.Contains("generic:impersonate"))
                            errGenericImpersonate = true;
                    };

                    if (Logger.CanDebug)
                        proc.OutputDataReceived += (o, e) =>
                        {
                            if (sessionId == Handler.OpenCounter)
                                Log.Debug($"[stdout] {e.Data}");
                        };

                    watcher = new()
                    {
                        Path = workingDir,
                        EnableRaisingEvents = true,
                    };
                    watcher.Renamed += (o, e) =>
                    {
                        try
                        {
                            if (sessionId != Handler.OpenCounter)
                                return;

                            addingItem = true;

                            NewPlaylistItem(e.FullPath);

                            if (Playlist.Items.Count == 1)
                                Handler.OnPlaylistCompleted();

                        } catch (Exception e2) { Log.Warn($"Renamed Event Error {e2.Message} | {sessionId != Handler.OpenCounter}");
                        } finally { addingItem = false; }
                    };

                    proc.Start();
                    procId = proc.Id;
                    Log.Debug($"Process started ({procId})");

                    // Don't try to read them at once at the end as the buffers (hardcoded to 4096) can be full and proc will freeze
                    proc.BeginErrorReadLine();
                    if (Logger.CanDebug)
                        proc.BeginOutputReadLine();
                }

                while (Playlist.Items.Count < 1 && (proc != null || addingItem) && sessionId == Handler.OpenCounter)
                    Thread.Sleep(35);

                if (sessionId != Handler.OpenCounter)
                {
                    Log.Info("Session cancelled");
                    DisposeInternal();
                    return null;
                }

                if (Playlist.Items.Count == 0) // Allow fallback to default plugin in case of YT-DLP bug with windows filename (this affects proper direct URLs as well)
                {
                    if (!errGenericImpersonate || retries > 0)
                        return null;

                    Log.Warn("Re-trying with --extractor-args \"generic:impersonate\"");
                    DisposeInternal();
                    retries = 1;
                    dynamicOptions = "--extractor-args \"generic:impersonate\" ";
                    return Open();
                }
            }
            catch (Exception e) { Log.Error($"Open ({e.Message})"); return new OpenResults(e.Message); }

            return new OpenResults();
        }

        public OpenResults OpenItem()
            => new();

        public ExternalAudioStream SuggestExternalAudio()
        {
            if (Handler.OpenedPlugin == null || Handler.OpenedPlugin.Name != Name) return null;

            var fmt = GetAudioOnly((YoutubeDLJson)GetTag(Selected));
            if (fmt == null) return null;

            foreach (var extStream in Selected.ExternalAudioStreams)
                if (fmt.url == extStream.Url) return extStream;

            return null;
        }
        public ExternalVideoStream SuggestExternalVideo()
        {
            if (Handler.OpenedPlugin == null || Handler.OpenedPlugin.Name != Name) return null;

            Format fmt = GetBestMatch((YoutubeDLJson)GetTag(Selected));
            if (fmt == null) return null;

            foreach (var extStream in Selected.ExternalVideoStreams)
                if (fmt.url == extStream.Url) return extStream;

            return null;
        }
    }
}

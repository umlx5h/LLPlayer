using System;
using System.IO;

using FlyleafLib.MediaFramework.MediaPlaylist;

namespace FlyleafLib.Plugins;

public class OpenDefault : PluginBase, IOpen, IScrapeItem
{
    /* TODO
     *
     * 1) Current Url Syntax issues
     *  ..\..\..\..\folder\file.mp3 | Cannot handle this
     *  file:///C:/folder/fi%20le.mp3 | FFmpeg & File.Exists cannot handle this
     *
     */

    public new int  Priority    { get; set; } = 3000;

    public bool CanOpen() => true;

    public OpenResults Open()
    {
        try
        {
            if (Playlist.IOStream != null)
            {
                AddPlaylistItem(new()
                {
                    IOStream= Playlist.IOStream,
                    Title   = "Custom IO Stream",
                    FileSize= Playlist.IOStream.Length
                });

                Handler.OnPlaylistCompleted();

                return new();
            }

            // Proper Url Format
            string scheme;
            bool   isWeb    = false;
            string uriType  = "";
            string ext      = Utils.GetUrlExtention(Playlist.Url);
            string localPath= null;

            try
            {
                Uri uri     = new(Playlist.Url);
                scheme      = uri.Scheme.ToLower();
                isWeb       = scheme.StartsWith("http");
                uriType     = uri.IsFile ? "file" : (uri.IsUnc ? "unc" : "");
                localPath   = uri.LocalPath;
            } catch { }


            // Playlists (M3U, M3U8, PLS | TODO: WPL, XSPF)
            if (ext == "m3u")// || ext == "m3u8")
            {
                Playlist.InputType = InputType.Web; // TBR: Can be mixed
                Playlist.FolderBase = Path.GetTempPath();

                var items = isWeb ? M3UPlaylist.ParseFromHttp(Playlist.Url) : M3UPlaylist.Parse(Playlist.Url);

                foreach(var mitem in items)
                {
                    AddPlaylistItem(new()
                    {
                        Title       = mitem.Title,
                        Url         = mitem.Url,
                        DirectUrl   = mitem.Url,
                        UserAgent   = mitem.UserAgent,
                        Referrer    = mitem.Referrer
                    });
                }

                Handler.OnPlaylistCompleted();

                return new();
            }
            else if (ext == "pls")
            {
                Playlist.InputType = InputType.Web; // TBR: Can be mixed
                Playlist.FolderBase = Path.GetTempPath();

                var items = PLSPlaylist.Parse(Playlist.Url);

                foreach(var mitem in items)
                {
                    AddPlaylistItem(new PlaylistItem()
                    {
                        Title       = mitem.Title,
                        Url         = mitem.Url,
                        DirectUrl   = mitem.Url,
                        // Duration
                    });
                }

                Handler.OnPlaylistCompleted();

                return new();
            }

            FileInfo fi = null;
            // Single Playlist Item
            if (uriType == "file")
            {
                Playlist.InputType = InputType.File;
                if (File.Exists(Playlist.Url))
                {
                    fi = new(Playlist.Url);
                    Playlist.FolderBase = fi.DirectoryName;
                }
            }
            else if (isWeb)
            {
                Playlist.InputType = InputType.Web;
                Playlist.FolderBase = Path.GetTempPath();
            }
            else if (uriType == "unc")
            {
                Playlist.InputType = InputType.UNC;
                Playlist.FolderBase = Path.GetTempPath();
            }
            else
            {
                //Playlist.InputType = InputType.Unknown;
                Playlist.FolderBase = Path.GetTempPath();
            }

            PlaylistItem item = new()
            {
                Url         = Playlist.Url,
                DirectUrl   = Playlist.Url
            };

            if (fi == null && File.Exists(Playlist.Url))
            {
                fi              = new(Playlist.Url);
                item.Title      = fi.Name;
                item.FileSize   = fi.Length;
            }
            else
            {
                if (localPath != null)
                    item.Title = Path.GetFileName(localPath);

                if (item.Title == null || item.Title.Trim().Length == 0)
                    item.Title = Playlist.Url;
            }

            AddPlaylistItem(item);
            Handler.OnPlaylistCompleted();

            return new();
        } catch (Exception e)
        {
            return new(e.Message);
        }
    }

    public OpenResults OpenItem() => new();

    public void ScrapeItem(PlaylistItem item)
    {
        // Update Title (TBR: don't mess with other media types - only movies/tv shows)
        if (Playlist.InputType != InputType.File && Playlist.InputType != InputType.UNC && Playlist.InputType != InputType.Torrent)
            return;

        item.FillMediaParts();
    }
}

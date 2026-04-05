using System.Drawing.Imaging;
using System.Windows;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaPlayer;

unsafe partial class Player
{
    public bool IsOpenFileDialogOpen    { get; private set; }


    public void SeekBackward()  => SeekBackward_(Config.Player.SeekOffset, Config.Player.SeekOffsetAccurate);
    public void SeekBackward2() => SeekBackward_(Config.Player.SeekOffset2, Config.Player.SeekOffsetAccurate2);
    public void SeekBackward3() => SeekBackward_(Config.Player.SeekOffset3, Config.Player.SeekOffsetAccurate3);
    public void SeekBackward4() => SeekBackward_(Config.Player.SeekOffset4, Config.Player.SeekOffsetAccurate4);
    public void SeekBackward_(long offset, bool accurate)
    {
        if (!CanPlay)
            return;

        long seekTs = curTime - (curTime % offset) - offset;

        if (Config.Player.SeekAccurate || accurate)
            SeekAccurate(Math.Max((int) (seekTs / 10000), 0));
        else
            Seek(Math.Max((int) (seekTs / 10000), 0), false);
    }

    public void SeekForward()   => SeekForward_(Config.Player.SeekOffset, Config.Player.SeekOffsetAccurate);
    public void SeekForward2()  => SeekForward_(Config.Player.SeekOffset2, Config.Player.SeekOffsetAccurate2);
    public void SeekForward3()  => SeekForward_(Config.Player.SeekOffset3, Config.Player.SeekOffsetAccurate3);
    public void SeekForward4()  => SeekForward_(Config.Player.SeekOffset4, Config.Player.SeekOffsetAccurate4);
    public void SeekForward_(long offset, bool accurate)
    {
        if (!CanPlay)
            return;

        long seekTs = curTime - (curTime % offset) + offset;

        if (seekTs > duration && !isLive)
            return;

        if (Config.Player.SeekAccurate || accurate)
            SeekAccurate((int)(seekTs / 10000));
        else
            Seek((int)(seekTs / 10000), true);
    }

    public void SeekToStart()   => Seek(0);
    public void SeekToEnd()     => Seek((int)((Duration / 10_000) - TimeSpan.FromSeconds(5).TotalMilliseconds));

    public void SeekToChapter(Demuxer.Chapter chapter) =>
        /* TODO
* Accurate pts required (backward/forward check)
* Get current chapter implementation + next/prev
*/
        Seek((int)(chapter.StartTime / 10000.0), true);

    public void CopyToClipboard()
    {
        var url = Playlist.Url;
        if (url == null)
            return;

        Clipboard.SetText(url);
        OSDMessage = $"Copy {url}";
    }
    public void CopyItemToClipboard()
    {
        if (Playlist.Selected == null || Playlist.Selected.DirectUrl == null)
            return;

        string url = Playlist.Selected.DirectUrl;

        Clipboard.SetText(url);
        OSDMessage = $"Copy {url}";
    }
    public void OpenFromClipboard()
    {
        string text = Clipboard.GetText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            OpenAsync(text);
        }
    }

    public void OpenFromClipboardSafe()
    {
        if (decoder.Playlist.Selected != null)
        {
            return;
        }

        OpenFromClipboard();
    }

    public void OpenFromFileDialog()
    {
        int prevTimeout = Activity.Timeout;
        Activity.IsEnabled = false;
        Activity.Timeout = 0;
        IsOpenFileDialogOpen = true;

        System.Windows.Forms.OpenFileDialog openFileDialog = new();

        // If there is currently an open file, set that folder as the base folder
        if (decoder.Playlist.Url != null && File.Exists(decoder.Playlist.Url))
        {
            var folder = Path.GetDirectoryName(decoder.Playlist.Url);
            if (folder != null)
                openFileDialog.InitialDirectory = folder;
        }

        var res = openFileDialog.ShowDialog();

        if (res == System.Windows.Forms.DialogResult.OK)
            OpenAsync(openFileDialog.FileName);

        Activity.Timeout = prevTimeout;
        Activity.IsEnabled = true;
        IsOpenFileDialogOpen = false;
    }

    public void ShowFrame(int frameIndex)
    {
        if (!Video.IsOpened || !CanPlay || VideoDemuxer.IsHLSLive) return;

        lock (lockActions)
        {
            Pause();
            dFrame = null;
            for (int i = 0; i < subNum; i++)
            {
                sFrames[i] = null;
                SubtitleClear(i);
            }

            decoder.Flush();
            decoder.RequiresResync = true;

            var vFrame = VideoDecoder.GetFrame(frameIndex);
            if (vFrame == null)
                return;

            if (CanDebug) Log.Debug($"SFI: {VideoDecoder.GetFrameNumber(vFrame.Timestamp)}");
            vFrames.Enqueue(vFrame, true);
            Renderer.RenderRequest(vFrame);
            UpdateCurTime(vFrame.Timestamp);
            reversePlaybackResync = true;
        }
    }

    // Whether video queue should be flushed as it could have opposite direction frames
    bool shouldFlushNext;
    bool shouldFlushPrev;
    public void ShowFrameNext()
    {
        if (!Video.IsOpened || !canPlay || VideoDemuxer.IsHLSLive)
            return;

        lock (lockActions)
        {
            Pause();

            if (status == Status.Ended)
            {
                status = Status.Paused;
                UI(() => Status = status);
            }

            shouldFlushPrev = true;
            decoder.RequiresResync = true;

            if (shouldFlushNext)
            {
                decoder.StopThreads();
                decoder.Flush();
                shouldFlushNext = false;

                VideoDecoder.GetFrame(VideoDecoder.GetFrameNumber(curTime))?.Dispose();
            }

            for (int i = 0; i < subNum; i++)
            {
                sFrames[i] = null;
                SubtitleClear(i);
            }

            if (!vFrames.TryDequeue(out var vFrame))
            {
                Renderer.Frames.PushCurrentToLast();
                vFrame = VideoDecoder.GetFrameNext();
                if (vFrame == null) return;
                vFrames.Enqueue(vFrame, true);
            }

            if (CanDebug) Log.Debug($"SFN: {VideoDecoder.GetFrameNumber(vFrame.Timestamp)}");

            Renderer.RenderRequest(vFrame);
            UpdateCurTime(vFrame.Timestamp);
            reversePlaybackResync = true;
        }
    }
    public void ShowFramePrev()
    {
        if (!Video.IsOpened || !canPlay || VideoDemuxer.IsHLSLive)
            return;

        lock (lockActions)
        {
            Pause();

            if (status == Status.Ended)
            {
                status = Status.Paused;
                UI(() => Status = status);
            }

            shouldFlushNext = true;
            decoder.RequiresResync = true;

            if (shouldFlushPrev)
            {
                decoder.StopThreads();
                decoder.Flush();
                shouldFlushPrev = false;
            }

            for (int i = 0; i < subNum; i++)
            {
                sFrames[i] = null;
                SubtitleClear(i);
            }

            if (!vFrames.TryDequeue(out var vFrame))
            {
                reversePlaybackResync = true; // Temp fix for previous timestamps until we seperate GetFrame for Extractor and the Player
                Renderer.Frames.PushCurrentToLast();
                vFrame = VideoDecoder.GetFrame(VideoDecoder.GetFrameNumber(CurTime) - 1, true);
                if (vFrame == null) return;
                vFrames.Enqueue(vFrame, true);
            }

            if (CanDebug) Log.Debug($"SFB: {VideoDecoder.GetFrameNumber(vFrame.Timestamp)}");

            Renderer.RenderRequest(vFrame);
            UpdateCurTime(vFrame.Timestamp);
        }
    }

    public void SpeedUp()       => Speed += Config.Player.SpeedOffset;
    public void SpeedUp2()      => Speed += Config.Player.SpeedOffset2;
    public void SpeedDown()     => Speed -= Config.Player.SpeedOffset;
    public void SpeedDown2()    => Speed -= Config.Player.SpeedOffset2;

    public void FullScreen()    => Host?.Player_SetFullScreen(true);
    public void NormalScreen()  => Host?.Player_SetFullScreen(false);
    public void ToggleFullScreen()
    {
        if (Host == null)
            return;

        if (Host.Player_GetFullScreen())
            Host.Player_SetFullScreen(false);
        else
            Host.Player_SetFullScreen(true);
    }

    /// <summary>
    /// Starts recording (uses Config.Player.FolderRecordings and default filename title_curTime)
    /// </summary>
    public void StartRecording()
    {
        if (!CanPlay)
            return;
        try
        {
            if (!Directory.Exists(Config.Player.FolderRecordings))
                Directory.CreateDirectory(Config.Player.FolderRecordings);

            string filename = GetValidFileName(string.IsNullOrEmpty(Playlist.Selected.Title) ? "Record" : Playlist.Selected.Title) + $"_{new TimeSpan(CurTime):hhmmss}." + decoder.Extension;
            filename = FindNextAvailableFile(Path.Combine(Config.Player.FolderRecordings, filename));
            StartRecording(ref filename, false);
        } catch { }
    }

    /// <summary>
    /// Starts recording
    /// </summary>
    /// <param name="filename">Path of the new recording file</param>
    /// <param name="useRecommendedExtension">You can force the output container's format or use the recommended one to avoid incompatibility</param>
    public void StartRecording(ref string filename, bool useRecommendedExtension = true)
    {
        if (!CanPlay)
            return;

        OSDMessage = $"Start recording to {Path.GetFileName(filename)}";
        decoder.StartRecording(ref filename, useRecommendedExtension);
        IsRecording = decoder.IsRecording;
    }

    /// <summary>
    /// Stops recording
    /// </summary>
    public void StopRecording()
    {
        decoder.StopRecording();
        IsRecording = decoder.IsRecording;
        OSDMessage = "Stop recording";
    }
    public void ToggleRecording()
    {
        if (!CanPlay) return;

        if (IsRecording)
            StopRecording();
        else
            StartRecording();
    }

    /// <summary>
    /// <para>Saves the current video frame (encoding based on file extention .bmp, .png, .jpg)</para>
    /// <para>If filename not specified will use Config.Player.FolderSnapshots and with default filename title_frameNumber.ext (ext from Config.Player.SnapshotFormat)</para>
    /// <para>If width/height not specified will use the original size. If one of them will be set, the other one will be set based on original ratio</para>
    /// <para>If frame not specified will use the current/last frame</para>
    /// </summary>
    /// <param name="filename">Specify the filename (null: will use Config.Player.FolderSnapshots and with default filename title_frameNumber.ext (ext from Config.Player.SnapshotFormat)</param>
    /// <param name="width">Specify the width (0: will keep the ratio based on height)</param>
    /// <param name="height">Specify the height (0: will keep the ratio based on width)</param>
    /// <exception cref="Exception"></exception>
    public void TakeSnapshotToFile(string filename = null, uint width = 0, uint height = 0)
    {
        if (!CanPlay)
            return;

        if (filename == null)
        {
            try
            {
                if (!Directory.Exists(Config.Player.FolderSnapshots))
                    Directory.CreateDirectory(Config.Player.FolderSnapshots);

                // TBR: if frame is specified we don't know the frame's number
                filename = GetValidFileName(string.IsNullOrEmpty(Playlist.Selected.Title) ? "Snapshot" : Playlist.Selected.Title) + $"_{VideoDecoder.GetFrameNumber(CurTime).ToString()}.{Config.Player.SnapshotFormat}";
                filename = FindNextAvailableFile(Path.Combine(Config.Player.FolderSnapshots, filename));
            } catch { return; }
        }

        string ext = GetUrlExtention(filename);

        var imageFormat = ext switch
        {
            "bmp"           => ImageFormat.Bmp,
            "png"           => ImageFormat.Png,
            "jpg" or "jpeg" => ImageFormat.Jpeg,
            _ => throw new($"Invalid snapshot extention '{ext}' (valid .bmp, .png, .jpeg, .jpg"),
        };

        var snapshotBitmap = Renderer.TakeSnapshot(width, height);
        if (snapshotBitmap == null)
            return;

        try
        {
            snapshotBitmap.Save(filename, imageFormat);

            UI(() =>
            {
                OSDMessage = $"Save snapshot to {Path.GetFileName(filename)}";
            });
        }
        catch (Exception)
        {
            snapshotBitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <para>Returns a bitmap of the current or specified video frame</para>
    /// <para>If width/height not specified will use the original size. If one of them will be set, the other one will be set based on original ratio</para>
    /// <para>If frame not specified will use the current/last frame</para>
    /// </summary>
    /// <param name="width">Specify the width (0: will keep the ratio based on height)</param>
    /// <param name="height">Specify the height (0: will keep the ratio based on width)</param>
    /// <returns></returns>
    public System.Drawing.Bitmap TakeSnapshotToBitmap(uint width = 0, uint height = 0) => Renderer?.TakeSnapshot(width, height);

    /// <summary>
    /// <para>Returns a bitmap of the current or specified video frame</para>
    /// <para>If width/height not specified will use the original size. If one of them will be set, the other one will be set based on original ratio</para>
    /// <para>If frame not specified will use the current/last frame</para>
    /// </summary>
    /// <param name="width">Specify the width (0: will keep the ratio based on height)</param>
    /// <param name="height">Specify the height (0: will keep the ratio based on width)</param>
    /// <returns></returns>
    public System.Windows.Media.Imaging.BitmapSource TakeSnapshotToBitmapSource(uint width = 0, uint height = 0) => Renderer?.TakeSnapshotBitmapSource(width, height);

    public void ResetAll()
    {
        ReversePlayback = false;

        ResetDelay();
        ResetSpeed();
        // reset zoom and rotation at once
        Config.Video.ResetViewport();
    }

    public void ResetSpeed()
    {
        Speed = 1;
    }

    public void ResetDelay()
    {
        Config.Audio.Delay = 0;
        for (int i = 0; i < subNum; i++)
            Config.Subtitles[i].Delay = 0;
    }

    public void ResetRotation()
    {
        Config.Video.Rotation = 0;
    }

    public void ResetZoom()
    {
        // reset without rotation
        Config.Video.ResetViewport(Config.Video.Rotation);
    }
}

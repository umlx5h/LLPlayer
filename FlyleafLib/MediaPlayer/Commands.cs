using System.Threading.Tasks;
using System.Windows.Input;

using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaFramework.MediaPlaylist;
using FlyleafLib.MediaFramework.MediaStream;

namespace FlyleafLib.MediaPlayer;

public class Commands
{
    public ICommand AudioDelaySet           { get; set; }
    public ICommand AudioDelaySet2          { get; set; }
    public ICommand AudioDelayAdd           { get; set; }
    public ICommand AudioDelayAdd2          { get; set; }
    public ICommand AudioDelayRemove        { get; set; }
    public ICommand AudioDelayRemove2       { get; set; }

    public ICommand SubtitlesDelaySet       { get; set; }
    public ICommand SubtitlesDelaySet2      { get; set; }
    public ICommand SubtitlesDelayAdd       { get; set; }
    public ICommand SubtitlesDelayAdd2      { get; set; }
    public ICommand SubtitlesDelayRemove    { get; set; }
    public ICommand SubtitlesDelayRemove2   { get; set; }

    public ICommand Open                    { get; set; }
    public ICommand OpenFromClipboard       { get; set; }
    public ICommand OpenFromFileDialog      { get; set; }
    public ICommand Reopen                  { get; set; }

    public ICommand Play                    { get; set; }
    public ICommand Pause                   { get; set; }
    public ICommand Stop                    { get; set; }
    public ICommand TogglePlayPause         { get; set; }

    public ICommand SeekBackward            { get; set; }
    public ICommand SeekBackward2           { get; set; }
    public ICommand SeekBackward3           { get; set; }
    public ICommand SeekForward             { get; set; }
    public ICommand SeekForward2            { get; set; }
    public ICommand SeekForward3            { get; set; }
    public ICommand SeekToChapter           { get; set; }

    public ICommand ShowFramePrev           { get; set; }
    public ICommand ShowFrameNext           { get; set; }

    public ICommand NormalScreen            { get; set; }
    public ICommand FullScreen              { get; set; }
    public ICommand ToggleFullScreen        { get; set; }

    public ICommand ToggleReversePlayback   { get; set; }
    public ICommand StartRecording          { get; set; }
    public ICommand StopRecording           { get; set; }
    public ICommand ToggleRecording         { get; set; }

    public ICommand TakeSnapshot            { get; set; }
    public ICommand ZoomIn                  { get; set; }
    public ICommand ZoomOut                 { get; set; }
    public ICommand RotationSet             { get; set; }
    public ICommand RotateLeft              { get; set; }
    public ICommand RotateRight             { get; set; }
    public ICommand ResetAll                { get; set; }

    public ICommand SpeedSet                { get; set; }
    public ICommand SpeedUp                 { get; set; }
    public ICommand SpeedUp2                { get; set; }
    public ICommand SpeedDown               { get; set; }
    public ICommand SpeedDown2              { get; set; }

    public ICommand VolumeUp                { get; set; }
    public ICommand VolumeDown              { get; set; }
    public ICommand ToggleMute              { get; set; }

    public ICommand ForceIdle               { get; set; }
    public ICommand ForceActive             { get; set; }
    public ICommand ForceFullActive         { get; set; }
    public ICommand RefreshActive           { get; set; }
    public ICommand RefreshFullActive       { get; set; }

    public ICommand ResetFilter             { get; set; }

    Player player;

    public Commands(Player player)
    {
        this.player = player;

        Open                    = new RelayCommand(OpenAction);
        OpenFromClipboard       = new RelayCommandSimple(player.OpenFromClipboard);
        OpenFromFileDialog      = new RelayCommandSimple(player.OpenFromFileDialog);
        Reopen                  = new RelayCommand(ReopenAction);

        Play                    = new RelayCommandSimple(player.Play);
        Pause                   = new RelayCommandSimple(player.Pause);
        TogglePlayPause         = new RelayCommandSimple(player.TogglePlayPause);
        Stop                    = new RelayCommandSimple(player.Stop);

        SeekBackward            = new RelayCommandSimple(player.SeekBackward);
        SeekBackward2           = new RelayCommandSimple(player.SeekBackward2);
        SeekBackward3           = new RelayCommandSimple(player.SeekBackward3);
        SeekForward             = new RelayCommandSimple(player.SeekForward);
        SeekForward2            = new RelayCommandSimple(player.SeekForward2);
        SeekForward3            = new RelayCommandSimple(player.SeekForward3);
        SeekToChapter           = new RelayCommand(SeekToChapterAction);

        ShowFrameNext           = new RelayCommandSimple(player.ShowFrameNext);
        ShowFramePrev           = new RelayCommandSimple(player.ShowFramePrev);

        NormalScreen            = new RelayCommandSimple(player.NormalScreen);
        FullScreen              = new RelayCommandSimple(player.FullScreen);
        ToggleFullScreen        = new RelayCommandSimple(player.ToggleFullScreen);

        ToggleReversePlayback   = new RelayCommandSimple(player.ToggleReversePlayback);
        StartRecording          = new RelayCommandSimple(player.StartRecording);
        StopRecording           = new RelayCommandSimple(player.StopRecording);
        ToggleRecording         = new RelayCommandSimple(player.ToggleRecording);

        TakeSnapshot            = new RelayCommandSimple(TakeSnapshotAction);
        ZoomIn                  = new RelayCommandSimple(player.ZoomIn);
        ZoomOut                 = new RelayCommandSimple(player.ZoomOut);
        RotationSet             = new RelayCommand(RotationSetAction);
        RotateLeft              = new RelayCommandSimple(player.RotateLeft);
        RotateRight             = new RelayCommandSimple(player.RotateRight);
        ResetAll                = new RelayCommandSimple(player.ResetAll);

        SpeedSet                = new RelayCommand(SpeedSetAction);
        SpeedUp                 = new RelayCommandSimple(player.SpeedUp);
        SpeedDown               = new RelayCommandSimple(player.SpeedDown);
        SpeedUp2                = new RelayCommandSimple(player.SpeedUp2);
        SpeedDown2              = new RelayCommandSimple(player.SpeedDown2);

        VolumeUp                = new RelayCommandSimple(player.Audio.VolumeUp);
        VolumeDown              = new RelayCommandSimple(player.Audio.VolumeDown);
        ToggleMute              = new RelayCommandSimple(player.Audio.ToggleMute);

        AudioDelaySet           = new RelayCommand(AudioDelaySetAction);
        AudioDelaySet2          = new RelayCommand(AudioDelaySetAction2);
        AudioDelayAdd           = new RelayCommandSimple(player.Audio.DelayAdd);
        AudioDelayAdd2          = new RelayCommandSimple(player.Audio.DelayAdd2);
        AudioDelayRemove        = new RelayCommandSimple(player.Audio.DelayRemove);
        AudioDelayRemove2       = new RelayCommandSimple(player.Audio.DelayRemove2);

        SubtitlesDelaySet       = new RelayCommand(SubtitlesDelaySetAction);
        SubtitlesDelaySet2      = new RelayCommand(SubtitlesDelaySetAction2);
        SubtitlesDelayAdd       = new RelayCommandSimple(player.Subtitles.DelayAdd);
        SubtitlesDelayAdd2      = new RelayCommandSimple(player.Subtitles.DelayAdd2);
        SubtitlesDelayRemove    = new RelayCommandSimple(player.Subtitles.DelayRemove);
        SubtitlesDelayRemove2   = new RelayCommandSimple(player.Subtitles.DelayRemove2);

        ForceIdle               = new RelayCommandSimple(player.Activity.ForceIdle);
        ForceActive             = new RelayCommandSimple(player.Activity.ForceActive);
        ForceFullActive         = new RelayCommandSimple(player.Activity.ForceFullActive);
        RefreshActive           = new RelayCommandSimple(player.Activity.RefreshActive);
        RefreshFullActive       = new RelayCommandSimple(player.Activity.RefreshFullActive);

        ResetFilter             = new RelayCommand(ResetFilterAction);
    }

    private void RotationSetAction(object obj)
        => player.Rotation = uint.Parse(obj.ToString());

    private void ResetFilterAction(object filter)
        => player.Config.Video.Filters[(VideoFilters)filter].Value = player.Config.Video.Filters[(VideoFilters)filter].DefaultValue;

    public void SpeedSetAction(object speed)
    {
        string speedstr = speed.ToString().Replace(',', '.');
        if (double.TryParse(speedstr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double value))
            player.Speed = value;
    }

    public void AudioDelaySetAction(object delay)
        => player.Config.Audio.Delay = int.Parse(delay.ToString()) * (long)10000;
    public void AudioDelaySetAction2(object delay)
        => player.Config.Audio.Delay += int.Parse(delay.ToString()) * (long)10000;
    public void SubtitlesDelaySetAction(object delay)
        => player.Config.Subtitles.Delay = int.Parse(delay.ToString()) * (long)10000;
    public void SubtitlesDelaySetAction2(object delay)
        => player.Config.Subtitles.Delay += int.Parse(delay.ToString()) * (long)10000;

    public void TakeSnapshotAction() => Task.Run(() => { try { player.TakeSnapshotToFile(); } catch { } });

    public void SeekToChapterAction(object chapter)
    {
        if (player.Chapters == null || player.Chapters.Count == 0)
            return;

        if (chapter is MediaFramework.MediaDemuxer.Demuxer.Chapter)
            player.SeekToChapter((MediaFramework.MediaDemuxer.Demuxer.Chapter)chapter);
        else if (int.TryParse(chapter.ToString(), out int chapterId) && chapterId < player.Chapters.Count)
            player.SeekToChapter(player.Chapters[chapterId]);
    }

    public void OpenAction(object input)
    {
        if (input == null)
            return;

        if (input is StreamBase)
            player.OpenAsync((StreamBase)input);
        else if (input is PlaylistItem)
            player.OpenAsync((PlaylistItem)input);
        else if (input is ExternalStream)
            player.OpenAsync((ExternalStream)input);
        else if (input is System.IO.Stream)
            player.OpenAsync((System.IO.Stream)input);
        else
            player.OpenAsync(input.ToString());
    }

    public void ReopenAction(object playlistItem)
    {
        if (playlistItem == null)
            return;

        PlaylistItem item = (PlaylistItem)playlistItem;
        if (item.OpenedCounter > 0)
        {
            var session = player.GetSession(item);
            session.isReopen = true;
            session.CurTime = 0;

            // TBR: in case of disabled audio/video/subs it will save the session with them to be disabled

            // TBR: This can cause issues and it might not useful either
            //if (session.CurTime < 60 * (long)1000 * 10000)
            //    session.CurTime = 0;

            player.OpenAsync(session);
        }
        else
            player.OpenAsync(item);
    }
}

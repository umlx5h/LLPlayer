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

    public ICommand SubtitlesDelaySetPrimary       { get; set; }
    public ICommand SubtitlesDelaySet2Primary      { get; set; }
    public ICommand SubtitlesDelayAddPrimary       { get; set; }
    public ICommand SubtitlesDelayAdd2Primary      { get; set; }
    public ICommand SubtitlesDelayRemovePrimary    { get; set; }
    public ICommand SubtitlesDelayRemove2Primary   { get; set; }

    public ICommand SubtitlesDelaySetSecondary     { get; set; }
    public ICommand SubtitlesDelaySet2Secondary    { get; set; }
    public ICommand SubtitlesDelayAddSecondary     { get; set; }
    public ICommand SubtitlesDelayAdd2Secondary    { get; set; }
    public ICommand SubtitlesDelayRemoveSecondary  { get; set; }
    public ICommand SubtitlesDelayRemove2Secondary { get; set; }

    public ICommand OpenSubtitles           { get; set; }
    public ICommand OpenSubtitlesASR        { get; set; }
    public ICommand SubtitlesOff            { get; set; }

    public ICommand Open                    { get; set; }
    public ICommand OpenFromClipboard       { get; set; }
    public ICommand OpenFromFileDialog      { get; set; }
    public ICommand Reopen                  { get; set; }
    public ICommand CopyToClipboard         { get; set; }
    public ICommand CopyItemToClipboard     { get; set; }

    public ICommand Play                    { get; set; }
    public ICommand Pause                   { get; set; }
    public ICommand Stop                    { get; set; }
    public ICommand TogglePlayPause         { get; set; }

    public ICommand SeekBackward            { get; set; }
    public ICommand SeekBackward2           { get; set; }
    public ICommand SeekBackward3           { get; set; }
    public ICommand SeekBackward4           { get; set; }
    public ICommand SeekForward             { get; set; }
    public ICommand SeekForward2            { get; set; }
    public ICommand SeekForward3            { get; set; }
    public ICommand SeekForward4            { get; set; }
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
    public ICommand ResetSpeed              { get; set; }
    public ICommand ResetRotation           { get; set; }
    public ICommand ResetZoom               { get; set; }

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
        CopyToClipboard         = new RelayCommandSimple(player.CopyToClipboard);
        CopyItemToClipboard     = new RelayCommandSimple(player.CopyItemToClipboard);

        Play                    = new RelayCommandSimple(player.Play);
        Pause                   = new RelayCommandSimple(player.Pause);
        TogglePlayPause         = new RelayCommandSimple(player.TogglePlayPause);
        Stop                    = new RelayCommandSimple(player.Stop);

        SeekBackward            = new RelayCommandSimple(player.SeekBackward);
        SeekBackward2           = new RelayCommandSimple(player.SeekBackward2);
        SeekBackward3           = new RelayCommandSimple(player.SeekBackward3);
        SeekBackward4           = new RelayCommandSimple(player.SeekBackward4);
        SeekForward             = new RelayCommandSimple(player.SeekForward);
        SeekForward2            = new RelayCommandSimple(player.SeekForward2);
        SeekForward3            = new RelayCommandSimple(player.SeekForward3);
        SeekForward4            = new RelayCommandSimple(player.SeekForward4);
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
        ResetSpeed              = new RelayCommandSimple(player.ResetSpeed);
        ResetRotation           = new RelayCommandSimple(player.ResetRotation);
        ResetZoom               = new RelayCommandSimple(player.ResetZoom);

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

        SubtitlesDelaySetPrimary       = new RelayCommand(SubtitlesDelaySetActionPrimary);
        SubtitlesDelaySet2Primary      = new RelayCommand(SubtitlesDelaySetAction2Primary);
        SubtitlesDelayAddPrimary       = new RelayCommandSimple(player.Subtitles.DelayAddPrimary);
        SubtitlesDelayAdd2Primary      = new RelayCommandSimple(player.Subtitles.DelayAdd2Primary);
        SubtitlesDelayRemovePrimary    = new RelayCommandSimple(player.Subtitles.DelayRemovePrimary);
        SubtitlesDelayRemove2Primary   = new RelayCommandSimple(player.Subtitles.DelayRemove2Primary);

        SubtitlesDelaySetSecondary     = new RelayCommand(SubtitlesDelaySetActionSecondary);
        SubtitlesDelaySet2Secondary    = new RelayCommand(SubtitlesDelaySetAction2Secondary);
        SubtitlesDelayAddSecondary     = new RelayCommandSimple(player.Subtitles.DelayAddSecondary);
        SubtitlesDelayAdd2Secondary    = new RelayCommandSimple(player.Subtitles.DelayAdd2Secondary);
        SubtitlesDelayRemoveSecondary  = new RelayCommandSimple(player.Subtitles.DelayRemoveSecondary);
        SubtitlesDelayRemove2Secondary = new RelayCommandSimple(player.Subtitles.DelayRemove2Secondary);

        OpenSubtitles           = new RelayCommand(OpenSubtitlesAction);
        OpenSubtitlesASR        = new RelayCommand(OpenSubtitlesASRAction);
        SubtitlesOff            = new RelayCommand(SubtitlesOffAction);

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

    public void SubtitlesDelaySetActionPrimary(object delay)
        => player.Config.Subtitles[0].Delay = int.Parse(delay.ToString()) * (long)10000;
    public void SubtitlesDelaySetAction2Primary(object delay)
        => player.Config.Subtitles[0].Delay += int.Parse(delay.ToString()) * (long)10000;

    // TODO: L: refactor
    public void SubtitlesDelaySetActionSecondary(object delay)
        => player.Config.Subtitles[1].Delay = int.Parse(delay.ToString()) * (long)10000;
    public void SubtitlesDelaySetAction2Secondary(object delay)
        => player.Config.Subtitles[1].Delay += int.Parse(delay.ToString()) * (long)10000;


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

    public void OpenSubtitlesAction(object input)
    {
        if (input is not ValueTuple<object, object, object> tuple)
        {
            return;
        }

        if (tuple is { Item1: string, Item2: SubtitlesStream, Item3: SelectSubMethod })
        {
            var subIndex = int.Parse((string)tuple.Item1);
            var stream = (SubtitlesStream)tuple.Item2;
            var selectSubMethod = (SelectSubMethod)tuple.Item3;

            if (selectSubMethod == SelectSubMethod.OCR)
            {
                if (!TryInitializeOCR(subIndex, stream.Language))
                {
                    return;
                }
            }

            SubtitlesSelectedHelper.Set(subIndex, (stream.StreamIndex, null));
            SubtitlesSelectedHelper.SetMethod(subIndex, selectSubMethod);
            SubtitlesSelectedHelper.CurIndex = subIndex;
        }
        else if (tuple is { Item1: string, Item2: ExternalSubtitlesStream, Item3: SelectSubMethod })
        {
            var subIndex = int.Parse((string)tuple.Item1);
            var stream = (ExternalSubtitlesStream)tuple.Item2;
            var selectSubMethod = (SelectSubMethod)tuple.Item3;

            if (selectSubMethod == SelectSubMethod.OCR)
            {
                if (!TryInitializeOCR(subIndex, stream.Language))
                {
                    return;
                }
            }

            SubtitlesSelectedHelper.Set(subIndex, (null, stream));
            SubtitlesSelectedHelper.SetMethod(subIndex, selectSubMethod);
            SubtitlesSelectedHelper.CurIndex = subIndex;
        }

        OpenAction(tuple.Item2);
        return;

        bool TryInitializeOCR(int subIndex, Language lang)
        {
            if (!player.SubtitlesOCR.TryInitialize(subIndex, lang, out string err))
            {
                player.RaiseKnownErrorOccurred(err, KnownErrorType.Configuration);
                return false;
            }

            return true;
        }
    }

    public void OpenSubtitlesASRAction(object input)
    {
        if (!int.TryParse(input.ToString(), out var subIndex))
        {
            return;
        }

        if (!player.SubtitlesASR.CanExecute(out string err))
        {
            player.RaiseKnownErrorOccurred(err, KnownErrorType.Configuration);

            return;
        }

        SubtitlesSelectedHelper.CurIndex = subIndex;

        int otherIndex = (subIndex + 1) % 2;

        // First, turn off existing subtitles
        player.Subtitles[subIndex].Disable();

        // Cancel one of the ASRs since simultaneous ASR execution is not allowed
        // (actual cancellation is done in SubtitlesASR)
        player.Subtitles[otherIndex].EnabledASR = false;
        player.Subtitles[subIndex].EnableASR();
    }

    public void SubtitlesOffAction(object input)
    {
        if (int.TryParse(input.ToString(), out var subIndex))
        {
            SubtitlesSelectedHelper.CurIndex = subIndex;
            player.Subtitles[subIndex].Disable();
        }
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

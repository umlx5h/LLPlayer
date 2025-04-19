using System.ComponentModel;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using LLPlayer.Extensions;
using LLPlayer.Services;

namespace LLPlayer.ViewModels;

public class SubtitlesSidebarVM : Bindable, IDisposable
{
    private string _subtitleSearchText = string.Empty;
    public string SubtitleSearchText
    {
        get => _subtitleSearchText;
        set
        {
            if (Set(ref _subtitleSearchText, value))
            {
                FilterSubtitles();
            }
        }
    }

    public DelegateCommand CmdSearchSubtitle => field ??= new(() =>
    {
        FilterSubtitles();
    });

    private void FilterSubtitles()
    {
        if (SubManager == null) return;
        if (string.IsNullOrWhiteSpace(SubtitleSearchText))
        {
            SubManager.RestoreAllSubs();
        }
        else
        {
            var query = SubtitleSearchText.Trim().ToLower();
            SubManager.SetFilteredSubs(SubManager.AllSubs.Where(s => !string.IsNullOrEmpty(s.Text) && s.Text.ToLower().Contains(query)));
        }
    }

    public FlyleafManager FL { get; }

    // Call this after loading new subtitles to backup the full list
    public void BackupAllSubs()
    {
        SubManager?.BackupAllSubs();
    }

    public SubtitlesSidebarVM(FlyleafManager fl)
    {
        FL = fl;

        FL.Config.PropertyChanged += OnConfigOnPropertyChanged;

        // Attach to SubManager.Subs changes to keep AllSubs up to date
        bool allSubsBackedUp = false;
        SubManager.Subs.CollectionChanged += (s, e) =>
        {
            // Only backup once, when Subs is first populated (avoid overwriting AllSubs with filtered results)
            if (!allSubsBackedUp && SubManager.Subs.Count > 0)
            {
                SubManager.BackupAllSubs();
                allSubsBackedUp = true;
            }
            // Reset flag if all subtitles are cleared (e.g. on new file load)
            if (SubManager.Subs.Count == 0)
            {
                allSubsBackedUp = false;
            }
        };
    }

    private void OnConfigOnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(FL.Config.SidebarShowSecondary):
                OnPropertyChanged(nameof(SubIndex));
                OnPropertyChanged(nameof(SubManager));
                break;
            case nameof(FL.Config.SidebarShowOriginalText):
                // Update ListBox
                OnPropertyChanged(nameof(SubManager));
                break;
        }
    }

    public void Dispose()
    {
        FL.Config.PropertyChanged -= OnConfigOnPropertyChanged;
    }

    public int SubIndex => !FL.Config.SidebarShowSecondary ? 0 : 1;

    // Expose filtered subtitles if available
public SubManager SubManager => FL.Player.SubtitlesManager[SubIndex];


    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    // TODO: L: Fix implicit changes to reflect
    public DelegateCommand<string> CmdSubFontSizeChange => field ??= new(increase =>
    {
        if (int.TryParse(increase, out var value))
        {
            FL.Config.SidebarFontSize += value;
        }
    });

    public DelegateCommand CmdSubTextMaskToggle => field ??= new(() =>
    {
        FL.Config.SidebarTextMask = !FL.Config.SidebarTextMask;

        // Update ListBox
        OnPropertyChanged(nameof(SubManager));
    });

    public DelegateCommand CmdShowDownloadSubsDialog => field ??= new(() =>
    {
        FL.Action.CmdOpenWindowSubsDownloader.Execute();
    });

    public DelegateCommand CmdShowExportSubsDialog => field ??= new(() =>
    {
        FL.Action.CmdOpenWindowSubsExporter.Execute();
    });

    public DelegateCommand CmdSwapSidebarPosition => field ??= new(() =>
    {
        FL.Config.SidebarLeft = !FL.Config.SidebarLeft;
    });

    public DelegateCommand<int?> CmdSubPlay => field ??= new((index) =>
    {
        if (!index.HasValue)
        {
            return;
        }

        // If paused, start playing after seek
        if (!FL.Player.IsPlaying)
        {
            FL.Player.SeekCompleted += PlayerOnSeekCompleted;

            void PlayerOnSeekCompleted(object? sender, int args)
            {
                FL.Player.SeekCompleted -= PlayerOnSeekCompleted;

                if (args != -1)
                {
                    Utils.UI(() =>
                    {
                        if (!FL.Player.IsPlaying)
                        {
                            FL.Player.Play();
                        }
                    });
                }
            }
        }

        var sub = SubManager.Subs[index.Value];
        FL.Player.SeekAccurate(sub.StartTime, SubIndex);
    });

    public DelegateCommand<int?> CmdSubSync => field ??= new((index) =>
    {
        if (!index.HasValue)
        {
            return;
        }

        var sub = SubManager.Subs[index.Value];
        var newDelay = FL.Player.CurTime - sub.StartTime.Ticks;

        // Delay's OSD is not displayed, temporarily set to Active
        FL.Player.Activity.ForceActive();

        FL.PlayerConfig.Subtitles[SubIndex].Delay = newDelay;
    });

    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
}


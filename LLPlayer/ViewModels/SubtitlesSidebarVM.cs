using System.ComponentModel;
using System.Windows.Data;
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
                DebounceFilter();
            }
        }
    }

    private bool _isSearchActive;
    public bool IsSearchActive
    {
        get => _isSearchActive;
        set => Set(ref _isSearchActive, value);
    }

    public DelegateCommand CmdShowSearchInput => field ??= new(() =>
    {
        IsSearchActive = true;
        // Focus will be handled in code-behind
    });

    public DelegateCommand CmdClearSearch => field ??= new(() =>
    {
        SubtitleSearchText = string.Empty;
        IsSearchActive = false;
    });

    // Debounce logic
    private System.Timers.Timer? _debounceTimer;
    private void DebounceFilter()
    {
        _debounceTimer?.Stop();
        _debounceTimer = _debounceTimer ?? new System.Timers.Timer(300);
        _debounceTimer.Interval = 300;
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += (s, e) =>
        {
            _debounceTimer?.Stop();
            App.Current.Dispatcher.Invoke(FilterSubtitles);
        };
        _debounceTimer.Start();
    }

    public DelegateCommand CmdSearchSubtitle => field ??= new(() =>
    {
        FilterSubtitles();
    });

    public ICollectionView FilteredSubs { get; private set; }

    private void FilterSubtitles()
    {
        if (FilteredSubs == null) return;
        var search = SubtitleSearchText.Trim();
        FilteredSubs.Filter = obj =>
        {
            if (obj is not SubtitleData sub)
                return false;
            if (string.IsNullOrWhiteSpace(search))
                return true;
            return sub.Text?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        };
        FilteredSubs.Refresh();
    }

    public FlyleafManager FL { get; }



    public SubtitlesSidebarVM(FlyleafManager fl)
    {
        FL = fl;

        FL.Config.PropertyChanged += OnConfigOnPropertyChanged;

        // Initialize filtered view for the sidebar
        FilteredSubs = CollectionViewSource.GetDefaultView(SubManager.Subs);
        FilterSubtitles();
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

        if (index.Value < 0 || index.Value >= SubManager.Subs.Count)
        {
            ErrorDialogHelper.ShowKnownErrorPopup("Subtitle index out of range.", "Subtitle Play");
            return;
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

        if (index.Value < 0 || index.Value >= SubManager.Subs.Count)
        {
            ErrorDialogHelper.ShowKnownErrorPopup("Subtitle index out of range.", "Subtitle Sync");
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


using System.ComponentModel;
using System.Windows.Data;
using FlyleafLib.MediaPlayer;
using LLPlayer.Extensions;
using LLPlayer.Services;

namespace LLPlayer.ViewModels;

public class SubtitlesSidebarVM : Bindable, IDisposable
{
    public FlyleafManager FL { get; }

    public SubtitlesSidebarVM(FlyleafManager fl)
    {
        FL = fl;

        FL.Config.PropertyChanged += OnConfigOnPropertyChanged;

        // Initialize filtered view for the sidebar
        for (int i = 0; i < _filteredSubs.Length; i++)
        {
            // TODO: L: Address issue of incorrect SelectedIndex during filtering
            _filteredSubs[i] = CollectionViewSource.GetDefaultView(FL.Player.SubtitlesManager[i].Subs);
        }
    }

    public void Dispose()
    {
        FL.Config.PropertyChanged -= OnConfigOnPropertyChanged;
    }

    private void OnConfigOnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(FL.Config.SidebarShowSecondary):
                OnPropertyChanged(nameof(SubIndex));
                OnPropertyChanged(nameof(SubManager));

                // prevent unnecessary filter update
                if (_lastSearchText[SubIndex] != _trimSearchText)
                {
                    ApplyFilter(); // ensure filter applied
                }
                break;
            case nameof(FL.Config.SidebarShowOriginalText):
                // Update ListBox
                OnPropertyChanged(nameof(SubManager));

                if (_trimSearchText.Length != 0)
                {
                    // because of switch between Text and DisplayText
                    ApplyFilter();
                }
                break;
        }
    }

    public int SubIndex => !FL.Config.SidebarShowSecondary ? 0 : 1;

    public SubManager SubManager => FL.Player.SubtitlesManager[SubIndex];

    private readonly ICollectionView[] _filteredSubs = new ICollectionView[2];

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                DebounceFilter();
            }
        }
    }

    private string _trimSearchText = string.Empty; // for performance

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

    public DelegateCommand CmdClearSearch => field ??= new(() =>
    {
        Set(ref _searchText, string.Empty, nameof(SearchText));
        _trimSearchText = string.Empty;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;

        for (int i = 0; i < _filteredSubs.Length; i++)
        {
            _lastSearchText[i] = string.Empty;
            _filteredSubs[i].Filter = null; // remove filter completely
            _filteredSubs[i].Refresh();
        }

        FL.Config.SidebarSearchActive = false;

        // move focus to video and enable keybindings
        FL.FlyleafHost!.Surface.Focus();
        // for scrolling to current sub
        // TODO: L: not working sometimes?
        SubManager.RaisePropertyChanged(nameof(SubManager.CurrentIndex));
    });

    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    // Debounce logic
    private CancellationTokenSource? _debounceCts;
    private async void DebounceFilter()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            await Task.Delay(300, token); // 300ms debounce

            if (!token.IsCancellationRequested)
            {
                ApplyFilter();
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    private readonly string[] _lastSearchText = [string.Empty, string.Empty];

    private void ApplyFilter()
    {
        _trimSearchText = SearchText.Trim();
        _lastSearchText[SubIndex] = _trimSearchText;

        // initialize filter lazily
        _filteredSubs[SubIndex].Filter ??= SubFilter;
        _filteredSubs[SubIndex].Refresh();
    }

    private bool SubFilter(object obj)
    {
        if (_trimSearchText.Length == 0) return true;
        if (obj is not SubtitleData sub) return false;

        string? source = FL.Config.SidebarShowOriginalText ? sub.Text : sub.DisplayText;
        return source?.IndexOf(_trimSearchText, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

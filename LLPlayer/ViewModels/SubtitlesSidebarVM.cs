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
            _filteredSubs[i] = (ListCollectionView)CollectionViewSource.GetDefaultView(FL.Player.SubtitlesManager[i].Subs);
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

    private readonly ListCollectionView[] _filteredSubs = new ListCollectionView[2];

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

    public string HitCount { get; set => Set(ref field, value); } = string.Empty;

    public event EventHandler? RequestScrollToTop;

    // TODO: L: Fix implicit changes to reflect
    public DelegateCommand<string>? CmdSubFontSizeChange => field ??= new(increase =>
    {
        if (int.TryParse(increase, out var value))
        {
            FL.Config.SidebarFontSize += value;
        }
    });

    public DelegateCommand? CmdSubTextMaskToggle => field ??= new(() =>
    {
        FL.Config.SidebarTextMask = !FL.Config.SidebarTextMask;

        // Update ListBox
        OnPropertyChanged(nameof(SubManager));
    });

    public DelegateCommand? CmdShowDownloadSubsDialog => field ??= new(() =>
    {
        FL.Action.CmdOpenWindowSubsDownloader.Execute();
    });

    public DelegateCommand? CmdShowExportSubsDialog => field ??= new(() =>
    {
        FL.Action.CmdOpenWindowSubsExporter.Execute();
    });

    public DelegateCommand? CmdSwapSidebarPosition => field ??= new(() =>
    {
        FL.Config.SidebarLeft = !FL.Config.SidebarLeft;
    });

    public DelegateCommand<int?>? CmdSubPlay => field ??= new((index) =>
    {
        if (!index.HasValue)
        {
            return;
        }

        var sub = SubManager.Subs[index.Value];
        FL.Player.SeekAccurate(sub.StartTime, SubIndex);
    });

    public DelegateCommand<int?>? CmdSubSync => field ??= new((index) =>
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

    public DelegateCommand? CmdClearSearch => field ??= new(() =>
    {
        if (!FL.Config.SidebarSearchActive)
            return;

        Set(ref _searchText, string.Empty, nameof(SearchText));
        _trimSearchText = string.Empty;
        HitCount = string.Empty;

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
        var prev = SubManager.SelectedSub;
        SubManager.SelectedSub = null;
        SubManager.SelectedSub = prev;
    });

    public DelegateCommand? CmdNextMatch => field ??= new(() =>
    {
        if (_filteredSubs[SubIndex].IsEmpty)
            return;

        if (!_filteredSubs[SubIndex].MoveCurrentToNext())
        {
            // if last, move to first
            _filteredSubs[SubIndex].MoveCurrentToFirst();
        }

        var nextItem = (SubtitleData)_filteredSubs[SubIndex].CurrentItem;
        if (nextItem != null)
        {
            FL.Player.SeekAccurate(nextItem.StartTime, SubIndex);
            FL.Player.Activity.RefreshFullActive();
        }
    });

    public DelegateCommand? CmdPrevMatch => field ??= new(() =>
    {
        if (_filteredSubs[SubIndex].IsEmpty)
            return;

        if (!_filteredSubs[SubIndex].MoveCurrentToPrevious())
        {
            // if first, move to last
            _filteredSubs[SubIndex].MoveCurrentToLast();
        }

        var prevItem = (SubtitleData)_filteredSubs[SubIndex].CurrentItem;
        if (prevItem != null)
        {
            FL.Player.SeekAccurate(prevItem.StartTime, SubIndex);
            FL.Player.Activity.RefreshFullActive();
        }
    });

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

        int count = _filteredSubs[SubIndex].Count;
        HitCount = count > 0 ? $"{count} hits" : "No hits";

        if (SubManager.SelectedSub != null && _filteredSubs[SubIndex].MoveCurrentTo(SubManager.SelectedSub))
        {
            // scroll to current playing item
            var prev = SubManager.SelectedSub;
            SubManager.SelectedSub = null;
            SubManager.SelectedSub = prev;
        }
        else
        {
            // scroll to top
            if (!_filteredSubs[SubIndex].IsEmpty)
            {
                RequestScrollToTop?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool SubFilter(object obj)
    {
        if (_trimSearchText.Length == 0) return true;
        if (obj is not SubtitleData sub) return false;

        string? source = FL.Config.SidebarShowOriginalText ? sub.Text : sub.DisplayText;
        return source?.IndexOf(_trimSearchText, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

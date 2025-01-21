using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using FlyleafLib;
using LLPlayer.Extensions;
using LLPlayer.Services;
using LLPlayer.Views;
using Whisper.net.LibraryLoader;

namespace LLPlayer.Controls.Settings;

public partial class SettingsSubtitlesASR : UserControl
{
    public SettingsSubtitlesASR()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SettingsSubtitlesASRVM>();
    }
}

public class SettingsSubtitlesASRVM : Bindable
{
    public FlyleafManager FL { get; }
    private readonly IDialogService _dialogService;

    public SettingsSubtitlesASRVM(FlyleafManager fl, IDialogService dialogService)
    {
        FL = fl;
        _dialogService = dialogService;

        LoadDownloadedModels();

        foreach (RuntimeLibrary library in FL.PlayerConfig.Subtitles.WhisperRuntimeLibraries)
        {
            SelectedLibraries.Add(library);
        }

        foreach (RuntimeLibrary library in Enum.GetValues<RuntimeLibrary>().Where(l => l != RuntimeLibrary.CoreML))
        {
            if (!SelectedLibraries.Contains(library))
            {
                AvailableLibraries.Add(library);
            }
        }
        SelectedLibraries.CollectionChanged += SelectedLibrariesOnCollectionChanged;

        foreach (WhisperLanguage lang in WhisperLanguage.GetWhisperLanguages())
        {
            WhisperLanguages.Add(lang);
        }
    }

    public ObservableCollection<WhisperLanguage> WhisperLanguages { get; } = new();

    // TODO: L: Considering moving it because it's quite a lot of code.
    public ObservableCollection<RuntimeLibrary> AvailableLibraries { get; } = new();
    public ObservableCollection<RuntimeLibrary> SelectedLibraries { get; } = new();

    public RuntimeLibrary? SelectedAvailable
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(CanMoveRight));
            }
        }
    }

    public RuntimeLibrary? SelectedSelected
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(CanMoveLeft));
                OnPropertyChanged(nameof(CanMoveUp));
                OnPropertyChanged(nameof(CanMoveDown));
            }
        }
    }

    public bool CanMoveRight => SelectedAvailable.HasValue;
    public bool CanMoveLeft => SelectedSelected.HasValue;
    public bool CanMoveUp => SelectedSelected.HasValue && SelectedLibraries.IndexOf(SelectedSelected.Value) > 0;
    public bool CanMoveDown => SelectedSelected.HasValue && SelectedLibraries.IndexOf(SelectedSelected.Value) < SelectedLibraries.Count - 1;

    private void SelectedLibrariesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Apply to config
        FL.PlayerConfig.Subtitles.WhisperRuntimeLibraries = [.. SelectedLibraries];
    }

    private void LoadDownloadedModels()
    {
        WhisperModel? prevSelected = FL.PlayerConfig.Subtitles.WhisperModel;
        FL.PlayerConfig.Subtitles.WhisperModel = null;
        DownloadModels.Clear();

        List<WhisperModel> models = WhisperModelLoader.LoadDownloadedModels();
        foreach (var model in models)
        {
            DownloadModels.Add(model);
        }

        FL.PlayerConfig.Subtitles.WhisperModel = DownloadModels.FirstOrDefault(m => m.Equals(prevSelected));
    }

    public ObservableCollection<WhisperModel> DownloadModels { get; set => Set(ref field, value); } = new();

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
    public DelegateCommand CmdDownloadModel => field ??= new(() =>
    {
        _dialogService.ShowDialog(nameof(WhisperDownloadDialog));

        LoadDownloadedModels();
    });

    public DelegateCommand CmdMoveRight => field ??= new DelegateCommand(() =>
    {
        if (SelectedAvailable.HasValue && !SelectedLibraries.Contains(SelectedAvailable.Value))
        {
            SelectedLibraries.Add(SelectedAvailable.Value);
            AvailableLibraries.Remove(SelectedAvailable.Value);
        }
    }).ObservesCanExecute(() => CanMoveRight);

    public DelegateCommand CmdMoveLeft => field ??= new DelegateCommand(() =>
    {
        if (SelectedSelected.HasValue)
        {
            AvailableLibraries.Add(SelectedSelected.Value);
            SelectedLibraries.Remove(SelectedSelected.Value);
        }
    }).ObservesCanExecute(() => CanMoveLeft);

    public DelegateCommand CmdMoveUp => field ??= new DelegateCommand(() =>
    {
        int index = SelectedLibraries.IndexOf(SelectedSelected!.Value);
        if (index > 0)
        {
            SelectedLibraries.Move(index, index - 1);
            OnPropertyChanged(nameof(CanMoveUp));
            OnPropertyChanged(nameof(CanMoveDown));
        }
    }).ObservesCanExecute(() => CanMoveUp);

    public DelegateCommand CmdMoveDown => field ??= new DelegateCommand(() =>
    {
        int index = SelectedLibraries.IndexOf(SelectedSelected!.Value);
        if (index < SelectedLibraries.Count - 1)
        {
            SelectedLibraries.Move(index, index + 1);
            OnPropertyChanged(nameof(CanMoveUp));
            OnPropertyChanged(nameof(CanMoveDown));
        }
    }).ObservesCanExecute(() => CanMoveDown);
    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
}

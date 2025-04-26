using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CliWrap.Builders;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
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

        foreach (RuntimeLibrary library in FL.PlayerConfig.Subtitles.WhisperCppConfig.RuntimeLibraries)
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

        ActiveEngineTabNo = (int)FL.PlayerConfig.Subtitles.ASREngine;
    }

    public int ActiveEngineTabNo { get; }

    public ObservableCollection<WhisperLanguage> WhisperLanguages { get; } = new();

    #region whisper.cpp
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
        FL.PlayerConfig.Subtitles.WhisperCppConfig.RuntimeLibraries = [.. SelectedLibraries];
    }

    private void LoadDownloadedModels()
    {
        WhisperCppModel? prevSelected = FL.PlayerConfig.Subtitles.WhisperCppConfig.Model;
        FL.PlayerConfig.Subtitles.WhisperCppConfig.Model = null;
        DownloadModels.Clear();

        List<WhisperCppModel> models = WhisperCppModelLoader.LoadDownloadedModels();
        foreach (var model in models)
        {
            DownloadModels.Add(model);
        }

        FL.PlayerConfig.Subtitles.WhisperCppConfig.Model = DownloadModels.FirstOrDefault(m => m.Equals(prevSelected));

        if (FL.PlayerConfig.Subtitles.WhisperCppConfig.Model == null && DownloadModels.Count == 1)
        {
            // automatically set first downloaded model
            FL.PlayerConfig.Subtitles.WhisperCppConfig.Model = DownloadModels.First();
        }
    }

    public ObservableCollection<WhisperCppModel> DownloadModels { get; set => Set(ref field, value); } = new();

    public OrderedDictionary<string, string> PromptPresets { get; } = new()
    {
        ["Use Chinese (Simplified), with punctuation"] = "以下是普通话的句子。",
        ["Use Chinese (Traditional), with punctuation"] = "以下是普通話的句子。"
    };

    public KeyValuePair<string, string>? SelectedPromptPreset
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                if (value.HasValue)
                {
                    FL.PlayerConfig.Subtitles.WhisperCppConfig.Prompt = value.Value.Value;
                }
            }
        }
    }

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
    public DelegateCommand CmdDownloadModel => field ??= new(() =>
    {
        _dialogService.ShowDialog(nameof(WhisperModelDownloadDialog));

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
    #endregion

    #region faster-whisper
    public OrderedDictionary<string, string> ExtraArgumentsPresets { get; } = new()
    {
        ["Use CUDA device"] = "--device cuda",
        ["Use CUDA second device"] = "--device cuda:1",
        ["Use CPU device"] = "--device cpu",
        ["Use Chinese (Simplified), with punctuation"] = "--initial_prompt \"以下是普通话的句子。\"",
        ["Use Chinese (Traditional), with punctuation"] = "--initial_prompt \"以下是普通話的句子。\""
    };

    public KeyValuePair<string, string>? SelectedExtraArgumentsPreset
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                if (value.HasValue)
                {
                    FL.PlayerConfig.Subtitles.FasterWhisperConfig.ExtraArguments = value.Value.Value;
                }
            }
        }
    }

    public DelegateCommand CmdDownloadEngine => field ??= new(() =>
    {
        _dialogService.ShowDialog(nameof(WhisperEngineDownloadDialog));

        // update binding of downloaded state forcefully
        var prev = FL.PlayerConfig.Subtitles.ASREngine;
        FL.PlayerConfig.Subtitles.ASREngine = SubASREngineType.WhisperCpp;
        FL.PlayerConfig.Subtitles.ASREngine = prev;
    });

    public DelegateCommand CmdOpenModelFolder => field ??= new(() =>
    {
        if (!Directory.Exists(WhisperConfig.ModelsDirectory))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = WhisperConfig.ModelsDirectory,
            UseShellExecute = true,
            CreateNoWindow = true
        });
    });

    public DelegateCommand CmdCopyDebugCommand => field ??= new(() =>
    {
        var cmdBase = FasterWhisperASRService.BuildCommand(FL.PlayerConfig.Subtitles.FasterWhisperConfig,
            FL.PlayerConfig.Subtitles.WhisperConfig);

        var sampleWavePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "kennedy.wav");
        ArgumentsBuilder args = new();
        args.Add(sampleWavePath);

        string addedArgs = args.Build();

        var cmd = cmdBase.WithArguments($"{cmdBase.Arguments} {addedArgs}");
        Clipboard.SetText(cmd.CommandToText());
    });

    public DelegateCommand CmdCopyHelpCommand => field ??= new(() =>
    {
        var cmdBase = FasterWhisperASRService.BuildCommand(FL.PlayerConfig.Subtitles.FasterWhisperConfig,
            FL.PlayerConfig.Subtitles.WhisperConfig);

        var cmd = cmdBase.WithArguments("--help");
        Clipboard.SetText(cmd.CommandToText());
    });
#endregion
    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
}

[ValueConversion(typeof(Enum), typeof(bool))]
public class ASREngineDownloadedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SubASREngineType engine)
        {
            if (engine == SubASREngineType.FasterWhisper)
            {
                if (File.Exists(FasterWhisperConfig.DefaultEnginePath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

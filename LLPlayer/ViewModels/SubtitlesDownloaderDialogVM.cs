using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using FlyleafLib;
using LLPlayer.Extensions;
using LLPlayer.Services;
using Microsoft.Win32;
using static FlyleafLib.MediaFramework.MediaContext.DecoderContext;

namespace LLPlayer.ViewModels;

public class SubtitlesDownloaderDialogVM : Bindable, IDialogAware
{
    public FlyleafManager FL { get; }
    private readonly OpenSubtitlesProvider _subProvider;

    public SubtitlesDownloaderDialogVM(
        FlyleafManager fl,
        OpenSubtitlesProvider subProvider
        )
    {
        FL = fl;
        _subProvider = subProvider;
    }

    public ObservableCollection<SearchResponse> Subs { get; } = new();

    public SearchResponse? SelectedSub
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(CanAction));
            }
        }
    } = null;

    public string Query
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(CanSearch));
            }
        }
    } = string.Empty;

    public bool CanSearch => !string.IsNullOrWhiteSpace(Query);
    public bool CanAction => SelectedSub != null;

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    public AsyncDelegateCommand CmdSearch => field ??= new AsyncDelegateCommand(async () =>
    {
        Subs.Clear();

        var result = await _subProvider.Search(Query);

        // TODO: L: prefer user-configured languages instead of "English"
        var query = result
            .OrderByDescending(r => r.LanguageName == "English") // English first
            .ThenBy(r => r.LanguageName)
            .ThenByDescending(r => r.SubDownloadsCnt)
            .ThenBy(r => r.SubFileName);

        foreach (var record in query)
        {
            Subs.Add(record);
        }
    }).ObservesCanExecute(() => CanSearch);

    public AsyncDelegateCommand CmdLoad => field ??= new AsyncDelegateCommand(async () =>
    {
        var sub = SelectedSub;
        if (sub == null)
        {
            return;
        }
        // TODO: L: error handling (error dialog)
        (byte[] subData, _) = await _subProvider.Download(sub);

        string subDir = Path.Combine(Path.GetTempPath(), App.Name, "Subs");
        string subPath = Path.Combine(subDir, sub.SubFileName);
        if (!Directory.Exists(subDir))
        {
            Directory.CreateDirectory(subDir);
        }

        var ext = Path.GetExtension(subPath).ToLower();
        if (ext.StartsWith("."))
        {
            ext = ext.Substring(1);
        }

        if (!Utils.ExtensionsSubtitles.Contains(ext))
        {
            throw new InvalidOperationException($"'{ext}' extension is not supported");
        }

        await File.WriteAllBytesAsync(subPath, subData);

        // TODO: L: Refactor to pass language directly at Open
        // TODO: L: Allow to load as a secondary subtitle
        FL.Player.decoder.OpenExternalSubtitlesStreamCompleted += DecoderOnOpenExternalSubtitlesStreamCompleted;

        void DecoderOnOpenExternalSubtitlesStreamCompleted(object? sender, OpenExternalSubtitlesStreamCompletedArgs e)
        {
            FL.Player.decoder.OpenExternalSubtitlesStreamCompleted -= DecoderOnOpenExternalSubtitlesStreamCompleted;

            if (e.Success)
            {
                var stream = e.ExtStream;
                if (stream != null && stream.Url == subPath)
                {
                    // Override if different from auto-detected language
                    stream.ManualDownloaded = true;

                    if (stream.Language.ISO6391 != sub.ISO639)
                    {
                        stream.Language = Language.Get(sub.ISO639);
                        FL.Player.SubtitlesManager[0].LanguageSource = stream.Language;
                    }
                }
            }
        }

        FL.Player.OpenAsync(subPath);

    }).ObservesCanExecute(() => CanAction);

    public AsyncDelegateCommand CmdDownload => field ??= new AsyncDelegateCommand(async () =>
    {
        // TODO: L: error handling
        var sub = SelectedSub;
        if (sub == null)
        {
            return;
        }

        string fileName = sub.SubFileName;

        string? initDir = null;
        if (FL.Player.Playlist.Selected != null)
        {
            var url = FL.Player.Playlist.Selected.DirectUrl;
            if (File.Exists(url))
            {
                initDir = Path.GetDirectoryName(url);
            }
        }

        SaveFileDialog dialog = new()
        {
            Title = "Save subtitles to file",
            InitialDirectory = initDir,
            FileName = fileName,
            Filter = "All Files (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            (byte[] subData, _) = await _subProvider.Download(sub);
            await File.WriteAllBytesAsync(dialog.FileName, subData);
        }
    }).ObservesCanExecute(() => CanAction);

    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    private void Playlist_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FL.Player.Playlist.Selected) &&
            FL.Player.Playlist.Selected != null)
        {
            // Update query when video changes
            string? title = FL.Player.Playlist.Selected.Title;
            if (Query != title)
            {
                Query = title;
            }
        }
    }

    #region IDialogAware
    public string Title { get; set => Set(ref field, value); }
        = $"Subtitles Downloader - {App.Name}";
    public double WindowWidth { get; set => Set(ref field, value); } = 900;
    public double WindowHeight { get; set => Set(ref field, value); } = 600;

    public DialogCloseListener RequestClose { get; }

    public bool CanCloseDialog()
    {
        return true;
    }
    public void OnDialogClosed()
    {
        FL.Player.Playlist.PropertyChanged -= Playlist_OnPropertyChanged;
    }
    public void OnDialogOpened(IDialogParameters parameters)
    {
        // Set query from current video
        var selected = FL.Player.Playlist.Selected;
        if (selected != null)
        {
            Query = selected.Title;
        }

        // Register update playlist event
        FL.Player.Playlist.PropertyChanged += Playlist_OnPropertyChanged;
    }
    #endregion
}

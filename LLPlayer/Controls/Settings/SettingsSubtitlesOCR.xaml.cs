using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using Windows.Media.Ocr;
using FlyleafLib;
using LLPlayer.Extensions;
using LLPlayer.Services;
using LLPlayer.Views;

namespace LLPlayer.Controls.Settings;

public partial class SettingsSubtitlesOCR : UserControl
{
    public SettingsSubtitlesOCR()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SettingsSubtitlesOCRVM>();
    }
}

public class SettingsSubtitlesOCRVM : Bindable
{
    public FlyleafManager FL { get; }
    private readonly IDialogService _dialogService;

    public SettingsSubtitlesOCRVM(FlyleafManager fl, IDialogService dialogService)
    {
        FL = fl;
        _dialogService = dialogService;

        LoadDownloadedTessModels();
        LoadAvailableMsModels();
    }

    public ObservableCollection<TesseractModel> DownloadedTessLanguages { get; } = new();
    public ObservableCollection<TessOcrLanguageGroup> TessLanguageGroups { get; } = new();

    public ObservableCollection<Windows.Globalization.Language> AvailableMsOcrLanguages { get; } = new();
    public ObservableCollection<MsOcrLanguageGroup> MsLanguageGroups { get; } = new();

    [field: AllowNull, MaybeNull]
    public DelegateCommand CmdDownloadTessModel => field ??= new(() =>
    {
        _dialogService.ShowDialog(nameof(TesseractDownloadDialog));

        // reload
        LoadDownloadedTessModels();
    });

    private void LoadDownloadedTessModels()
    {
        DownloadedTessLanguages.Clear();

        List<TesseractModel> langs = TesseractModelLoader.LoadDownloadedModels().ToList();
        foreach (var lang in langs)
        {
            DownloadedTessLanguages.Add(lang);
        }

        TessLanguageGroups.Clear();

        List<TessOcrLanguageGroup> langGroups = langs
            .GroupBy(l => l.ISO6391)
            .Where(lg => lg.Count() >= 2)
            .Select(lg => new TessOcrLanguageGroup
            {
                ISO6391 = lg.Key,
                Members = new ObservableCollection<TessOcrLanguageGroupMember>(
                    lg.Select(m => new TessOcrLanguageGroupMember()
                    {
                        DisplayName = m.Lang.ToString(),
                        LangCode = m.LangCode
                    })
                )
            }).ToList();

        Dictionary<string, string>? regionConfig = FL.PlayerConfig.Subtitles.TesseractOcrRegions;
        foreach (TessOcrLanguageGroup group in langGroups)
        {
            // Load preferred region settings from config
            if (regionConfig != null && regionConfig.TryGetValue(group.ISO6391, out string? code))
            {
                group.SelectedMember = group.Members.FirstOrDefault(m => m.LangCode == code);
            }
            group.PropertyChanged += TessLanguageGroup_OnPropertyChanged;

            TessLanguageGroups.Add(group);
        }
    }

    private void LoadAvailableMsModels()
    {
        var langs = OcrEngine.AvailableRecognizerLanguages.ToList();
        foreach (var lang in langs)
        {
            AvailableMsOcrLanguages.Add(lang);
        }

        List<MsOcrLanguageGroup> langGroups = langs
            .GroupBy(l => l.LanguageTag.Split('-').First())
            .Where(lg => lg.Count() >= 2)
            .Select(lg => new MsOcrLanguageGroup
            {
                ISO6391 = lg.Key,
                Members = new ObservableCollection<MsOcrLanguageGroupMember>(
                    lg.Select(m => new MsOcrLanguageGroupMember()
                    {
                        DisplayName = m.DisplayName,
                        LanguageTag = m.LanguageTag,
                        NativeName = m.NativeName
                    })
                )
            }).ToList();

        Dictionary<string, string>? regionConfig = FL.PlayerConfig.Subtitles.MsOcrRegions;
        foreach (MsOcrLanguageGroup group in langGroups)
        {
            // Load preferred region settings from config
            if (regionConfig != null && regionConfig.TryGetValue(group.ISO6391, out string? tag))
            {
                group.SelectedMember = group.Members.FirstOrDefault(m => m.LanguageTag == tag);
            }
            group.PropertyChanged += MsLanguageGroup_OnPropertyChanged;

            MsLanguageGroups.Add(group);
        }
    }

    private void MsLanguageGroup_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MsOcrLanguageGroup.SelectedMember))
        {
            Dictionary<string, string> iso6391ToTag = new();

            foreach (MsOcrLanguageGroup group in MsLanguageGroups)
            {
                if (group.SelectedMember != null)
                {
                    iso6391ToTag.Add(group.ISO6391, group.SelectedMember.LanguageTag);
                }
            }

            FL.PlayerConfig.Subtitles.MsOcrRegions = iso6391ToTag;
        }
    }

    private void TessLanguageGroup_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TessOcrLanguageGroup.SelectedMember))
        {
            Dictionary<string, string> iso6391ToCode = new();

            foreach (TessOcrLanguageGroup group in TessLanguageGroups)
            {
                if (group.SelectedMember != null)
                {
                    iso6391ToCode.Add(group.ISO6391, group.SelectedMember.LangCode);
                }
            }

            FL.PlayerConfig.Subtitles.TesseractOcrRegions = iso6391ToCode;
        }
    }
}

#region Tesseract
public class TessOcrLanguageGroupMember : Bindable
{
    public required string LangCode { get; init; }
    public required string DisplayName { get; init; }
}

public class TessOcrLanguageGroup : Bindable
{
    public required string ISO6391 { get; init; }

    public string DisplayName
    {
        get
        {
            var lang = Language.Get(ISO6391);
            return lang.TopEnglishName;
        }
    }

    public required ObservableCollection<TessOcrLanguageGroupMember> Members { get; init; }
    public TessOcrLanguageGroupMember? SelectedMember { get; set => Set(ref field, value); }
}
#endregion

#region Microsoft OCR
public class MsOcrLanguageGroupMember : Bindable
{
    public required string LanguageTag { get; init; }
    public required string DisplayName { get; init; }
    public required string NativeName { get; init; }
}

public class MsOcrLanguageGroup : Bindable
{
    public required string ISO6391 { get; init; }

    public string DisplayName
    {
        get
        {
            var lang = Language.Get(ISO6391);
            return lang.TopEnglishName;
        }
    }

    public required ObservableCollection<MsOcrLanguageGroupMember> Members { get; init; }
    public MsOcrLanguageGroupMember? SelectedMember { get; set => Set(ref field, value); }
}
#endregion

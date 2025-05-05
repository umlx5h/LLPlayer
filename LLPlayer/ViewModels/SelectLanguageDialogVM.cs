using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using FlyleafLib;
using LLPlayer.Extensions;
using LLPlayer.Services;

namespace LLPlayer.ViewModels;

public class SelectLanguageDialogVM : Bindable, IDialogAware
{
    public FlyleafManager FL { get; }

    public SelectLanguageDialogVM(FlyleafManager fl)
    {
        FL = fl;

        _filteredAvailableLanguages = CollectionViewSource.GetDefaultView(AvailableLanguages);
        _filteredAvailableLanguages.Filter = obj =>
        {
            if (obj is not Language lang)
                return false;

            if (string.IsNullOrWhiteSpace(SearchText))
                return true;

            string query = SearchText.Trim();

            if (lang.TopEnglishName.Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;

            return lang.TopNativeName.Contains(query, StringComparison.OrdinalIgnoreCase);
        };
    }

    public string SearchText
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                _filteredAvailableLanguages.Refresh();
            }
        }
    } = string.Empty;

    private readonly ICollectionView _filteredAvailableLanguages;
    public ObservableCollection<Language> AvailableLanguages { get; } = new();
    public ObservableCollection<Language> SelectedLanguages { get; } = new();

    public Language? SelectedAvailable
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

    // TODO: L: weird naming
    public Language? SelectedSelected
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

    public bool CanMoveRight => SelectedAvailable != null;
    public bool CanMoveLeft => SelectedSelected != null;
    public bool CanMoveUp => SelectedSelected != null && SelectedLanguages.IndexOf(SelectedSelected) > 0;
    public bool CanMoveDown => SelectedSelected != null && SelectedLanguages.IndexOf(SelectedSelected) < SelectedLanguages.Count - 1;

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    public DelegateCommand CmdMoveRight => field ??= new DelegateCommand(() =>
    {
        if (SelectedAvailable != null && !SelectedLanguages.Contains(SelectedAvailable))
        {
            SelectedLanguages.Add(SelectedAvailable);
            AvailableLanguages.Remove(SelectedAvailable);
        }
    }).ObservesCanExecute(() => CanMoveRight);

    public DelegateCommand CmdMoveLeft => field ??= new DelegateCommand(() =>
    {
        if (SelectedSelected != null)
        {
            // conform to order
            int insertIndex = 0;
            while (insertIndex < AvailableLanguages.Count &&
                   string.Compare(AvailableLanguages[insertIndex].TopEnglishName, SelectedSelected.TopEnglishName, StringComparison.InvariantCulture) < 0)
            {
                insertIndex++;
            }

            AvailableLanguages.Insert(insertIndex, SelectedSelected);

            SelectedLanguages.Remove(SelectedSelected);
        }
    }).ObservesCanExecute(() => CanMoveLeft);

    public DelegateCommand CmdMoveUp => field ??= new DelegateCommand(() =>
    {
        int index = SelectedLanguages.IndexOf(SelectedSelected!);
        if (index > 0)
        {
            SelectedLanguages.Move(index, index - 1);
            OnPropertyChanged(nameof(CanMoveUp));
            OnPropertyChanged(nameof(CanMoveDown));
        }
    }).ObservesCanExecute(() => CanMoveUp);

    public DelegateCommand CmdMoveDown => field ??= new DelegateCommand(() =>
    {
        int index = SelectedLanguages.IndexOf(SelectedSelected!);
        if (index < SelectedLanguages.Count - 1)
        {
            SelectedLanguages.Move(index, index + 1);
            OnPropertyChanged(nameof(CanMoveUp));
            OnPropertyChanged(nameof(CanMoveDown));
        }
    }).ObservesCanExecute(() => CanMoveDown);
    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    #region IDialogAware
    public string Title { get; set => Set(ref field, value); }
        = $"Select Language - {App.Name}";
    public double WindowWidth { get; set => Set(ref field, value); } = 600;
    public double WindowHeight { get; set => Set(ref field, value); } = 360;

    public DialogCloseListener RequestClose { get; }

    public bool CanCloseDialog()
    {
        return true;
    }
    public void OnDialogClosed()
    {
        List<Language> langs = [.. SelectedLanguages];
        DialogParameters p = new()
        {
            { "languages", langs }
        };

        RequestClose.Invoke(p);
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
        List<Language> langs = parameters.GetValue<List<Language>>("languages");

        foreach (var lang in langs)
        {
            SelectedLanguages.Add(lang);
        }

        foreach (Language lang in Language.AllLanguages)
        {
            if (!SelectedLanguages.Contains(lang))
            {
                AvailableLanguages.Add(lang);
            }
        }
    }
    #endregion
}

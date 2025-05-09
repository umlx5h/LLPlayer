using LLPlayer.Extensions;
using LLPlayer.Services;

namespace LLPlayer.ViewModels;

public class SettingsDialogVM : Bindable, IDialogAware
{
    public FlyleafManager FL { get; }
    public SettingsDialogVM(FlyleafManager fl)
    {
        FL = fl;
    }

    public DelegateCommand<string>? CmdCloseDialog => field ??= new((parameter) =>
    {
        ButtonResult result = ButtonResult.None;

        if (parameter == "Save")
        {
            result = ButtonResult.OK;
        }

        RequestClose.Invoke(result);
    });

    #region IDialogAware
    public string Title { get; set => Set(ref field, value); } = $"Settings - {App.Name}";
    public double WindowWidth { get; set => Set(ref field, value); } = 1000;
    public double WindowHeight { get; set => Set(ref field, value); } = 700;

    public bool CanCloseDialog() => true;

    public void OnDialogClosed()
    {
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
    }

    public DialogCloseListener RequestClose { get; }
    #endregion
}

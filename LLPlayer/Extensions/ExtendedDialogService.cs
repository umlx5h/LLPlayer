using System.Windows;

namespace LLPlayer.Extensions;

/// <summary>
/// Customize DialogService's Show(), which sets the Owner of the Window and sets it to always-on-top.
/// ref: https://stackoverflow.com/questions/64420093/prism-idialogservice-show-non-modal-dialog-acts-as-modal
/// </summary>
public class ExtendedDialogService(IContainerExtension containerExtension) : DialogService(containerExtension)
{
    private bool _isOrphan;

    protected override void ConfigureDialogWindowContent(string dialogName, IDialogWindow window, IDialogParameters parameters)
    {
        base.ConfigureDialogWindowContent(dialogName, window, parameters);

        if (parameters != null &&
            parameters.ContainsKey(MyKnownDialogParameters.OrphanWindow))
        {
            _isOrphan = true;
        }
    }

    protected override void ShowDialogWindow(IDialogWindow dialogWindow, bool isModal)
    {
        base.ShowDialogWindow(dialogWindow, isModal);

        if (_isOrphan)
        {
            // Show and then clear Owner to place the window based on the parent window and then make it an orphan window.
            _isOrphan = false;
            dialogWindow.Owner = null;
        }
    }
}

// ref: https://github.com/PrismLibrary/Prism/blob/master/src/Wpf/Prism.Wpf/Dialogs/IDialogServiceCompatExtensions.cs
public static class ExtendedDialogServiceExtensions
{
    /// <summary>
    /// Shows a non-modal and singleton dialog.
    /// </summary>
    /// <param name="dialogService">The DialogService</param>
    /// <param name="name">The name of the dialog to show.</param>
    /// <param name="orphan">Whether to set owner to window</param>
    public static void ShowSingleton(this IDialogService dialogService, string name, bool orphan)
    {
        ShowSingleton(dialogService, name, null!, orphan);
    }

    /// <summary>
    /// Shows a non-modal and singleton dialog.
    /// </summary>
    /// <param name="dialogService">The DialogService</param>
    /// <param name="name">The name of the dialog to show.</param>
    /// <param name="callback">The action to perform when the dialog is closed.</param>
    /// <param name="orphan">Whether to set owner to window</param>
    public static void ShowSingleton(this IDialogService dialogService, string name, Action<IDialogResult> callback, bool orphan)
    {
        var parameters = EnsureShowNonModalParameter(null);

        var windows = Application.Current.Windows.OfType<IDialogWindow>();
        if (windows.Any())
        {
            var curWindow = windows.FirstOrDefault(w => w.Content.GetType().Name == name);
            if (curWindow != null && curWindow is Window win)
            {
                // If minimized, it will not be displayed after Activate, so set it back to Normal in advance.
                if (win.WindowState == WindowState.Minimized)
                {
                    win.WindowState = WindowState.Normal;
                }
                // TODO: L: Notify to ViewModel to update query
                win.Activate();
                return;
            }
        }

        if (orphan)
        {
            parameters.Add(MyKnownDialogParameters.OrphanWindow, true);
        }

        dialogService.Show(name, parameters, callback);
    }

    private static IDialogParameters EnsureShowNonModalParameter(IDialogParameters? parameters)
    {
        parameters ??= new DialogParameters();

        if (!parameters.ContainsKey(KnownDialogParameters.ShowNonModal))
            parameters.Add(KnownDialogParameters.ShowNonModal, true);

        return parameters;
    }
}

public static class MyKnownDialogParameters
{
    public const string OrphanWindow = "orphanWindow";
}

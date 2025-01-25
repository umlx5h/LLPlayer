using System.Windows;
using FlyleafLib.MediaPlayer;
using LLPlayer.Views;

namespace LLPlayer.Services;

public static class ErrorDialogHelper
{
    private static int _showCount;

    public static void ShowKnownErrorPopup(string message, string errorType)
    {
        // prevent double popup
        if (_showCount > 0)
        {
            // TODO: L: logging
            return;
        }

        var dialogService = ((App)Application.Current).Container.Resolve<DialogService>();

        DialogParameters p = new()
        {
            { "type", "known" },
            { "message", message },
            { "errorType", errorType }
        };

        ActiveMainWindowIfNotActivated();

        dialogService.ShowDialog(nameof(ErrorDialog), p);
    }

    public static void ShowKnownErrorPopup(string message, KnownErrorType errorType)
    {
        ShowKnownErrorPopup(message, errorType.ToString());
    }

    public static void ShowUnknownErrorPopup(string message, string errorType, Exception? ex = null)
    {
        // prevent double popup
        if (_showCount > 0)
        {
            // TODO: L: logging
            return;
        }

        var dialogService = ((App)Application.Current).Container.Resolve<DialogService>();

        DialogParameters p = new()
        {
            { "type", "unknown" },
            { "message", message },
            { "errorType", errorType },
        };

        if (ex != null)
        {
            p.Add("exception", ex);
        }

        ActiveMainWindowIfNotActivated();

        _showCount++;
        dialogService.ShowDialog(nameof(ErrorDialog), p);
        _showCount--;
    }

    public static void ShowUnknownErrorPopup(string message, UnknownErrorType errorType, Exception? ex = null)
    {
        ShowUnknownErrorPopup(message, errorType.ToString(), ex);
    }

    private static void ActiveMainWindowIfNotActivated()
    {
        // Prevent the dialog from being displayed on the back side of MainWindow
        // even though Owner is set if it is displayed when it is not in the active state.
        if (Application.Current.MainWindow != null && !Application.Current.MainWindow.IsActive)
        {
            var dialogs = Application.Current.Windows.OfType<IDialogWindow>();
            IDialogWindow? activeDialog = dialogs
                .FirstOrDefault(dw => dw is Window { IsActive: true });

            if (activeDialog == null)
            {
                Application.Current.MainWindow.Activate();
            }
        }
    }
}

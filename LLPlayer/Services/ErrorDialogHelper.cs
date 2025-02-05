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
            return;
        }

        var dialogService = ((App)Application.Current).Container.Resolve<DialogService>();

        DialogParameters p = new()
        {
            { "type", "known" },
            { "message", message },
            { "errorType", errorType }
        };

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

        _showCount++;
        dialogService.ShowDialog(nameof(ErrorDialog), p);
        _showCount--;
    }

    public static void ShowUnknownErrorPopup(string message, UnknownErrorType errorType, Exception? ex = null)
    {
        ShowUnknownErrorPopup(message, errorType.ToString(), ex);
    }
}

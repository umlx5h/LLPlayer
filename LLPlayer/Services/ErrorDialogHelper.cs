using System.Windows;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using LLPlayer.Views;

namespace LLPlayer.Services;

public static class ErrorDialogHelper
{
    public static void ShowKnownErrorPopup(string message, string errorType)
    {
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

    public static void ShowUnknownErrorPopup(string message, Exception? ex = null)
    {
        var dialogService = ((App)Application.Current).Container.Resolve<DialogService>();

        DialogParameters p = new()
        {
            { "type", "unknown" },
            { "message", message },
        };

        if (ex != null)
        {
            p.Add("exception", ex);
        }

        dialogService.ShowDialog(nameof(ErrorDialog), p);
    }
}

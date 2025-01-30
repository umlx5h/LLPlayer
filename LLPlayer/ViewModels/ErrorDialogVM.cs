using System.Diagnostics.CodeAnalysis;
using System.Media;
using System.Windows;
using LLPlayer.Extensions;
using LLPlayer.Services;

namespace LLPlayer.ViewModels;

public class ErrorDialogVM : Bindable, IDialogAware
{
    private readonly FlyleafManager FL;

    public ErrorDialogVM(FlyleafManager fl)
    {
        FL = fl;
    }

    public string Message { get; set => Set(ref field, value); }

    public Exception? Exception
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(HasException));
                OnPropertyChanged(nameof(ExceptionDetail));
            }
        }
    }
    public bool HasException => Exception != null;

    public string ExceptionDetail => Exception == null ? "" : Exception.ToString();

    public bool IsUnknown
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(ErrorTitle));
            }
        }
    }

    public string ErrorType
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(ErrorTitle));
            }
        }
    }

    public string ErrorTitle => IsUnknown ? $"{ErrorType} Unknown Error" : $"{ErrorType} Error";

    [field: AllowNull, MaybeNull]
    public DelegateCommand CmdCopyMessage => field ??= new(() =>
    {
        string text = ErrorTitle + ":" + Environment.NewLine + Message;

        if (Exception != null)
        {
            text += Environment.NewLine + Environment.NewLine + Exception.ToString();
        }

        text += Environment.NewLine + Environment.NewLine;

        text += $"Version: {App.Version}, CommitHash: {App.CommitHash}";

        Clipboard.SetText(text);
    });

    [field: AllowNull, MaybeNull]
    public DelegateCommand CmdCloseDialog => field ??= new(() =>
    {
        RequestClose.Invoke(ButtonResult.OK);
    });

    #region IDialogAware
    public string Title => "Error Occured";
    public double WindowWidth { get; set => Set(ref field, value); } = 450;
    public double WindowHeight { get; set => Set(ref field, value); } = 250;

    public bool CanCloseDialog() => true;

    public void OnDialogClosed()
    {
        FL.Player.Activity.Timeout = _prevTimeout;
        FL.Player.Activity.IsEnabled = true;
    }

    private int _prevTimeout;

    public void OnDialogOpened(IDialogParameters parameters)
    {
        _prevTimeout = FL.Player.Activity.Timeout;
        FL.Player.Activity.Timeout = 0;
        FL.Player.Activity.IsEnabled = false;

        switch (parameters.GetValue<string>("type"))
        {
            case "known":
                Message = parameters.GetValue<string>("message");
                ErrorType = parameters.GetValue<string>("errorType");
                IsUnknown = false;

                break;
            case "unknown":
                Message = parameters.GetValue<string>("message");
                ErrorType = parameters.GetValue<string>("errorType");
                IsUnknown = true;

                if (parameters.ContainsKey("exception"))
                {
                    Exception = parameters.GetValue<Exception>("exception");
                }

                WindowHeight += 100;
                WindowWidth += 20;

                // Play alert sound
                SystemSounds.Hand.Play();

                break;
        }
    }

    public DialogCloseListener RequestClose { get; }
    #endregion IDialogAware
}

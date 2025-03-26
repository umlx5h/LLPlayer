using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Media;
using System.Text;
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
                ExceptionDetail = value == null ? "" : GetExceptionWithAllData(value);
            }
        }
    }
    public bool HasException => Exception != null;

    public string ExceptionDetail { get; set => Set(ref field, value); }

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
        string text = $"""
                       [{ErrorTitle}]
                       {Message}
                       """;

        if (Exception != null)
        {
            text += $"""


                    ```
                    {ExceptionDetail}
                    ```
                    """;
        }

        text += $"""


                 Version: {App.Version}, CommitHash: {App.CommitHash}
                 OS Architecture: {App.OSArchitecture}, Process Architecture: {App.ProcessArchitecture}
                 """;
        Clipboard.SetText(text);
    });

    [field: AllowNull, MaybeNull]
    public DelegateCommand CmdCloseDialog => field ??= new(() =>
    {
        RequestClose.Invoke(ButtonResult.OK);
    });

    private static string GetExceptionWithAllData(Exception ex)
    {
        string exceptionInfo = ex.ToString();

        // Collect all Exception.Data to dictionary
        Dictionary<object, object> allData = new();
        CollectExceptionData(ex, allData);

        if (allData.Count == 0)
        {
            // not found Exception.Data
            return exceptionInfo;
        }

        // found Exception.Data
        StringBuilder sb = new();
        sb.Append(exceptionInfo);

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("---------------------- All Exception Data ----------------------");
        foreach (var (i, entry) in allData.Index())
        {
            sb.AppendLine($"  [{entry.Key}]: {entry.Value}");
            if (i != allData.Count - 1)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static void CollectExceptionData(Exception? ex, Dictionary<object, object> allData)
    {
        if (ex == null) return;

        foreach (DictionaryEntry entry in ex.Data)
        {
            allData.TryAdd(entry.Key, entry.Value);
        }

        if (ex.InnerException != null)
        {
            CollectExceptionData(ex.InnerException, allData);
        }

        if (ex is AggregateException aggregateEx)
        {
            foreach (var innerEx in aggregateEx.InnerExceptions)
            {
                CollectExceptionData(innerEx, allData);
            }
        }
    }

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

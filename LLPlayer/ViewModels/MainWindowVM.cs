using System.IO;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using LLPlayer.Extensions;
using LLPlayer.Services;
using InputType = FlyleafLib.InputType;

namespace LLPlayer.ViewModels;

public class MainWindowVM : Bindable
{
    public FlyleafManager FL { get; }

    public MainWindowVM(FlyleafManager fl)
    {
        FL = fl;
    }

    public string Title { get; set => Set(ref field, value); } = App.Name;

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
    public DelegateCommand CmdOnLoaded => field ??= new(() =>
    {
        // error handling
        FL.Player.KnownErrorOccurred += (sender, args) =>
        {
            Utils.UI(() =>
            {
                ErrorDialogHelper.ShowKnownErrorPopup(args.Message, args.ErrorType);
            });
        };

        FL.Player.UnknownErrorOccurred += (sender, args) =>
        {
            Utils.UI(() =>
            {
                ErrorDialogHelper.ShowUnknownErrorPopup(args.Message, args.ErrorType, args.Exception);
            });
        };

        FL.Player.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(FL.Player.Status))
            {
                if (FL.Player.Status == Status.Stopped)
                {
                    // reset
                    Title = App.Name;
                }
            }
        };

        FL.Player.OpenCompleted += (sender, args) =>
        {
            if (!args.Success || args.IsSubtitles)
            {
                return;
            }

            string name = Path.GetFileName(args.Url);
            if (FL.Player.Playlist.InputType == InputType.Web)
            {
                name = FL.Player.Playlist.Selected.Title;
            }
            Title = $"{name} - {App.Name}";
        };

        if (App.CmdUrl != null)
        {
            FL.Player.OpenAsync(App.CmdUrl);
        }
    });

    public DelegateCommand CmdOnClosing => field ??= new(() =>
    {
        FL.Player.Dispose();
    });

    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
}

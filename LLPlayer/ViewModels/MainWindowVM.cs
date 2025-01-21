using System.IO;
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

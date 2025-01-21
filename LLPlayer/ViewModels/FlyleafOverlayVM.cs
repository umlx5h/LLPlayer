using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyleafLib.MediaFramework.MediaRenderer;
using FlyleafLib.MediaPlayer;
using LLPlayer.Extensions;
using LLPlayer.Services;
using static FlyleafLib.Utils.NativeMethods;

namespace LLPlayer.ViewModels;

public class FlyleafOverlayVM : Bindable
{
    public FlyleafManager FL { get; }

    public FlyleafOverlayVM(FlyleafManager fl)
    {
        FL = fl;
    }

    /// <summary>
    /// Whether a video file is being loaded or not (for Spinner)
    /// </summary>
    public bool IsLoading { get; set => Set(ref field, value); }

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
    public DelegateCommand CmdOnLoaded => field ??= new(() =>
    {
        SetupOSDStatus();
        SetupFlyleafContextMenu();
        SetupFlyleafKeybindings();

        FL.Player.Opening += (sender, args) =>
        {
            if (!args.IsSubtitles)
            {
                Utils.UI(() =>
                {
                    IsLoading = true;
                });
            }

        };
        FL.Player.OpenCompleted += (sender, args) =>
        {
            if (!args.IsSubtitles)
            {
                Utils.UI(() =>
                {
                    IsLoading = false;
                });
            }
        };

        FL.Config.FlyleafHostLoaded();
    });
    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    private void SetupFlyleafContextMenu()
    {
        // When a context menu is opened using the Overlay.MouseRightButtonUp event,
        // it is a bubble event and therefore has priority over the ContextMenu of the child controls.
        // so set ContextMenu directly to each of the controls in order to give priority to the child contextmenu.
        ContextMenu menu = (ContextMenu)Application.Current.FindResource("PopUpMenu")!;
        menu.DataContext = this;
        menu.PlacementTarget = FL.FlyleafHost!.Overlay;

        FL.FlyleafHost!.Overlay.ContextMenu = menu;
        FL.FlyleafHost!.Surface.ContextMenu = menu;
    }

    #region Flyleaf Keybindings
    // TODO: L: Mouse operation should also be customizable via Config.
    private void SetupFlyleafKeybindings()
    {
        // Subscribe to the same event to the following two
        // Surface: On Video Screen
        // Overlay: Content of FlyleafHost = FlyleafOverlay
        foreach (var window in (Window[])[FL.FlyleafHost!.Surface, FL.FlyleafHost!.Overlay])
        {
            window.MouseUp += FlyleafOnMouseUp;
            window.MouseWheel += FlyleafOnMouseWheel;
        }
    }

    private void FlyleafOnMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Middle click: seek to current subtitle
        if (e.ChangedButton == MouseButton.Middle)
        {
            bool ctrlDown = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            if (ctrlDown)
            {
                // CTRL + Middle click: Reset zoom
                // TODO: L: make it Command
                FL.Player.renderer.SetPanAll(0, 0, 0, 1, Renderer.ZoomCenterPoint, true);

                FL.Player.PanXOffset = 0;
                FL.Player.PanYOffset = 0;
                FL.Player.Rotation = 0;
                FL.Player.Zoom = 100;

                e.Handled = true;
                return;
            }

            if (FL.Player.Subtitles[0].Enabled)
            {
                FL.Player.Subtitles.CurSeek();
            }
            e.Handled = true;
        }
    }

    private void FlyleafOnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
        {
            return;
        }

        Window surface = FL.FlyleafHost!.Surface;

        bool ctrlDown = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        if (ctrlDown)
        {
            Point cur = e.GetPosition(surface);
            Point curDpi = new(cur.X * DpiX, cur.Y * DpiY);
            if (e.Delta > 0)
            {
                FL.Player.ZoomIn(curDpi);
            }
            else
            {
                FL.Player.ZoomOut(curDpi);
            }
        }

        e.Handled = true;
    }
    #endregion

    #region OSD
    // TODO: L: Extend OSD
    private void SetupOSDStatus()
    {
        var player = FL.Player;

        player.PropertyChanged += (o, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(player.Rotation):
                    OSDMessage = $"Rotation {player.Rotation}°";
                    break;
                case nameof(player.Speed):
                    OSDMessage = $"Speed x{player.Speed}";
                    break;
                case nameof(player.Zoom):
                    OSDMessage = $"Zoom {player.Zoom}%";
                    break;

                case nameof(player.Status):
                    if (player.Activity.Mode == ActivityMode.Idle)
                    {
                        // not related to OSD
                        player.Activity.ForceActive();
                    }

                    // TODO: L: Playback toggling can be done on mouse click, but it should be visually clear.
                    break;
            }
        };

        player.Audio.PropertyChanged += (o, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(player.Audio.Volume):
                    OSDMessage = $"Volume {player.Audio.Volume}%";
                    break;
                case nameof(player.Audio.Mute):
                    OSDMessage = player.Audio.Mute ? "Muted" : "Unmuted";
                    break;
            }
        };

        player.Config.Audio.PropertyChanged += (o, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(player.Config.Audio.Delay):
                    OSDMessage = $"Audio Delay {player.Config.Audio.Delay / 10000}ms";
                    break;
            }
        };

        foreach (var (i, config) in player.Config.Subtitles.SubConfigs.Index())
        {
            config.PropertyChanged += (o, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(config.Delay):
                        OSDMessage = $"{(i == 0 ? "Primary" : "Secondary")} Subs Delay {config.Delay / 10000}ms";
                        break;
                    case nameof(config.Visible):
                        var primary = player.Config.Subtitles[0].Visible ? "visible" : "hidden";
                        var secondary = player.Config.Subtitles[1].Visible ? "visible" : "hidden";

                        if (player.Subtitles[1].Enabled)
                        {
                            OSDMessage = $"Subs {primary} / {secondary}";
                        }
                        else
                        {
                            OSDMessage = $"Subs {primary}";
                        }
                        break;
                }
            };
        }
    }

    private const int MSG_TIMEOUT = 3000;
    CancellationTokenSource _cancelMsgToken = new();
    public string OSDMessage
    {
        get => _osdMessage;
        set
        {
            _cancelMsgToken.Cancel();
            Set(ref _osdMessage, value);
            _cancelMsgToken = new CancellationTokenSource();
            Task.Run(FadeOutMsg, _cancelMsgToken.Token);
        }
    }

    string _osdMessage = "";
    private async Task FadeOutMsg()
    {
        await Task.Delay(MSG_TIMEOUT, _cancelMsgToken.Token);
        Application.Current.Dispatcher.Invoke(() =>
        {
            _osdMessage = "";
            OnPropertyChanged(nameof(OSDMessage));
        });
    }
    #endregion
}

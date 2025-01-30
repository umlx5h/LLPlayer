using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyleafLib;
using FlyleafLib.MediaFramework.MediaRenderer;
using FlyleafLib.MediaPlayer;
using LLPlayer.Extensions;
using LLPlayer.Services;
using MaterialDesignThemes.Wpf;
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
        // CTRL + Wheel  : Zoom in / out
        // SHIFT + Wheel : Subtitles Up / Down
        // CTRL + SHIFT + Wheel : Subtitles Size Increase / Decrease
        if (e.Delta == 0)
        {
            return;
        }

        Window surface = FL.FlyleafHost!.Surface;

        bool ctrlDown = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        bool shiftDown = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

        if (ctrlDown && shiftDown)
        {
            if (e.Delta > 0)
            {
                FL.Action.CmdSubsSizeIncrease.Execute();
            }
            else
            {
                FL.Action.CmdSubsSizeDecrease.Execute();
            }
        }
        else if (ctrlDown)
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
        else if (shiftDown)
        {
            if (e.Delta > 0)
            {
                FL.Action.CmdSubsPositionUp.Execute();
            }
            else
            {
                FL.Action.CmdSubsPositionDown.Execute();
            }
        }

        e.Handled = true;
    }
    #endregion

    #region OSD
    private void SetupOSDStatus()
    {
        var player = FL.Player;

        player.PropertyChanged += (o, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(player.OSDMessage):
                    OSDMessage = player.OSDMessage;
                    break;
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
                    // Change only Play and Pause to icon
                    switch (player.Status)
                    {
                        case Status.Paused:
                            OSDIcon = PackIconKind.Pause;
                            return;
                        case Status.Playing:
                            OSDIcon = PackIconKind.Play;
                            return;
                    }
                    OSDMessage = $"{player.Status.ToString()}";
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

        player.Config.Player.PropertyChanged += (o, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(player.Config.Player.SeekAccurate):
                    OSDMessage = $"Always Seek Accurate {(player.Config.Player.SeekAccurate ? "On" : "Off")}";
                    break;
            }
        };

        player.Config.Audio.PropertyChanged += (o, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(player.Config.Audio.Enabled):
                    OSDMessage = $"Audio {(player.Config.Audio.Enabled ? "Enabled" : "Disabled")}";
                    break;
                case nameof(player.Config.Audio.Delay):
                    OSDMessage = $"Audio Delay {player.Config.Audio.Delay / 10000}ms";
                    break;
            }
        };

        player.Config.Video.PropertyChanged += (o, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(player.Config.Video.Enabled):
                    OSDMessage = $"Video {(player.Config.Video.Enabled ? "Enabled" : "Disabled")}";
                    break;
                case nameof(player.Config.Video.AspectRatio):
                    OSDMessage = $"Aspect Ratio {player.Config.Video.AspectRatio.ToString()}";
                    break;
                case nameof(player.Config.Video.VideoAcceleration):
                    OSDMessage = $"Video Acceleration {(player.Config.Video.VideoAcceleration ? "On" : "Off")}";
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

    private CancellationTokenSource? _cancelMsgToken;

    public string OSDMessage
    {
        get => _osdMessage;
        set
        {
            Utils.UIInvokeIfRequired(() =>
            {
                if (Set(ref _osdMessage, value))
                {
                    if (_cancelMsgToken != null)
                    {
                        _cancelMsgToken.Cancel();
                        _cancelMsgToken.Dispose();
                        _cancelMsgToken = null;
                    }

                    if (Set(ref _osdIcon, null, nameof(OSDIcon)))
                    {
                        OnPropertyChanged(nameof(IsOSDIcon));
                    }

                    _cancelMsgToken = new();

                    var token = _cancelMsgToken.Token;
                    _ = Task.Run(() => ClearOSD(3000, token));
                }
            });
        }
    }
    private string _osdMessage;

    public PackIconKind? OSDIcon
    {
        get => _osdIcon;
        set
        {
            Utils.UIInvokeIfRequired(() =>
            {
                if (Set(ref _osdIcon, value))
                {
                    OnPropertyChanged(nameof(IsOSDIcon));

                    if (_cancelMsgToken != null)
                    {
                        _cancelMsgToken.Cancel();
                        _cancelMsgToken.Dispose();
                        _cancelMsgToken = null;
                    }

                    Set(ref _osdMessage, "", nameof(OSDMessage));

                    _cancelMsgToken = new();

                    var token = _cancelMsgToken.Token;
                    _ = Task.Run(() => ClearOSD(500, token));
                }
            });
        }
    }
    private PackIconKind? _osdIcon;

    public bool IsOSDIcon => OSDIcon != null;

    private async Task ClearOSD(int timeoutMs, CancellationToken token)
    {
        await Task.Delay(timeoutMs, token);

        if (token.IsCancellationRequested)
            return;

        Utils.UIInvoke(() =>
        {
            Set(ref _osdMessage, "", nameof(OSDMessage));
            if (Set(ref _osdIcon, null, nameof(OSDIcon)))
            {
                OnPropertyChanged(nameof(IsOSDIcon));
            }
        });
    }
    #endregion
}

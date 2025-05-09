using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyleafLib;
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

    public DelegateCommand? CmdOnLoaded => field ??= new(() =>
    {
        SetupOSDStatus();
        SetupFlyleafContextMenu();
        SetupFlyleafKeybindings();

        FL.Config.FlyleafHostLoaded();
    });

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
    // TODO: L: Make it fully customizable like PotPlayer
    private void SetupFlyleafKeybindings()
    {
        // Subscribe to the same event to the following two
        // Surface: On Video Screen
        // Overlay: Content of FlyleafHost = FlyleafOverlay
        foreach (var window in (Window[])[FL.FlyleafHost!.Surface, FL.FlyleafHost!.Overlay])
        {
            window.MouseWheel += FlyleafOnMouseWheel;
        }
        FL.FlyleafHost.Surface.MouseUp += SurfaceOnMouseUp;
        FL.FlyleafHost.Surface.MouseDoubleClick += SurfaceOnMouseDoubleClick;
        FL.FlyleafHost.Surface.MouseLeftButtonDown += SurfaceOnMouseLeftButtonDown;
        FL.FlyleafHost.Surface.MouseLeftButtonUp += SurfaceOnMouseLeftButtonUp;
        FL.FlyleafHost.Surface.MouseMove += SurfaceOnMouseMove;
        FL.FlyleafHost.Surface.LostMouseCapture += SurfaceOnLostMouseCapture;
    }

    private void SurfaceOnMouseUp(object sender, MouseButtonEventArgs e)
    {
        switch (e.ChangedButton)
        {
            // Middle click: seek to current subtitle
            case MouseButton.Middle:
                bool ctrlDown = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
                if (ctrlDown)
                {
                    // CTRL + Middle click: Reset zoom
                    FL.Player.ResetZoom();
                    e.Handled = true;
                    return;
                }

                if (FL.Player.Subtitles[0].Enabled)
                {
                    FL.Player.Subtitles.CurSeek();
                }
                e.Handled = true;
                break;

            // X1 click: subtitles prev seek with fallback
            case MouseButton.XButton1:
                FL.Player.Subtitles.PrevSeekFallback();
                e.Handled = true;
                break;

            // X2 click: subtitles next seek with fallback
            case MouseButton.XButton2:
                FL.Player.Subtitles.NextSeekFallback();
                e.Handled = true;
                break;
        }
    }

    private void SurfaceOnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FL.Config.MouseDoubleClickToFullScreen && e.ChangedButton == MouseButton.Left)
        {
            FL.Player.ToggleFullScreen();

            // Double and single clicks are not currently distinguished, so need to be re-fired
            // Timers need to be added to distinguish between the two,
            // but this is controversial because it delays a single click action
            SingleClickAction();

            e.Handled = true;
        }
    }

    private bool _isLeftDragging;
    private Point _downPoint;
    private Point _downCursorPoint;
    private long _downTick;

    private void SurfaceOnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Support window dragging & play / pause toggle
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        if (FL.FlyleafHost is not { IsResizing: false, IsPanMoving: false, IsDragMoving: false })
            return;

        Point downPoint = FL.FlyleafHost!.Surface.PointToScreen(default);
        long downTick = Stopwatch.GetTimestamp();

        if (!FL.FlyleafHost.IsFullScreen && FL.FlyleafHost.Owner.WindowState == WindowState.Normal)
        {
            // normal window: window dragging
            DragMoveOwner();

            MouseLeftButtonUpAction(downPoint, downTick);
        }
        else
        {
            // fullscreen or maximized window: do action in KeyUp event
            _isLeftDragging = true;
            _downPoint = downPoint;
            _downTick = downTick;

            _downCursorPoint = e.GetPosition((IInputElement)sender);
        }
    }

    // When left dragging while maximized, move window back to normal and move window
    private void SurfaceOnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isLeftDragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (FL.FlyleafHost!.IsFullScreen || FL.FlyleafHost.Owner.WindowState != WindowState.Maximized)
            return;

        Point curPoint = e.GetPosition((IInputElement)sender);

        // distinguish between mouse click and dragging (to prevent misfire)
        if (Math.Abs(curPoint.X - _downCursorPoint.X) >= 60 ||
            Math.Abs(curPoint.Y - _downCursorPoint.Y) >= 60)
        {
            _isLeftDragging = false;

            // change to normal window
            FL.FlyleafHost.Owner.WindowState = WindowState.Normal;

            // start dragging
            DragMoveOwner();
        }
    }

    private void DragMoveOwner()
    {
        // (!SeekBarShowOnlyMouseOver) always show cursor when moving
        // (SeekBarShowOnlyMouseOver)  prevent to activate seek bar
        if (!FL.Config.SeekBarShowOnlyMouseOver)
        {
            FL.Player.Activity.IsEnabled = false;
        }

        FL.FlyleafHost!.Owner.DragMove();

        if (!FL.Config.SeekBarShowOnlyMouseOver)
        {
            FL.Player.Activity.IsEnabled = true;
        }
    }

    private void SurfaceOnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isLeftDragging)
            return;

        _isLeftDragging = false;

        MouseLeftButtonUpAction(_downPoint, _downTick);
    }

    private void SurfaceOnLostMouseCapture(object sender, MouseEventArgs e)
    {
        _isLeftDragging = false;
    }

    private void MouseLeftButtonUpAction(Point downPoint, long downTick)
    {
        Point upPoint = FL.FlyleafHost!.Surface.PointToScreen(default);

        if (downPoint == upPoint // if not moved at all
            ||
            Stopwatch.GetElapsedTime(downTick) <= TimeSpan.FromMilliseconds(200)
            &&                   // if click within 200ms and not so much moved
            Math.Abs(upPoint.X - downPoint.X) < 60 && Math.Abs(upPoint.Y - downPoint.Y) < 60)
        {
            SingleClickAction();
        }
    }

    private void SingleClickAction()
    {
        // Toggle play/pause on left click
        if (FL.Config.MouseSingleClickToPlay && FL.Player.CanPlay)
            FL.Player.TogglePlayPause();
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
        else
        {
            if (FL.Config.MouseWheelToVolumeUpDown)
            {
                if (e.Delta > 0)
                {
                    FL.Player.Audio.VolumeUp();
                }
                else
                {
                    FL.Player.Audio.VolumeDown();
                }
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
                case nameof(player.ReversePlayback):
                    OSDMessage = $"Reverse Playback {(player.ReversePlayback ? "On" : "Off")}";
                    break;
                case nameof(player.LoopPlayback):
                    OSDMessage = $"Loop Playback {(player.LoopPlayback ? "On" : "Off")}";
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
                    OSDIcon = player.Audio.Mute ? PackIconKind.VolumeOff : PackIconKind.VolumeHigh;
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

        // app config
        FL.Config.PropertyChanged += (o, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(FL.Config.AlwaysOnTop):
                    OSDMessage = $"Always on Top {(FL.Config.AlwaysOnTop ? "On" : "Off")}";
                    break;
            }
        };

        FL.Config.Subs.PropertyChanged += (o, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(FL.Config.Subs.SubsAutoTextCopy):
                    OSDMessage = $"Subs Auto Text Copy {(FL.Config.Subs.SubsAutoTextCopy ? "On" : "Off")}";
                    break;
            }
        };
    }

    private CancellationTokenSource? _cancelMsgToken;

    public string OSDMessage
    {
        get => _osdMessage;
        set
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
        }
    }
    private string _osdMessage = "";

    public PackIconKind? OSDIcon
    {
        get => _osdIcon;
        set
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
        }
    }
    private PackIconKind? _osdIcon;

    public bool IsOSDIcon => OSDIcon != null;

    private async Task ClearOSD(int timeoutMs, CancellationToken token)
    {
        await Task.Delay(timeoutMs, token);

        if (token.IsCancellationRequested)
            return;

        Utils.UI(() =>
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

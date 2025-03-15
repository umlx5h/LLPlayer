using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using LLPlayer.Extensions;
using LLPlayer.Views;
using WpfColorFontDialog;
using KeyBinding = FlyleafLib.MediaPlayer.KeyBinding;

namespace LLPlayer.Services;

public class AppActions
{
    private readonly Player _player;
    private readonly AppConfig _config;
    private readonly IDialogService _dialogService;

    public AppActions(Player player, AppConfig config, IDialogService dialogService)
    {
        _player = player;
        _config = config;
        _dialogService = dialogService;

        CustomActions = GetCustomActions();

        RegisterCustomKeyBindingsAction();
    }

    private void RegisterCustomKeyBindingsAction()
    {
        // Since the action name is defined in PlayerConfig, get the Action name from there and register delegate.
        foreach (var binding in _player.Config.Player.KeyBindings.Keys)
        {
            if (binding.Action == KeyBindingAction.Custom && !string.IsNullOrEmpty(binding.ActionName))
            {
                if (Enum.TryParse(binding.ActionName, out CustomKeyBindingAction key))
                {
                    binding.SetAction(CustomActions[key], binding.IsKeyUp);
                }
            }
        }
    }

    public Dictionary<CustomKeyBindingAction, Action> CustomActions { get; }

    private Dictionary<CustomKeyBindingAction, Action> GetCustomActions()
    {
        return new Dictionary<CustomKeyBindingAction, Action>
        {
            [CustomKeyBindingAction.OpenNextFile] = CmdOpenNextFile.Execute,
            [CustomKeyBindingAction.OpenPrevFile] = CmdOpenPrevFile.Execute,
            [CustomKeyBindingAction.OpenCurrentPath] = CmdOpenCurrentPath.Execute,

            [CustomKeyBindingAction.SubsPositionUp] = CmdSubsPositionUp.Execute,
            [CustomKeyBindingAction.SubsPositionDown] = CmdSubsPositionDown.Execute,
            [CustomKeyBindingAction.SubsSizeIncrease] = CmdSubsSizeIncrease.Execute,
            [CustomKeyBindingAction.SubsSizeDecrease] = CmdSubsSizeDecrease.Execute,
            [CustomKeyBindingAction.SubsPrimarySizeIncrease] = CmdSubsPrimarySizeIncrease.Execute,
            [CustomKeyBindingAction.SubsPrimarySizeDecrease] = CmdSubsPrimarySizeDecrease.Execute,
            [CustomKeyBindingAction.SubsSecondarySizeIncrease] = CmdSubsSecondarySizeIncrease.Execute,
            [CustomKeyBindingAction.SubsSecondarySizeDecrease] = CmdSubsSecondarySizeDecrease.Execute,
            [CustomKeyBindingAction.SubsDistanceIncrease] = CmdSubsDistanceIncrease.Execute,
            [CustomKeyBindingAction.SubsDistanceDecrease] = CmdSubsDistanceDecrease.Execute,

            [CustomKeyBindingAction.SubsTextCopy] = () => CmdSubsTextCopy.Execute(false),
            [CustomKeyBindingAction.SubsPrimaryTextCopy] = () => CmdSubsPrimaryTextCopy.Execute(false),
            [CustomKeyBindingAction.SubsSecondaryTextCopy] = () => CmdSubsSecondaryTextCopy.Execute(false),
            [CustomKeyBindingAction.ToggleSubsAutoTextCopy] = CmdToggleSubsAutoTextCopy.Execute,
            [CustomKeyBindingAction.ToggleSidebarShowSecondary] = CmdToggleSidebarShowSecondary.Execute,
            [CustomKeyBindingAction.ToggleSidebarShowOriginalText] = CmdToggleSidebarShowOriginalText.Execute,

            [CustomKeyBindingAction.ToggleSidebar] = CmdToggleSidebar.Execute,
            [CustomKeyBindingAction.ToggleDebugOverlay] = CmdToggleDebugOverlay.Execute,

            [CustomKeyBindingAction.OpenWindowSettings] = CmdOpenWindowSettings.Execute,
            [CustomKeyBindingAction.OpenWindowSubsDownloader] = CmdOpenWindowSubsDownloader.Execute,
            [CustomKeyBindingAction.OpenWindowSubsExporter] = CmdOpenWindowSubsExporter.Execute,
            [CustomKeyBindingAction.OpenWindowCheatSheet] = CmdOpenWindowCheatSheet.Execute,

            [CustomKeyBindingAction.AppNew] = CmdAppNew.Execute,
            [CustomKeyBindingAction.AppClone] = CmdAppClone.Execute,
            [CustomKeyBindingAction.AppRestart] = CmdAppRestart.Execute,
            [CustomKeyBindingAction.AppExit] = CmdAppExit.Execute,
        };
    }

    public static List<KeyBinding> DefaultCustomActionsMap()
    {
        return
        [
            new() { ActionName = nameof(CustomKeyBindingAction.SubsPositionUp), Key = Key.Up, Shift = true },
            new() { ActionName = nameof(CustomKeyBindingAction.SubsPositionDown), Key = Key.Down, Shift = true },
            new() { ActionName = nameof(CustomKeyBindingAction.SubsSizeIncrease), Key = Key.Right, Shift = true },
            new() { ActionName = nameof(CustomKeyBindingAction.SubsSizeDecrease), Key = Key.Left, Shift = true },
            new() { ActionName = nameof(CustomKeyBindingAction.SubsSecondarySizeIncrease), Key = Key.Right, Ctrl = true, Shift = true },
            new() { ActionName = nameof(CustomKeyBindingAction.SubsSecondarySizeDecrease), Key = Key.Left, Ctrl = true, Shift = true },
            new() { ActionName = nameof(CustomKeyBindingAction.SubsDistanceIncrease), Key = Key.Up, Ctrl = true, Shift = true },
            new() { ActionName = nameof(CustomKeyBindingAction.SubsDistanceDecrease), Key = Key.Down, Ctrl = true, Shift = true },
            new() { ActionName = nameof(CustomKeyBindingAction.SubsPrimaryTextCopy), Key = Key.C, Ctrl = true, IsKeyUp = true },
            new() { ActionName = nameof(CustomKeyBindingAction.ToggleSubsAutoTextCopy), Key = Key.A, Alt = true, IsKeyUp = true },
            new() { ActionName = nameof(CustomKeyBindingAction.ToggleSidebar), Key = Key.B, Ctrl = true, IsKeyUp = true },
            new() { ActionName = nameof(CustomKeyBindingAction.ToggleDebugOverlay), Key = Key.D, Ctrl = true, Shift = true, IsKeyUp = true },
            new() { ActionName = nameof(CustomKeyBindingAction.OpenWindowSettings), Key = Key.OemComma, Ctrl = true, IsKeyUp = true },
            new() { ActionName = nameof(CustomKeyBindingAction.OpenWindowCheatSheet), Key = Key.F1, IsKeyUp = true },
            new() { ActionName = nameof(CustomKeyBindingAction.AppNew), Key = Key.N, Ctrl = true, IsKeyUp = true },
        ];
    }

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    #region Command used in key

    public DelegateCommand CmdOpenNextFile => field ?? new(() =>
    {
        OpenNextPrevInternal(isNext: true);
    });

    public DelegateCommand CmdOpenPrevFile => field ?? new(() =>
    {
        OpenNextPrevInternal(isNext: false);
    });

    private void OpenNextPrevInternal(bool isNext)
    {
        var playlist = _player.Playlist;
        if (playlist.Url == null)
            return;

        string url = playlist.Url;

        try
        {
            (string? prev, string? next) = FileHelper.GetNextAndPreviousFile(url);
            if (isNext && next != null)
            {
                _player.OpenAsync(next);
            }
            else if (!isNext && prev != null)
            {
                _player.OpenAsync(prev);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"OpenNextPrevFile is failed: {ex.Message}");
        }
    }

    public DelegateCommand CmdOpenCurrentPath => field ?? new(() =>
    {
        var playlist = _player.Playlist;
        if (playlist.Selected == null)
            return;

        string url = playlist.Url;

        bool isFile = File.Exists(url);
        bool isUrl = url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https:", StringComparison.OrdinalIgnoreCase);

        if (!isFile && !isUrl)
        {
            return;
        }

        if (isUrl)
        {
            // fix url
            url = playlist.Selected.DirectUrl;
        }

        // Open folder or URL
        string? fileName = isFile ? Path.GetDirectoryName(url) : url;

        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = true,
            Verb = "open"
        });
    });

    public DelegateCommand CmdSubsPositionUp => field ?? new(() =>
    {
        SubsPositionUpActionInternal(true);
    });

    public DelegateCommand CmdSubsPositionDown => field ?? new(() =>
    {
        SubsPositionUpActionInternal(false);
    });

    public DelegateCommand CmdSubsSizeIncrease => field ?? new(() =>
    {
        SubsSizeActionInternal(SubsSizeActionType.All, increase: true);
    });

    public DelegateCommand CmdSubsSizeDecrease => field ?? new(() =>
    {
        SubsSizeActionInternal(SubsSizeActionType.All, increase: false);
    });

    public DelegateCommand CmdSubsPrimarySizeIncrease => field ?? new(() =>
    {
        SubsSizeActionInternal(SubsSizeActionType.Primary, increase: true);
    });

    public DelegateCommand CmdSubsPrimarySizeDecrease => field ?? new(() =>
    {
        SubsSizeActionInternal(SubsSizeActionType.Primary, increase: false);
    });

    public DelegateCommand CmdSubsSecondarySizeIncrease => field ?? new(() =>
    {
        SubsSizeActionInternal(SubsSizeActionType.Secondary, increase: true);
    });

    public DelegateCommand CmdSubsSecondarySizeDecrease => field ?? new(() =>
    {
        SubsSizeActionInternal(SubsSizeActionType.Secondary, increase: false);
    });

    private void SubsSizeActionInternal(SubsSizeActionType type, bool increase)
    {
        var primary = _player.Subtitles[0];
        var secondary = _player.Subtitles[1];

        if (type is SubsSizeActionType.All or SubsSizeActionType.Primary)
        {
            // TODO: L: Match scaling ratios in text and bitmap subtitles
            if (primary.Enabled && (!primary.IsBitmap || !string.IsNullOrEmpty(primary.Data.Text)))
            {
                _config.Subs.SubsFontSize += _config.Subs.SubsFontSizeOffset * (increase ? 1 : -1);
            }
            else if (primary.Enabled && primary.IsBitmap)
            {
                _player.Subtitles[0].Data.BitmapPosition.ConfScale += _config.Subs.SubsBitmapScaleOffset / 100.0 * (increase ? 1.0 : -1.0);
            }
        }

        if (type is SubsSizeActionType.All or SubsSizeActionType.Secondary)
        {
            if (secondary.Enabled && (!secondary.IsBitmap || !string.IsNullOrEmpty(secondary.Data.Text)))
            {
                _config.Subs.SubsFontSize2 += _config.Subs.SubsFontSizeOffset * (increase ? 1 : -1);
            }
            else if (secondary.Enabled && secondary.IsBitmap)
            {
                _player.Subtitles[1].Data.BitmapPosition.ConfScale += _config.Subs.SubsBitmapScaleOffset / 100.0 * (increase ? 1.0 : -1.0);
            }
        }
    }

    public DelegateCommand CmdSubsDistanceIncrease => field ?? new(() =>
    {
        SubsDistanceActionInternal(true);
    });

    public DelegateCommand CmdSubsDistanceDecrease => field ?? new(() =>
    {
        SubsDistanceActionInternal(false);
    });

    private void SubsDistanceActionInternal(bool increase)
    {
        _config.Subs.SubsDistance += _config.Subs.SubsDistanceOffset * (increase ? 1 : -1);
    }

    public DelegateCommand<bool?> CmdSubsTextCopy => field ?? new((suppressOsd) =>
    {
        if (!_player.Subtitles[0].Enabled && !_player.Subtitles[1].Enabled)
        {
            return;
        }

        string text = _player.Subtitles[0].Data.Text;
        if (!string.IsNullOrEmpty(_player.Subtitles[1].Data.Text))
        {
            text += Environment.NewLine + "( " + _player.Subtitles[1].Data.Text + " )";
        }

        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                WindowsClipboard.SetText(text);
                if (!suppressOsd.HasValue || !suppressOsd.Value)
                {
                    _player.OSDMessage = "Copy All Subtitle Text";
                }
            }
            catch
            {
                // ignored
            }
        }
    });

    public DelegateCommand<bool?> CmdSubsPrimaryTextCopy => field ?? new((suppressOsd) =>
    {
        SubsTextCopyInternal(0, suppressOsd);
    });

    public DelegateCommand<bool?> CmdSubsSecondaryTextCopy => field ?? new((suppressOsd) =>
    {
        SubsTextCopyInternal(1, suppressOsd);
    });

    private void SubsTextCopyInternal(int subIndex, bool? suppressOsd)
    {
        if (!_player.Subtitles[subIndex].Enabled)
        {
            return;
        }

        string text = _player.Subtitles[subIndex].Data.Text;

        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                WindowsClipboard.SetText(text);
                if (!suppressOsd.HasValue || !suppressOsd.Value)
                {
                    _player.OSDMessage = $"Copy {(subIndex == 0 ? "Primary" : "Secondary")} Subtitle Text";
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    public DelegateCommand CmdToggleSubsAutoTextCopy => field ?? new(() =>
    {
        _config.Subs.SubsAutoTextCopy = !_config.Subs.SubsAutoTextCopy;
    });

    public DelegateCommand CmdToggleSidebarShowSecondary => field ?? new(() =>
    {
        _config.SidebarShowSecondary = !_config.SidebarShowSecondary;
    });

    public DelegateCommand CmdToggleSidebarShowOriginalText => field ?? new(() =>
    {
        _config.SidebarShowOriginalText = !_config.SidebarShowOriginalText;
    });

    public DelegateCommand CmdToggleSidebar => field ?? new(() =>
    {
        _config.ShowSidebar = !_config.ShowSidebar;
    });

    public DelegateCommand CmdToggleDebugOverlay => field ?? new(() =>
    {
        _config.ShowDebug = !_config.ShowDebug;
    });

    public DelegateCommand CmdOpenWindowSettings => field ?? new(() =>
    {
        if (_player.IsPlaying)
        {
            _player.Pause();
        }

        // Detects configuration changes necessary for restart
        // TODO: L: refactor
        bool requiredRestart = false;

        _config.PropertyChanged += ConfigOnPropertyChanged;
        _player.Config.Subtitles.PropertyChanged += ConfigOnPropertyChanged;

        void ConfigOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(_config.IsDarkTitlebar):
                case nameof(_player.Config.Subtitles.WhisperRuntimeLibraries):
                    requiredRestart = true;
                    break;
            }
        }

        _player.Activity.ForceFullActive();
        _dialogService.ShowSingleton(nameof(SettingsDialog), result =>
        {
            // Activate as it may be minimized for some reason
            if (!Application.Current.MainWindow!.IsActive)
            {
                Application.Current.MainWindow!.Activate();
            }

            _config.PropertyChanged -= ConfigOnPropertyChanged;
            _player.Config.Subtitles.PropertyChanged -= ConfigOnPropertyChanged;

            if (result.Result == ButtonResult.OK)
            {
                SaveAllConfig();

                if (requiredRestart)
                {
                    // Show confirmation dialog
                    MessageBoxResult confirm = MessageBox.Show(
                        "A restart is required for the settings to take effect. Do you want to restart?",
                        "Confirm to restart",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question
                    );

                    if (confirm == MessageBoxResult.Yes)
                    {
                        CmdAppRestart.Execute();
                    }
                }
            }
        }, false);
    });

    public DelegateCommand CmdOpenWindowSubsDownloader => field ?? new(() =>
    {
        _player.Activity.ForceFullActive();
        _dialogService.ShowSingleton(nameof(SubtitlesDownloaderDialog), true);
    });

    public DelegateCommand CmdOpenWindowSubsExporter => field ?? new(() =>
    {
        _player.Activity.ForceFullActive();
        _dialogService.ShowSingleton(nameof(SubtitlesExportDialog), _ =>
        {
            // Activate as it may be minimized for some reason
            if (!Application.Current.MainWindow!.IsActive)
            {
                Application.Current.MainWindow!.Activate();
            }
        }, false);
    });

    public DelegateCommand CmdOpenWindowCheatSheet => field ?? new(() =>
    {
        _player.Activity.ForceFullActive();
        _dialogService.ShowSingleton(nameof(CheatSheetDialog), true);
    });

    public DelegateCommand CmdAppNew => field ?? new(() =>
    {
        string exePath = Process.GetCurrentProcess().MainModule!.FileName;
        Process.Start(exePath);
    });

    public DelegateCommand CmdAppClone => field ?? new(() =>
    {
        string exePath = Process.GetCurrentProcess().MainModule!.FileName;

        ProcessStartInfo startInfo = new()
        {
            FileName = exePath,
            UseShellExecute = false
        };

        // Launch New App with current url if opened
        var prevPlaylist = _player.Playlist.Selected;
        if (prevPlaylist != null)
        {
            startInfo.ArgumentList.Add(prevPlaylist.DirectUrl);
        }

        Process.Start(startInfo);
    });

    public DelegateCommand CmdAppRestart => field ?? new(() =>
    {
        // Clone
        CmdAppClone.Execute();

        // Exit
        CmdAppExit.Execute();
    });

    public DelegateCommand CmdAppExit => field ?? new(() =>
    {
        Application.Current.Shutdown();
    });
    #endregion

    #region Command not used in key
    private static FontWeightConverter _fontWeightConv = new();
    private static FontStyleConverter _fontStyleConv = new();
    private static FontStretchConverter _fontStretchConv = new();

    public DelegateCommand CmdSetSubtitlesFont => field ??= new(() =>
    {
        ColorFontDialog dialog = new();

        dialog.Font = new FontInfo(new FontFamily(_config.Subs.SubsFontFamily), _config.Subs.SubsFontSize, (FontStyle)_fontStyleConv.ConvertFromString(_config.Subs.SubsFontStyle)!, (FontStretch)_fontStretchConv.ConvertFromString(_config.Subs.SubsFontStretch)!, (FontWeight)_fontWeightConv.ConvertFromString(_config.Subs.SubsFontWeight)!, new SolidColorBrush(_config.Subs.SubsFontColor));

        _player.Activity.ForceFullActive();

        if (dialog.ShowDialog() == true && dialog.Font != null)
        {
            _config.Subs.SubsFontFamily = dialog.Font.Family.ToString();
            _config.Subs.SubsFontSize = dialog.Font.Size;
            _config.Subs.SubsFontWeight = dialog.Font.Weight.ToString();
            _config.Subs.SubsFontStretch = dialog.Font.Stretch.ToString();
            _config.Subs.SubsFontStyle = dialog.Font.Style.ToString();
            _config.Subs.SubsFontColor = dialog.Font.BrushColor.Color;
        }
    });

    public DelegateCommand CmdSetSubtitlesFontSidebar => field ??= new(() =>
    {
        ColorFontDialog dialog = new();

        dialog.Font = new FontInfo(new FontFamily(_config.SidebarFontFamily), _config.SidebarFontSize, FontStyles.Normal, FontStretches.Normal, (FontWeight)_fontWeightConv.ConvertFromString(_config.SidebarFontWeight)!, new SolidColorBrush(Colors.Black));

        _player.Activity.ForceFullActive();

        if (dialog.ShowDialog() == true && dialog.Font != null)
        {
            _config.SidebarFontFamily = dialog.Font.Family.ToString();
            _config.SidebarFontSize = dialog.Font.Size;
            _config.SidebarFontWeight = dialog.Font.Weight.ToString();
        }
    });

    public DelegateCommand CmdResetSubsPosition => field ??= new(() =>
    {
        // TODO: L: Reset bitmap as well
        _config.Subs.ResetSubsPosition();
    });

    public DelegateCommand<AspectRatio?> CmdChangeAspectRatio => field ??= new((ratio) =>
    {
        if (!ratio.HasValue)
            return;

        _player.Config.Video.AspectRatio = ratio.Value;
    });

    public DelegateCommand<SubPositionAlignment?> CmdSetSubPositionAlignment => field ??= new((alignment) =>
    {
        if (!alignment.HasValue)
            return;

        _config.Subs.SubsPositionAlignment = alignment.Value;
    });

    public DelegateCommand<SubPositionAlignment?> CmdSetSubPositionAlignmentWhenDual => field ??= new((alignment) =>
    {
        if (!alignment.HasValue)
            return;

        _config.Subs.SubsPositionAlignmentWhenDual = alignment.Value;
    });

    #endregion

    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    // TODO: L: make it command?
    public void SaveAllConfig()
    {
        _config.Save(App.AppConfigPath);
        _player.Config.Save(App.PlayerConfigPath, AppConfig.GetJsonSerializerOptions());
        Engine.Config.Save(App.EngineConfigPath, AppConfig.GetJsonSerializerOptions());
    }

    private enum SubsSizeActionType
    {
        All,
        Primary,
        Secondary
    }

    private void SubsPositionUpActionInternal(bool up)
    {
        var primary = _player.Subtitles[0];
        var secondary = _player.Subtitles[1];

        bool bothEnabled = primary.Enabled && secondary.Enabled;

        if (bothEnabled || // both enabled
            primary.Enabled && !primary.IsBitmap || //  When text subtitles are enabled
            secondary.Enabled && !secondary.IsBitmap || !string.IsNullOrEmpty(primary.Data.Text) || //  If OCR subtitles are available
            !string.IsNullOrEmpty(secondary.Data.Text))
        {
            _config.Subs.SubsPosition += _config.Subs.SubsPositionOffset * (up ? -1 : 1);
        }
        else if (primary.IsBitmap || secondary.IsBitmap)
        {
            // For bitmap subtitles (absolute position)
            for (int i = 0; i < _player.Config.Subtitles.Max; i++)
            {
                _player.Subtitles[i].Data.BitmapPosition.ConfPos += _config.Subs.SubsPositionOffset * (up ? -1 : 1);
            }
        }
    }
}

// List of key actions to be added from the app side
public enum CustomKeyBindingAction
{
    [Description("Open Next File")]
    OpenNextFile,
    [Description("Open Previous File")]
    OpenPrevFile,
    [Description("Open Folder or URL of the currently opened file")]
    OpenCurrentPath,

    [Description("Subtitles Position Up")]
    SubsPositionUp,
    [Description("Subtitles Position Down")]
    SubsPositionDown,
    [Description("Subtitles Size Increase")]
    SubsSizeIncrease,
    [Description("Subtitles Size Decrease")]
    SubsSizeDecrease,
    [Description("Primary Subtitles Size Increase")]
    SubsPrimarySizeIncrease,
    [Description("Primary Subtitles Size Decrease")]
    SubsPrimarySizeDecrease,
    [Description("Secondary Subtitles Size Increase")]
    SubsSecondarySizeIncrease,
    [Description("Secondary Subtitles Size Decrease")]
    SubsSecondarySizeDecrease,
    [Description("Primary/Secondary Subtitles Distance Increase")]
    SubsDistanceIncrease,
    [Description("Primary/Secondary Subtitles Distance Decrease")]
    SubsDistanceDecrease,

    [Description("Copy All Subtiltes Text")]
    SubsTextCopy,
    [Description("Copy Primary Subtiltes Text")]
    SubsPrimaryTextCopy,
    [Description("Copy Secondary Subtiltes Text")]
    SubsSecondaryTextCopy,
    [Description("Toggle Auto Subtitles Text Copy")]
    ToggleSubsAutoTextCopy,

    [Description("Toggle Primary / Secondary in Subtitles Sidebar")]
    ToggleSidebarShowSecondary,
    [Description("Toggle to show original text in Subtitles Sidebar")]
    ToggleSidebarShowOriginalText,

    [Description("Toggle Subitltes Sidebar")]
    ToggleSidebar,
    [Description("Toggle Debug Overlay")]
    ToggleDebugOverlay,

    [Description("Open Settings Window")]
    OpenWindowSettings,
    [Description("Open Subtitles Downloader Window")]
    OpenWindowSubsDownloader,
    [Description("Open Subtitles Exporter Window")]
    OpenWindowSubsExporter,
    [Description("Open Cheat Sheet Window")]
    OpenWindowCheatSheet,

    [Description("Launch New Application")]
    AppNew,
    [Description("Launch Clone Application")]
    AppClone,
    [Description("Restart Application")]
    AppRestart,
    [Description("Exit Application")]
    AppExit,
}

public enum KeyBindingActionGroup
{
    Playback,
    Player,
    Audio,
    Video,
    Subtitles,
    SubtitlesPosition,
    Window,
    Other
}

public static class KeyBindingActionExtensions
{
    public static string ToString(this KeyBindingActionGroup group)
    {
        var str = group.ToString();
        if (group == KeyBindingActionGroup.SubtitlesPosition)
        {
            str = "Subtitles Position";
        }

        return str;
    }

    public static KeyBindingActionGroup ToGroup(this CustomKeyBindingAction action)
    {
        switch (action)
        {
            case CustomKeyBindingAction.OpenNextFile:
            case CustomKeyBindingAction.OpenPrevFile:
            case CustomKeyBindingAction.OpenCurrentPath:
                return KeyBindingActionGroup.Player; // TODO: L: ?

            case CustomKeyBindingAction.SubsPositionUp:
            case CustomKeyBindingAction.SubsPositionDown:
            case CustomKeyBindingAction.SubsSizeIncrease:
            case CustomKeyBindingAction.SubsSizeDecrease:
            case CustomKeyBindingAction.SubsPrimarySizeIncrease:
            case CustomKeyBindingAction.SubsPrimarySizeDecrease:
            case CustomKeyBindingAction.SubsSecondarySizeIncrease:
            case CustomKeyBindingAction.SubsSecondarySizeDecrease:
            case CustomKeyBindingAction.SubsDistanceIncrease:
            case CustomKeyBindingAction.SubsDistanceDecrease:
                return KeyBindingActionGroup.SubtitlesPosition;

            case CustomKeyBindingAction.SubsTextCopy:
            case CustomKeyBindingAction.SubsPrimaryTextCopy:
            case CustomKeyBindingAction.SubsSecondaryTextCopy:
            case CustomKeyBindingAction.ToggleSubsAutoTextCopy:
            case CustomKeyBindingAction.ToggleSidebarShowSecondary:
            case CustomKeyBindingAction.ToggleSidebarShowOriginalText:
                return KeyBindingActionGroup.Subtitles;

            case CustomKeyBindingAction.ToggleSidebar:
            case CustomKeyBindingAction.ToggleDebugOverlay:
            case CustomKeyBindingAction.OpenWindowSettings:
            case CustomKeyBindingAction.OpenWindowSubsDownloader:
            case CustomKeyBindingAction.OpenWindowSubsExporter:
            case CustomKeyBindingAction.OpenWindowCheatSheet:
                return KeyBindingActionGroup.Window;

            // TODO: L: review group
            case CustomKeyBindingAction.AppNew:
            case CustomKeyBindingAction.AppClone:
            case CustomKeyBindingAction.AppRestart:
            case CustomKeyBindingAction.AppExit:
                return KeyBindingActionGroup.Other;

            default:
                return KeyBindingActionGroup.Other;
        }
    }

    public static KeyBindingActionGroup ToGroup(this KeyBindingAction action)
    {
        switch (action)
        {
            // TODO: L: review order and grouping
            case KeyBindingAction.ForceIdle:
            case KeyBindingAction.ForceActive:
            case KeyBindingAction.ForceFullActive:
                return KeyBindingActionGroup.Player;

            case KeyBindingAction.AudioDelayAdd:
            case KeyBindingAction.AudioDelayAdd2:
            case KeyBindingAction.AudioDelayRemove:
            case KeyBindingAction.AudioDelayRemove2:
            case KeyBindingAction.ToggleAudio:
            case KeyBindingAction.ToggleMute:
            case KeyBindingAction.VolumeUp:
            case KeyBindingAction.VolumeDown:
                return KeyBindingActionGroup.Audio;

            case KeyBindingAction.SubsDelayAddPrimary:
            case KeyBindingAction.SubsDelayAdd2Primary:
            case KeyBindingAction.SubsDelayRemovePrimary:
            case KeyBindingAction.SubsDelayRemove2Primary:
            case KeyBindingAction.SubsDelayAddSecondary:
            case KeyBindingAction.SubsDelayAdd2Secondary:
            case KeyBindingAction.SubsDelayRemoveSecondary:
            case KeyBindingAction.SubsDelayRemove2Secondary:
                return KeyBindingActionGroup.SubtitlesPosition;
            case KeyBindingAction.ToggleSubtitlesVisibility:
            case KeyBindingAction.ToggleSubtitlesVisibilityPrimary:
            case KeyBindingAction.ToggleSubtitlesVisibilitySecondary:
                return KeyBindingActionGroup.Subtitles;

            case KeyBindingAction.CopyToClipboard:
            case KeyBindingAction.CopyItemToClipboard:
                return KeyBindingActionGroup.Other; // TODO: L: ?

            case KeyBindingAction.OpenFromClipboard:
            case KeyBindingAction.OpenFromClipboardSafe:
            case KeyBindingAction.OpenFromFileDialog:
                return KeyBindingActionGroup.Player; // TODO: L: ?

            case KeyBindingAction.Stop:
            case KeyBindingAction.Pause:
            case KeyBindingAction.Play:
            case KeyBindingAction.TogglePlayPause:
            case KeyBindingAction.ToggleReversePlayback:
            case KeyBindingAction.ToggleLoopPlayback:
            case KeyBindingAction.SeekForward:
            case KeyBindingAction.SeekBackward:
            case KeyBindingAction.SeekForward2:
            case KeyBindingAction.SeekBackward2:
            case KeyBindingAction.SeekForward3:
            case KeyBindingAction.SeekBackward3:
            case KeyBindingAction.SeekForward4:
            case KeyBindingAction.SeekBackward4:
                return KeyBindingActionGroup.Playback;

            case KeyBindingAction.Flush:
            case KeyBindingAction.NormalScreen:
            case KeyBindingAction.FullScreen:
            case KeyBindingAction.ToggleFullScreen:
                return KeyBindingActionGroup.Player;

            case KeyBindingAction.ToggleVideo:
            case KeyBindingAction.ToggleKeepRatio:
            case KeyBindingAction.ToggleVideoAcceleration:
            case KeyBindingAction.TakeSnapshot:
            case KeyBindingAction.ToggleRecording:
                return KeyBindingActionGroup.Video;

            case KeyBindingAction.SubsPrevSeek:
            case KeyBindingAction.SubsCurSeek:
            case KeyBindingAction.SubsNextSeek:
            case KeyBindingAction.SubsPrevSeekFallback:
            case KeyBindingAction.SubsNextSeekFallback:
                return KeyBindingActionGroup.Subtitles;
            case KeyBindingAction.ShowNextFrame:
            case KeyBindingAction.ShowPrevFrame:
            case KeyBindingAction.SpeedAdd:
            case KeyBindingAction.SpeedAdd2:
            case KeyBindingAction.SpeedRemove:
            case KeyBindingAction.SpeedRemove2:
            case KeyBindingAction.ToggleSeekAccurate:
                return KeyBindingActionGroup.Playback;

            case KeyBindingAction.ResetAll:
            case KeyBindingAction.ResetSpeed:
            case KeyBindingAction.ResetRotation:
            case KeyBindingAction.ResetZoom:
            case KeyBindingAction.ZoomIn:
            case KeyBindingAction.ZoomOut:
                return KeyBindingActionGroup.Player;

            default:
                return KeyBindingActionGroup.Other;
        }
    }

    /// <summary>
    /// Gets the value of the Description attribute assigned to the Enum member.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public static string GetDescription(this Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Type type = value.GetType();

        string name = value.ToString();

        MemberInfo[] member = type.GetMember(name);

        if (member.Length > 0)
        {
            if (Attribute.GetCustomAttribute(member[0], typeof(DescriptionAttribute)) is DescriptionAttribute attr)
            {
                return attr.Description;
            }
        }

        return name;
    }
}

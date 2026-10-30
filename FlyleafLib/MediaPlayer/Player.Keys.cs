using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace FlyleafLib.MediaPlayer;

partial class Player
{
    /* Player Key Bindings
     *
     * Config.Player.KeyBindings.Keys
     *
     * KeyDown / KeyUp Events (Control / WinFormsHost / WindowFront (FlyleafWindow))
     * Exposes KeyDown/KeyUp if required to listen on additional Controls/Windows
     * Allows KeyBindingAction.Custom to set an external Action for Key Binding
     */

    Tuple<KeyBinding,long> onKeyUpBinding;

    /// <summary>
    /// Can be used to route KeyDown events (WPF)
    /// </summary>
    /// <param name="player"></param>
    /// <param name="e"></param>
    public static bool KeyDown(Player player, KeyEventArgs e)
    {
        e.Handled = KeyDown(player, e.Key == Key.System ? e.SystemKey : e.Key);

        return e.Handled;
    }

    /// <summary>
    /// Can be used to route KeyDown events (WinForms)
    /// </summary>
    /// <param name="player"></param>
    /// <param name="e"></param>
    public static void KeyDown(Player player, System.Windows.Forms.KeyEventArgs e)
        => e.Handled = KeyDown(player, KeyInterop.KeyFromVirtualKey((int)e.KeyCode));

    /// <summary>
    /// Can be used to route KeyUp events (WPF)
    /// </summary>
    /// <param name="player"></param>
    /// <param name="e"></param>
    public static bool KeyUp(Player player, KeyEventArgs e)
    {
        e.Handled = KeyUp(player, e.Key == Key.System ? e.SystemKey : e.Key);

        return e.Handled;
    }

    /// <summary>
    /// Can be used to route KeyUp events (WinForms)
    /// </summary>
    /// <param name="player"></param>
    /// <param name="e"></param>
    public static void KeyUp(Player player, System.Windows.Forms.KeyEventArgs e)
        => e.Handled = KeyUp(player, KeyInterop.KeyFromVirtualKey((int)e.KeyCode));

    public static bool KeyDown(Player player, Key key)
    {
        if (player == null)
            return false;

        player.Activity.RefreshActive();

        if (player.onKeyUpBinding != null)
        {
            if (player.onKeyUpBinding.Item1.Key == key)
                return true;

            if (DateTime.UtcNow.Ticks - player.onKeyUpBinding.Item2 < TimeSpan.FromSeconds(2).Ticks)
                return false;

            player.onKeyUpBinding = null; // In case of keyboard lost capture (should be handled from hosts)
        }

        List<KeyBinding> keysList = new();
        var spanList = CollectionsMarshal.AsSpan(player.Config.Player.KeyBindings.Keys); // should create dictionary here with key+alt+ctrl+shift hash
        foreach(var binding in spanList)
            if (binding.Key == key && binding.IsEnabled)
                keysList.Add(binding);

        if (keysList.Count == 0)
            return false;

        bool alt, ctrl, shift;
        alt     = Keyboard.IsKeyDown(Key.LeftAlt)   || Keyboard.IsKeyDown(Key.RightAlt);
        ctrl    = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
        shift   = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

        var spanList2 = CollectionsMarshal.AsSpan(keysList);
        foreach(var binding in spanList2)
        {
            if (binding.Alt == alt && binding.Ctrl == ctrl && binding.Shift == shift && binding.IsEnabled)
            {
                if (binding.IsKeyUp)
                    player.onKeyUpBinding = new(binding, DateTime.UtcNow.Ticks);
                else
                    ExecuteBinding(player, binding, false);

                return true;
            }
        }

        return false;
    }
    public static bool KeyUp(Player player, Key key)
    {
        if (player == null || player.onKeyUpBinding == null || player.onKeyUpBinding.Item1.Key != key)
            return false;

        ExecuteBinding(player, player.onKeyUpBinding.Item1, true);
        player.onKeyUpBinding = null;
        return true;
    }

    static void ExecuteBinding(Player player, KeyBinding binding, bool isKeyUp)
    {
        if (CanDebug) player.Log.Debug($"[Keys|{(isKeyUp ? "Up" : "Down")}] {(binding.Action == KeyBindingAction.Custom && binding.ActionName != null ? binding.ActionName : binding.Action)}");
        binding.ActionInternal?.Invoke();
    }

    public Action GetKeyBindingAction(KeyBindingAction action)
        => keyBindingActions.GetValueOrDefault(action);
    Dictionary<KeyBindingAction, Action> keyBindingActions;
    void InitializeKeyBindingActions() => keyBindingActions = new()
    {
        [KeyBindingAction.ForceIdle]                = Activity.ForceIdle,
        [KeyBindingAction.ForceActive]              = Activity.ForceActive,
        [KeyBindingAction.ForceFullActive]          = Activity.ForceFullActive,

        [KeyBindingAction.AudioDelayAdd]            = Config.Audio.DelayAdd,
        [KeyBindingAction.AudioDelayRemove]         = Config.Audio.DelayRemove,
        [KeyBindingAction.AudioDelayAdd2]           = Config.Audio.DelayAdd2,
        [KeyBindingAction.AudioDelayRemove2]        = Config.Audio.DelayRemove2,
        [KeyBindingAction.ToggleAudio]              = Config.Audio.Toggle,
        [KeyBindingAction.ToggleMute]               = Config.Audio.ToggleMute,
        [KeyBindingAction.VolumeUp]                 = Config.Audio.VolumeUp,
        [KeyBindingAction.VolumeDown]               = Config.Audio.VolumeDown,

        [KeyBindingAction.TakeSnapshot]             = Commands.TakeSnapshotAction,
        [KeyBindingAction.NormalScreen]             = NormalScreen,
        [KeyBindingAction.FullScreen]               = FullScreen,
        [KeyBindingAction.ToggleFullScreen]         = ToggleFullScreen,
        [KeyBindingAction.ToggleRecording]          = ToggleRecording,
        [KeyBindingAction.ToggleVideo]              = Config.Video.Toggle,
        [KeyBindingAction.ToggleKeepRatio]          = Config.Video.ToggleKeepRatio,
        [KeyBindingAction.ToggleVideoAcceleration]  = Config.Video.ToggleVideoAcceleration,
        [KeyBindingAction.ZoomIn]                   = Config.Video.ZoomIn,
        [KeyBindingAction.ZoomOut]                  = Config.Video.ZoomOut,
        [KeyBindingAction.ShowPrevFrame]            = ShowFramePrev,
        [KeyBindingAction.ShowNextFrame]            = ShowFrameNext,

        [KeyBindingAction.SubsDelayAddPrimary]      = Config.Subtitles[0].DelayAdd,
        [KeyBindingAction.SubsDelayRemovePrimary]   = Config.Subtitles[0].DelayRemove,
        [KeyBindingAction.SubsDelayAdd2Primary]     = Config.Subtitles[0].DelayAdd2,
        [KeyBindingAction.SubsDelayRemove2Primary]  = Config.Subtitles[0].DelayRemove2,
        [KeyBindingAction.SubsDelayAddSecondary]    = Config.Subtitles[1].DelayAdd,
        [KeyBindingAction.SubsDelayRemoveSecondary] = Config.Subtitles[1].DelayRemove,
        [KeyBindingAction.SubsDelayAdd2Secondary]   = Config.Subtitles[1].DelayAdd2,
        [KeyBindingAction.SubsDelayRemove2Secondary]= Config.Subtitles[1].DelayRemove2,

        [KeyBindingAction.ToggleSubtitlesVisibility]          = Config.Subtitles.ToggleVisible,
        [KeyBindingAction.ToggleSubtitlesVisibilityPrimary]   = Config.Subtitles[0].ToggleVisible,
        [KeyBindingAction.ToggleSubtitlesVisibilitySecondary] = Config.Subtitles[1].ToggleVisible,

        [KeyBindingAction.OpenFromClipboard]        = OpenFromClipboard,
        [KeyBindingAction.OpenFromClipboardSafe]    = OpenFromClipboardSafe,
        [KeyBindingAction.OpenFromFileDialog]       = OpenFromFileDialog,
        [KeyBindingAction.OpenNextItem]             = OpenNextItem,
        [KeyBindingAction.OpenPrevItem]             = OpenPrevItem,
        [KeyBindingAction.CopyToClipboard]          = CopyToClipboard,
        [KeyBindingAction.CopyItemToClipboard]      = CopyItemToClipboard,

        [KeyBindingAction.Flush]                    = Flush,
        [KeyBindingAction.Stop]                     = Stop,
        [KeyBindingAction.Pause]                    = Pause,
        [KeyBindingAction.Play]                     = Play,
        [KeyBindingAction.TogglePlayPause]          = TogglePlayPause,
        [KeyBindingAction.ToggleReversePlayback]    = ToggleReversePlayback,
        [KeyBindingAction.ToggleLoopPlayback]       = ToggleLoopPlayback,

        [KeyBindingAction.ToggleSeekAccurate]       = ToggleSeekAccurate,
        [KeyBindingAction.SeekBackward]             = SeekBackward,
        [KeyBindingAction.SeekForward]              = SeekForward,
        [KeyBindingAction.SeekBackward2]            = SeekBackward2,
        [KeyBindingAction.SeekForward2]             = SeekForward2,
        [KeyBindingAction.SeekBackward3]            = SeekBackward3,
        [KeyBindingAction.SeekForward3]             = SeekForward3,
        [KeyBindingAction.SeekBackward4]            = SeekBackward4,
        [KeyBindingAction.SeekForward4]             = SeekForward4,
        [KeyBindingAction.SeekToStart]              = SeekToStart,
        [KeyBindingAction.SeekToEnd]                = SeekToEnd,

        [KeyBindingAction.SubsCurSeek]              = Subtitles.CurSeek,
        [KeyBindingAction.SubsPrevSeek]             = Subtitles.PrevSeek,
        [KeyBindingAction.SubsNextSeek]             = Subtitles.NextSeek,
        [KeyBindingAction.SubsPrevSeekFallback]     = Subtitles.PrevSeekFallback,
        [KeyBindingAction.SubsNextSeekFallback]     = Subtitles.NextSeekFallback,

        [KeyBindingAction.SubsCurSeek2]             = Subtitles.CurSeek2,
        [KeyBindingAction.SubsPrevSeek2]            = Subtitles.PrevSeek2,
        [KeyBindingAction.SubsNextSeek2]            = Subtitles.NextSeek2,
        [KeyBindingAction.SubsPrevSeekFallback2]    = Subtitles.PrevSeekFallback2,
        [KeyBindingAction.SubsNextSeekFallback2]    = Subtitles.NextSeekFallback2,

        [KeyBindingAction.SpeedAdd]                 = SpeedUp,
        [KeyBindingAction.SpeedAdd2]                = SpeedUp2,
        [KeyBindingAction.SpeedRemove]              = SpeedDown,
        [KeyBindingAction.SpeedRemove2]             = SpeedDown2,

        [KeyBindingAction.ResetAll]                 = ResetAll,
        [KeyBindingAction.ResetSpeed]               = ResetSpeed,
        [KeyBindingAction.ResetDelay]               = ResetDelay,
        [KeyBindingAction.ResetRotation]            = ResetRotation,
        [KeyBindingAction.ResetZoom]                = ResetZoom,
    };
}

public class KeysConfig
{
    /// <summary>
    /// Currently configured key bindings
    /// (Normally you should not access this directly)
    /// </summary>
    public List<KeyBinding> Keys            { get ; set; }

    Player player;

    public KeysConfig() { }

    public KeysConfig Clone()
    {
        KeysConfig keys = (KeysConfig) MemberwiseClone();
        keys.player     = null;
        keys.Keys       = null;

        return keys;
    }

    internal void SetPlayer(Player player)
    {
        Keys ??= [];

        if (!player.Config.Loaded && Keys.Count == 0)
            LoadDefault();

        foreach(var binding in Keys)
            if (binding.Action != KeyBindingAction.Custom)
                binding.ActionInternal = player.GetKeyBindingAction(binding.Action);

        this.player = player;
    }

    /// <summary>
    /// Adds a custom keybinding
    /// </summary>
    /// <param name="key">The key to bind</param>
    /// <param name="isKeyUp">If should fire on each keydown or just on keyup</param>
    /// <param name="action">The action to execute</param>
    /// <param name="actionName">A unique name to be able to identify it</param>
    /// <param name="alt">If Alt should be pressed</param>
    /// <param name="ctrl">If Ctrl should be pressed</param>
    /// <param name="shift">If Shift should be pressed</param>
    /// <exception cref="Exception">Keybinding already exists</exception>
    public void AddCustom(Key key, bool isKeyUp, Action action, string actionName, bool alt = false, bool ctrl = false, bool shift = false)
    {
        for (int i=0; i<Keys.Count; i++)
            if (Keys[i].Key == key && Keys[i].Alt == alt && Keys[i].Ctrl == ctrl && Keys[i].Shift == shift)
            {
                Keys[i].IsKeyUp         = isKeyUp;
                Keys[i].Action          = KeyBindingAction.Custom;
                Keys[i].ActionName      = actionName;
                Keys[i].ActionInternal  = action;

                return;
            }

        Keys.Add(new KeyBinding() { Alt = alt, Ctrl = ctrl, Shift = shift, Key = key, IsKeyUp = isKeyUp, Action = KeyBindingAction.Custom, ActionName = actionName, ActionInternal = action });
    }

    /// <summary>
    /// Adds a new key binding
    /// </summary>
    /// <param name="key">The key to bind</param>
    /// <param name="action">Which action from the available to assign</param>
    /// <param name="alt">If Alt should be pressed</param>
    /// <param name="ctrl">If Ctrl should be pressed</param>
    /// <param name="shift">If Shift should be pressed</param>
    /// <exception cref="Exception">Keybinding already exists</exception>
    public void Add(Key key, KeyBindingAction action, bool alt = false, bool ctrl = false, bool shift = false)
    {
        for (int i=0; i<Keys.Count; i++)
            if (Keys[i].Key == key && Keys[i].Alt == alt && Keys[i].Ctrl == ctrl && Keys[i].Shift == shift)
            {
                Keys[i].IsKeyUp         = isKeyUpBinding.Contains(action);
                Keys[i].Action          = action;
                Keys[i].ActionInternal  = player?.GetKeyBindingAction(action);

                return;
            }

        Keys.Add(new()
        {
            Alt     = alt,
            Ctrl    = ctrl,
            Shift   = shift,
            Key     = key,
            IsKeyUp = isKeyUpBinding.Contains(action),
            Action  = action,
            ActionInternal = player?.GetKeyBindingAction(action)
        });
    }

    public bool Exists(string actionName)
    {
        foreach (var keybinding in Keys)
            if (keybinding.ActionName == actionName)
                return true;

        return false;
    }

    public KeyBinding Get(string actionName)
    {
        foreach (var keybinding in Keys)
            if (keybinding.ActionName == actionName)
                return keybinding;

        return null;
    }

    /// <summary>
    /// Removes a binding based on Key/Ctrl combination
    /// </summary>
    /// <param name="key">The assigned key</param>
    /// <param name="alt">If Alt is assigned</param>
    /// <param name="ctrl">If Ctrl is assigned</param>
    /// <param name="shift">If Shift is assigned</param>
    public void Remove(Key key, bool alt = false, bool ctrl = false, bool shift = false)
    {
        for (int i=Keys.Count-1; i >=0; i--)
            if (Keys[i].Key == key && Keys[i].Alt == alt && Keys[i].Ctrl == ctrl && Keys[i].Shift == shift)
                Keys.RemoveAt(i);
    }

    /// <summary>
    /// Removes a binding based on assigned action
    /// </summary>
    /// <param name="action">The assigned action</param>
    public void Remove(KeyBindingAction action)
    {
        for (int i=Keys.Count-1; i >=0; i--)
            if (Keys[i].Action == action)
                Keys.RemoveAt(i);
    }

    /// <summary>
    /// Removes a binding based on assigned action's name
    /// </summary>
    /// <param name="actionName">The assigned action's name</param>
    public void Remove(string actionName)
    {
        for (int i=Keys.Count-1; i >=0; i--)
            if (Keys[i].ActionName == actionName)
                Keys.RemoveAt(i);
    }

    /// <summary>
    /// Removes all the bindings
    /// </summary>
    public void RemoveAll() => Keys.Clear();

    /// <summary>
    /// Resets to default bindings
    /// </summary>
    public void LoadDefault()
    {
        if (Keys == null)
            Keys = new List<KeyBinding>();
        else
            Keys.Clear();

        Add(Key.OemSemicolon,       KeyBindingAction.SubsDelayRemovePrimary);
        Add(Key.OemQuotes,          KeyBindingAction.SubsDelayAddPrimary);
        Add(Key.OemSemicolon,       KeyBindingAction.SubsDelayRemoveSecondary, shift: true);
        Add(Key.OemQuotes,          KeyBindingAction.SubsDelayAddSecondary, shift: true);

        Add(Key.A,                  KeyBindingAction.SubsPrevSeek);
        Add(Key.S,                  KeyBindingAction.SubsCurSeek);
        Add(Key.D,                  KeyBindingAction.SubsNextSeek);

        Add(Key.J,                  KeyBindingAction.SubsPrevSeek);
        Add(Key.K,                  KeyBindingAction.SubsCurSeek);
        Add(Key.L,                  KeyBindingAction.SubsNextSeek);

        // Mouse backford/forward button
        Add(Key.Left,               KeyBindingAction.SubsPrevSeekFallback, alt: true);
        Add(Key.Right,              KeyBindingAction.SubsNextSeekFallback, alt: true);

        Add(Key.V,                  KeyBindingAction.OpenFromClipboardSafe, ctrl: true);
        Add(Key.O,                  KeyBindingAction.OpenFromFileDialog, ctrl: true);
        Add(Key.C,                  KeyBindingAction.CopyToClipboard, ctrl: true, shift: true);

        Add(Key.Left,               KeyBindingAction.SeekBackward2);
        Add(Key.Left,               KeyBindingAction.SeekBackward, ctrl: true);
        Add(Key.Right,              KeyBindingAction.SeekForward2);
        Add(Key.Right,              KeyBindingAction.SeekForward, ctrl: true);

        Add(Key.S,                  KeyBindingAction.ToggleSeekAccurate, ctrl: true);

        Add(Key.OemPlus,            KeyBindingAction.SpeedAdd);
        Add(Key.OemPlus,            KeyBindingAction.SpeedAdd2, shift: true);
        Add(Key.OemMinus,           KeyBindingAction.SpeedRemove);
        Add(Key.OemMinus,           KeyBindingAction.SpeedRemove2, shift: true);

        Add(Key.OemPlus,            KeyBindingAction.ZoomIn, ctrl: true);
        Add(Key.OemMinus,           KeyBindingAction.ZoomOut, ctrl: true);

        Add(Key.F,                  KeyBindingAction.ToggleFullScreen);

        Add(Key.Space,              KeyBindingAction.TogglePlayPause);
        Add(Key.MediaPlayPause,     KeyBindingAction.TogglePlayPause);
        Add(Key.Play,               KeyBindingAction.TogglePlayPause);

        Add(Key.H,                  KeyBindingAction.ToggleSubtitlesVisibility);
        Add(Key.V,                  KeyBindingAction.ToggleSubtitlesVisibility);

        Add(Key.M,                  KeyBindingAction.ToggleMute);
        Add(Key.Up,                 KeyBindingAction.VolumeUp);
        Add(Key.Down,               KeyBindingAction.VolumeDown);

        Add(Key.D0,                 KeyBindingAction.ResetAll);
        Add(Key.D1,                 KeyBindingAction.ResetSpeed);

        Add(Key.Escape,             KeyBindingAction.NormalScreen);
        Add(Key.Q,                  KeyBindingAction.Stop, ctrl: true);
    }

    static HashSet<KeyBindingAction> isKeyUpBinding = new()
    {   // TODO: Should Fire once one KeyDown and not again until KeyUp is fired (in case of Tasks keep track of already running actions?)
        // Having issues with alt/ctrl/shift (should save state of alt/ctrl/shift on keydown and not checked on keyup)

        { KeyBindingAction.OpenFromClipboard },
        { KeyBindingAction.OpenFromClipboardSafe },
        { KeyBindingAction.OpenFromFileDialog },
        { KeyBindingAction.OpenNextItem },
        { KeyBindingAction.OpenPrevItem },
        { KeyBindingAction.CopyToClipboard },
        { KeyBindingAction.TakeSnapshot },
        { KeyBindingAction.NormalScreen },
        { KeyBindingAction.FullScreen },
        { KeyBindingAction.ToggleFullScreen },
        { KeyBindingAction.ToggleAudio },
        { KeyBindingAction.ToggleVideo },
        { KeyBindingAction.ToggleKeepRatio },
        { KeyBindingAction.ToggleVideoAcceleration },
        { KeyBindingAction.ToggleSubtitlesVisibility },
        { KeyBindingAction.ToggleSubtitlesVisibilityPrimary },
        { KeyBindingAction.ToggleSubtitlesVisibilitySecondary },
        { KeyBindingAction.ToggleMute },
        { KeyBindingAction.TogglePlayPause },
        { KeyBindingAction.ToggleRecording },
        { KeyBindingAction.ToggleReversePlayback },
        { KeyBindingAction.ToggleLoopPlayback },
        { KeyBindingAction.Play },
        { KeyBindingAction.Pause },
        { KeyBindingAction.Stop },
        { KeyBindingAction.Flush },
        { KeyBindingAction.ToggleSeekAccurate },
        { KeyBindingAction.SeekToStart },
        { KeyBindingAction.SeekToEnd },
        { KeyBindingAction.SpeedAdd },
        { KeyBindingAction.SpeedAdd2 },
        { KeyBindingAction.SpeedRemove },
        { KeyBindingAction.SpeedRemove2 },
        { KeyBindingAction.ResetAll },
        { KeyBindingAction.ResetDelay },
        { KeyBindingAction.ResetSpeed },
        { KeyBindingAction.ResetRotation },
        { KeyBindingAction.ResetZoom },
        { KeyBindingAction.ForceIdle },
        { KeyBindingAction.ForceActive },
        { KeyBindingAction.ForceFullActive }
    };
}
public class KeyBinding
{
    public bool             IsEnabled       { get; set; } = true;
    public bool             Alt             { get; set; }
    public bool             Ctrl            { get; set; }
    public bool             Shift           { get; set; }
    public Key              Key             { get; set; }
    public KeyBindingAction Action          { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ActionName
    {
        get => Action == KeyBindingAction.Custom ? field : null;
        set;
    }

    public bool             IsKeyUp         { get; set; }

    /// <summary>
    /// Sets action for custom key binding
    /// </summary>
    /// <param name="action"></param>
    /// <param name="isKeyUp"></param>
    public void SetAction(Action action, bool isKeyUp)
    {
        ActionInternal  = action;
        IsKeyUp = isKeyUp;
    }

    [JsonIgnore]
    public Action ActionInternal { get; internal set; }
}

public enum KeyBindingAction
{   // NOTE: To be able to support compatibility with previous config versions add new to end
    [Description(nameof(Custom))]
    Custom,
    [Description("Set Activity to Idle forcibly")]
    ForceIdle,
    [Description("Set Activity to Active forcibly")]
    ForceActive,
    [Description("Set Activity to FullActive forcibly")]
    ForceFullActive,

    [Description("Increase Audio Delay (1)")]
    AudioDelayAdd,
    [Description("Increase Audio Delay (2)")]
    AudioDelayAdd2,
    [Description("Decrease Audio Delay (1)")]
    AudioDelayRemove,
    [Description("Decrease Audio Delay (2)")]
    AudioDelayRemove2,

    [Description("Toggle Audio Mute/Unmute")]
    ToggleMute,
    [Description("Volume Up (1)")]
    VolumeUp,
    [Description("Volume Down (1)")]
    VolumeDown,

    [Description("Increase Primary Subtitles Delay (1)")]
    SubsDelayAddPrimary,
    [Description("Increase Primary Subtitles Delay (2)")]
    SubsDelayAdd2Primary,
    [Description("Decrease Primary Subtitles Delay (1)")]
    SubsDelayRemovePrimary,
    [Description("Decrease Primary Subtitles Delay (2)")]
    SubsDelayRemove2Primary,
    [Description("Increase Secondary Subtitles Delay (1)")]
    SubsDelayAddSecondary,
    [Description("Increase Secondary Subtitles Delay (2)")]
    SubsDelayAdd2Secondary,
    [Description("Decrease Secondary Subtitles Delay (1)")]
    SubsDelayRemoveSecondary,
    [Description("Decrease Secondary Subtitles Delay (2)")]
    SubsDelayRemove2Secondary,

    [Description("Copy Opened Item to Clipboard")]
    CopyToClipboard,
    [Description("Copy Current Played Item to Clipboard")]
    CopyItemToClipboard,
    [Description("Open a media from clipboard")]
    OpenFromClipboard,
    [Description("Open a media from clipboard if not open")]
    OpenFromClipboardSafe,
    [Description("Open a media from file dialog")]
    OpenFromFileDialog,

    [Description("Stop playback")]
    Stop,
    [Description("Pause playback")]
    Pause,
    [Description("Play playback")]
    Play,
    [Description("Toggle play playback")]
    TogglePlayPause,

    [Description("Toggle reverse playback")]
    ToggleReversePlayback,
    [Description("Toggle loop playback")]
    ToggleLoopPlayback,
    [Description("Flushes the buffer of the player")]
    Flush,
    [Description("Take snapshot")]
    TakeSnapshot,
    [Description("Change to NormalScreen")]
    NormalScreen,
    [Description("Change to FullScreen")]
    FullScreen,
    [Description("Toggle NormalScreen / FullScreen")]
    ToggleFullScreen,

    [Description("Toggle Audio Enabled")]
    ToggleAudio,
    [Description("Toggle Video Enabled")]
    ToggleVideo,

    [Description("Toggle All Subtitles Visibility")]
    ToggleSubtitlesVisibility,
    [Description("Toggle Primary Subtitles Visibility")]
    ToggleSubtitlesVisibilityPrimary,
    [Description("Toggle Secondary Subtitles Visibility")]
    ToggleSubtitlesVisibilitySecondary,

    [Description("Toggle Keep Aspect Ratio")]
    ToggleKeepRatio,
    [Description("Toggle Video Acceleration")]
    ToggleVideoAcceleration,
    [Description("Toggle Recording")]
    ToggleRecording,
    [Description("Toggle Always Seek Accurate Mode")]
    ToggleSeekAccurate,

    [Description("Seek forwards (1)")]
    SeekForward,
    [Description("Seek backwards (1)")]
    SeekBackward,
    [Description("Seek forwards (2)")]
    SeekForward2,
    [Description("Seek backwards (2)")]
    SeekBackward2,
    [Description("Seek forwards (3)")]
    SeekForward3,
    [Description("Seek backwards (3)")]
    SeekBackward3,
    [Description("Seek forwards (4)")]
    SeekForward4,
    [Description("Seek backwards (4)")]
    SeekBackward4,
    [Description("Seek to start position")]
    SeekToStart,
    [Description("Seek to end position")]
    SeekToEnd,

    [Description("Seek to the previous subtitle")]
    SubsPrevSeek,
    [Description("Seek to the current subtitle")]
    SubsCurSeek,
    [Description("Seek to the next subtitle")]
    SubsNextSeek,
    [Description("Seek to the previous subtitle or seek backwards")]
    SubsPrevSeekFallback,
    [Description("Seek to the next subtitle or seek forwards")]
    SubsNextSeekFallback,

    [Description("Seek to the previous secondary subtitle")]
    SubsPrevSeek2,
    [Description("Seek to the current secondary subtitle")]
    SubsCurSeek2,
    [Description("Seek to the next secondary subtitle")]
    SubsNextSeek2,
    [Description("Seek to the previous secondary subtitle or seek backwards")]
    SubsPrevSeekFallback2,
    [Description("Seek to the next secondary subtitle or seek forwards")]
    SubsNextSeekFallback2,

    [Description("Speed Up (1)")]
    SpeedAdd,
    [Description("Speed Up (2)")]
    SpeedAdd2,

    [Description("Speed Down (1)")]
    SpeedRemove,
    [Description("Speed Down (2)")]
    SpeedRemove2,

    [Description("Show Next Frame")]
    ShowNextFrame,
    [Description("Show Previous Frame")]
    ShowPrevFrame,

    [Description("Reset Zoom / Rotation / Speed / Delay")]
    ResetAll,
    [Description("Reset Speed")]
    ResetSpeed,
    [Description("Reset Audio / Subtitles Delay")]
    ResetDelay,
    [Description("Reset Rotation")]
    ResetRotation,
    [Description("Reset Zoom")]
    ResetZoom,

    [Description("Zoom In (1)")]
    ZoomIn,
    [Description("Zoom Out (1)")]
    ZoomOut,
    [Description("Open Next Playlist Item")]
    OpenNextItem,
    [Description("Open Previous Playlist Item")]
    OpenPrevItem,
}

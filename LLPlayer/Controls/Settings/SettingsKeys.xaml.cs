using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FlyleafLib.MediaPlayer;
using LLPlayer.Converters;
using LLPlayer.Extensions;
using LLPlayer.Services;
using KeyBinding = FlyleafLib.MediaPlayer.KeyBinding;

namespace LLPlayer.Controls.Settings;

public partial class SettingsKeys : UserControl
{
    private SettingsKeysVM VM => (SettingsKeysVM)DataContext;

    public SettingsKeys()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SettingsKeysVM>();
    }

    // Enable ComboBox to open when double-clicking a cell in Action
    private void ComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            comboBox.IsDropDownOpen = true;
        }
    }

    // Scroll to the added record when a new record is added.
    private void KeyBindingsDataGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VM.CmdLoad.IsExecuting)
        {
            return;
        }

        if (KeyBindingsDataGrid.SelectedItem != null)
        {
            KeyBindingsDataGrid.UpdateLayout();
            KeyBindingsDataGrid.ScrollIntoView(KeyBindingsDataGrid.SelectedItem);
        }
    }
}

public class SettingsKeysVM : Bindable
{
    public FlyleafManager FL { get; }

    public SettingsKeysVM(FlyleafManager fl)
    {
        FL = fl;

        CmdLoad.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(CmdLoad.IsExecuting))
            {
                OnPropertyChanged(nameof(CanApply));
            }
        };

        List<ActionData> mergeActions = new();

        foreach (KeyBindingAction action in Enum.GetValues<KeyBindingAction>())
        {
            if (action != KeyBindingAction.Custom)
            {
                mergeActions.Add(new ActionData()
                {
                    Action = action,
                    Description = action.GetDescription(),
                    DisplayName = action.ToString(),
                    Group = action.ToGroup()
                });
            }
        }

        foreach (CustomKeyBindingAction action in Enum.GetValues<CustomKeyBindingAction>())
        {
            mergeActions.Add(new ActionData()
            {
                Action = KeyBindingAction.Custom,
                CustomAction = action,
                Description = action.GetDescription(),
                DisplayName = action.ToString() + @" ©︎", // c=custom
                Group = action.ToGroup()
            });
        }

        mergeActions.Sort();

        Actions = mergeActions;

        // Grouping when displaying actions in ComboBox
        _actionsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Actions);
        _actionsView.SortDescriptions.Add(new SortDescription(nameof(ActionData.Group), ListSortDirection.Ascending));
        _actionsView.SortDescriptions.Add(new SortDescription(nameof(ActionData.DisplayName), ListSortDirection.Ascending));

        _actionsView.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(ActionData.Group)));
    }

    public List<ActionData> Actions { get; }
    private readonly ListCollectionView _actionsView;

    public ObservableCollection<KeyBindingWrapper> KeyBindings { get; } = new();

    public KeyBindingWrapper SelectedKeyBinding { get; set => Set(ref field, value); }

    public int DuplicationCount
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    // Disable Apply button if there are duplicates or during loading
    public bool CanApply => DuplicationCount == 0 && !CmdLoad.IsExecuting;

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    public DelegateCommand CmdAdd => field ??= new(() =>
    {
        KeyBindingWrapper added = new(new KeyBinding {Action = KeyBindingAction.AudioDelayAdd}, this);
        KeyBindings.Add(added);
        SelectedKeyBinding = added;

        added.ValidateShortcut();
    });

    public AsyncDelegateCommand CmdLoad => field ??= new(async () =>
    {
        KeyBindings.Clear();
        DuplicationCount = 0;

        var keys = FL.PlayerConfig.Player.KeyBindings.Keys;

        var keyBindings = await Task.Run(() =>
        {
            List<KeyBindingWrapper> keyBindings = new(keys.Count);

            foreach (var k in keys)
            {
                try
                {
                    keyBindings.Add(new KeyBindingWrapper(k, this));
                }
                catch (SettingsPropertyNotFoundException)
                {
                    // ignored
                    // TODO: L: error handling
                    Debug.Fail("Weird custom key for settings?");
                }
            }

            return Task.FromResult(keyBindings);
        });

        foreach (var b in keyBindings)
        {
            KeyBindings.Add(b);
        }
    });

    /// <summary>
    /// Reflect customized key settings to Config.
    /// </summary>
    public DelegateCommand CmdApply => field ??= new DelegateCommand(() =>
    {
        foreach (var b in KeyBindings)
        {
            Debug.Assert(!b.Duplicated, "Duplicate check not working");
        }

        var newKeys = KeyBindings.Select(k => k.ToKeyBinding()).ToList();

        foreach (var k in newKeys)
        {
            if (k.Action == KeyBindingAction.Custom)
            {
                if (!Enum.TryParse(k.ActionName, out CustomKeyBindingAction customAction))
                {
                    Guards.Fail();
                }
                k.SetAction(FL.Action.CustomActions[customAction], k.IsKeyUp);
            }
            else
            {
                k.SetAction(FL.PlayerConfig.Player.KeyBindings.GetKeyBindingAction(k.Action), k.IsKeyUp);
            }
        }

        FL.PlayerConfig.Player.KeyBindings.RemoveAll();
        FL.PlayerConfig.Player.KeyBindings.Keys = newKeys;

    }).ObservesCanExecute(() => CanApply);

    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
}

public class ActionData : IComparable<ActionData>
{
    public string Description { get; set; }
    public string DisplayName { get; set; }
    public KeyBindingActionGroup Group { get; set; }
    public KeyBindingAction Action { get; set; }
    public CustomKeyBindingAction? CustomAction { get; set; }

    public override bool Equals(object? obj)
    {
        if (obj is null || obj is not ActionData data)
        {
            return false;
        }

        return Action == data.Action && CustomAction == data.CustomAction;
    }

    public override int GetHashCode() => HashCode.Combine(Action, CustomAction);

    public int CompareTo(ActionData? other)
    {
        if (ReferenceEquals(this, other))
            return 0;
        if (other is null)
            return 1;
        return string.Compare(DisplayName, other.DisplayName, StringComparison.Ordinal);
    }
}

public class KeyBindingWrapper : Bindable, ICloneable
{
    private readonly SettingsKeysVM _parent;

    /// <summary>
    /// constructor
    /// </summary>
    /// <param name="keyBinding"></param>
    /// <param name="parent"></param>
    /// <exception cref="SettingsPropertyNotFoundException"></exception>
    public KeyBindingWrapper(KeyBinding keyBinding, SettingsKeysVM parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        // TODO: L: performance issues when initializing?
        _parent = parent;

        Key = keyBinding.Key;

        ActionData action = new()
        {
            Action = keyBinding.Action,
        };

        if (keyBinding.Action != KeyBindingAction.Custom)
        {
            action.Description = keyBinding.Action.GetDescription();
            action.DisplayName = keyBinding.Action.ToString();
        }
        else if (Enum.TryParse(keyBinding.ActionName, out CustomKeyBindingAction customAction))
        {
            action.CustomAction = customAction;
            action.Description = customAction.GetDescription();
            action.DisplayName = keyBinding.ActionName + @" ©︎";
        }
        else
        {
            throw new SettingsPropertyNotFoundException($"Custom Action '{keyBinding.ActionName}' does not exist.");
        }

        Action = action;
        Alt = keyBinding.Alt;
        Ctrl = keyBinding.Ctrl;
        Shift = keyBinding.Shift;
        IsKeyUp = keyBinding.IsKeyUp;
        IsEnabled = keyBinding.IsEnabled;
    }

    public bool IsEnabled
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                if (!value)
                {
                    Duplicated = false;
                    DuplicatedInfo = null;
                }
                else
                {
                    ValidateShortcut();
                }

                ValidateKey(Key);
            }
        }
    }

    public bool Alt
    {
        get;
        set
        {
            if (Set(ref field, value) && IsEnabled)
            {
                ValidateShortcut();
                ValidateKey(Key);
            }
        }
    }

    public bool Ctrl
    {
        get;
        set
        {
            if (Set(ref field, value) && IsEnabled)
            {
                ValidateShortcut();
                ValidateKey(Key);
            }
        }
    }

    public bool Shift
    {
        get;
        set
        {
            if (Set(ref field, value) && IsEnabled)
            {
                ValidateShortcut();
                ValidateKey(Key);
            }
        }
    }

    public Key Key
    {
        get;
        set
        {
            Key prevKey = field;
            if (Set(ref field, value) && IsEnabled)
            {
                ValidateShortcut();
                ValidateKey(Key); // maybe don't need?
                // When the key is changed from A -> B, the state of A also needs to be updated.
                ValidateKey(prevKey);
            }
        }
    }

    public ActionData Action { get; set => Set(ref field, value); }

    public bool IsKeyUp { get; set => Set(ref field, value); }

    public bool Duplicated
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                // Update duplicate counters held by the parent.
                _parent.DuplicationCount += value ? 1 : -1;
            }
        }
    }

    public string? DuplicatedInfo { get; private set => Set(ref field, value); }

    private bool IsSameKey(KeyBindingWrapper key)
    {
        return key.Key == Key && key.Alt == Alt && key.Shift == Shift && key.Ctrl == Ctrl;
    }

    private void ValidateKey(Key key)
    {
        foreach (var b in _parent.KeyBindings.Where(k => k.IsEnabled && k != this && k.Key == key))
        {
            b.ValidateShortcut();
        }
    }

    public object Clone()
    {
        KeyBindingWrapper clone = (KeyBindingWrapper)MemberwiseClone();

        return clone;
    }

    /// <summary>
    /// Convert Wrapper to KeyBinding
    /// </summary>
    /// <returns></returns>
    public KeyBinding ToKeyBinding()
    {
        KeyBinding binding = new ()
        {
            Key = Key,
            Action = Action.Action,
            Ctrl = Ctrl,
            Alt = Alt,
            Shift = Shift,
            IsKeyUp = IsKeyUp,
            IsEnabled = IsEnabled
        };

        if (Action.Action == KeyBindingAction.Custom)
        {
            binding.ActionName = Action.CustomAction.ToString();
        }

        return binding;
    }

    // TODO: L: This validation might be better done in the parent with event firing (I want to eliminate references to the parent).
    public void ValidateShortcut()
    {
        List<KeyBindingWrapper> sameKeys = _parent.KeyBindings
            .Where(k => k != this && k.IsEnabled && k.IsSameKey(this)).ToList();

        bool isDuplicate = sameKeys.Count > 0;

        UpdateDuplicated(isDuplicate);

        // Other records with the same key also update duplicate status
        foreach (KeyBindingWrapper b in sameKeys)
        {
            b.UpdateDuplicated(isDuplicate);
        }
    }

    private void UpdateDuplicated(bool duplicated)
    {
        Duplicated = duplicated;

        if (duplicated)
        {
            List<string> duplicateActions = _parent.KeyBindings
                        .Where(k => k != this && k.IsSameKey(this)).Select(k => k.Action.DisplayName)
                        .ToList();

            DuplicatedInfo = $"Key:{Key} is conflicted.\r\nAction:{string.Join(',', duplicateActions)} already uses.";
        }
        else
        {
            DuplicatedInfo = null;
        }
    }

    // TODO: L: Enable firing for multiple selections
    public DelegateCommand<KeyBindingWrapper> CmdDeleteRow => new((binding) =>
    {
        if (binding.Duplicated)
        {
            // Reduce duplicate counters of parents
            binding.Duplicated = false;
        }
        _parent.KeyBindings.Remove(binding);

        // Update other keys
        if (binding.IsEnabled)
        {
            ValidateKey(binding.Key);
        }
    });

    public DelegateCommand<Key?> CmdSetKey => new((key) =>
    {
        if (key.HasValue)
        {
            Key = key.Value;
        }
    });

    public DelegateCommand<KeyBindingWrapper> CmdCloneRow => new((keyBinding) =>
    {
        int index = _parent.KeyBindings.IndexOf(keyBinding);
        if (index != -1)
        {
            KeyBindingWrapper clone = (KeyBindingWrapper)Clone();

            _parent.KeyBindings.Insert(index + 1, clone);

            // Select added record
            _parent.SelectedKeyBinding = clone;

            // validate it
            clone.ValidateShortcut();
        }
    });
}

public class KeyCaptureTextBox : TextBox
{
    private static readonly HashSet<Key> IgnoreKeys =
    [
        Key.LeftShift,
        Key.RightShift,
        Key.LeftCtrl,
        Key.RightCtrl,
        Key.LeftAlt,
        Key.RightAlt,
        Key.LWin,
        Key.RWin,
        Key.CapsLock,
        Key.NumLock,
        Key.Scroll
    ];

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.Register(nameof(Key), typeof(Key), typeof(KeyCaptureTextBox),
            new FrameworkPropertyMetadata(Key.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public Key Key
    {
        get => (Key)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public KeyCaptureTextBox()
    {
        Loaded += KeyCaptureTextBox_Loaded;
    }

    // Key input does not get focus when mouse clicked, so it gets it.
    private void KeyCaptureTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        Focus();
        Keyboard.Focus(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (IgnoreKeys.Contains(e.Key))
        {
            return;
        }

        // Press Enter to confirm.
        if (e.Key == Key.Enter)
        {
            // This would go to the right cell.
            //MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));

            // If the Enter key is pressed, it remains in place and the edit is confirmed
            var dataGrid = UIHelper.FindParent<DataGrid>(this);
            if (dataGrid != null)
            {
                dataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            }

            e.Handled = true; // Suppress default behavior (focus movement)
        }
        else
        {
            // Capture other key
            Key = e.Key;

            // Converts input key into human understandable name.
            if (KeyToStringConverter.KeyMappings.TryGetValue(e.Key, out string? keyName))
            {
                Text = keyName;
            }
            else
            {
                Text = e.Key.ToString();
            }
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }
}

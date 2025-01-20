using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

using FlyleafLib.MediaPlayer;

namespace FlyleafLib.Controls.WinForms;

public partial class FlyleafHost : UserControl, IHostPlayer, INotifyPropertyChanged
{
    /* TODO
     *
     * Attached: (UserControl) Host = Surface
     * Detached: (Form) Surface
     * (Form) Overlay
     *
     */

    #region Properties / Variables
    Player _Player;
    public Player       Player          {
        get => _Player;
        set {
            if (_Player == value)
                return;

            var oldPlayer = _Player;
            _Player = value;
            SetPlayer(oldPlayer);
            Raise(nameof(Player));
            }
        }

    bool _IsFullScreen;
    public bool         IsFullScreen    {
        get => _IsFullScreen;
        set
            {
            if (_IsFullScreen == value)
                return;

            if (value)
                FullScreen();
            else
                NormalScreen();
            }
        }

    bool _ToggleFullScreenOnDoubleClick = true;
    public bool         ToggleFullScreenOnDoubleClick
                                                { get => _ToggleFullScreenOnDoubleClick; set => Set(ref _ToggleFullScreenOnDoubleClick, value); }

    public int          UniqueId                { get; private set; } = -1;

    bool _KeyBindings = true;
    public bool         KeyBindings             { get => _KeyBindings; set => Set(ref _KeyBindings, value); }

    bool _PanMoveOnCtrl = true;
    public bool         PanMoveOnCtrl           { get => _PanMoveOnCtrl; set => Set(ref _PanMoveOnCtrl, value); }

    bool _PanZoomOnCtrlWheel = true;
    public bool         PanZoomOnCtrlWheel      { get => _PanZoomOnCtrlWheel; set => Set(ref _PanZoomOnCtrlWheel, value); }

    bool _PanRotateOnShiftWheel = true;
    public bool         PanRotateOnShiftWheel   { get => _PanRotateOnShiftWheel; set => Set(ref _PanRotateOnShiftWheel, value); }

    bool _DragMove = true;
    public bool         DragMove                { get => _DragMove; set => Set(ref _DragMove, value); }

    bool _OpenOnDrop;
    public bool         OpenOnDrop              { get => _OpenOnDrop; set { Set(ref _OpenOnDrop, value); AllowDrop = _SwapOnDrop || _OpenOnDrop; } }

    bool _SwapOnDrop = true;
    public bool         SwapOnDrop              { get => _SwapOnDrop; set { Set(ref _SwapOnDrop, value);  AllowDrop = _SwapOnDrop || _OpenOnDrop; } }

    bool _SwapDragEnterOnShift = true;
    public bool         SwapDragEnterOnShift    { get => _SwapDragEnterOnShift; set => Set(ref _SwapDragEnterOnShift, value); }


    int panPrevX, panPrevY;
    Point mouseLeftDownPoint = new(0, 0);
    Point mouseMoveLastPoint;
    Point oldLocation = Point.Empty;
    Size oldSize = Size.Empty;
    FormBorderStyle oldStyle = FormBorderStyle.None;
    Control oldParent = null;
    LogHandler Log;
    bool designMode = LicenseManager.UsageMode == LicenseUsageMode.Designtime;
    static int idGenerator;

    private class FlyleafHostDropWrap { public FlyleafHost FlyleafHost; } // To allow non FlyleafHosts to drag & drop
    #endregion

    public FlyleafHost()
    {
        UniqueId  = idGenerator++;
        AllowDrop = _SwapOnDrop || _OpenOnDrop;
        BackColor = Color.Black;

        if (designMode)
            return;

        Log = new LogHandler(("[#" + UniqueId + "]").PadRight(8, ' ') + $" [FlyleafHost NP] ");

        KeyUp       += Host_KeyUp;
        KeyDown     += Host_KeyDown;
        DoubleClick += Host_DoubleClick;
        MouseDown   += Host_MouseDown;
        MouseMove   += Host_MouseMove;
        MouseWheel  += Host_MouseWheel;
        DragEnter   += Host_DragEnter;
        DragDrop    += Host_DragDrop;
    }

    private void Host_DragDrop(object sender, DragEventArgs e)
    {
        if (Player == null)
            return;

        FlyleafHostDropWrap hostWrap = (FlyleafHostDropWrap) e.Data.GetData(typeof(FlyleafHostDropWrap));

        if (hostWrap != null)
        {
            (hostWrap.FlyleafHost.Player, Player) = (Player, hostWrap.FlyleafHost.Player);
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string filename = ((string[])e.Data.GetData(DataFormats.FileDrop, false))[0];
            Player.OpenAsync(filename);
        }
        else if (e.Data.GetDataPresent(DataFormats.Text))
        {
            string text = e.Data.GetData(DataFormats.Text, false).ToString();
            if (text.Length > 0)
                Player.OpenAsync(text);
        }
    }
    private void Host_DragEnter(object sender, DragEventArgs e) { if (Player != null) e.Effect = DragDropEffects.All; }
    private void Host_MouseWheel(object sender, MouseEventArgs e)
    {
        if (Player == null || e.Delta == 0)
            return;

        if (PanZoomOnCtrlWheel && ModifierKeys.HasFlag(Keys.Control))
        {
            System.Windows.Point curDpi = new(e.Location.X, e.Location.Y);
            if (e.Delta > 0)
                Player.ZoomIn(curDpi);
            else
                Player.ZoomOut(curDpi);
        }
        else if (PanRotateOnShiftWheel && ModifierKeys.HasFlag(Keys.Shift))
        {
            if (e.Delta > 0)
                Player.RotateRight();
            else
                Player.RotateLeft();
        }
    }
    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        if (Player == null)
            return;

        if (e.Location != mouseMoveLastPoint)
        {
            Player.Activity.RefreshFullActive();
            mouseMoveLastPoint = e.Location;
        }

        if (e.Button != MouseButtons.Left)
            return;

        if (PanMoveOnCtrl && ModifierKeys.HasFlag(Keys.Control))
        {
            Player.PanXOffset = panPrevX + e.X - mouseLeftDownPoint.X;
            Player.PanYOffset = panPrevY + e.Y - mouseLeftDownPoint.Y;
        }
        else if (DragMove && Capture && ParentForm != null && !IsFullScreen)
        {
            ParentForm.Location  = new Point(ParentForm.Location.X + e.X - mouseLeftDownPoint.X, ParentForm.Location.Y + e.Y - mouseLeftDownPoint.Y);
        }
    }
    private void Host_MouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        mouseLeftDownPoint = new Point(e.X, e.Y);

        if (Player != null)
        {
            Player.Activity.RefreshFullActive();

            panPrevX = Player.PanXOffset;
            panPrevY = Player.PanYOffset;

            if (ModifierKeys.HasFlag(Keys.Shift))
            {
                DoDragDrop(new FlyleafHostDropWrap() { FlyleafHost = this }, DragDropEffects.Move);
            }
        }
    }
    private void Host_DoubleClick(object sender, EventArgs e) { if (!ToggleFullScreenOnDoubleClick) return; IsFullScreen = !IsFullScreen; }
    private void Host_KeyDown(object sender, KeyEventArgs e) { if (KeyBindings) Player.KeyDown(Player, e); }
    private void Host_KeyUp(object sender, KeyEventArgs e) { if (KeyBindings) Player.KeyUp(Player, e); }

    public void SetPlayer(Player oldPlayer)
    {
        // De-assign old Player's Handle/FlyleafHost
        if (oldPlayer != null)
        {
            Log.Debug($"De-assign Player #{oldPlayer.PlayerId}");

            oldPlayer.VideoDecoder.DestroySwapChain();
            oldPlayer.Host = null;
        }

        if (Player == null)
            return;

        Log.Prefix = ("[#" + UniqueId + "]").PadRight(8, ' ') + $" [FlyleafHost #{Player.PlayerId}] ";

        // De-assign new Player's Handle/FlyleafHost
        Player.Host?.Player_Disposed();


        // Assign new Player's (Handle/FlyleafHost)
        Log.Debug($"Assign Player #{Player.PlayerId}");

        Player.Host = this;
        Player.VideoDecoder.CreateSwapChain(Handle);

        BackColor = Utils.WPFToWinFormsColor(Player.Config.Video.BackgroundColor);
    }

    public void FullScreen()
    {
        if (ParentForm == null)
            return;

        oldStyle = ParentForm.FormBorderStyle;
        oldLocation = Location;
        oldSize = Size;
        oldParent = Parent;

        ParentForm.FormBorderStyle = FormBorderStyle.None;
        ParentForm.WindowState = FormWindowState.Maximized;
        Parent = ParentForm;
        Location = new Point(0, 0);
        Size = ParentForm.ClientSize;

        BringToFront();
        Focus();

        _IsFullScreen = true;
        Raise(nameof(IsFullScreen));
    }
    public void NormalScreen()
    {
        if (ParentForm == null)
            return;

        ParentForm.FormBorderStyle = oldStyle;
        ParentForm.WindowState = FormWindowState.Normal;
        Parent = oldParent;

        Location = oldLocation;
        Size = oldSize;

        Focus();

        _IsFullScreen = false;
        Raise(nameof(IsFullScreen));
    }

    public bool Player_CanHideCursor() => Focused;
    public bool Player_GetFullScreen() => IsFullScreen;
    public void Player_SetFullScreen(bool value) => IsFullScreen = value;
    public void Player_Disposed() => Player = null;

    protected override bool IsInputKey(Keys keyData) => Player != null && Player.Host != null;  // Required to allow keybindings such as arrows etc.

    // TBR: Related to Renderer's WndProc
    protected override void OnPaintBackground(PaintEventArgs pe)
    {
        if (Player == null || (Player != null && !Player.WFPresent()))
            base.OnPaintBackground(pe);
    }
    protected override void OnPaint(PaintEventArgs pe) { }

    public event PropertyChangedEventHandler PropertyChanged;
    protected bool Set<T>(ref T field, T value, bool check = true, [CallerMemberName] string propertyName = "")
    {
        if (!check || (field == null && value != null) || (field != null && !field.Equals(value)))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        return false;
    }
    protected void Raise([CallerMemberName] string propertyName = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

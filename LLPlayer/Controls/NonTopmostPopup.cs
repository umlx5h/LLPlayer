using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace LLPlayer.Controls;

/// <summary>
/// Popup with code to not be the topmost control
/// ref: https://gist.github.com/flq/903202/8bf56606b7c6fad341c31482f196b3aba9373e07
/// </summary>
public class NonTopmostPopup : Popup
{
    private bool? _appliedTopMost;
    private bool _alreadyLoaded;
    private Window? _parentWindow;

    public static readonly DependencyProperty IsTopmostProperty =
        DependencyProperty.Register(nameof(IsTopmost), typeof(bool), typeof(NonTopmostPopup), new FrameworkPropertyMetadata(false, OnIsTopmostChanged));

    public bool IsTopmost
    {
        get => (bool)GetValue(IsTopmostProperty);
        set => SetValue(IsTopmostProperty, value);
    }

    public NonTopmostPopup()
    {
        Loaded += OnPopupLoaded;
    }

    void OnPopupLoaded(object sender, RoutedEventArgs e)
    {
        if (_alreadyLoaded)
            return;

        _alreadyLoaded = true;

        if (Child != null)
        {
            Child.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnChildPreviewMouseLeftButtonDown), true);
        }

        //_parentWindow = Window.GetWindow(this);
        _parentWindow = Application.Current.MainWindow;

        if (_parentWindow == null)
            return;

        _parentWindow.Activated += OnParentWindowActivated;
        _parentWindow.Deactivated += OnParentWindowDeactivated;
        _parentWindow.LocationChanged += OnParentWindowLocationChanged;
    }

    private void OnParentWindowLocationChanged(object? sender, EventArgs e)
    {
        try
        {
            var method = typeof(Popup).GetMethod("UpdatePosition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (IsOpen)
            {
                method?.Invoke(this, null);
            }
        }
        catch
        {
            // ignored
        }
    }

    private void OnParentWindowActivated(object? sender, EventArgs e)
    {
        //Debug.WriteLine("Parent Window Activated");
        SetTopmostState(true);
    }

    private void OnParentWindowDeactivated(object? sender, EventArgs e)
    {
        //Debug.WriteLine("Parent Window Deactivated");

        if (IsTopmost == false)
        {
            SetTopmostState(IsTopmost);
        }
    }

    private void OnChildPreviewMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        //Debug.WriteLine("Child Mouse Left Button Down");

        SetTopmostState(true);

        if (_parentWindow != null && !_parentWindow.IsActive && IsTopmost == false)
        {
            _parentWindow.Activate();
            //Debug.WriteLine("Activating Parent from child Left Button Down");
        }
    }

    private static void OnIsTopmostChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var thisobj = (NonTopmostPopup)obj;

        thisobj.SetTopmostState(thisobj.IsTopmost);
    }

    protected override void OnOpened(EventArgs e)
    {
        SetTopmostState(IsTopmost);
    }

    private void SetTopmostState(bool isTop)
    {
        // Don't apply state if it's the same as incoming state
        if (_appliedTopMost.HasValue && _appliedTopMost == isTop)
        {
            // TODO: L: Maybe commenting out this section will solve the problem of the rare popups coming to the forefront?
            //return;
        }

        if (Child == null)
            return;

        var hwndSource = (PresentationSource.FromVisual(Child)) as HwndSource;

        if (hwndSource == null)
            return;
        var hwnd = hwndSource.Handle;

        if (!GetWindowRect(hwnd, out RECT rect))
            return;

        //Debug.WriteLine("setting z-order " + isTop);

        if (isTop)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, rect.Left, rect.Top, (int)Width, (int)Height, TOPMOST_FLAGS);
        }
        else
        {
            // Z-Order would only get refreshed/reflected if clicking the
            // the titlebar (as opposed to other parts of the external
            // window) unless I first set the popup to HWND_BOTTOM
            // then HWND_TOP before HWND_NOTOPMOST
            SetWindowPos(hwnd, HWND_BOTTOM, rect.Left, rect.Top, (int)Width, (int)Height, TOPMOST_FLAGS);
            SetWindowPos(hwnd, HWND_TOP, rect.Left, rect.Top, (int)Width, (int)Height, TOPMOST_FLAGS);
            SetWindowPos(hwnd, HWND_NOTOPMOST, rect.Left, rect.Top, (int)Width, (int)Height, TOPMOST_FLAGS);
        }

        _appliedTopMost = isTop;
    }

    #region P/Invoke imports & definitions
#pragma warning disable 1591 //Xml-doc
#pragma warning disable 169 //Never used-warning
    // ReSharper disable InconsistentNaming
    // Imports etc. with their naming rules

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT

    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X,
    int Y, int cx, int cy, uint uFlags);

    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    static readonly IntPtr HWND_TOP = new IntPtr(0);
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

    private const UInt32 SWP_NOSIZE = 0x0001;
    const UInt32 SWP_NOMOVE = 0x0002;
    //const UInt32 SWP_NOZORDER = 0x0004;
    const UInt32 SWP_NOREDRAW = 0x0008;
    const UInt32 SWP_NOACTIVATE = 0x0010;

    //const UInt32 SWP_FRAMECHANGED = 0x0020; /* The frame changed: send WM_NCCALCSIZE */
    //const UInt32 SWP_SHOWWINDOW = 0x0040;
    //const UInt32 SWP_HIDEWINDOW = 0x0080;
    //const UInt32 SWP_NOCOPYBITS = 0x0100;
    const UInt32 SWP_NOOWNERZORDER = 0x0200; /* Don't do owner Z ordering */
    const UInt32 SWP_NOSENDCHANGING = 0x0400; /* Don't send WM_WINDOWPOSCHANGING */

    const UInt32 TOPMOST_FLAGS =
          SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSIZE | SWP_NOMOVE | SWP_NOREDRAW | SWP_NOSENDCHANGING;

    // ReSharper restore InconsistentNaming
#pragma warning restore 1591
#pragma warning restore 169
    #endregion
}

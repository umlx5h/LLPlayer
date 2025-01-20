using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using static FlyleafLib.Utils.NativeMethods;

using Brushes = System.Windows.Media.Brushes;

namespace FlyleafLib.Controls.WPF;

/// <summary>
/// Shared Overlay on top of multiple FlyleafHosts
/// </summary>
public class FlyleafSharedOverlay : ContentControl
{
    /* TODO
     * Sub-classing or find all FlyleafHosts and/or add static event for fullscreens to force back on-top (Instead of current Host_LayoutUpdated constantly measuring/updating rect on-top)
     * Review Active/Keyboard Focus?
     */

    public object DetachedContent
    {
        get => GetValue(DetachedContentProperty);
        set => SetValue(DetachedContentProperty, value);
    }
    public static readonly DependencyProperty DetachedContentProperty =
        DependencyProperty.Register(nameof(DetachedContent), typeof(object), typeof(FlyleafSharedOverlay), new PropertyMetadata(null));

    public ControlTemplate OverlayTemplate
    {
        get => (ControlTemplate)GetValue(OverlayTemplateProperty);
        set => SetValue(OverlayTemplateProperty, value);
    }
    public static readonly DependencyProperty OverlayTemplateProperty =
        DependencyProperty.Register(nameof(OverlayTemplate), typeof(ControlTemplate), typeof(FlyleafSharedOverlay), new PropertyMetadata(null, new PropertyChangedCallback(OnOverlayTemplateChanged)));

    private static object OnContentChanging(DependencyObject d, object baseValue)
    {
        if (isDesginMode)
            return baseValue;

        FlyleafSharedOverlay host = d as FlyleafSharedOverlay;
        host.Overlay.Content = baseValue;
        return host.DetachedContent;
    }
    private static void OnOverlayTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesginMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        host.Overlay.Template = host.OverlayTemplate;
    }

    public Window       Owner           { get; private set; }
    public IntPtr       OwnerHandle     { get; private set; }

    public Window       Overlay         { get; private set; } = new Window() { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true };
    public IntPtr       OverlayHandle   { get; private set; }

    static bool isDesginMode;
    Point zeroPoint = new(0, 0);
    static Rect rectRandom = new(1, 2, 3, 4);
    Rect rectIntersectLast = rectRandom;
    //Rect rectInitLast = rectRandom;

    static FlyleafSharedOverlay()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(FlyleafSharedOverlay), new FrameworkPropertyMetadata(typeof(FlyleafSharedOverlay)));
        ContentProperty.OverrideMetadata(typeof(FlyleafSharedOverlay), new FrameworkPropertyMetadata(null, new CoerceValueCallback(OnContentChanging)));
    }
    public FlyleafSharedOverlay()
    {
        isDesginMode = DesignerProperties.GetIsInDesignMode(this);
        if (isDesginMode)
            return;

        Loaded              += Host_Loaded;
        DataContextChanged  += Host_DataContextChanged;
    }

    private void Host_Loaded(object sender, RoutedEventArgs e)
    {
        if (isDesginMode)
            return;

        Owner                   = Window.GetWindow(this);
        OwnerHandle             = new WindowInteropHelper(Owner).EnsureHandle();
        OverlayHandle           = new WindowInteropHelper(Overlay).EnsureHandle();
        Owner.ContentRendered   += (o, e) => SetWindowPos(OverlayHandle, IntPtr.Zero, 0, 0, 0, 0, (UInt32)(SetWindowPosFlags.SWP_SHOWWINDOW | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOMOVE));

        LayoutUpdated           += Host_LayoutUpdated;
        IsVisibleChanged        += Host_IsVisibleChanged;
        Overlay.MouseDown       += (o, e) => BringToFront();

        Overlay.MinWidth        = MinWidth;
        Overlay.MinHeight       = MinHeight;
        Overlay.MaxWidth        = MaxWidth;
        Overlay.MaxHeight       = MaxHeight;
        Overlay.Background      = Brushes.Transparent;
        Overlay.ShowInTaskbar   = false;
        Overlay.Owner           = Owner;
        SetParent(OverlayHandle, OwnerHandle);
        SetWindowLong(OverlayHandle, (int)WindowLongFlags.GWL_STYLE, (nint) (WindowStyles.WS_MINIMIZEBOX | WindowStyles.WS_CLIPSIBLINGS | WindowStyles.WS_CLIPCHILDREN | WindowStyles.WS_VISIBLE | WindowStyles.WS_CHILD));
        SetWindowPos(OverlayHandle, IntPtr.Zero, 0, 0, 0, 0, (uint)(SetWindowPosFlags.SWP_FRAMECHANGED | SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOOWNERZORDER));

        /*rectInitLast =*/ rectIntersectLast = rectRandom;
        Host_LayoutUpdated(null, null);
        Host_IsVisibleChanged(null, new());
    }
    private void Host_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Overlay.DataContext = e.NewValue;
    private void Host_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            Overlay.Show();
        else
            Overlay.Hide();
    }
    private void Host_LayoutUpdated(object sender, EventArgs e)
    {
        if (!IsVisible)
            return;

        Rect rectInit = new(TransformToAncestor(Owner).Transform(zeroPoint), RenderSize);
        var rectIntersect = rectInit;

        FrameworkElement parent = this;
        while ((parent = VisualTreeHelper.GetParent(parent) as FrameworkElement) != null)
            rectIntersect.Intersect(new Rect(parent.TransformToAncestor(Owner).Transform(zeroPoint), parent.RenderSize));

        //if (rectInit != rectInitLast)
        //{
            SetRect(rectInit); // TBR: Performance
            //rectInitLast = rectInit;
        //}

        if (rectIntersect == Rect.Empty)
        {
            if (rectIntersect == rectIntersectLast)
                return;

            rectIntersectLast = rectIntersect;
            SetVisibleRect(new Rect(0, 0, 0, 0));
        }
        else
        {
            rectIntersect.X -= rectInit.X;
            rectIntersect.Y -= rectInit.Y;

            if (rectIntersect == rectIntersectLast)
                return;

            rectIntersectLast = rectIntersect;

            SetVisibleRect(rectIntersect);
        }
    }

    public void ResetVisibleRect()
    {
        SetWindowRgn(OverlayHandle, IntPtr.Zero, true);
    }
    public void SetVisibleRect(Rect rect)
    {
        SetWindowRgn(OverlayHandle, CreateRectRgn((int)(rect.X * DpiX), (int)(rect.Y * DpiY), (int)(rect.Right * DpiX), (int)(rect.Bottom * DpiY)), true);
    }
    public void SetRect(Rect rect)
        => SetWindowPos(OverlayHandle, IntPtr.Zero, (int)Math.Round(rect.X * DpiX), (int)Math.Round(rect.Y * DpiY), (int)Math.Round(rect.Width * DpiX), (int)Math.Round(rect.Height * DpiY),
            (uint)SetWindowPosFlags.SWP_NOACTIVATE); // force Z-order always on-top

    public void BringToFront() => SetWindowPos(OverlayHandle, IntPtr.Zero, 0, 0, 0, 0, (UInt32)(SetWindowPosFlags.SWP_SHOWWINDOW | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOMOVE));
}

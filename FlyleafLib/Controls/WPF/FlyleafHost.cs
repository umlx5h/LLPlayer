using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using Brushes = System.Windows.Media.Brushes;

using static FlyleafLib.Utils.NativeMethods;

using FlyleafLib.MediaPlayer;

namespace FlyleafLib.Controls.WPF;

public class FlyleafHost : ContentControl, IHostPlayer, IDisposable
{
    /* -= FlyleafHost Properties Notes =-

        Player							[Can be changed, can be null]
        ReplicaPlayer                                                       | Replicates frames of the assigned Player (useful for interactive zoom) without the pan/zoom config

        Surface							[ReadOnly / Required]
        Overlay							[AutoCreated OnContentChanged       | Provided directly | Provided in Stand Alone Constructor]

        Content							[Overlay's Content]
        DetachedContent					[Host's actual content]

        DataContext						[Set by the user or default inheritance]
        HostDataContext					[Will be set Sync with DataContext as helper to Overlay when we pass this as Overlay's DataContext]

        OpenOnDrop						[None, Surface, Overlay, Both]		| Requires Player and AllowDrop
        SwapOnDrop						[None, Surface, Overlay, Both]		| Requires Player and AllowDrop

        SwapDragEnterOnShift			[None, Surface, Overlay, Both]		| Requires Player and SwapOnDrop
        ToggleFullScreenOnDoubleClick	[None, Surface, Overlay, Both]

        PanMoveOnCtrl					[None, Surface, Overlay, Both]		| Requires Player and VideoStream Opened
        PanZoomOnCtrlWheel			    [None, Surface, Overlay, Both]		| Requires Player and VideoStream Opened
        PanRotateOnShiftWheel           [None, Surface, Overlay, Both]      | Requires Player and VideoStream Opened

        AttachedDragMove				[None, Surface, Overlay, Both, SurfaceOwner, OverlayOwner, BothOwner]
        DetachedDragMove				[None, Surface, Overlay, Both]

        AttachedResize					[None, Surface, Overlay, Both]
        DetachedResize					[None, Surface, Overlay, Both]
        KeepRatioOnResize				[False, True]
        PreferredLandscapeWidth         [X]                                 | When KeepRatioOnResize will use it as helper and try to stay close to this value (CurResizeRatio >= 1) - Will be updated when user resizes a landscape
        PreferredPortraitHeight         [Y]                                 | When KeepRatioOnResize will use it as helper and try to stay close to this value (CurResizeRatio <  1) - Will be updated when user resizes a portrait
        CurResizeRatio                  [Ratio in use when KeepRatioOnResize]
        ResizeSensitivity               Pixels sensitivity from the window's edges

        BringToFrontOnClick             [False, True]

        DetachedPosition				[Custom, TopLeft, TopCenter, TopRight, CenterLeft, CenterCenter, CenterRight, BottomLeft, BottomCenter, BottomRight]
        DetachedPositionMargin			[X, Y, CX, CY]						| Does not affect the Size / Eg. No point to provide both X/CX
        DetachedFixedPosition			[X, Y]								| if remember only first time
        DetachedFixedSize				[CX, CY]							| if remember only first time
        DetachedRememberPosition		[False, True]
        DetachedRememberSize			[False, True]
        DetachedTopMost					[False, True] (Surfaces Only Required?)
        DetachedShowInTaskbar           [False, True]                       | When Detached or Fullscreen will be in Switch Apps
        DetachedNoOwner                 [False, True]                       | When Detached will not follow the owner's window state (Minimize/Maximize)

        KeyBindings						[None, Surface, Overlay, Both]
        MouseBindings                   [None, Surface, Overlay, Both]      | Required for all other mouse events

        ActivityTimeout					[0: Disabled]						| Requires Player?
        ActivityRefresh?				[None, Surface, Overlay, Both]		| MouseMove / MouseDown / KeyUp

        PassWheelToOwner?				[None, Surface, Overlay, Both]		| When host belongs to ScrollViewer

        IsAttached                      [False, True]
        IsFullScreen                    [False, True]                       | Should be used instead of WindowStates
        IsMinimized                     [False, True]                       | Should be used instead of WindowStates
        IsResizing						[ReadOnly]
        IsSwapping						[ReadOnly]
        IsStandAlone					[ReadOnly]
     */

    /* TODO
     * 1) The surface / overlay events code is repeated
     * 2) PassWheelToOwner (Related with LayoutUpdate performance / ScrollViewer) / ActivityRefresh
     * 3) Attach to different Owner (Load/Unload) and change Overlay?
     * 4) WindowStates should not be used by user directly. Use IsMinimized and IsFullScreen instead.
     * 5) WS_EX_NOACTIVATE should be set but for some reason is not required (for none-styled windows)? Currently BringToFront does the job but (only for left clicks?)
     */

    #region Properties / Variables
    public event EventHandler       SurfaceCreated;
    public event EventHandler       OverlayCreated;
    public event DragEventHandler   OnSurfaceDrop;
    public event DragEventHandler   OnOverlayDrop;

    public Window       Owner               { get; private set; }
    public Window       Surface             { get; private set; }
    public IntPtr       SurfaceHandle       { get; private set; }
    public IntPtr       OverlayHandle       { get; private set; }
    public IntPtr       OwnerHandle         { get; private set; }

    public int          UniqueId            { get; private set; }
    public bool         Disposed            { get; private set; }

    public double       DpiX                { get; private set
        {
            field = value;
            // copy to global variable
            NativeMethods.DpiX = value;
        }
    } = 1;
    public double       DpiY                { get; private set
        {
            field = value;
            NativeMethods.DpiY = value;
        }
    } = 1;
    public double       DpiXSource          { get; private set
        {
            field = value;
            NativeMethods.DpiXSource = value;
        }
    } = 96;
    public double       DpiYSource          { get; private set
        {
            field = value;
            NativeMethods.DpiYSource = value;
        }
    } = 96;

    public bool         IsResizing          { get; private set; }
    public bool         IsStandAlone        { get; private set; }
    public bool         IsSwappingStarted   { get; private set; }
    public bool         IsPanMoving         { get; private set; }
    public bool         IsDragMoving        { get; private set; }
    public bool         IsDragMovingOwner   { get; private set; }
    public int          ResizeSensitivity   { get; set; } = 6;
    public double       CurResizeRatio      => curResizeRatio;

    static bool         isDesignMode;
    static int          idGenerator = 1;
    static WindowStyles NONE_STYLE      = WindowStyles.WS_MINIMIZEBOX | WindowStyles.WS_CLIPSIBLINGS | WindowStyles.WS_CLIPCHILDREN | WindowStyles.WS_VISIBLE; // WS_MINIMIZEBOX required for swapchain
    
    double              curResizeRatio;
    bool                surfaceClosed, surfaceClosing, overlayClosed;
    double              panPrevX, panPrevY;
    bool                isMouseBindingsSubscribedSurface;
    bool                isMouseBindingsSubscribedOverlay;
    Window              standAloneOverlay;

    ResizeSide          resizingSide;
    double              ratioBeforeFullScreen;
    int                 wantedWidth, wantedHeight;

    RECT                curRect;
    Rect                rectDetachedDpi = Rect.Empty;
    Rect                rectInit, rectInitLast;
    Rect                rectIntersect, rectIntersectLast;
    POINT               pMLD;
    POINT               pMM;
    RECT                rectSizeMLD;
    Thickness           rectMarginDpiMLD;
    SizeConstraints     sizeBoundsMLD;
    DragOwnerMLD        dragOwnerMLD;

    private class FlyleafHostDropWrap { public FlyleafHost FlyleafHost; } // To allow non FlyleafHosts to drag & drop
    protected readonly LogHandler Log;
    static readonly Type _flType    = typeof(FlyleafHost);
    static readonly Type _awType    = typeof(AvailableWindows);
    static readonly Type _intType   = typeof(int);
    static readonly Type _boolType  = typeof(bool);
    #endregion

    #region Dependency Properties
    public void BringToFront() => SetWindowPos(SurfaceHandle, IntPtr.Zero, 0, 0, 0, 0, (UInt32)(SetWindowPosFlags.SWP_SHOWWINDOW | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOMOVE));
    public bool BringToFrontOnClick
    {
        get { return (bool)GetValue(BringToFrontOnClickProperty); }
        set { SetValue(BringToFrontOnClickProperty, value); }
    }
    public static readonly DependencyProperty BringToFrontOnClickProperty =
        DependencyProperty.Register(nameof(BringToFrontOnClick), _boolType, _flType, new(true));

    public AvailableWindows OpenOnDrop
    {
        get => (AvailableWindows)GetValue(OpenOnDropProperty);
        set => SetValue(OpenOnDropProperty, value);
    }
    public static readonly DependencyProperty OpenOnDropProperty =
        DependencyProperty.Register(nameof(OpenOnDrop), _awType, _flType, new(AvailableWindows.Surface, new(DropChanged)));

    public AvailableWindows SwapOnDrop
    {
        get => (AvailableWindows)GetValue(SwapOnDropProperty);
        set => SetValue(SwapOnDropProperty, value);
    }
    public static readonly DependencyProperty SwapOnDropProperty =
        DependencyProperty.Register(nameof(SwapOnDrop), _awType, _flType, new(AvailableWindows.Surface, new(DropChanged)));

    public AvailableWindows SwapDragEnterOnShift
    {
        get => (AvailableWindows)GetValue(SwapDragEnterOnShiftProperty);
        set => SetValue(SwapDragEnterOnShiftProperty, value);
    }
    public static readonly DependencyProperty SwapDragEnterOnShiftProperty =
        DependencyProperty.Register(nameof(SwapDragEnterOnShift), _awType, _flType, new(AvailableWindows.Surface));

    public AvailableWindows ToggleFullScreenOnDoubleClick
    {
        get => (AvailableWindows)GetValue(ToggleFullScreenOnDoubleClickProperty);
        set => SetValue(ToggleFullScreenOnDoubleClickProperty, value);
    }
    public static readonly DependencyProperty ToggleFullScreenOnDoubleClickProperty =
        DependencyProperty.Register(nameof(ToggleFullScreenOnDoubleClick), _awType, _flType, new(AvailableWindows.Surface));

    public AvailableWindows PanMoveOnCtrl
    {
        get => (AvailableWindows)GetValue(PanMoveOnCtrlProperty);
        set => SetValue(PanMoveOnCtrlProperty, value);
    }
    public static readonly DependencyProperty PanMoveOnCtrlProperty =
        DependencyProperty.Register(nameof(PanMoveOnCtrl), _awType, _flType, new(AvailableWindows.Surface));

    public AvailableWindows PanRotateOnShiftWheel
    {
        get => (AvailableWindows)GetValue(PanRotateOnShiftWheelProperty);
        set => SetValue(PanRotateOnShiftWheelProperty, value);
    }
    public static readonly DependencyProperty PanRotateOnShiftWheelProperty =
        DependencyProperty.Register(nameof(PanRotateOnShiftWheel), _awType, _flType, new(AvailableWindows.Surface));

    public AvailableWindows PanZoomOnCtrlWheel
    {
        get => (AvailableWindows)GetValue(PanZoomOnCtrlWheelProperty);
        set => SetValue(PanZoomOnCtrlWheelProperty, value);
    }
    public static readonly DependencyProperty PanZoomOnCtrlWheelProperty =
        DependencyProperty.Register(nameof(PanZoomOnCtrlWheel), _awType, _flType, new(AvailableWindows.Surface));

    public AttachedDragMoveOptions AttachedDragMove
    {
        get => (AttachedDragMoveOptions)GetValue(AttachedDragMoveProperty);
        set => SetValue(AttachedDragMoveProperty, value);
    }
    public static readonly DependencyProperty AttachedDragMoveProperty =
        DependencyProperty.Register(nameof(AttachedDragMove), typeof(AttachedDragMoveOptions), _flType, new(AttachedDragMoveOptions.Surface));

    public AvailableWindows DetachedDragMove
    {
        get => (AvailableWindows)GetValue(DetachedDragMoveProperty);
        set => SetValue(DetachedDragMoveProperty, value);
    }
    public static readonly DependencyProperty DetachedDragMoveProperty =
        DependencyProperty.Register(nameof(DetachedDragMove), _awType, _flType, new(AvailableWindows.Surface));

    public AvailableWindows AttachedResize
    {
        get => (AvailableWindows)GetValue(AttachedResizeProperty);
        set => SetValue(AttachedResizeProperty, value);
    }
    public static readonly DependencyProperty AttachedResizeProperty =
        DependencyProperty.Register(nameof(AttachedResize), _awType, _flType, new(AvailableWindows.Surface));

    public AvailableWindows DetachedResize
    {
        get => (AvailableWindows)GetValue(DetachedResizeProperty);
        set => SetValue(DetachedResizeProperty, value);
    }
    public static readonly DependencyProperty DetachedResizeProperty =
        DependencyProperty.Register(nameof(DetachedResize), _awType, _flType, new(AvailableWindows.Surface));

    bool _KeepRatioOnResize;
    public bool KeepRatioOnResize
    {
        get => (bool)GetValue(KeepRatioOnResizeProperty);
        set { _KeepRatioOnResize = value; SetValue(KeepRatioOnResizeProperty, value); }
    }
    public static readonly DependencyProperty KeepRatioOnResizeProperty =
        DependencyProperty.Register(nameof(KeepRatioOnResize), _boolType, _flType, new(false, new(OnKeepRatioOnResizeChanged)));

    public int PreferredLandscapeWidth
    {
        get { return (int)GetValue(PreferredLandscapeWidthProperty); }
        set { SetValue(PreferredLandscapeWidthProperty, value); }
    }
    public static readonly DependencyProperty PreferredLandscapeWidthProperty =
    DependencyProperty.Register(nameof(PreferredLandscapeWidth), _intType, _flType, new(0));

    public int PreferredPortraitHeight
    {
        get { return (int)GetValue(PreferredPortraitHeightProperty); }
        set { SetValue(PreferredPortraitHeightProperty, value); }
    }
    public static readonly DependencyProperty PreferredPortraitHeightProperty =
    DependencyProperty.Register(nameof(PreferredPortraitHeight), _intType, _flType, new(0));

    public int PreferredLandscapeWidthAttached
    {
        get { return (int)GetValue(PreferredLandscapeWidthAttachedProperty); }
        set { SetValue(PreferredLandscapeWidthAttachedProperty, value); }
    }
    public static readonly DependencyProperty PreferredLandscapeWidthAttachedProperty =
    DependencyProperty.Register(nameof(PreferredLandscapeWidthAttached), _intType, _flType, new(0));

    public int PreferredPortraitHeightAttached
    {
        get { return (int)GetValue(PreferredPortraitHeightAttachedProperty); }
        set { SetValue(PreferredPortraitHeightAttachedProperty, value); }
    }
    public static readonly DependencyProperty PreferredPortraitHeightAttachedProperty =
    DependencyProperty.Register(nameof(PreferredPortraitHeightAttached), _intType, _flType, new(0));

    public DetachedPositionOptions DetachedPosition
    {
        get => (DetachedPositionOptions)GetValue(DetachedPositionProperty);
        set => SetValue(DetachedPositionProperty, value);
    }
    public static readonly DependencyProperty DetachedPositionProperty =
        DependencyProperty.Register(nameof(DetachedPosition), typeof(DetachedPositionOptions), _flType, new(DetachedPositionOptions.CenterCenter));

    public Thickness DetachedPositionMargin
    {
        get => (Thickness)GetValue(DetachedPositionMarginProperty);
        set => SetValue(DetachedPositionMarginProperty, value);
    }
    public static readonly DependencyProperty DetachedPositionMarginProperty =
        DependencyProperty.Register(nameof(DetachedPositionMargin), typeof(Thickness), _flType, new(new Thickness(0, 0, 0, 0)));

    public Point DetachedFixedPosition
    {
        get => (Point)GetValue(DetachedFixedPositionProperty);
        set => SetValue(DetachedFixedPositionProperty, value);
    }
    public static readonly DependencyProperty DetachedFixedPositionProperty =
        DependencyProperty.Register(nameof(DetachedFixedPosition), typeof(Point), _flType, new(new Point()));

    public Size DetachedFixedSize
    {
        get => (Size)GetValue(DetachedFixedSizeProperty);
        set => SetValue(DetachedFixedSizeProperty, value);
    }
    public static readonly DependencyProperty DetachedFixedSizeProperty =
        DependencyProperty.Register(nameof(DetachedFixedSize), typeof(Size), _flType, new(new Size(300, 200)));

    public bool DetachedRememberPosition
    {
        get => (bool)GetValue(DetachedRememberPositionProperty);
        set => SetValue(DetachedRememberPositionProperty, value);
    }
    public static readonly DependencyProperty DetachedRememberPositionProperty =
        DependencyProperty.Register(nameof(DetachedRememberPosition), _boolType, _flType, new(true));

    public bool DetachedRememberSize
    {
        get => (bool)GetValue(DetachedRememberSizeProperty);
        set => SetValue(DetachedRememberSizeProperty, value);
    }
    public static readonly DependencyProperty DetachedRememberSizeProperty =
        DependencyProperty.Register(nameof(DetachedRememberSize), _boolType, _flType, new(true));

    public bool DetachedTopMost
    {
        get => (bool)GetValue(DetachedTopMostProperty);
        set => SetValue(DetachedTopMostProperty, value);
    }
    public static readonly DependencyProperty DetachedTopMostProperty =
        DependencyProperty.Register(nameof(DetachedTopMost), _boolType, _flType, new(false, new(OnDetachedTopMostChanged)));

    public bool DetachedShowInTaskbar
    {
        get { return (bool)GetValue(DetachedShowInTaskbarProperty); }
        set { SetValue(DetachedShowInTaskbarProperty, value); }
    }
    public static readonly DependencyProperty DetachedShowInTaskbarProperty =
        DependencyProperty.Register(nameof(DetachedShowInTaskbar), _boolType, _flType, new(false, new(OnShowInTaskBarChanged)));

    public bool DetachedNoOwner
    {
        get { return (bool)GetValue(DetachedNoOwnerProperty); }
        set { SetValue(DetachedNoOwnerProperty, value); }
    }
    public static readonly DependencyProperty DetachedNoOwnerProperty =
        DependencyProperty.Register(nameof(DetachedNoOwner), _boolType, _flType, new(false, new(OnNoOwnerChanged)));

    public int DetachedMinHeight
    {
        get { return (int)GetValue(DetachedMinHeightProperty); }
        set { SetValue(DetachedMinHeightProperty, value); }
    }
    public static readonly DependencyProperty DetachedMinHeightProperty =
        DependencyProperty.Register(nameof(DetachedMinHeight), _intType, _flType, new(0));

    public int DetachedMinWidth
    {
        get { return (int)GetValue(DetachedMinWidthProperty); }
        set { SetValue(DetachedMinWidthProperty, value); }
    }
    public static readonly DependencyProperty DetachedMinWidthProperty =
        DependencyProperty.Register(nameof(DetachedMinWidth), _intType, _flType, new(0));

    public double DetachedMaxHeight
    {
        get { return (double)GetValue(DetachedMaxHeightProperty); }
        set { SetValue(DetachedMaxHeightProperty, value); }
    }
    public static readonly DependencyProperty DetachedMaxHeightProperty =
        DependencyProperty.Register(nameof(DetachedMaxHeight), typeof(double), _flType, new(double.PositiveInfinity));

    public double DetachedMaxWidth
    {
        get { return (double)GetValue(DetachedMaxWidthProperty); }
        set { SetValue(DetachedMaxWidthProperty, value); }
    }
    public static readonly DependencyProperty DetachedMaxWidthProperty =
        DependencyProperty.Register(nameof(DetachedMaxWidth), typeof(double), _flType, new(double.PositiveInfinity));

    public AvailableWindows KeyBindings
    {
        get => (AvailableWindows)GetValue(KeyBindingsProperty);
        set => SetValue(KeyBindingsProperty, value);
    }
    public static readonly DependencyProperty KeyBindingsProperty =
        DependencyProperty.Register(nameof(KeyBindings), _awType, _flType, new(AvailableWindows.Surface));

    public AvailableWindows MouseBindings
    {
        get => (AvailableWindows)GetValue(MouseBindingsProperty);
        set => SetValue(MouseBindingsProperty, value);
    }
    public static readonly DependencyProperty MouseBindingsProperty =
        DependencyProperty.Register(nameof(MouseBindings), _awType, _flType, new(AvailableWindows.Both, new(OnMouseBindings)));

    public int ActivityTimeout
    {
        get => (int)GetValue(ActivityTimeoutProperty);
        set => SetValue(ActivityTimeoutProperty, value);
    }
    public static readonly DependencyProperty ActivityTimeoutProperty =
        DependencyProperty.Register(nameof(ActivityTimeout), _intType, _flType, new(0, new(OnActivityTimeoutChanged)));

    bool _IsAttached = true;
    public bool IsAttached
    {
        get => (bool)GetValue(IsAttachedProperty);
        set { _IsAttached = value; SetValue(IsAttachedProperty, value); }
    }
    public static readonly DependencyProperty IsAttachedProperty =
        DependencyProperty.Register(nameof(IsAttached), _boolType, _flType, new(true, new(OnIsAttachedChanged)));

    public bool IsMinimized
    {
        get => (bool)GetValue(IsMinimizedProperty);
        set => SetValue(IsMinimizedProperty, value);
    }
    public static readonly DependencyProperty IsMinimizedProperty =
        DependencyProperty.Register(nameof(IsMinimized), _boolType, _flType, new(false, new(OnIsMinimizedChanged)));

    bool _IsFullScreen;
    public bool IsFullScreen
    {
        get => (bool)GetValue(IsFullScreenProperty);
        set { _IsFullScreen = value; SetValue(IsFullScreenProperty, value); }
    }
    public static readonly DependencyProperty IsFullScreenProperty =
        DependencyProperty.Register(nameof(IsFullScreen), _boolType, _flType, new(false, new(OnIsFullScreenChanged)));

    public FrameworkElement MarginTarget
    {
        get => (FrameworkElement)GetValue(MarginTargetProperty);
        set => SetValue(MarginTargetProperty, value);
    }
    public static readonly DependencyProperty MarginTargetProperty =
        DependencyProperty.Register(nameof(MarginTarget), typeof(FrameworkElement), _flType, new(null));

    public object HostDataContext
    {
        get => GetValue(HostDataContextProperty);
        set => SetValue(HostDataContextProperty, value);
    }
    public static readonly DependencyProperty HostDataContextProperty =
        DependencyProperty.Register(nameof(HostDataContext), typeof(object), _flType, new(null));

    public object DetachedContent
    {
        get => GetValue(DetachedContentProperty);
        set => SetValue(DetachedContentProperty, value);
    }
    public static readonly DependencyProperty DetachedContentProperty =
        DependencyProperty.Register(nameof(DetachedContent), typeof(object), _flType, new(null));

    public Player Player
    {
        get => (Player)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }
    public static readonly DependencyProperty PlayerProperty =
        DependencyProperty.Register(nameof(Player), typeof(Player), _flType, new(null, OnPlayerChanged));

    public ControlTemplate OverlayTemplate
    {
        get => (ControlTemplate)GetValue(OverlayTemplateProperty);
        set => SetValue(OverlayTemplateProperty, value);
    }
    public static readonly DependencyProperty OverlayTemplateProperty =
        DependencyProperty.Register(nameof(OverlayTemplate), typeof(ControlTemplate), _flType, new(null, new(OnOverlayTemplateChanged)));

    public Window Overlay
    {
        get => (Window)GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }
    public static readonly DependencyProperty OverlayProperty =
        DependencyProperty.Register(nameof(Overlay), typeof(Window), _flType, new(null, new(OnOverlayChanged)));

    CornerRadius _CornerRadius;
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set { _CornerRadius = value; SetValue(CornerRadiusProperty, value); }
    }
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), _flType, new(new CornerRadius(0), new(OnCornerRadiusChanged)));

    public Brush VideoBackground
    {
        get => (Brush)GetValue(VideoBackgroundProperty);
        set => SetValue(VideoBackgroundProperty, value);
    }
    public static readonly DependencyProperty VideoBackgroundProperty =
        DependencyProperty.Register(nameof(VideoBackground), typeof(Brush), _flType, new(Brushes.Black, new(OnVideoBackgroundChanged)));
    #endregion

    #region Events
    private static void OnMouseBindings(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesignMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        host.SetMouseSurface();
        host.SetMouseOverlay();
    }
    private static void OnDetachedTopMostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesignMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.Surface != null)
            host.Surface.Topmost = !host.IsAttached && host.DetachedTopMost;
    }
    private static void DropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.Surface == null)
            return;

        host.Surface.AllowDrop =
            host.OpenOnDrop == AvailableWindows.Surface || host.OpenOnDrop == AvailableWindows.Both ||
            host.SwapOnDrop == AvailableWindows.Surface || host.SwapOnDrop == AvailableWindows.Both;

        if (host.Overlay == null)
            return;

        host.Overlay.AllowDrop =
            host.OpenOnDrop == AvailableWindows.Overlay || host.OpenOnDrop == AvailableWindows.Both ||
            host.SwapOnDrop == AvailableWindows.Overlay || host.SwapOnDrop == AvailableWindows.Both;
    }
    private static void OnShowInTaskBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.Surface == null)
            return;

        if (host.DetachedShowInTaskbar)
            SetWindowLong(host.SurfaceHandle, GetWindowLongEx(host.SurfaceHandle) |  WindowStylesEx.WS_EX_APPWINDOW);
        else
            SetWindowLong(host.SurfaceHandle, GetWindowLongEx(host.SurfaceHandle) & ~WindowStylesEx.WS_EX_APPWINDOW);
    }
    private static void OnNoOwnerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.Surface == null)
            return;

        if (!host.IsAttached)
            host.Surface.Owner = host.DetachedNoOwner ? null : host.Owner;
    }
    private static void OnKeepRatioOnResizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesignMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        host._KeepRatioOnResize = (bool)e.NewValue;
        host.ResizeRatio();
    }
    private static void OnPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesignMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        host.SetPlayer((Player)e.OldValue);
    }
    private static void OnIsFullScreenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesignMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        host._IsFullScreen = (bool)e.NewValue;
        host.RefreshNormalFullScreen();
    }
    private static void OnIsMinimizedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesignMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.Surface != null)
            host.Surface.WindowState = host.IsMinimized ? WindowState.Minimized : (host.IsFullScreen ? WindowState.Maximized : WindowState.Normal);
    }
    private static void OnIsAttachedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesignMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.IsStandAlone)
        {
            host.IsAttached = false;
            return;
        }

        host._IsAttached = (bool)e.NewValue;

        if (!host.IsLoaded)
            return;

        if (host.IsAttached)
            host.Attach();
        else
            host.Detach();
    }
    private static void OnActivityTimeoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.Player == null)
            return;

        host.Player.Activity.Timeout = host.ActivityTimeout;
    }
    bool setTemplate; // Issue #481 - FlyleafME override SetOverlay will not have a template to initialize properly *bool required if SetOverlay can be called multiple times and with different configs
    private static void OnOverlayTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesignMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.Overlay == null)
        {
            host.setTemplate= true;
            host.Overlay    = new Window() { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true };
            host.setTemplate= false;
        }
        else
            host.Overlay.Template = host.OverlayTemplate;
    }
    private static void OnOverlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (!isDesignMode)
            host.SetOverlay();
        else
        {
            // XSurface.Wpf.Window (can this work on designer?
        }
    }
    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesignMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        host._CornerRadius = (CornerRadius)e.NewValue;

        if (host.Surface == null)
            return;

        if (host?.Player == null)
            return;

        host.Player.Config.Video.CornerRadius = (CornerRadius)e.NewValue;

    }
    private static void OnVideoBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.Surface != null)
            host.Surface.Background = (Brush)e.NewValue;
    }
    private static object OnContentChanging(DependencyObject d, object baseValue)
    {
        if (isDesignMode)
            return baseValue;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return host.DetachedContent;

        if (baseValue != null && host.Overlay == null)
            host.Overlay = new Window() { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true };

        if (host.Overlay != null)
            host.Overlay.Content = baseValue;

        return host.DetachedContent;
    }

    private void Host_Loaded(object sender, RoutedEventArgs e)
    {
        Window owner = Window.GetWindow(this);
        if (owner == null)
            return;

        var ownerHandle = new WindowInteropHelper(owner).EnsureHandle();

        // Owner Changed
        if (Owner != null)
        {
            if (!_IsAttached || OwnerHandle == ownerHandle)
                return; // Check OwnerHandle changed (NOTE: Owner can be the same class/window but the handle can be different)

            Owner.DpiChanged    -= Owner_DpiChanged;
            Owner.SizeChanged   -= Owner_SizeChanged;

            Surface.Hide();
            Overlay?.Hide();
            Detach();

            Owner           = owner;
            OwnerHandle     = ownerHandle;
            Surface.Title   = Owner.Title;
            Surface.Icon    = Owner.Icon;

            Owner.DpiChanged    += Owner_DpiChanged;
            Owner.SizeChanged   += Owner_SizeChanged;

            Attach();
            rectDetachedDpi = Rect.Empty; // Attach will set it wrong first time
            Host_IsVisibleChanged(null, new());

            return;
        }

        Owner           = owner;
        OwnerHandle     = ownerHandle;
        HostDataContext = DataContext;

        SetSurface();

        Surface.Title   = Owner.Title;
        Surface.Icon    = Owner.Icon;

        Owner.DpiChanged    += Owner_DpiChanged;
        Owner.SizeChanged   += Owner_SizeChanged;
        DataContextChanged  += Host_DataContextChanged;
        LayoutUpdated       += Host_LayoutUpdated;
        IsVisibleChanged    += Host_IsVisibleChanged;

        // TBR: We need to ensure that Surface/Overlay will be initial Show once to work properly (issue #415)
        if (_IsAttached)
        {
            if (curResizeRatio == 0 && ActualWidth > 10 && ActualHeight > 10)
                curResizeRatio = ActualWidth / ActualHeight;

            Attach();
            rectDetachedDpi = Rect.Empty; // Attach will set it wrong first time
            Surface.Show();
            Overlay?.Show();
            Host_IsVisibleChanged(null, new());
        }
        else
        {
            if (curResizeRatio == 0 && Surface.ActualWidth > 10 && Surface.ActualHeight > 10)
                curResizeRatio = Surface.ActualWidth / Surface.ActualHeight;

            Detach();
            ResizeRatio();
            Surface.Show();
            Overlay?.Show();

            if (curResizeRatio == 0 && Surface.ActualWidth > 10 && Surface.ActualHeight > 10)
                curResizeRatio = Surface.ActualWidth / Surface.ActualHeight;
        }
    }

    // WindowChrome Issue #410: It will not properly move child windows when resized from top or left
    private void Owner_SizeChanged(object sender, SizeChangedEventArgs e)
        => rectInitLast = rectIntersectLast = Rect.Empty;

    private void Owner_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (e.OriginalSource == Owner && IsAttached)
        {
            rectInitLast = rectIntersectLast = Rect.Empty;
            DpiX = e.NewDpi.DpiScaleX;
            DpiY = e.NewDpi.DpiScaleY;
            DpiXSource = e.NewDpi.PixelsPerInchX;
            DpiYSource = e.NewDpi.PixelsPerInchY;
            ResizeRatio();
            RefreshBitmapSubtitlesDpi();
        }
    }

    private void Host_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        // TBR
        // 1. this.DataContext: FlyleafHost's DataContext will not be affected (Inheritance)
        // 2. Overlay.DataContext: Overlay's DataContext will be FlyleafHost itself
        // 3. Overlay.DataContext.HostDataContext: FlyleafHost's DataContext includes HostDataContext to access FlyleafHost's DataContext
        // 4. In case of Stand Alone will let the user to decide

        HostDataContext = DataContext;
    private void Host_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_IsAttached)
            return;

        if (IsVisible)
        {
            Host_Loaded(null, null);
            Surface.Show();

            if (Overlay != null)
            {
                Overlay.Show();

                // It happens (eg. with MetroWindow) that overlay left will not be equal to surface left so we reset it by detach/attach the overlay to surface (https://github.com/SuRGeoNix/Flyleaf/issues/370)
                RECT surfRect = new();
                RECT overRect = new();
                GetWindowRect(SurfaceHandle, ref surfRect);
                GetWindowRect(OverlayHandle, ref overRect);

                if (surfRect.Left != overRect.Left)
                {
                    // Detach Overlay
                    SetParent(OverlayHandle, IntPtr.Zero);
                    SetWindowLong(OverlayHandle, NONE_STYLE);
                    Overlay.Owner = null;

                    SetWindowPos(OverlayHandle, IntPtr.Zero, 0, 0, (int)Surface.ActualWidth, (int)Surface.ActualHeight,
                        (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));

                    // Attache Overlay
                    SetWindowLong(OverlayHandle, NONE_STYLE | WindowStyles.WS_CHILD | WindowStyles.WS_MAXIMIZE);
                    Overlay.Owner = Surface;
                    SetParent(OverlayHandle, SurfaceHandle);

                    // Required to restore overlay
                    SetRect(Rect.Empty);
                }
            }

            // TBR: First time loaded in a tab control could cause UCEERR_RENDERTHREADFAILURE (can be avoided by hide/show again here)
        }
        else
        {
            Surface.Hide();
            Overlay?.Hide();
        }
    }
    private void Host_LayoutUpdated(object sender, EventArgs e)
    {
        // Finds Rect Intersect with FlyleafHost's parents and Clips Surface/Overlay (eg. within ScrollViewer)
        // TBR: Option not to clip rect or stop at first/second parent?
        // For performance should focus only on ScrollViewer if any and Owner Window (other sources that clip our host?)

        if (!IsVisible || !_IsAttached || _IsFullScreen)
            return;

        try
        {
            rectInit = rectIntersect = new(TransformToAncestor(Owner).Transform(PointEmpty), RenderSize);

            FrameworkElement parent = this;
            while ((parent = VisualTreeHelper.GetParent(parent) as FrameworkElement) != null)
            {
                if (parent.FlowDirection == FlowDirection.RightToLeft)
                {
                    var location = parent.TransformToAncestor(Owner).Transform(PointEmpty);
                    location.X  -= parent.RenderSize.Width;
                    rectIntersect.Intersect(new(location, parent.RenderSize));
                }
                else
                    rectIntersect.Intersect(new(parent.TransformToAncestor(Owner).Transform(PointEmpty), parent.RenderSize));
            }

            //Log.Error($"{rectInit} | {rectIntersect}");
            
            if (rectInit != rectInitLast)
            {
                rectInitLast = rectInit;
                SetRect(rectInit);
            }
            
            if (rectIntersect != Rect.Empty)
            {
                rectIntersect.X -= rectInit.X;
                rectIntersect.Y -= rectInit.Y;

                if (rectIntersectLast != rectIntersect)
                {
                    rectIntersectLast = rectIntersect;
                    SetVisibleRect(rectIntersect);
                }
            }
            else if (rectIntersectLast != RectZero)
            {
                rectIntersectLast = RectZero;
                SetVisibleRect(RectZero);
            }
        }
        catch (Exception ex)
        {
            // It has been noticed with NavigationService (The visual tree changes, visual root IsVisible is false but FlyleafHost is still visible)
            if (CanDebug) Log.Debug($"Host_LayoutUpdated: {ex.Message}");

            // TBR: (Currently handle on each time Visible=true) It's possible that the owner/parent has been changed (for some reason Host_Loaded will not be called) *probably when the Owner stays the same but the actual Handle changes
            //if (ex.Message == "The specified Visual is not an ancestor of this Visual.")
                //Host_Loaded(null, null);
        }
    }
    #endregion

    #region Events Surface / Overlay
    private void Surface_KeyDown(object sender, KeyEventArgs e) { if (KeyBindings == AvailableWindows.Surface || KeyBindings == AvailableWindows.Both) e.Handled = Player.KeyDown(Player, e); }
    private void Overlay_KeyDown(object sender, KeyEventArgs e) { if (KeyBindings == AvailableWindows.Overlay || KeyBindings == AvailableWindows.Both) e.Handled = Player.KeyDown(Player, e); }

    private void Surface_KeyUp(object sender, KeyEventArgs e) { if (KeyBindings == AvailableWindows.Surface || KeyBindings == AvailableWindows.Both) e.Handled = Player.KeyUp(Player, e); }
    private void Overlay_KeyUp(object sender, KeyEventArgs e) { if (KeyBindings == AvailableWindows.Overlay || KeyBindings == AvailableWindows.Both) e.Handled = Player.KeyUp(Player, e); }

    private void Surface_Drop(object sender, DragEventArgs e)
    {
        IsSwappingStarted = false;
        Surface.ReleaseMouseCapture();
        if (e.Data.GetDataPresent(typeof(FlyleafHostDropWrap)))
        {
            FlyleafHostDropWrap hostWrap = (FlyleafHostDropWrap) e.Data.GetData(typeof(FlyleafHostDropWrap));

            // Swap FlyleafHosts
            if (hostWrap != null)
            {
                (hostWrap.FlyleafHost.Player, Player) = (Player, hostWrap.FlyleafHost.Player);
                Surface.Activate();
                return;
            }
        }

        if (Player == null)
            return;

        // Invoke event first and see if it gets handled
        OnSurfaceDrop?.Invoke(this, e);

        if (!e.Handled)
        {
            // Player Open Text (TBR: Priority matters, eg. firefox will set both - cached file thumbnail of a video & the link of the video)
            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                string text = e.Data.GetData(DataFormats.Text, false).ToString();
                if (text.Length > 0)
                    Player.OpenAsync(text);
            }

            // Player Open File
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string filename = ((string[])e.Data.GetData(DataFormats.FileDrop, false))[0];
                Player.OpenAsync(filename);
            }
        }

        Surface.Activate();
    }
    private void Overlay_Drop(object sender, DragEventArgs e)
    {
        IsSwappingStarted = false;
        Overlay.ReleaseMouseCapture();
        if (e.Data.GetDataPresent(typeof(FlyleafHostDropWrap)))
        {
            FlyleafHostDropWrap hostWrap = (FlyleafHostDropWrap) e.Data.GetData(typeof(FlyleafHostDropWrap));

            // Swap FlyleafHosts
            if (hostWrap != null)
            {
                (hostWrap.FlyleafHost.Player, Player) = (Player, hostWrap.FlyleafHost.Player);
                Overlay.Activate();
                return;
            }
        }

        if (Player == null)
            return;

        // Invoke event first and see if it gets handled
        OnOverlayDrop?.Invoke(this, e);

        if (!e.Handled)
        {
            // Player Open Text
            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                string text = e.Data.GetData(DataFormats.Text, false).ToString();
                if (text.Length > 0)
                    Player.OpenAsync(text);
            }

            // Player Open File
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string filename = ((string[])e.Data.GetData(DataFormats.FileDrop, false))[0];
                Player.OpenAsync(filename);
            }
        }

        Overlay.Activate();
    }
    private void Surface_DragEnter(object sender, DragEventArgs e) { if (Player != null) e.Effects = DragDropEffects.All; }
    private void Overlay_DragEnter(object sender, DragEventArgs e) { if (Player != null) e.Effects = DragDropEffects.All; }
    private void Surface_StateChanged(object sender, EventArgs e)
    {
        switch (Surface.WindowState)
        {
            case WindowState.Maximized:
                IsFullScreen = true;
                IsMinimized = false;
                Player?.Activity.RefreshFullActive();

                break;

            case WindowState.Normal:

                IsFullScreen = false;
                IsMinimized = false;
                Player?.Activity.RefreshFullActive();

                break;

            case WindowState.Minimized:

                IsMinimized = true;
                break;
        }
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SO_MouseLeftButtonDown(e, Surface);
    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SO_MouseLeftButtonDown(e, Overlay);
    private void SO_MouseLeftButtonDown(MouseButtonEventArgs e, Window window)
    {
        AvailableWindows availWindow;
        AttachedDragMoveOptions availDragMove;
        AttachedDragMoveOptions availDragMoveOwner;

        if (window == Surface)
        {
            availWindow         = AvailableWindows.Surface;
            availDragMove       = AttachedDragMoveOptions.Surface;
            availDragMoveOwner  = AttachedDragMoveOptions.SurfaceOwner;
        }
        else
        {
            availWindow         = AvailableWindows.Overlay;
            availDragMove       = AttachedDragMoveOptions.Overlay;
            availDragMoveOwner  = AttachedDragMoveOptions.OverlayOwner;
        }

        if (BringToFrontOnClick) // Activate and Z-order top
            BringToFront();

        window.Focus();
        Player?.Activity.RefreshFullActive();

        IsSwappingStarted = false; // currently we don't care if it was cancelled (it can be stay true if we miss the mouse up) - QueryContinueDrag

        // Resize
        if (resizingSide != ResizeSide.None)
        {
            IsResizing = true;
            _ = GetCursorPos(out pMLD);
            GetWindowRect(SurfaceHandle, ref rectSizeMLD);

            if (_IsAttached)
            {
                LayoutUpdated      -= Host_LayoutUpdated;
                ResetVisibleRect();

                sizeBoundsMLD       = new((int)(MinWidth * DpiX), (int)(Owner.ActualWidth * DpiX), (int)(MinHeight * DpiY), (int)(Owner.ActualHeight * DpiY));
                rectMarginDpiMLD    = MarginTarget.Margin;
                var screenPos       = Owner.PointToScreen(PointEmpty); // No DPI
                rectSizeMLD.Left   -= (int) screenPos.X;
                rectSizeMLD.Right  -= (int) screenPos.X;
                rectSizeMLD.Top    -= (int) screenPos.Y;
                rectSizeMLD.Bottom -= (int) screenPos.Y;
            }
            else
            {
                var bounds          = System.Windows.Forms.Screen.FromPoint(new(rectSizeMLD.Left, rectSizeMLD.Top)).Bounds;
                sizeBoundsMLD       = new((int)(Surface.MinWidth * DpiX), (int)(Math.Min(Surface.MaxWidth * DpiX, bounds.Width) ), (int)(Surface.MinHeight * DpiY), (int)(Math.Min(Surface.MaxHeight * DpiY, bounds.Height)));
            }
        }

        // Swap
        else if ((SwapOnDrop == availWindow || SwapOnDrop == AvailableWindows.Both) &&
            (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
        {
            IsSwappingStarted = true;
            DragDrop.DoDragDrop(this, new FlyleafHostDropWrap() { FlyleafHost = this }, DragDropEffects.Move);

            return; // No Capture
        }

        // PanMove
        else if (Player != null &&
            (PanMoveOnCtrl == availWindow || PanMoveOnCtrl == AvailableWindows.Both) &&
            (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)))
        {
            IsPanMoving = true;
            _ = GetCursorPos(out pMLD);
            panPrevX    = Player.Config.Video.PanXOffset;
            panPrevY    = Player.Config.Video.PanYOffset;
        }

        // DragMoveOwner
        else if (_IsAttached && Owner != null &&
            (AttachedDragMove == availDragMoveOwner || AttachedDragMove == AttachedDragMoveOptions.BothOwner))
        {
            IsDragMovingOwner   = true;
            _ = GetCursorPos(out pMLD);
            dragOwnerMLD.Window = Owner.Owner;
            dragOwnerMLD.Window ??= Owner;
            dragOwnerMLD.Left   = dragOwnerMLD.Window.Left;
            dragOwnerMLD.Top    = dragOwnerMLD.Window.Top;
        }

        // DragMove (Attach|Detach)
        else if ((_IsAttached && (AttachedDragMove == availDragMove  || AttachedDragMove == AttachedDragMoveOptions.Both))
            ||  (!_IsAttached && (DetachedDragMove == availWindow    || DetachedDragMove == AvailableWindows.Both)))
        {
            IsDragMoving = true;
            _ = GetCursorPos(out pMLD);

            if (_IsAttached)
                rectMarginDpiMLD = MarginTarget.Margin;
            else
                GetWindowRect(SurfaceHandle, ref rectSizeMLD);
        }

        else
            return; // No Capture

        window.CaptureMouse();
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => SO_ReleaseCapture(Surface);
    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => SO_ReleaseCapture(Overlay);
    private void Surface_LostMouseCapture(object sender, MouseEventArgs e) => SO_ReleaseCapture(Surface);
    private void Overlay_LostMouseCapture(object sender, MouseEventArgs e) => SO_ReleaseCapture(Overlay);

    private void SO_ReleaseCapture(Window window)
    {
        if (!IsResizing && !IsPanMoving && !IsDragMoving && !IsDragMovingOwner)
            return;

        window.ReleaseMouseCapture();

        if (IsResizing)
        {
            resizingSide    = ResizeSide.None;
            window.Cursor   = Cursors.Arrow;
            IsResizing      = false;

            if (_IsAttached)
            {
                if (curResizeRatio < 1)
                    PreferredPortraitHeightAttached = (int)(wantedHeight / DpiY);
                else
                    PreferredLandscapeWidthAttached = (int)(wantedWidth / DpiX);

                Host_LayoutUpdated(null, null); // Restores clipped rect
                LayoutUpdated += Host_LayoutUpdated;
            }
            else
            {
                if (curResizeRatio < 1)
                    PreferredPortraitHeight = (int)(wantedHeight / DpiY);
                else
                    PreferredLandscapeWidth = (int)(wantedWidth / DpiX);
            }
        }
        else if (IsPanMoving)
            IsPanMoving = false;
        else if (IsDragMoving)
            IsDragMoving = false;
        else if (IsDragMovingOwner)
            IsDragMovingOwner = false;
        else
            return;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        _ = GetCursorPos(out var cur);

        if (Player != null && cur != pMM)
        {
            Player.Activity.RefreshFullActive();
            pMM = cur;
        }

        // Resize Sides (CanResize + !MouseDown + !FullScreen)
        if (e.MouseDevice.LeftButton != MouseButtonState.Pressed)
        {
            if ( !_IsFullScreen &&
                ((_IsAttached && (AttachedResize == AvailableWindows.Surface || AttachedResize == AvailableWindows.Both)) ||
                (!_IsAttached && (DetachedResize == AvailableWindows.Surface || DetachedResize == AvailableWindows.Both))))
            {
                Surface.Cursor = ResizeSides(cur);
            }

            return;
        }

        SO_MouseLeftDownAndMove(cur);
    }
    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        _ = GetCursorPos(out var cur);

        if (Player != null && cur != pMM)
        {
            Player.Activity.RefreshFullActive();
            pMM = cur;
        }

        // Resize Sides (CanResize + !MouseDown + !FullScreen)
        if (e.MouseDevice.LeftButton != MouseButtonState.Pressed)
        {
            if (!_IsFullScreen && cur != POINT.Empty &&
                ((_IsAttached && (AttachedResize == AvailableWindows.Overlay || AttachedResize == AvailableWindows.Both)) ||
                (!_IsAttached && (DetachedResize == AvailableWindows.Overlay || DetachedResize == AvailableWindows.Both))))
            {
                Overlay.Cursor = ResizeSides(cur);
            }

            return;
        }

        SO_MouseLeftDownAndMove(cur);
    }
    private void SO_MouseLeftDownAndMove(POINT cur)
    {
        if (IsSwappingStarted)
            return;

        // Player's Pan Move (Ctrl + Drag Move)
        if (IsPanMoving)
        {
            Player.Config.Video.PanXOffset = panPrevX + ((cur.X - pMLD.X) / Surface.ActualWidth);
            Player.Config.Video.PanYOffset = panPrevY + ((cur.Y - pMLD.Y) / Surface.ActualHeight);
            return;
        }

        if (_IsFullScreen)
            return;

        // Resize (MouseDown + ResizeSide != 0)
        if (IsResizing)
            Resize(cur);

        // Drag Move Self (Attached|Detached)
        // TBR: UI Freeze / Frame Drop / Audio Crackling while playing possible (it does not happen with resize and it happens only -SetWindowPos- with this?) | Same results with task /fps
        else if (IsDragMoving)
        {
            if (_IsAttached)
            {
                MarginTarget.Margin = new(
                    rectMarginDpiMLD.Left + ((cur.X - pMLD.X) / DpiX),
                    rectMarginDpiMLD.Top  + ((cur.Y - pMLD.Y) / DpiY),
                    rectMarginDpiMLD.Right,
                    rectMarginDpiMLD.Bottom);
            }
            else
                SetWindowPos(SurfaceHandle, IntPtr.Zero, rectSizeMLD.Left + cur.X - pMLD.X, rectSizeMLD.Top + cur.Y - pMLD.Y, 0, 0,
                    (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_NOSIZE));
        }

        // Drag Move Owner (Attached)
        else if (IsDragMovingOwner)
        {
            dragOwnerMLD.Window.Left    = dragOwnerMLD.Left + (cur.X - pMLD.X) / DpiX;
            dragOwnerMLD.Window.Top     = dragOwnerMLD.Top +  (cur.Y - pMLD.Y) / DpiY;
        }
    }

    private void Surface_MouseLeave(object sender, MouseEventArgs e) { resizingSide = ResizeSide.None; Surface.Cursor = Cursors.Arrow; }
    private void Overlay_MouseLeave(object sender, MouseEventArgs e) { resizingSide = ResizeSide.None; Overlay.Cursor = Cursors.Arrow; }

    private void Surface_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ToggleFullScreenOnDoubleClick == AvailableWindows.Surface || ToggleFullScreenOnDoubleClick == AvailableWindows.Both) { IsFullScreen = !IsFullScreen; e.Handled = true; } }
    private void Overlay_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ToggleFullScreenOnDoubleClick == AvailableWindows.Overlay || ToggleFullScreenOnDoubleClick == AvailableWindows.Both) { IsFullScreen = !IsFullScreen; e.Handled = true; } }

    private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // NOTE/WPF BUG: Attached Surface (with Owner) will fire the global events (MouseWheel only?*) twice

        if (Player == null || e.Delta == 0 || e.Handled)
            return;

        if      ((Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) &&
            (PanZoomOnCtrlWheel == AvailableWindows.Surface || PanZoomOnCtrlWheel == AvailableWindows.Both))
        {
            var cur = e.GetPosition(Surface);
            Point curDpi = new(cur.X * DpiX, cur.Y * DpiY);
            if (e.Delta > 0)
                Player.Config.Video.ZoomIn(curDpi);
            else
                Player.Config.Video.ZoomOut(curDpi);

            e.Handled = true;
        }
        else if ((Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) &&
            (PanRotateOnShiftWheel == AvailableWindows.Surface || PanZoomOnCtrlWheel == AvailableWindows.Both))
        {
            if (e.Delta > 0)
                Player.Config.Video.RotateRight();
            else
                Player.Config.Video.RotateLeft();

            e.Handled = true;
        }

        //else if (IsAttached) // TBR ScrollViewer
        //{
        //    RaiseEvent(e);
        //}
    }
    private void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Player == null || e.Delta == 0)
            return;

        if      ((Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) &&
            (PanZoomOnCtrlWheel == AvailableWindows.Overlay || PanZoomOnCtrlWheel == AvailableWindows.Both))
        {
            var cur = e.GetPosition(Overlay);
            Point curDpi = new(cur.X * DpiX, cur.Y * DpiY);
            if (e.Delta > 0)
                Player.Config.Video.ZoomIn(curDpi);
            else
                Player.Config.Video.ZoomOut(curDpi);
        }
        else if ((Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) &&
            (PanRotateOnShiftWheel == AvailableWindows.Overlay || PanZoomOnCtrlWheel == AvailableWindows.Both))
        {
            if (e.Delta > 0)
                Player.Config.Video.RotateRight();
            else
                Player.Config.Video.RotateLeft();
        }
    }

    private void Surface_Closed(object sender, EventArgs e)
    {
        surfaceClosed = true;
        Dispose();
    }
    public void CloseCanceled() => surfaceClosing = false; // TBR: Better way of handling Closing/Canceled
    private void Surface_Closing(object sender, CancelEventArgs e) => surfaceClosing = true;
    private void Overlay_Closed(object sender, EventArgs e)
    {
        overlayClosed = true;
        if (!surfaceClosing)
            Surface?.Close();
    }
    private void OverlayStandAlone_Loaded(object sender, RoutedEventArgs e)
    {
        if (Overlay != null)
            return;

        if (standAloneOverlay.WindowStyle != WindowStyle.None || standAloneOverlay.AllowsTransparency == false)
            throw new Exception("Stand-alone FlyleafHost requires WindowStyle = WindowStyle.None and AllowsTransparency = true");

        var source = PresentationSource.FromVisual(standAloneOverlay);
        if (source != null)
        {
            DpiX = source.CompositionTarget.TransformToDevice.M11;
            DpiY = source.CompositionTarget.TransformToDevice.M22;
            DpiXSource = DpiX * 96;
            DpiYSource = DpiY * 96;
        }
        else // should never hit this?* | How to ask for point's dpi if we don't know the point's dpi?
        {
            (DpiX, DpiY) = GetDpiAtPoint(new((int)standAloneOverlay.Left, (int)standAloneOverlay.Top));
            DpiXSource = DpiX * 96;
            DpiYSource = DpiY * 96;
        }

        SetSurface();
        Overlay = standAloneOverlay;
        Overlay.IsVisibleChanged += OverlayStandAlone_IsVisibleChanged;
        Surface.ShowInTaskbar = false; Surface.ShowInTaskbar = true; // It will not be visible in taskbar if user clicks in another window when loading
        OverlayStandAlone_IsVisibleChanged(null, new());
    }
    private void OverlayStandAlone_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Surface should be visible first (this happens only on initialization of standalone)

        if (Surface.IsVisible)
            return;

        if (Overlay.IsVisible)
        {
            Surface.Show();
            ShowWindow(OverlayHandle, (int)ShowWindowCommands.SW_SHOWMINIMIZED);
            ShowWindow(OverlayHandle, (int)ShowWindowCommands.SW_SHOWMAXIMIZED);
        }
    }
    private void Surface_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!Surface.IsVisible || Overlay == null)
            return;

        /* Out of monitor's bound issue
         * When hiding the surface and showing it back, windows will consider it's position/size invalid and will try to fix it without sending any position/size changed events
         * C# ActualWidth/ActualHeight will not be updated and the overlay will not fit properly to the surface
         *
         * TBR: Consider when showing the window to prevent windows changing its position/size (requires win32 API and it seems that causes more issues)?
         */

        GetWindowRect(SurfaceHandle, ref curRect);
        SetWindowPos(OverlayHandle, IntPtr.Zero, 0, 0, (int)Math.Round((curRect.Right - curRect.Left) * DpiX), (int)Math.Round((curRect.Bottom - curRect.Top) * DpiY),
            (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));
    }
    private void Surface_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (!_IsAttached)
        {
            DpiX = e.NewDpi.DpiScaleX;
            DpiY = e.NewDpi.DpiScaleY;
            DpiXSource = e.NewDpi.PixelsPerInchX;
            DpiYSource = e.NewDpi.PixelsPerInchY;
            SetRectOverlay(null, null);
            ResizeRatio();
            RefreshBitmapSubtitlesDpi();
        }
    }

    private void RefreshBitmapSubtitlesDpi()
    {
        Player?.Subtitles?.RefreshBitmap();
    }
    #endregion

    #region Constructors
    static FlyleafHost()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(FlyleafHost), new FrameworkPropertyMetadata(typeof(FlyleafHost)));
        ContentProperty.OverrideMetadata(typeof(FlyleafHost), new FrameworkPropertyMetadata(null, new CoerceValueCallback(OnContentChanging)));
    }
    public FlyleafHost()
    {
        UniqueId = idGenerator++;
        isDesignMode = DesignerProperties.GetIsInDesignMode(this);
        if (isDesignMode)
            return;

        MarginTarget= this;
        Log         = new(("[#" + UniqueId + "]").PadRight(8, ' ') + $" [FlyleafHost NP] ");
        Loaded     += Host_Loaded;
    }
    public FlyleafHost(Window standAloneOverlay)
    {
        UniqueId    = idGenerator++;
        Log         = new(("[#" + UniqueId + "]").PadRight(8, ' ') + $" [FlyleafHost NP] ");

        IsStandAlone= true;
        IsAttached  = false;

        this.standAloneOverlay = standAloneOverlay;
        standAloneOverlay.Loaded += OverlayStandAlone_Loaded;
        if (standAloneOverlay.IsLoaded)
            OverlayStandAlone_Loaded(null, null);
    }
    #endregion

    #region Methods
    public virtual void SetPlayer(Player oldPlayer)
    {
        // De-assign old Player's Handle/FlyleafHost
        if (oldPlayer != null)
        {
            Log.Debug($"De-assign Player #{oldPlayer.PlayerId}");

            oldPlayer.Renderer?.SwapChain.Dispose(rendererFrame: false);
            oldPlayer.Host = null;
        }

        if (Player == null)
            return;

        Log.Prefix = ("[#" + UniqueId + "]").PadRight(8, ' ') + $" [FlyleafHost #{Player.PlayerId}] ";

        // De-assign new Player's Handle/FlyleafHost
        Player.Host?.Player_Disposed();

        if (Player == null) // We might just de-assign our Player
            return;

        // Assign new Player's (Handle/FlyleafHost)
        Log.Debug($"Assign Player #{Player.PlayerId}");

        Player.Host = this;
        Player.Activity.Timeout = ActivityTimeout;
        if (Player.Renderer != null) // TBR: using as AudioOnly with a Control*
        {
            Player.Config.Video.CornerRadius = IsFullScreen ? CornerRadiusEmpty : CornerRadius;
            Player_RatioChanged(Player.Renderer.curRatio);
        }

        if (Surface != null)
            Player.Renderer.SwapChain.Setup(SurfaceHandle);
    }
    public virtual void SetSurface(bool fromSetOverlay = false)
    {
        if (Surface != null)
            return;

        // Required for some reason (WindowStyle.None will not be updated with our style)
        Surface = new()
        {
            Name            = $"Surface_{UniqueId}",
            Width           = 1,
            Height          = 1,
            WindowStyle     = WindowStyle.None,
            ResizeMode      = ResizeMode.NoResize,
            ShowInTaskbar   = false,
            Background      = VideoBackground
        };

        // NOTE: AllowsTransparency will cause performance issues (enable only if really required)
        if (Surface.Background is SolidColorBrush scb)
            Surface.AllowsTransparency = scb.Color.A < 255;
        else
            Surface.AllowsTransparency = true; // Non-solid consider true?
        
        // When using ItemsControl with ObservableCollection<Player> to fill DataTemplates with FlyleafHost EnsureHandle will call Host_loaded
        if (_IsAttached) Loaded -= Host_Loaded;
        SurfaceHandle = new WindowInteropHelper(Surface).EnsureHandle();
        if (_IsAttached) Loaded += Host_Loaded;

        if (_IsAttached)
        {
            SetWindowLong(SurfaceHandle, NONE_STYLE | WindowStyles.WS_CHILD);
            SetWindowLong(SurfaceHandle, WindowStylesEx.WS_EX_LAYERED);
        }
        else // Detached || StandAlone
        {
            SetWindowLong(SurfaceHandle, NONE_STYLE);
            if (DetachedShowInTaskbar || IsStandAlone)
                SetWindowLong(SurfaceHandle, WindowStylesEx.WS_EX_APPWINDOW | WindowStylesEx.WS_EX_LAYERED);
            else
                SetWindowLong(SurfaceHandle, WindowStylesEx.WS_EX_LAYERED);
        }

        // AllowsTransparency = true will ignore it (if we have it with other flags it might cause issues)
        SetWindowLong(SurfaceHandle, GetWindowLongEx(SurfaceHandle) | WindowStylesEx.WS_EX_NOREDIRECTIONBITMAP);

        Player?.Renderer.SwapChain.Setup(SurfaceHandle);

        Surface.IsVisibleChanged
                            += Surface_IsVisibleChanged;
        Surface.Closed      += Surface_Closed;
        Surface.Closing     += Surface_Closing;
        Surface.KeyDown     += Surface_KeyDown;
        Surface.KeyUp       += Surface_KeyUp;
        Surface.Drop        += Surface_Drop;
        Surface.DragEnter   += Surface_DragEnter;
        Surface.StateChanged+= Surface_StateChanged;
        Surface.SizeChanged += SetRectOverlay;
        Surface.DpiChanged  += Surface_DpiChanged;

        SetMouseSurface();

        Surface.AllowDrop =
            OpenOnDrop == AvailableWindows.Surface || OpenOnDrop == AvailableWindows.Both ||
            SwapOnDrop == AvailableWindows.Surface || SwapOnDrop == AvailableWindows.Both;

        if (_IsAttached && IsLoaded && Owner == null && !fromSetOverlay)
            Host_Loaded(null, null);

        SurfaceCreated?.Invoke(this, new());
    }

    public virtual void SetOverlay()
    {
        if (Overlay == null)
            return;

        SetSurface(true);

        // We can't set parent/child properly if it is minimized
        bool wasMinimized = false;
        var prevBounds = Overlay.RestoreBounds;
        if (IsStandAlone && Overlay.WindowState == WindowState.Minimized)
        {
            wasMinimized = true;
            Overlay.WindowState = WindowState.Normal;
            Overlay.Left        = Overlay.Top = -2000;
        }

        if (_IsAttached) Loaded -= Host_Loaded;
        OverlayHandle = new WindowInteropHelper(Overlay).EnsureHandle();
        if (_IsAttached) Loaded += Host_Loaded;

        if (IsStandAlone)
        {
            GetWindowRect(OverlayHandle, ref curRect);
            SetWindowPos(SurfaceHandle, IntPtr.Zero, curRect.Left, curRect.Top, curRect.Right - curRect.Left, curRect.Bottom - curRect.Top, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));

            Surface.Title       = Overlay.Title;
            Surface.Icon        = Overlay.Icon;
            Surface.MinHeight   = Overlay.MinHeight;
            Surface.MaxHeight   = Overlay.MaxHeight;
            Surface.MinWidth    = Overlay.MinWidth;
            Surface.MaxWidth    = Overlay.MaxWidth;
            Surface.Topmost     = DetachedTopMost;
        }
        else
        {
            Overlay.Resources   = Resources;
            Overlay.DataContext = this; // TBR: or this.DataContext?
        }

        GetWindowRect(SurfaceHandle, ref curRect);
        int cx = curRect.Right  - curRect.Left;
        int cy = curRect.Bottom - curRect.Top;
        SetWindowPos(OverlayHandle, IntPtr.Zero, 0, 0, cx, cy, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));

        if (cx > 8 && cy > 8)
            curResizeRatio = cx / (double)cy;

        Overlay.Name            = $"Overlay_{UniqueId}";
        Overlay.Background      = Brushes.Transparent;
        Overlay.ShowInTaskbar   = false;
        Overlay.Owner           = Surface;
        SetParent(OverlayHandle, SurfaceHandle);
        SetWindowLong(OverlayHandle, NONE_STYLE | WindowStyles.WS_CHILD | WindowStyles.WS_MAXIMIZE); // TBR: WS_MAXIMIZE required? (possible better for DWM on fullscreen?)

        Overlay.KeyUp       += Overlay_KeyUp;
        Overlay.KeyDown     += Overlay_KeyDown;
        Overlay.Closed      += Overlay_Closed;
        Overlay.Drop        += Overlay_Drop;
        Overlay.DragEnter   += Overlay_DragEnter;

        SetMouseOverlay();

        // Owner will close the overlay
        Overlay.KeyDown += (o, e) => { if (e.Key == Key.System && e.SystemKey == Key.F4) Surface?.Focus(); };

        Overlay.AllowDrop =
            OpenOnDrop == AvailableWindows.Overlay || OpenOnDrop == AvailableWindows.Both ||
            SwapOnDrop == AvailableWindows.Overlay || SwapOnDrop == AvailableWindows.Both;

        if (setTemplate)
            Overlay.Template = OverlayTemplate;

        if (Surface.IsVisible)
            Overlay.Show();
        else if (!IsStandAlone && !Overlay.IsVisible)
        {
            Overlay.Show();
            Overlay.Hide();
        }

        if (_IsAttached && IsLoaded && Owner == null)
            Host_Loaded(null, null);

        OverlayCreated?.Invoke(this, new());

        // Restore size and minimize
        if (wasMinimized)
        {
            Surface.Show();
            IsMinimized     = true;

            Surface.Width   = prevBounds.Width;
            Surface.Height  = prevBounds.Height;

            if (Overlay.WindowStartupLocation == WindowStartupLocation.CenterScreen)
            {
                var screen      = System.Windows.Forms.Screen.FromPoint(new(0, 0)).Bounds;
                Surface.Left    = screen.Left + (screen.Width  / 2) - (Surface.Width  / 2);
                Surface.Top     = screen.Top  + (screen.Height / 2) - (Surface.Height / 2);
            }
            else
            {
                Surface.Left    = prevBounds.Left;
                Surface.Top     = prevBounds.Top;
            }
        }
    }
    private void SetMouseSurface()
    {
        if (Surface == null)
            return;

        if ((MouseBindings == AvailableWindows.Surface || MouseBindings == AvailableWindows.Both) && !isMouseBindingsSubscribedSurface)
        {
            Surface.LostMouseCapture    += Surface_LostMouseCapture;
            Surface.MouseLeftButtonDown += Surface_MouseLeftButtonDown;
            Surface.MouseLeftButtonUp   += Surface_MouseLeftButtonUp;
            if (PanZoomOnCtrlWheel != AvailableWindows.None ||
                PanRotateOnShiftWheel != AvailableWindows.None)
            {
                Surface.MouseWheel += Surface_MouseWheel;
            }
            Surface.MouseMove           += Surface_MouseMove;
            Surface.MouseLeave          += Surface_MouseLeave;
            if (ToggleFullScreenOnDoubleClick != AvailableWindows.None)
            {
                Surface.MouseDoubleClick += Surface_MouseDoubleClick;
            }
            isMouseBindingsSubscribedSurface = true;
        }
        else if (isMouseBindingsSubscribedSurface)
        {
            Surface.LostMouseCapture    -= Surface_LostMouseCapture;
            Surface.MouseLeftButtonDown -= Surface_MouseLeftButtonDown;
            Surface.MouseLeftButtonUp   -= Surface_MouseLeftButtonUp;
            if (PanZoomOnCtrlWheel != AvailableWindows.None ||
                PanRotateOnShiftWheel != AvailableWindows.None)
            {
                Surface.MouseWheel -= Surface_MouseWheel;
            }
            Surface.MouseMove           -= Surface_MouseMove;
            Surface.MouseLeave          -= Surface_MouseLeave;
            if (ToggleFullScreenOnDoubleClick != AvailableWindows.None)
            {
                Surface.MouseDoubleClick -= Surface_MouseDoubleClick;
            }
            isMouseBindingsSubscribedSurface = false;
        }
    }
    private void SetMouseOverlay()
    {
        if (Overlay == null)
            return;

        if ((MouseBindings == AvailableWindows.Overlay || MouseBindings == AvailableWindows.Both) && !isMouseBindingsSubscribedOverlay)
        {
            Overlay.LostMouseCapture    += Overlay_LostMouseCapture;
            Overlay.MouseLeftButtonDown += Overlay_MouseLeftButtonDown;
            Overlay.MouseLeftButtonUp   += Overlay_MouseLeftButtonUp;
            if (PanZoomOnCtrlWheel != AvailableWindows.None ||
                PanRotateOnShiftWheel != AvailableWindows.None)
            {
                Overlay.MouseWheel += Overlay_MouseWheel;
            }
            Overlay.MouseMove           += Overlay_MouseMove;
            Overlay.MouseLeave          += Overlay_MouseLeave;
            if (ToggleFullScreenOnDoubleClick != AvailableWindows.None)
            {
                Overlay.MouseDoubleClick += Overlay_MouseDoubleClick;
            }
            isMouseBindingsSubscribedOverlay = true;
        }
        else if (isMouseBindingsSubscribedOverlay)
        {
            Overlay.LostMouseCapture    -= Overlay_LostMouseCapture;
            Overlay.MouseLeftButtonDown -= Overlay_MouseLeftButtonDown;
            Overlay.MouseLeftButtonUp   -= Overlay_MouseLeftButtonUp;
            if (PanZoomOnCtrlWheel != AvailableWindows.None ||
                PanRotateOnShiftWheel != AvailableWindows.None)
            {
                Overlay.MouseWheel -= Overlay_MouseWheel;
            }
            Overlay.MouseMove           -= Overlay_MouseMove;
            Overlay.MouseLeave          -= Overlay_MouseLeave;
            if (ToggleFullScreenOnDoubleClick != AvailableWindows.None)
            {
                Overlay.MouseDoubleClick -= Overlay_MouseDoubleClick;
            }

            isMouseBindingsSubscribedOverlay = false;
        }
    }

    public virtual void Attach(bool ignoreRestoreRect = false)
    {
        Window wasFocus = Overlay != null && Overlay.IsKeyboardFocusWithin ? Overlay : Surface;

        var source = PresentationSource.FromVisual(Owner);
        if (source != null)
        {
            DpiX = source.CompositionTarget.TransformToDevice.M11;
            DpiY = source.CompositionTarget.TransformToDevice.M22;
            DpiXSource = DpiX * 96;
            DpiYSource = DpiY * 96;
        }

        if (_IsFullScreen)
        {
            IsFullScreen = false;
            return;
        }

        if (!ignoreRestoreRect)
            rectDetachedDpi= new(Surface.Left, Surface.Top, Surface.Width, Surface.Height);

        Surface.Topmost     = false;
        Surface.MinWidth    = MinWidth;
        Surface.MinHeight   = MinHeight;
        Surface.MaxWidth    = MaxWidth;
        Surface.MaxHeight   = MaxHeight;

        SetWindowLong(SurfaceHandle, NONE_STYLE | WindowStyles.WS_CHILD);
        Surface.Owner = Owner;
        SetParent(SurfaceHandle, OwnerHandle);

        ResizeRatio();
        rectInitLast = rectIntersectLast = Rect.Empty;
        Host_LayoutUpdated(null, null);
        Owner.Activate();
        wasFocus.Focus();
    }
    public virtual void Detach()
    {
        // TBR: Issue with rectDetachedDpi should drop Dpi before storing (happens when switching DPI)

        if (_IsFullScreen)
            IsFullScreen = false;

        Surface.MinWidth    = DetachedMinWidth;
        Surface.MinHeight   = DetachedMinHeight;
        Surface.MaxWidth    = DetachedMaxWidth;
        Surface.MaxHeight   = DetachedMaxHeight;

        // Calculate Size
        var newSize = DetachedRememberSize && rectDetachedDpi != Rect.Empty
            ? new Size(rectDetachedDpi.Width, rectDetachedDpi.Height)
            : DetachedFixedSize;

        // Calculate Position
        Point newPos;
        if (DetachedRememberPosition && rectDetachedDpi != Rect.Empty)
        {
            newPos = new(rectDetachedDpi.X, rectDetachedDpi.Y);
            (DpiX, DpiY) = GetDpiAtPoint(new((int)rectDetachedDpi.X, (int)rectDetachedDpi.Y)); // How to ask for point's dpi if we don't know the point's dpi?
            DpiXSource = DpiX * 96;
            DpiYSource = DpiY * 96;
        }
        else
        {
            var screen = System.Windows.Forms.Screen.FromPoint(new((int)(Surface.Left * DpiX), (int)(Surface.Top * DpiY))).Bounds;
            (DpiX, DpiY) = GetDpiAtPoint(new((int)Surface.Top, (int)Surface.Left));
            DpiXSource = DpiX * 96;
            DpiYSource = DpiY * 96;

            // Drop Dpi to work with screen (no Dpi)
            newSize.Width   *= DpiX;
            newSize.Height  *= DpiY;

            newPos = DetachedPosition switch
            {
                DetachedPositionOptions.TopLeft     => new(screen.Left, screen.Top),
                DetachedPositionOptions.TopCenter   => new(screen.Left + (screen.Width / 2) - (newSize.Width / 2), screen.Top),
                DetachedPositionOptions.TopRight    => new(screen.Left + screen.Width - newSize.Width, screen.Top),
                DetachedPositionOptions.CenterLeft  => new(screen.Left, screen.Top + (screen.Height / 2) - (newSize.Height / 2)),
                DetachedPositionOptions.CenterCenter=> new(screen.Left + (screen.Width / 2) - (newSize.Width / 2), screen.Top + (screen.Height / 2) - (newSize.Height / 2)),
                DetachedPositionOptions.CenterRight => new(screen.Left + screen.Width - newSize.Width, screen.Top + (screen.Height / 2) - (newSize.Height / 2)),
                DetachedPositionOptions.BottomLeft  => new(screen.Left, screen.Top + screen.Height - newSize.Height),
                DetachedPositionOptions.BottomCenter=> new(screen.Left + (screen.Width / 2) - (newSize.Width / 2), screen.Top + screen.Height - newSize.Height),
                DetachedPositionOptions.BottomRight => new(screen.Left + screen.Width - newSize.Width, screen.Top + screen.Height - newSize.Height),
                DetachedPositionOptions.Custom      => DetachedFixedPosition,
                _ => new(),//satisfy the compiler
            };

            // SetRect will drop DPI so we add it
            newPos.X /= DpiX;
            newPos.Y /= DpiY;

            newPos.X += DetachedPositionMargin.Left - DetachedPositionMargin.Right;
            newPos.Y += DetachedPositionMargin.Top - DetachedPositionMargin.Bottom;

            // Restore DPI
            newSize.Width   /= DpiX;
            newSize.Height  /= DpiY;
        }

        Rect final = new(newPos.X, newPos.Y, newSize.Width, newSize.Height);

        // Detach (Parent=Null, Owner=Null ?, ShowInTaskBar?, TopMost?)
        SetParent(SurfaceHandle, IntPtr.Zero);
        SetWindowLong(SurfaceHandle, NONE_STYLE); // TBR (also in Attach/FullScren): Needs to be after SetParent. when detached and trying to close the owner will take two clicks (like mouse capture without release) //SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE, GetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE) & ~(nint)WindowStyles.WS_CHILD);
        Surface.Owner = DetachedNoOwner ? null : Owner;
        Surface.Topmost = DetachedTopMost;

        SetRect(final);
        ResetVisibleRect();
        ResizeRatio();

        if (Surface.IsVisible) // Initially detached will not be visible yet and activate not required (in case of multiple)
            Surface.Activate();
    }

    public void RefreshNormalFullScreen()
    {
        if (_IsFullScreen)
        {
            ratioBeforeFullScreen = curResizeRatio;

            if (_IsAttached)
            {
                // When we set the parent to null we don't really know in which left/top will be transfered and maximized into random screen
                GetWindowRect(SurfaceHandle, ref curRect);

                ResetVisibleRect();
                SetParent(SurfaceHandle, IntPtr.Zero);
                SetWindowLong(SurfaceHandle, NONE_STYLE); // TBR (also in Attach/FullScren): Needs to be after SetParent. when detached and trying to close the owner will take two clicks (like mouse capture without release) //SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE, GetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE) & ~(nint)WindowStyles.WS_CHILD);
                Surface.Owner   = DetachedNoOwner ? null : Owner;
                Surface.Topmost = DetachedTopMost;

                SetWindowPos(SurfaceHandle, IntPtr.Zero, curRect.Left, curRect.Top, 0, 0, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_NOSIZE));
            }

            if (Player != null)
                Player.Config.Video.CornerRadius = CornerRadiusEmpty;

            if (Overlay != null)
            {
                Overlay.Hide();
                Surface.WindowState = WindowState.Maximized;
                Overlay.Show();
            }
            else
                Surface.WindowState = WindowState.Maximized;

            Player?.Activity.RefreshFullActive();

            // If it was above the borders and double click (mouse didn't move to refresh)
            Surface.Cursor = Cursors.Arrow;
            if (Overlay != null)
                Overlay.Cursor = Cursors.Arrow;
        }
        else
        {
            if (IsStandAlone)
                Surface.WindowState = WindowState.Normal;

            if (_IsAttached)
            {
                Attach(true);
                InvalidateVisual(); // To force the FlyleafSharedOverlay (if any) redraw on-top
            }
            else if (Surface.Topmost || DetachedTopMost) // Bring to front (in Desktop, above windows bar)
            {
                Surface.Topmost = false;
                Surface.Topmost = true;
            }

            // TBR: CornerRadius background has issue it's like a mask color?
            if (Player != null)
                Player.Config.Video.CornerRadius = CornerRadius;

            if (!IsStandAlone) //when play with alpha video and not standalone, we need to set window state to normal last, otherwise it will be lost the background
                Surface.WindowState = WindowState.Normal;

            if (ratioBeforeFullScreen != curResizeRatio)
                ResizeRatio();
        }
    }
    public void SetRect(Rect rect)
        => SetWindowPos(SurfaceHandle, IntPtr.Zero, (int)Math.Round(rect.X * DpiX), (int)Math.Round(rect.Y * DpiY), (int)Math.Round(rect.Width * DpiX), (int)Math.Round(rect.Height * DpiY),
            (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));

    private void SetRectOverlay(object sender, SizeChangedEventArgs e)
    {
        if (OverlayHandle != 0)
            SetWindowPos(OverlayHandle, IntPtr.Zero, 0, 0, (int)Math.Round(Surface.ActualWidth * DpiX), (int)Math.Round(Surface.ActualHeight * DpiY),
                (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));
    }

    public void ResetVisibleRect()
    {
        _ = SetWindowRgn(SurfaceHandle, IntPtr.Zero, true);
        if (OverlayHandle != 0)
            _ = SetWindowRgn(OverlayHandle, IntPtr.Zero, true);
    }
    public void SetVisibleRect(Rect rect)
    {
        _ = SetWindowRgn(SurfaceHandle, CreateRectRgn((int)Math.Round(rect.X * DpiX), (int)Math.Round(rect.Y * DpiY), (int)Math.Round(rect.Right * DpiX), (int)Math.Round(rect.Bottom * DpiY)), true);
        if (OverlayHandle != 0)
            _ = SetWindowRgn(OverlayHandle, CreateRectRgn((int)Math.Round(rect.X * DpiX), (int)Math.Round(rect.Y * DpiY), (int)Math.Round(rect.Right * DpiX), (int)Math.Round(rect.Bottom * DpiY)), true);
    }

    /// <summary>
    /// Disposes the Surface and Overlay Windows and de-assigns the Player
    /// </summary>
    public void Dispose()
    {
        lock (this)
        {
            if (Disposed)
                return;

            // Disposes SwapChain Only
            Player          = null;
            Disposed        = true;

            DataContextChanged  -= Host_DataContextChanged;
            LayoutUpdated       -= Host_LayoutUpdated;
            IsVisibleChanged    -= Host_IsVisibleChanged;
            Loaded     			-= Host_Loaded;

            if (Overlay != null)
            {
                if (isMouseBindingsSubscribedOverlay)
                    SetMouseOverlay();

                Overlay.IsVisibleChanged-= OverlayStandAlone_IsVisibleChanged;
                Overlay.KeyUp           -= Overlay_KeyUp;
                Overlay.KeyDown         -= Overlay_KeyDown;
                Overlay.Closed          -= Overlay_Closed;
                Overlay.Drop            -= Overlay_Drop;
                Overlay.DragEnter       -= Overlay_DragEnter;
            }

            if (Surface != null)
            {
                if (isMouseBindingsSubscribedSurface)
                    SetMouseSurface();

                Surface.IsVisibleChanged
                                    -= Surface_IsVisibleChanged;
                Surface.Closed      -= Surface_Closed;
                Surface.Closing     -= Surface_Closing;
                Surface.KeyDown     -= Surface_KeyDown;
                Surface.KeyUp       -= Surface_KeyUp;
                Surface.Drop        -= Surface_Drop;
                Surface.DragEnter   -= Surface_DragEnter;
                Surface.StateChanged-= Surface_StateChanged;
                Surface.SizeChanged -= SetRectOverlay;
                Surface.DpiChanged  -= Surface_DpiChanged;

                // If not shown yet app will not close properly
                if (!surfaceClosed)
                {
                    Surface.Owner = null;
                    SetParent(SurfaceHandle, IntPtr.Zero);
                    Surface.Width = Surface.Height = 1;
                    Surface.Show();
                    if (!overlayClosed)
                        Overlay?.Show();
                    Surface.Close();
                }
            }

            if (Owner != null)
            {
                Owner.DpiChanged  -= Owner_DpiChanged;
                Owner.SizeChanged -= Owner_SizeChanged;
            }

            Surface = null;
            Overlay = null;
            Owner   = null;

            SurfaceHandle   = IntPtr.Zero;
            OverlayHandle   = IntPtr.Zero;
            OwnerHandle     = IntPtr.Zero;

            Log.Debug("Disposed");
        }
    }

    public bool Player_CanHideCursor() => (Surface != null && Surface.IsMouseOver) ||
                                          (Overlay != null && Overlay.IsActive);
    public bool Player_GetFullScreen() => _IsFullScreen;
    public void Player_SetFullScreen(bool value) => IsFullScreen = value;
    public void Player_Disposed() => UIInvokeIfRequired(() => Player = null);
    #endregion

    #region Resize
    public void Player_RatioChanged(double keepRatio)
    {
        if (keepRatio != curResizeRatio && keepRatio > 0)
        {
            curResizeRatio = keepRatio;
            UI(() => ResizeRatio()); // Requires UI (comes from renderer)
        }
    }
    public bool Player_HandlesRatioResize(int width, int height)
        => _KeepRatioOnResize && !_IsFullScreen && wantedWidth == width && wantedHeight == height;

    private void ResizeRatio()
    {   // NOTE: Here we work on DPIs -not physical- pixels | TODO: Fix prev -> new ratio change for wished size
        if (!_KeepRatioOnResize || _IsFullScreen)
            return;

        Rect    screen;
        double  WindowWidth;
        double  WindowHeight;

        if (_IsAttached)
        {
            if (curResizeRatio == 0)
            {
                if (ActualWidth < 10 || ActualHeight < 10)
                    return;

                curResizeRatio = ActualWidth / ActualHeight;
            }

            if (Owner == null)
            {
                Height = ActualWidth / curResizeRatio;
                return;
            }

            if (PreferredLandscapeWidthAttached == 0)
	            PreferredLandscapeWidthAttached = (int)ActualWidth;

            if (PreferredPortraitHeightAttached == 0)
	            PreferredPortraitHeightAttached = (int)ActualHeight;

            WindowWidth     = PreferredLandscapeWidthAttached;
            WindowHeight    = PreferredPortraitHeightAttached;
            screen          = new(PointEmpty, Owner.RenderSize);
            sizeBoundsMLD   = new((int)MinWidth, (int)Owner.ActualWidth, (int)MinHeight, (int)Owner.ActualHeight);
        }
        else
        {
            if (Surface == null)
                return;

            if (curResizeRatio == 0)
            {
                if (Surface == null || Surface.ActualWidth < 10 || Surface.ActualHeight < 10)
                    return;

                curResizeRatio = Surface.ActualWidth / Surface.ActualHeight;
            }

            if (PreferredLandscapeWidth == 0)
	            PreferredLandscapeWidth = (int)Surface.Width;

            if (PreferredPortraitHeight == 0)
	            PreferredPortraitHeight = (int)Surface.Height;

            WindowWidth     = PreferredLandscapeWidth;
            WindowHeight    = PreferredPortraitHeight;
            var bounds      = System.Windows.Forms.Screen.FromPoint(new((int)(Surface.Left * DpiX), (int)(Surface.Top * DpiY))).Bounds;
            screen          = new(bounds.Left / DpiX, bounds.Top / DpiY, bounds.Width / DpiX, bounds.Height / DpiY);
            sizeBoundsMLD   = new((int)Surface.MinWidth, (int)Math.Min(Surface.MaxWidth, bounds.Width), (int)Surface.MinHeight, (int)Math.Min(Surface.MaxHeight, bounds.Height));
        }

        if (curResizeRatio >= 1)
        {
            WindowHeight = WindowWidth / curResizeRatio;

            if (WindowHeight < sizeBoundsMLD.MinHeight)
            {
                WindowHeight    = sizeBoundsMLD.MinHeight;
                WindowWidth     = WindowHeight * curResizeRatio;
            }
            else if (WindowHeight > sizeBoundsMLD.MaxHeight)
            {
                WindowHeight    = sizeBoundsMLD.MaxHeight;
                WindowWidth     = WindowHeight * curResizeRatio;
            }
        }
        else
        {
            WindowWidth = WindowHeight * curResizeRatio;

            if (WindowWidth < sizeBoundsMLD.MinWidth)
            {
                WindowWidth     = sizeBoundsMLD.MinWidth;
                WindowHeight    = WindowWidth / curResizeRatio;
            }
            else if (WindowWidth > sizeBoundsMLD.MaxWidth)
            {
                WindowWidth     = sizeBoundsMLD.MaxWidth;
                WindowHeight    = WindowWidth / curResizeRatio;
            }
        }

        if (_IsAttached)
        {
            Width   = WindowWidth;
            Height  = WindowHeight;
        }

        else if (Surface != null)
        {
            double WindowLeft;
            double WindowTop;

            if (Surface.Left + Surface.Width / 2 > screen.Width / 2  && false)
                WindowLeft = Math.Min(Math.Max(Surface.Left + Surface.Width - WindowWidth, 0), screen.Width - WindowWidth);
            else
                WindowLeft = Surface.Left;

            if (Surface.Top + Surface.Height / 2 > screen.Height / 2 && false)
                WindowTop = Math.Min(Math.Max(Surface.Top + Surface.Height - WindowHeight, 0), screen.Height - WindowHeight);
            else
                WindowTop = Surface.Top;

            wantedWidth  = (int)(WindowWidth  * DpiX);
            wantedHeight = (int)(WindowHeight * DpiY);

            SetWindowPos(SurfaceHandle, IntPtr.Zero, (int)(WindowLeft * DpiX), (int)(WindowTop * DpiY), wantedWidth, wantedHeight, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));
        }
    }
    private void Resize(POINT p)
    {   // TODO: Corners using always width/dx *? | Consider min/max size to set on failure? | Consider even sizes?
        var cx  = rectSizeMLD.Right   - rectSizeMLD.Left;
        var cy  = rectSizeMLD.Bottom  - rectSizeMLD.Top;
        var dx  = p.X - pMLD.X;
        var dy  = p.Y - pMLD.Y;

        int left, top, width, height;

        switch (resizingSide)
        {
            case ResizeSide.Right:
                width = cx + dx;
                if (width < sizeBoundsMLD.MinWidth || width > sizeBoundsMLD.MaxWidth) return;

                if (_KeepRatioOnResize)
                {
                    height = (int)(width / curResizeRatio);
                    if (height < sizeBoundsMLD.MinHeight || height > sizeBoundsMLD.MaxHeight) return;
                }
                else
                    height = cy;

                wantedWidth     = width;
                wantedHeight    = height;

                if (_IsAttached)
                {
                    Width   = (int)(width  / DpiX);
                    Height  = (int)(height / DpiY);
                }

                SetWindowPos(SurfaceHandle, IntPtr.Zero, 0, 0, wantedWidth, wantedHeight, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_NOMOVE));
                break;

            case ResizeSide.Left:
                width = cx - dx;
                if (width < sizeBoundsMLD.MinWidth || width > sizeBoundsMLD.MaxWidth) return;

                if (_KeepRatioOnResize)
                {
                    height = (int)(width / curResizeRatio);
                    if (height < sizeBoundsMLD.MinHeight || height > sizeBoundsMLD.MaxHeight) return;
                }
                else
                    height = cy;

                left = rectSizeMLD.Left + dx;

                wantedWidth     = width;
                wantedHeight    = height;

                if (_IsAttached)
                {
                    Width   = (int)(width  / DpiX);
                    Height  = (int)(height / DpiY);
                    MarginTarget.Margin = new(rectMarginDpiMLD.Left + (dx / DpiX), rectMarginDpiMLD.Top, rectMarginDpiMLD.Right, rectMarginDpiMLD.Bottom);
                }

                SetWindowPos(SurfaceHandle, IntPtr.Zero, left, rectSizeMLD.Top, wantedWidth, wantedHeight, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));
                break;

            case ResizeSide.Top:
                height = cy - dy;
                if (height < sizeBoundsMLD.MinHeight || height > sizeBoundsMLD.MaxHeight) return;

                if (_KeepRatioOnResize)
                {
                    width = (int)(height * curResizeRatio);
                    if (width < sizeBoundsMLD.MinWidth || width > sizeBoundsMLD.MaxWidth) return;
                }
                else
                    width = cx;

                top = rectSizeMLD.Top + dy;

                wantedWidth     = width;
                wantedHeight    = height;

                if (_IsAttached)
                {
                    Width   = (int)(width  / DpiX);
                    Height  = (int)(height / DpiY);
                    MarginTarget.Margin = new(rectMarginDpiMLD.Left, rectMarginDpiMLD.Top + (dy / DpiY), rectMarginDpiMLD.Right, rectMarginDpiMLD.Bottom);
                }

                SetWindowPos(SurfaceHandle, IntPtr.Zero, rectSizeMLD.Left, top, wantedWidth, wantedHeight, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));
                break;

            case ResizeSide.Bottom:
                height = cy + dy;
                if (height < sizeBoundsMLD.MinHeight || height > sizeBoundsMLD.MaxHeight) return;

                if (_KeepRatioOnResize)
                {
                    width = (int)(height * curResizeRatio);
                    if (width < sizeBoundsMLD.MinWidth || width > sizeBoundsMLD.MaxWidth) return;
                }
                else
                    width = cx;

                wantedWidth     = width;
                wantedHeight    = height;

                if (_IsAttached)
                {
                    Width   = (int)(width  / DpiX);
                    Height  = (int)(height / DpiY);
                }

                SetWindowPos(SurfaceHandle, IntPtr.Zero, 0, 0, wantedWidth, wantedHeight, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_NOMOVE));
                break;
            case ResizeSide.TopRight:
                width = cx + dx;
                if (width < sizeBoundsMLD.MinWidth || width > sizeBoundsMLD.MaxWidth) return;

                if (_KeepRatioOnResize)
                {
                    height = (int)(width / curResizeRatio);
                    top = rectSizeMLD.Top - (height - cy);
                }
                else
                {
                    height = cy - dy;
                    top = rectSizeMLD.Top + dy;
                }

                if (height < sizeBoundsMLD.MinHeight || height > sizeBoundsMLD.MaxHeight) return;

                wantedWidth     = width;
                wantedHeight    = height;

                if (_IsAttached)
                {
                    Width   = (int)(width  / DpiX);
                    Height  = (int)(height / DpiY);
                    MarginTarget.Margin = new(rectMarginDpiMLD.Left, rectMarginDpiMLD.Top + ((_KeepRatioOnResize ? cy - height : dy) / DpiY), rectMarginDpiMLD.Right, rectMarginDpiMLD.Bottom);
                }

                SetWindowPos(SurfaceHandle, IntPtr.Zero, rectSizeMLD.Left, top, wantedWidth, wantedHeight, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));
                break;

            case ResizeSide.BottomRight:
                width = cx + dx;
                if (width < sizeBoundsMLD.MinWidth || width > sizeBoundsMLD.MaxWidth) return;

                if (_KeepRatioOnResize)
                    height = (int)(width / curResizeRatio);
                else
                    height = cy + dy;

                if (height < sizeBoundsMLD.MinHeight || height > sizeBoundsMLD.MaxHeight) return;

                wantedWidth     = width;
                wantedHeight    = height;

                if (_IsAttached)
                {
                    Width   = (int)(width  / DpiX);
                    Height  = (int)(height / DpiY);
                }

                SetWindowPos(SurfaceHandle, IntPtr.Zero, 0, 0, wantedWidth, wantedHeight, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_NOMOVE));
                break;

            case ResizeSide.TopLeft:
                width = cx - dx;
                if (width < sizeBoundsMLD.MinWidth || width > sizeBoundsMLD.MaxWidth) return;

                if (_KeepRatioOnResize)
                {
                    height = (int)(width / curResizeRatio);
                    top = rectSizeMLD.Top - (height - cy);
                }
                else
                {
                    height = cy - dy;
                    top = rectSizeMLD.Top + dy;
                }

                if (height < sizeBoundsMLD.MinHeight || height > sizeBoundsMLD.MaxHeight) return;

                left = rectSizeMLD.Left + dx;

                wantedWidth     = width;
                wantedHeight    = height;

                if (_IsAttached)
                {
                    Width   = (int)(width  / DpiX);
                    Height  = (int)(height / DpiY);
                    MarginTarget.Margin = new(rectMarginDpiMLD.Left + (dx / DpiX), rectMarginDpiMLD.Top + ((_KeepRatioOnResize ? cy - height : dy) / DpiY), rectMarginDpiMLD.Right, rectMarginDpiMLD.Bottom);
                }

                SetWindowPos(SurfaceHandle, IntPtr.Zero, left, top, wantedWidth, wantedHeight, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));
                break;

            case ResizeSide.BottomLeft:
                width = cx - dx;
                if (width < sizeBoundsMLD.MinWidth || width > sizeBoundsMLD.MaxWidth) return;

                if (_KeepRatioOnResize)
                    height = (int)(width / curResizeRatio);
                else
                    height = cy + dy;

                if (height < sizeBoundsMLD.MinHeight || height > sizeBoundsMLD.MaxHeight) return;

                left = rectSizeMLD.Left + dx;

                wantedWidth     = width;
                wantedHeight    = height;

                if (_IsAttached)
                {
                    Width   = (int)(width  / DpiX);
                    Height  = (int)(height / DpiY);
                    MarginTarget.Margin = new(rectMarginDpiMLD.Left + (dx / DpiX), rectMarginDpiMLD.Top, rectMarginDpiMLD.Right, rectMarginDpiMLD.Bottom);
                }

                SetWindowPos(SurfaceHandle, IntPtr.Zero, left, rectSizeMLD.Top, wantedWidth, wantedHeight, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));
                break;
        }
    }
    Cursor ResizeSides(POINT p)
    {
        GetWindowRect(SurfaceHandle, ref curRect);

        var cx  = curRect.Right - curRect.Left;
        var cy  = curRect.Bottom - curRect.Top;
        var dx  = p.X - curRect.Left;
        var dy  = p.Y - curRect.Top;

        if (dx <= ResizeSensitivity + (_CornerRadius.TopLeft / 2) && dy <= ResizeSensitivity + (_CornerRadius.TopLeft / 2))
        {
            resizingSide = ResizeSide.TopLeft;
            return Cursors.SizeNWSE;
        }
        else if (dx + ResizeSensitivity + (_CornerRadius.BottomRight / 2) >= cx && dy + ResizeSensitivity + (_CornerRadius.BottomRight / 2) >= cy)
        {
            resizingSide = ResizeSide.BottomRight;
            return Cursors.SizeNWSE;
        }
        else if (dx + ResizeSensitivity + (_CornerRadius.TopRight / 2) >= cx && dy <= ResizeSensitivity + (_CornerRadius.TopRight / 2))
        {
            resizingSide = ResizeSide.TopRight;
            return Cursors.SizeNESW;
        }
        else if (dx <= ResizeSensitivity + (_CornerRadius.BottomLeft / 2) && dy + ResizeSensitivity + (_CornerRadius.BottomLeft / 2) >= cy)
        {
            resizingSide = ResizeSide.BottomLeft;
            return Cursors.SizeNESW;
        }
        else if (dx <= ResizeSensitivity)
        {
            resizingSide = ResizeSide.Left;
            return Cursors.SizeWE;
        }
        else if (dx + ResizeSensitivity >= cx)
        {
            resizingSide = ResizeSide.Right;
            return Cursors.SizeWE;
        }
        else if (dy <= ResizeSensitivity)
        {
            resizingSide = ResizeSide.Top;
            return Cursors.SizeNS;
        }
        else if (dy + ResizeSensitivity >= cy)
        {
            resizingSide = ResizeSide.Bottom;
            return Cursors.SizeNS;
        }
        else
        {
            resizingSide = ResizeSide.None;
            return Cursors.Arrow;
        }
    }
    #endregion
}

public enum AvailableWindows
{
    None, Surface, Overlay, Both
}

public enum AttachedDragMoveOptions
{
    None, Surface, Overlay, Both, SurfaceOwner, OverlayOwner, BothOwner
}

public enum DetachedPositionOptions
{
    Custom, TopLeft, TopCenter, TopRight, CenterLeft, CenterCenter, CenterRight, BottomLeft, BottomCenter, BottomRight
}

enum ResizeSide
{
    None,
    TopLeft,
    Top,
    TopRight,
    BottomLeft,
    Bottom,
    BottomRight,
    Left,
    Right,
}

struct SizeConstraints(int minWidth, int maxWidth, int minHeight, int maxHeight)
{
    public int MinWidth     = minWidth;
    public int MaxWidth     = maxWidth;
    public int MinHeight    = minHeight;
    public int MaxHeight    = maxHeight;
}

struct DragOwnerMLD
{
    public Window Window;
    public double Left;
    public double Top;
}

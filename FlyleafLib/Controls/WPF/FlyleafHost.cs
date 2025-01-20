using System;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;

using FlyleafLib.MediaPlayer;

using static FlyleafLib.Utils;
using static FlyleafLib.Utils.NativeMethods;

using Brushes = System.Windows.Media.Brushes;

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
        CurResizeRatio                  [0 if not Keep Ratio or Player's aspect ratio]
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
    public Window       Owner           { get; private set; }
    public Window       Surface         { get; private set; }
    public IntPtr       SurfaceHandle   { get; private set; }
    public IntPtr       OverlayHandle   { get; private set; }
    public IntPtr       OwnerHandle     { get; private set; }
    public int          ResizingSide    { get; private set; }

    public int          UniqueId        { get; private set; }
    public bool         Disposed        { get; private set; }


    public event EventHandler SurfaceCreated;
    public event EventHandler OverlayCreated;
    public event DragEventHandler OnSurfaceDrop;
    public event DragEventHandler OnOverlayDrop;

    static bool isDesginMode;
    static int  idGenerator = 1;
    static nint NONE_STYLE = (nint) (WindowStyles.WS_MINIMIZEBOX | WindowStyles.WS_CLIPSIBLINGS | WindowStyles.WS_CLIPCHILDREN | WindowStyles.WS_VISIBLE); // WS_MINIMIZEBOX required for swapchain
    static Rect rectRandom = new(1, 2, 3, 4);

    float curResizeRatio;
    float curResizeRatioIfEnabled;
    bool surfaceClosed, surfaceClosing, overlayClosed;
    int panPrevX, panPrevY;
    bool isMouseBindingsSubscribedSurface;
    bool isMouseBindingsSubscribedOverlay;
    Window standAloneOverlay;

    CornerRadius zeroCornerRadius = new(0);
    Point zeroPoint = new(0, 0);
    Point mouseLeftDownPoint = new(0, 0);
    Point mouseMoveLastPoint = new(0, 0);
    Point ownerZeroPointPos = new();

    Rect zeroRect = new(0, 0, 0, 0);
    Rect rectDetachedLast = Rect.Empty;
    Rect rectInit;
    Rect rectInitLast = rectRandom;
    Rect rectIntersect;
    Rect rectIntersectLast = rectRandom;
    RECT beforeResizeRect = new();
    RECT curRect = new();

    private class FlyleafHostDropWrap { public FlyleafHost FlyleafHost; } // To allow non FlyleafHosts to drag & drop
    protected readonly LogHandler Log;
    #endregion

    #region Dependency Properties
    public void BringToFront() => SetWindowPos(SurfaceHandle, IntPtr.Zero, 0, 0, 0, 0, (UInt32)(SetWindowPosFlags.SWP_SHOWWINDOW | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOMOVE));
    public bool BringToFrontOnClick
    {
        get { return (bool)GetValue(BringToFrontOnClickProperty); }
        set { SetValue(BringToFrontOnClickProperty, value); }
    }
    public static readonly DependencyProperty BringToFrontOnClickProperty =
    DependencyProperty.Register(nameof(BringToFrontOnClick), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(true));

    public AvailableWindows OpenOnDrop
    {
        get => (AvailableWindows)GetValue(OpenOnDropProperty);
        set => SetValue(OpenOnDropProperty, value);
    }
    public static readonly DependencyProperty OpenOnDropProperty =
        DependencyProperty.Register(nameof(OpenOnDrop), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface, new PropertyChangedCallback(DropChanged)));

    public AvailableWindows SwapOnDrop
    {
        get => (AvailableWindows)GetValue(SwapOnDropProperty);
        set => SetValue(SwapOnDropProperty, value);
    }
    public static readonly DependencyProperty SwapOnDropProperty =
        DependencyProperty.Register(nameof(SwapOnDrop), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface, new PropertyChangedCallback(DropChanged)));

    public AvailableWindows SwapDragEnterOnShift
    {
        get => (AvailableWindows)GetValue(SwapDragEnterOnShiftProperty);
        set => SetValue(SwapDragEnterOnShiftProperty, value);
    }
    public static readonly DependencyProperty SwapDragEnterOnShiftProperty =
        DependencyProperty.Register(nameof(SwapDragEnterOnShift), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface));

    public AvailableWindows ToggleFullScreenOnDoubleClick
    {
        get => (AvailableWindows)GetValue(ToggleFullScreenOnDoubleClickProperty);
        set => SetValue(ToggleFullScreenOnDoubleClickProperty, value);
    }
    public static readonly DependencyProperty ToggleFullScreenOnDoubleClickProperty =
        DependencyProperty.Register(nameof(ToggleFullScreenOnDoubleClick), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface));

    public AvailableWindows PanMoveOnCtrl
    {
        get => (AvailableWindows)GetValue(PanMoveOnCtrlProperty);
        set => SetValue(PanMoveOnCtrlProperty, value);
    }
    public static readonly DependencyProperty PanMoveOnCtrlProperty =
        DependencyProperty.Register(nameof(PanMoveOnCtrl), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface));

    public AvailableWindows PanRotateOnShiftWheel
    {
        get => (AvailableWindows)GetValue(PanRotateOnShiftWheelProperty);
        set => SetValue(PanRotateOnShiftWheelProperty, value);
    }
    public static readonly DependencyProperty PanRotateOnShiftWheelProperty =
        DependencyProperty.Register(nameof(PanRotateOnShiftWheel), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface));

    public AvailableWindows PanZoomOnCtrlWheel
    {
        get => (AvailableWindows)GetValue(PanZoomOnCtrlWheelProperty);
        set => SetValue(PanZoomOnCtrlWheelProperty, value);
    }
    public static readonly DependencyProperty PanZoomOnCtrlWheelProperty =
        DependencyProperty.Register(nameof(PanZoomOnCtrlWheel), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface));

    public AttachedDragMoveOptions AttachedDragMove
    {
        get => (AttachedDragMoveOptions)GetValue(AttachedDragMoveProperty);
        set => SetValue(AttachedDragMoveProperty, value);
    }
    public static readonly DependencyProperty AttachedDragMoveProperty =
        DependencyProperty.Register(nameof(AttachedDragMove), typeof(AttachedDragMoveOptions), typeof(FlyleafHost), new PropertyMetadata(AttachedDragMoveOptions.Surface));

    public AvailableWindows DetachedDragMove
    {
        get => (AvailableWindows)GetValue(DetachedDragMoveProperty);
        set => SetValue(DetachedDragMoveProperty, value);
    }
    public static readonly DependencyProperty DetachedDragMoveProperty =
        DependencyProperty.Register(nameof(DetachedDragMove), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface));

    public AvailableWindows AttachedResize
    {
        get => (AvailableWindows)GetValue(AttachedResizeProperty);
        set => SetValue(AttachedResizeProperty, value);
    }
    public static readonly DependencyProperty AttachedResizeProperty =
        DependencyProperty.Register(nameof(AttachedResize), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface));

    public AvailableWindows DetachedResize
    {
        get => (AvailableWindows)GetValue(DetachedResizeProperty);
        set => SetValue(DetachedResizeProperty, value);
    }
    public static readonly DependencyProperty DetachedResizeProperty =
        DependencyProperty.Register(nameof(DetachedResize), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface));

    public bool KeepRatioOnResize
    {
        get => (bool)GetValue(KeepRatioOnResizeProperty);
        set => SetValue(KeepRatioOnResizeProperty, value);
    }
    public static readonly DependencyProperty KeepRatioOnResizeProperty =
        DependencyProperty.Register(nameof(KeepRatioOnResize), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false, new PropertyChangedCallback(OnKeepRatioOnResizeChanged)));

    public int PreferredLandscapeWidth
    {
        get { return (int)GetValue(PreferredLandscapeWidthProperty); }
        set { SetValue(PreferredLandscapeWidthProperty, value); }
    }
    public static readonly DependencyProperty PreferredLandscapeWidthProperty =
    DependencyProperty.Register(nameof(PreferredLandscapeWidth), typeof(int), typeof(FlyleafHost), new PropertyMetadata(0));

    public int PreferredPortraitHeight
    {
        get { return (int)GetValue(PreferredPortraitHeightProperty); }
        set { SetValue(PreferredPortraitHeightProperty, value); }
    }
    public static readonly DependencyProperty PreferredPortraitHeightProperty =
    DependencyProperty.Register(nameof(PreferredPortraitHeight), typeof(int), typeof(FlyleafHost), new PropertyMetadata(0));

    public int ResizeSensitivity
    {
        get => (int)GetValue(ResizeSensitivityProperty);
        set => SetValue(ResizeSensitivityProperty, value);
    }
    public static readonly DependencyProperty ResizeSensitivityProperty =
        DependencyProperty.Register(nameof(ResizeSensitivity), typeof(int), typeof(FlyleafHost), new PropertyMetadata(6));

    public DetachedPositionOptions DetachedPosition
    {
        get => (DetachedPositionOptions)GetValue(DetachedPositionProperty);
        set => SetValue(DetachedPositionProperty, value);
    }
    public static readonly DependencyProperty DetachedPositionProperty =
        DependencyProperty.Register(nameof(DetachedPosition), typeof(DetachedPositionOptions), typeof(FlyleafHost), new PropertyMetadata(DetachedPositionOptions.CenterCenter));

    public Thickness DetachedPositionMargin
    {
        get => (Thickness)GetValue(DetachedPositionMarginProperty);
        set => SetValue(DetachedPositionMarginProperty, value);
    }
    public static readonly DependencyProperty DetachedPositionMarginProperty =
        DependencyProperty.Register(nameof(DetachedPositionMargin), typeof(Thickness), typeof(FlyleafHost), new PropertyMetadata(new Thickness(0, 0, 0, 0)));

    public Point DetachedFixedPosition
    {
        get => (Point)GetValue(DetachedFixedPositionProperty);
        set => SetValue(DetachedFixedPositionProperty, value);
    }
    public static readonly DependencyProperty DetachedFixedPositionProperty =
        DependencyProperty.Register(nameof(DetachedFixedPosition), typeof(Point), typeof(FlyleafHost), new PropertyMetadata(new Point()));

    public Size DetachedFixedSize
    {
        get => (Size)GetValue(DetachedFixedSizeProperty);
        set => SetValue(DetachedFixedSizeProperty, value);
    }
    public static readonly DependencyProperty DetachedFixedSizeProperty =
        DependencyProperty.Register(nameof(DetachedFixedSize), typeof(Size), typeof(FlyleafHost), new PropertyMetadata(new Size(300, 200)));

    public bool DetachedRememberPosition
    {
        get => (bool)GetValue(DetachedRememberPositionProperty);
        set => SetValue(DetachedRememberPositionProperty, value);
    }
    public static readonly DependencyProperty DetachedRememberPositionProperty =
        DependencyProperty.Register(nameof(DetachedRememberPosition), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(true));

    public bool DetachedRememberSize
    {
        get => (bool)GetValue(DetachedRememberSizeProperty);
        set => SetValue(DetachedRememberSizeProperty, value);
    }
    public static readonly DependencyProperty DetachedRememberSizeProperty =
        DependencyProperty.Register(nameof(DetachedRememberSize), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(true));

    public bool DetachedTopMost
    {
        get => (bool)GetValue(DetachedTopMostProperty);
        set => SetValue(DetachedTopMostProperty, value);
    }
    public static readonly DependencyProperty DetachedTopMostProperty =
        DependencyProperty.Register(nameof(DetachedTopMost), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false, new PropertyChangedCallback(OnDetachedTopMostChanged)));

    public bool DetachedShowInTaskbar
    {
        get { return (bool)GetValue(DetachedShowInTaskbarProperty); }
        set { SetValue(DetachedShowInTaskbarProperty, value); }
    }
    public static readonly DependencyProperty DetachedShowInTaskbarProperty =
        DependencyProperty.Register(nameof(DetachedShowInTaskbar), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false, new PropertyChangedCallback(OnShowInTaskBarChanged)));

    public bool DetachedNoOwner
    {
        get { return (bool)GetValue(DetachedNoOwnerProperty); }
        set { SetValue(DetachedNoOwnerProperty, value); }
    }
    public static readonly DependencyProperty DetachedNoOwnerProperty =
        DependencyProperty.Register(nameof(DetachedNoOwner), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false, new PropertyChangedCallback(OnNoOwnerChanged)));

    public int DetachedMinHeight
    {
        get { return (int)GetValue(DetachedMinHeightProperty); }
        set { SetValue(DetachedMinHeightProperty, value); }
    }
    public static readonly DependencyProperty DetachedMinHeightProperty =
        DependencyProperty.Register(nameof(DetachedMinHeight), typeof(int), typeof(FlyleafHost), new PropertyMetadata(0));

    public int DetachedMinWidth
    {
        get { return (int)GetValue(DetachedMinWidthProperty); }
        set { SetValue(DetachedMinWidthProperty, value); }
    }
    public static readonly DependencyProperty DetachedMinWidthProperty =
        DependencyProperty.Register(nameof(DetachedMinWidth), typeof(int), typeof(FlyleafHost), new PropertyMetadata(0));

    public double DetachedMaxHeight
    {
        get { return (double)GetValue(DetachedMaxHeightProperty); }
        set { SetValue(DetachedMaxHeightProperty, value); }
    }
    public static readonly DependencyProperty DetachedMaxHeightProperty =
        DependencyProperty.Register(nameof(DetachedMaxHeight), typeof(double), typeof(FlyleafHost), new PropertyMetadata(double.PositiveInfinity));

    public double DetachedMaxWidth
    {
        get { return (double)GetValue(DetachedMaxWidthProperty); }
        set { SetValue(DetachedMaxWidthProperty, value); }
    }
    public static readonly DependencyProperty DetachedMaxWidthProperty =
        DependencyProperty.Register(nameof(DetachedMaxWidth), typeof(double), typeof(FlyleafHost), new PropertyMetadata(double.PositiveInfinity));

    public AvailableWindows KeyBindings
    {
        get => (AvailableWindows)GetValue(KeyBindingsProperty);
        set => SetValue(KeyBindingsProperty, value);
    }
    public static readonly DependencyProperty KeyBindingsProperty =
        DependencyProperty.Register(nameof(KeyBindings), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Surface));

    public AvailableWindows MouseBindings
    {
        get => (AvailableWindows)GetValue(MouseBindingsProperty);
        set => SetValue(MouseBindingsProperty, value);
    }
    public static readonly DependencyProperty MouseBindingsProperty =
        DependencyProperty.Register(nameof(MouseBindings), typeof(AvailableWindows), typeof(FlyleafHost), new PropertyMetadata(AvailableWindows.Both, new PropertyChangedCallback(OnMouseBindings)));

    public int ActivityTimeout
    {
        get => (int)GetValue(ActivityTimeoutProperty);
        set => SetValue(ActivityTimeoutProperty, value);
    }
    public static readonly DependencyProperty ActivityTimeoutProperty =
        DependencyProperty.Register(nameof(ActivityTimeout), typeof(int), typeof(FlyleafHost), new PropertyMetadata(0, new PropertyChangedCallback(OnActivityTimeoutChanged)));

    public bool IsAttached
    {
        get => (bool)GetValue(IsAttachedProperty);
        set => SetValue(IsAttachedProperty, value);
    }
    public static readonly DependencyProperty IsAttachedProperty =
        DependencyProperty.Register(nameof(IsAttached), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(true, new PropertyChangedCallback(OnIsAttachedChanged)));

    public bool IsMinimized
    {
        get => (bool)GetValue(IsMinimizedProperty);
        set => SetValue(IsMinimizedProperty, value);
    }
    public static readonly DependencyProperty IsMinimizedProperty =
        DependencyProperty.Register(nameof(IsMinimized), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false, new PropertyChangedCallback(OnIsMinimizedChanged)));

    public bool IsFullScreen
    {
        get => (bool)GetValue(IsFullScreenProperty);
        set => SetValue(IsFullScreenProperty, value);
    }
    public static readonly DependencyProperty IsFullScreenProperty =
        DependencyProperty.Register(nameof(IsFullScreen), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false, new PropertyChangedCallback(OnIsFullScreenChanged)));

    public bool IsResizing
    {
        get => (bool)GetValue(IsResizingProperty);
        private set => SetValue(IsResizingProperty, value);
    }
    public static readonly DependencyProperty IsResizingProperty =
        DependencyProperty.Register(nameof(IsResizing), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false));

    public bool IsStandAlone
    {
        get => (bool)GetValue(IsStandAloneProperty);
        private set => SetValue(IsStandAloneProperty, value);
    }
    public static readonly DependencyProperty IsStandAloneProperty =
        DependencyProperty.Register(nameof(IsStandAlone), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false));

    public bool IsSwappingStarted
    {
        get => (bool)GetValue(IsSwappingStartedProperty);
        private set => SetValue(IsSwappingStartedProperty, value);
    }
    public static readonly DependencyProperty IsSwappingStartedProperty =
        DependencyProperty.Register(nameof(IsSwappingStarted), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false));

    public bool IsPanMoving
    {
        get { return (bool)GetValue(IsPanMovingProperty); }
        private set { SetValue(IsPanMovingProperty, value); }
    }
    public static readonly DependencyProperty IsPanMovingProperty =
        DependencyProperty.Register(nameof(IsPanMoving), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false));

    public bool IsDragMoving
    {
        get { return (bool)GetValue(IsDragMovingProperty); }
        set { SetValue(IsDragMovingProperty, value); }
    }
    public static readonly DependencyProperty IsDragMovingProperty =
        DependencyProperty.Register(nameof(IsDragMoving), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false));

    public bool IsDragMovingOwner
    {
        get { return (bool)GetValue(IsDragMovingOwnerProperty); }
        set { SetValue(IsDragMovingOwnerProperty, value); }
    }
    public static readonly DependencyProperty IsDragMovingOwnerProperty =
        DependencyProperty.Register(nameof(IsDragMovingOwner), typeof(bool), typeof(FlyleafHost), new PropertyMetadata(false));

    public FrameworkElement MarginTarget
    {
        get => (FrameworkElement)GetValue(MarginTargetProperty);
        set => SetValue(MarginTargetProperty, value);
    }
    public static readonly DependencyProperty MarginTargetProperty =
        DependencyProperty.Register(nameof(MarginTarget), typeof(FrameworkElement), typeof(FlyleafHost), new PropertyMetadata(null));

    public object HostDataContext
    {
        get => GetValue(HostDataContextProperty);
        set => SetValue(HostDataContextProperty, value);
    }
    public static readonly DependencyProperty HostDataContextProperty =
        DependencyProperty.Register(nameof(HostDataContext), typeof(object), typeof(FlyleafHost), new PropertyMetadata(null));

    public object DetachedContent
    {
        get => GetValue(DetachedContentProperty);
        set => SetValue(DetachedContentProperty, value);
    }
    public static readonly DependencyProperty DetachedContentProperty =
        DependencyProperty.Register(nameof(DetachedContent), typeof(object), typeof(FlyleafHost), new PropertyMetadata(null));

    public Player Player
    {
        get => (Player)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }
    public static readonly DependencyProperty PlayerProperty =
        DependencyProperty.Register(nameof(Player), typeof(Player), typeof(FlyleafHost), new PropertyMetadata(null, OnPlayerChanged));

    public Player ReplicaPlayer
    {
        get => (Player)GetValue(ReplicaPlayerProperty);
        set => SetValue(ReplicaPlayerProperty, value);
    }
    public static readonly DependencyProperty ReplicaPlayerProperty =
        DependencyProperty.Register(nameof(ReplicaPlayer), typeof(Player), typeof(FlyleafHost), new PropertyMetadata(null, OnReplicaPlayerChanged));

    public ControlTemplate OverlayTemplate
    {
        get => (ControlTemplate)GetValue(OverlayTemplateProperty);
        set => SetValue(OverlayTemplateProperty, value);
    }
    public static readonly DependencyProperty OverlayTemplateProperty =
        DependencyProperty.Register(nameof(OverlayTemplate), typeof(ControlTemplate), typeof(FlyleafHost), new PropertyMetadata(null, new PropertyChangedCallback(OnOverlayTemplateChanged)));

    public Window Overlay
    {
        get => (Window)GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }
    public static readonly DependencyProperty OverlayProperty =
        DependencyProperty.Register(nameof(Overlay), typeof(Window), typeof(FlyleafHost), new PropertyMetadata(null, new PropertyChangedCallback(OnOverlayChanged)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(FlyleafHost), new PropertyMetadata(new CornerRadius(0), new PropertyChangedCallback(OnCornerRadiusChanged)));
    #endregion

    #region Events
    private static void OnMouseBindings(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesginMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        host.SetMouseSurface();
        host.SetMouseOverlay();
    }
    private static void OnDetachedTopMostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesginMode)
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
    private void UpdateCurRatio()
    {
        if (!KeepRatioOnResize || IsFullScreen)
            return;

        if (Player != null && Player.Video.AspectRatio.Value > 0)
            curResizeRatio = Player.Video.AspectRatio.Value;
        else if (ReplicaPlayer != null && ReplicaPlayer.Video.AspectRatio.Value > 0)
            curResizeRatio = ReplicaPlayer.Video.AspectRatio.Value;
        else
            curResizeRatio = (float)(16.0/9.0);

        curResizeRatioIfEnabled = curResizeRatio;

        Rect screen;

        if (IsAttached)
        {
            if (Owner == null)
            {
                Height = ActualWidth / curResizeRatio;
                return;
            }

            screen = new(zeroPoint, Owner.RenderSize);
        }
        else
        {
            if (Surface == null)
                return;

            var bounds = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)Surface.Top, (int)Surface.Left)).Bounds;
            screen = new(bounds.Left / DpiX, bounds.Top / DpiY, bounds.Width / DpiX, bounds.Height / DpiY);
        }

        double WindowWidth;
        double WindowHeight;

        if (curResizeRatio >= 1)
        {
            WindowHeight = PreferredLandscapeWidth / curResizeRatio;

            if (WindowHeight < Surface.MinHeight)
            {
                WindowHeight    = Surface.MinHeight;
                WindowWidth     = WindowHeight * curResizeRatio;
            }
            else if (WindowHeight > Surface.MaxHeight)
            {
                WindowHeight    = Surface.MaxHeight;
                WindowWidth     = Surface.Height * curResizeRatio;
            }
            else if (WindowHeight > screen.Height)
            {
                WindowHeight    = screen.Height;
                WindowWidth     = WindowHeight * curResizeRatio;
            }
            else
                WindowWidth = PreferredLandscapeWidth;
        }
        else
        {
            WindowWidth = PreferredPortraitHeight * curResizeRatio;

            if (WindowWidth < Surface.MinWidth)
            {
                WindowWidth     = Surface.MinWidth;
                WindowHeight    = WindowWidth / curResizeRatio;
            }
            else if (WindowWidth > Surface.MaxWidth)
            {
                WindowWidth     = Surface.MaxWidth;
                WindowHeight    = WindowWidth / curResizeRatio;
            }
            else if (WindowWidth > screen.Width)
            {
                WindowWidth     = screen.Width;
                WindowHeight    = WindowWidth / curResizeRatio;
            }
            else
                WindowHeight    = PreferredPortraitHeight;
        }

        if (IsAttached)
        {

            Height  = WindowHeight;
            Width   = WindowWidth;
        }

        else if (Surface != null)
        {
            double WindowLeft;
            double WindowTop;

            if (Surface.Left + Surface.Width / 2 > screen.Width / 2)
                WindowLeft = Math.Min(Math.Max(Surface.Left + Surface.Width - WindowWidth, 0), screen.Width - WindowWidth);
            else
                WindowLeft = Surface.Left;

            if (Surface.Top + Surface.Height / 2 > screen.Height / 2)
                WindowTop = Math.Min(Math.Max(Surface.Top + Surface.Height - WindowHeight, 0), screen.Height - WindowHeight);
            else
                WindowTop = Surface.Top;

            WindowLeft  *= DpiX;
            WindowTop   *= DpiY;
            WindowWidth *= DpiX;
            WindowHeight*= DpiY;

            SetWindowPos(SurfaceHandle, IntPtr.Zero,
                (int)WindowLeft,
                (int)WindowTop,
                (int)Math.Ceiling(WindowWidth),
                (int)Math.Ceiling(WindowHeight),
                (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));
        }
    }
    private static void OnShowInTaskBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.Surface == null)
            return;

        if (host.DetachedShowInTaskbar)
            SetWindowLong(host.SurfaceHandle, (int)WindowLongFlags.GWL_EXSTYLE, GetWindowLong(host.SurfaceHandle, (int)WindowLongFlags.GWL_EXSTYLE) | (nint)WindowStylesEx.WS_EX_APPWINDOW);
        else
            SetWindowLong(host.SurfaceHandle, (int)WindowLongFlags.GWL_EXSTYLE, GetWindowLong(host.SurfaceHandle, (int)WindowLongFlags.GWL_EXSTYLE) & ~(nint)WindowStylesEx.WS_EX_APPWINDOW);
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
        if (isDesginMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (!host.KeepRatioOnResize)
            host.curResizeRatioIfEnabled = 0;
        else
            host.UpdateCurRatio();
    }
    private static void OnPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesginMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        host.SetPlayer((Player)e.OldValue);
    }
    private static void OnReplicaPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesginMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        host.SetReplicaPlayer((Player)e.OldValue);
    }
    private static void OnIsFullScreenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesginMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        host.RefreshNormalFullScreen();
    }
    private static void OnIsMinimizedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesginMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        host.Surface.WindowState = host.IsMinimized ? WindowState.Minimized : (host.IsFullScreen ? WindowState.Maximized : WindowState.Normal);
    }
    private static void OnIsAttachedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesginMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.IsStandAlone)
        {
            host.IsAttached = false;
            return;
        }

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
        if (isDesginMode)
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

        if (!isDesginMode)
            host.SetOverlay();
        else
        {
            // XSurface.Wpf.Window (can this work on designer?
        }
    }
    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (isDesginMode)
            return;

        FlyleafHost host = d as FlyleafHost;
        if (host.Disposed)
            return;

        if (host.Surface == null)
            return;

        if (host.CornerRadius == host.zeroCornerRadius)
            host.Surface.Background  = Brushes.Black;
        else
        {
            host.Surface.Background  = Brushes.Transparent;
            host.SetCornerRadiusBorder();
        }

        if (host?.Player == null)
            return;

        host.Player.renderer.CornerRadius = (CornerRadius)e.NewValue;

    }
    private void SetCornerRadiusBorder()
    {
        // Required to handle mouse events as the window's background will be transparent
        // This does not set the background color we do that with the renderer (which causes some issues eg. when returning from fullscreen to normalscreen)
        Surface.Content = new Border()
        {
            Background          = Brushes.Black, // TBR: for alpha channel -> Background == Brushes.Transparent || Background ==null ? new SolidColorBrush(Color.FromArgb(1,0,0,0)) : Background
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            CornerRadius        = CornerRadius,
        };
    }
    private static object OnContentChanging(DependencyObject d, object baseValue)
    {
        if (isDesginMode)
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
            if (!IsAttached || OwnerHandle == ownerHandle)
                return; // Check OwnerHandle changed (NOTE: Owner can be the same class/window but the handle can be different)

            Owner.SizeChanged -= Owner_SizeChanged;

            Surface.Hide();
            Overlay?.Hide();
            Detach();

            Owner           = owner;
            OwnerHandle     = ownerHandle;
            Surface.Title   = Owner.Title;
            Surface.Icon    = Owner.Icon;

            Owner.SizeChanged += Owner_SizeChanged;
            Attach();
            rectDetachedLast = Rect.Empty; // Attach will set it wrong first time
            Host_IsVisibleChanged(null, new());

            return;
        }

        Owner           = owner;
        OwnerHandle     = ownerHandle;
        HostDataContext = DataContext;

        SetSurface();

        Surface.Title   = Owner.Title;
        Surface.Icon    = Owner.Icon;

        Owner.SizeChanged   += Owner_SizeChanged;
        DataContextChanged  += Host_DataContextChanged;
        LayoutUpdated       += Host_LayoutUpdated;
        IsVisibleChanged    += Host_IsVisibleChanged;

        // TBR: We need to ensure that Surface/Overlay will be initial Show once to work properly (issue #415)
        if (IsAttached)
        {
            Attach();
            rectDetachedLast = Rect.Empty; // Attach will set it wrong first time
            Surface.Show();
            Overlay?.Show();
            Host_IsVisibleChanged(null, new());
        }
        else
        {
            Detach();

            if (PreferredLandscapeWidth == 0)
                PreferredLandscapeWidth = (int)Surface.Width;

            if (PreferredPortraitHeight == 0)
                PreferredPortraitHeight = (int)Surface.Height;

            UpdateCurRatio();
            Surface.Show();
            Overlay?.Show();
        }
    }

    // WindowChrome Issue #410: It will not properly move child windows when resized from top or left
    private void Owner_SizeChanged(object sender, SizeChangedEventArgs e)
        => rectInitLast = Rect.Empty;

    private void Host_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        // TBR
        // 1. this.DataContext: FlyleafHost's DataContext will not be affected (Inheritance)
        // 2. Overlay.DataContext: Overlay's DataContext will be FlyleafHost itself
        // 3. Overlay.DataContext.HostDataContext: FlyleafHost's DataContext includes HostDataContext to access FlyleafHost's DataContext
        // 4. In case of Stand Alone will let the user to decide

        HostDataContext = DataContext;
    private void Host_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsAttached)
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
                    SetWindowLong(OverlayHandle, (int)WindowLongFlags.GWL_STYLE, NONE_STYLE);
                    Overlay.Owner = null;

                    SetWindowPos(OverlayHandle, IntPtr.Zero, 0, 0, (int)Surface.ActualWidth, (int)Surface.ActualHeight,
                        (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));

                    // Attache Overlay
                    SetWindowLong(OverlayHandle, (int)WindowLongFlags.GWL_STYLE, NONE_STYLE | (nint)(WindowStyles.WS_CHILD | WindowStyles.WS_MAXIMIZE));
                    Overlay.Owner = Surface;
                    SetParent(OverlayHandle, SurfaceHandle);

                    // Required to restore overlay
                    Rect tt1 = new(0, 0, 0, 0);
                    SetRect(ref tt1);
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

        if (!IsVisible || !IsAttached || IsFullScreen || IsResizing)
            return;

        try
        {
            rectInit = rectIntersect = new(TransformToAncestor(Owner).Transform(zeroPoint), RenderSize);

            FrameworkElement parent = this;
            while ((parent = VisualTreeHelper.GetParent(parent) as FrameworkElement) != null)
            {
                if (parent.FlowDirection == FlowDirection.RightToLeft)
                {
                    var location = parent.TransformToAncestor(Owner).Transform(zeroPoint);
                    location.X -= parent.RenderSize.Width;
                    rectIntersect.Intersect(new Rect(location, parent.RenderSize));
                }
                else
                    rectIntersect.Intersect(new Rect(parent.TransformToAncestor(Owner).Transform(zeroPoint), parent.RenderSize));
            }

            if (rectInit != rectInitLast)
            {
                SetRect(ref rectInit);
                rectInitLast = rectInit;
            }

            if (rectIntersect == Rect.Empty)
            {
                if (rectIntersect == rectIntersectLast)
                    return;

                rectIntersectLast = rectIntersect;
                SetVisibleRect(ref zeroRect);
            }
            else
            {
                rectIntersect.X -= rectInit.X;
                rectIntersect.Y -= rectInit.Y;

                if (rectIntersect == rectIntersectLast)
                    return;

                rectIntersectLast = rectIntersect;

                SetVisibleRect(ref rectIntersect);
            }
        }
        catch (Exception ex)
        {
            // It has been noticed with NavigationService (The visual tree changes, visual root IsVisible is false but FlyleafHost is still visible)
            if (Logger.CanDebug) Log.Debug($"Host_LayoutUpdated: {ex.Message}");

            // TBR: (Currently handle on each time Visible=true) It's possible that the owner/parent has been changed (for some reason Host_Loaded will not be called) *probably when the Owner stays the same but the actual Handle changes
            //if (ex.Message == "The specified Visual is not an ancestor of this Visual.")
                //Host_Loaded(null, null);
        }
    }
    private void Player_Video_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (KeepRatioOnResize && e.PropertyName == nameof(Player.Video.AspectRatio) && Player.Video.AspectRatio.Value > 0)
            UpdateCurRatio();
    }
    private void ReplicaPlayer_Video_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (KeepRatioOnResize && e.PropertyName == nameof(ReplicaPlayer.Video.AspectRatio) && ReplicaPlayer.Video.AspectRatio.Value > 0)
            UpdateCurRatio();
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
        FlyleafHostDropWrap hostWrap = (FlyleafHostDropWrap) e.Data.GetData(typeof(FlyleafHostDropWrap));

        // Swap FlyleafHosts
        if (hostWrap != null)
        {
            (hostWrap.FlyleafHost.Player, Player) = (Player, hostWrap.FlyleafHost.Player);
            Surface.Activate();
            return;
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
        FlyleafHostDropWrap hostWrap = (FlyleafHostDropWrap) e.Data.GetData(typeof(FlyleafHostDropWrap));

        // Swap FlyleafHosts
        if (hostWrap != null)
        {
            (hostWrap.FlyleafHost.Player, Player) = (Player, hostWrap.FlyleafHost.Player);
            Overlay.Activate();
            return;
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

        mouseLeftDownPoint = e.GetPosition(window);
        IsSwappingStarted = false; // currently we don't care if it was cancelled (it can be stay true if we miss the mouse up) - QueryContinueDrag

        // Resize
        if (ResizingSide != 0)
        {
            IsResizing = true;

            if (IsAttached)
            {
                ownerZeroPointPos = Owner.PointToScreen(zeroPoint);
                GetWindowRect(SurfaceHandle, ref beforeResizeRect);
                LayoutUpdated -= Host_LayoutUpdated;
                ResetVisibleRect();
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
            panPrevX    = Player.PanXOffset;
            panPrevY    = Player.PanYOffset;
            IsPanMoving = true;
        }

        // DragMoveOwner
        else if (IsAttached && Owner != null &&
            (AttachedDragMove == availDragMoveOwner || AttachedDragMove == AttachedDragMoveOptions.BothOwner))
            IsDragMovingOwner = true;


        // DragMove (Attach|Detach)
        else if ((IsAttached && (AttachedDragMove == availDragMove  || AttachedDragMove == AttachedDragMoveOptions.Both))
            ||  (!IsAttached && (DetachedDragMove == availWindow    || DetachedDragMove == AvailableWindows.Both)))
            IsDragMoving = true;

        else
            return; // No Capture

        window.CaptureMouse();
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Surface_ReleaseCapture();
    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Overlay_ReleaseCapture();
    private void Surface_LostMouseCapture(object sender, MouseEventArgs e) => Surface_ReleaseCapture();
    private void Overlay_LostMouseCapture(object sender, MouseEventArgs e) => Overlay_ReleaseCapture();
    private void Surface_ReleaseCapture()
    {
        if (!IsResizing && !IsPanMoving && !IsDragMoving && !IsDragMovingOwner)
            return;

        Surface.ReleaseMouseCapture();

        if (IsResizing)
        {
            ResizingSide    = 0;
            Surface.Cursor  = Cursors.Arrow;
            IsResizing      = false;

            if (IsAttached)
            {
                GetWindowRect(SurfaceHandle, ref curRect);
                MarginTarget.Margin = new(MarginTarget.Margin.Left + (curRect.Left - beforeResizeRect.Left) / DpiX, MarginTarget.Margin.Top + (curRect.Top - beforeResizeRect.Top) / DpiY, MarginTarget.Margin.Right, MarginTarget.Margin.Bottom);
                Width   = Surface.Width;
                Height  = Surface.Height;
                Host_LayoutUpdated(null, null); // When attached to restore the clipped rect
                LayoutUpdated += Host_LayoutUpdated;
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
    private void Overlay_ReleaseCapture()
    {
        if (!IsResizing && !IsPanMoving && !IsDragMoving && !IsDragMovingOwner)
            return;

        Overlay.ReleaseMouseCapture();

        if (IsResizing)
        {
            ResizingSide    = 0;
            Overlay.Cursor  = Cursors.Arrow;
            IsResizing      = false;

            if (IsAttached)
            {
                GetWindowRect(SurfaceHandle, ref curRect);
                MarginTarget.Margin = new(MarginTarget.Margin.Left + (curRect.Left - beforeResizeRect.Left) / DpiX, MarginTarget.Margin.Top + (curRect.Top - beforeResizeRect.Top) / DpiY, MarginTarget.Margin.Right, MarginTarget.Margin.Bottom);
                Width   = Surface.Width;
                Height  = Surface.Height;
                Host_LayoutUpdated(null, null); // When attached to restore the clipped rect
                LayoutUpdated += Host_LayoutUpdated;
            }
        }
        else if (IsPanMoving)
            IsPanMoving = false;
        else if (IsDragMoving)
            IsDragMoving = false;
        else if (IsDragMovingOwner)
            IsDragMovingOwner = false;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        var cur = e.GetPosition(Surface);

        if (Player != null && cur != mouseMoveLastPoint)
        {
            Player.Activity.RefreshFullActive();
            mouseMoveLastPoint = cur;
        }

        // Resize Sides (CanResize + !MouseDown + !FullScreen)
        if (e.MouseDevice.LeftButton != MouseButtonState.Pressed)
        {
            if ( !IsFullScreen &&
                ((IsAttached && (AttachedResize == AvailableWindows.Surface || AttachedResize == AvailableWindows.Both)) ||
                (!IsAttached && (DetachedResize == AvailableWindows.Surface || DetachedResize == AvailableWindows.Both))))
            {
                ResizingSide = ResizeSides(Surface, cur, ResizeSensitivity, CornerRadius);
            }

            return;
        }

        SO_MouseLeftDownAndMove(cur);
    }
    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var cur = e.GetPosition(Overlay);

        if (Player != null && cur != mouseMoveLastPoint)
        {
            Player.Activity.RefreshFullActive();
            mouseMoveLastPoint = cur;
        }

        // Resize Sides (CanResize + !MouseDown + !FullScreen)
        if (e.MouseDevice.LeftButton != MouseButtonState.Pressed)
        {
            if (!IsFullScreen && cur != zeroPoint &&
                ((IsAttached && (AttachedResize == AvailableWindows.Overlay || AttachedResize == AvailableWindows.Both)) ||
                (!IsAttached && (DetachedResize == AvailableWindows.Overlay || DetachedResize == AvailableWindows.Both))))
            {
                ResizingSide = ResizeSides(Overlay, cur, ResizeSensitivity, CornerRadius);
            }

            return;
        }

        SO_MouseLeftDownAndMove(cur);
    }
    private void SO_MouseLeftDownAndMove(Point cur)
    {
        if (IsSwappingStarted)
            return;

        // Player's Pan Move (Ctrl + Drag Move)
        if (IsPanMoving)
        {
            Player.PanXOffset = panPrevX + (int) (cur.X - mouseLeftDownPoint.X);
            Player.PanYOffset = panPrevY + (int) (cur.Y - mouseLeftDownPoint.Y);

            return;
        }

        if (IsFullScreen)
            return;

        // Resize (MouseDown + ResizeSide != 0)
        if (IsResizing)
            Resize(cur, ResizingSide, curResizeRatioIfEnabled);

        // Drag Move Self (Attached|Detached)
        else if (IsDragMoving)
        {
            if (IsAttached)
            {
                MarginTarget.Margin = new(
                    MarginTarget.Margin.Left + cur.X - mouseLeftDownPoint.X,
                    MarginTarget.Margin.Top  + cur.Y - mouseLeftDownPoint.Y,
                    MarginTarget.Margin.Right,
                    MarginTarget.Margin.Bottom);
            }
            else
            {
                Surface.Left  += cur.X - mouseLeftDownPoint.X;
                Surface.Top   += cur.Y - mouseLeftDownPoint.Y;
            }
        }

        // Drag Move Owner (Attached)
        else if (IsDragMovingOwner)
        {
            if (Owner.Owner != null)
            {
                Owner.Owner.Left  += cur.X - mouseLeftDownPoint.X;
                Owner.Owner.Top   += cur.Y - mouseLeftDownPoint.Y;
            }
            else
            {
                Owner.Left  += cur.X - mouseLeftDownPoint.X;
                Owner.Top   += cur.Y - mouseLeftDownPoint.Y;
            }
        }
    }

    private void Surface_MouseLeave(object sender, MouseEventArgs e) { ResizingSide = 0; Surface.Cursor = Cursors.Arrow; }
    private void Overlay_MouseLeave(object sender, MouseEventArgs e) { ResizingSide = 0; Overlay.Cursor = Cursors.Arrow; }

    private void Surface_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ToggleFullScreenOnDoubleClick == AvailableWindows.Surface || ToggleFullScreenOnDoubleClick == AvailableWindows.Both) { IsFullScreen = !IsFullScreen; e.Handled = true; } }
    private void Overlay_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ToggleFullScreenOnDoubleClick == AvailableWindows.Overlay || ToggleFullScreenOnDoubleClick == AvailableWindows.Both) { IsFullScreen = !IsFullScreen; e.Handled = true; } }

    private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Player == null || e.Delta == 0)
            return;

        if      ((Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) &&
            (PanZoomOnCtrlWheel == AvailableWindows.Surface || PanZoomOnCtrlWheel == AvailableWindows.Both))
        {
            var cur = e.GetPosition(Surface);
            Point curDpi = new(cur.X * DpiX, cur.Y * DpiY);
            if (e.Delta > 0)
                Player.ZoomIn(curDpi);
            else
                Player.ZoomOut(curDpi);
        }
        else if ((Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) &&
            (PanRotateOnShiftWheel == AvailableWindows.Surface || PanZoomOnCtrlWheel == AvailableWindows.Both))
        {
            if (e.Delta > 0)
                Player.RotateRight();
            else
                Player.RotateLeft();
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
                Player.ZoomIn(curDpi);
            else
                Player.ZoomOut(curDpi);
        }
        else if ((Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) &&
            (PanRotateOnShiftWheel == AvailableWindows.Overlay || PanZoomOnCtrlWheel == AvailableWindows.Both))
        {
            if (e.Delta > 0)
                Player.RotateRight();
            else
                Player.RotateLeft();
        }
    }

    private void Surface_Closed(object sender, EventArgs e)
    {
        surfaceClosed = true;
        Dispose();
    }
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

    public void Resize(Point p, int resizingSide, double ratio = 0.0)
    {
        double WindowWidth  = Surface.ActualWidth;
        double WindowHeight = Surface.ActualHeight;
        double WindowLeft;
        double WindowTop;

        if (IsAttached) // NOTE: Window.Left will not be updated when the Owner moves, don't use it when attached
        {
            GetWindowRect(SurfaceHandle, ref curRect);
            WindowLeft  = (curRect.Left - ownerZeroPointPos.X) / DpiX;
            WindowTop   = (curRect.Top - ownerZeroPointPos.Y) / DpiY;
        }
        else
        {
            WindowLeft  = Surface.Left;
            WindowTop   = Surface.Top;
        }

        if (resizingSide == 2 || resizingSide == 3 || resizingSide == 6)
        {
            p.X += 5;

            WindowWidth = p.X > Surface.MinWidth ?
                p.X < Surface.MaxWidth ? p.X : Surface.MaxWidth :
                Surface.MinWidth;
        }
        else if (resizingSide == 1 || resizingSide == 4 || resizingSide == 5)
        {
            p.X -= 5;
            double temp = Surface.ActualWidth - p.X;
            if (temp > Surface.MinWidth && temp < Surface.MaxWidth)
            {
                WindowWidth = temp;
                WindowLeft  = WindowLeft + p.X;
            }
        }

        if (resizingSide == 2 || resizingSide == 4 || resizingSide == 8)
        {
            p.Y += 5;

            if (p.Y > Surface.MinHeight)
            {
                WindowHeight = p.Y < Surface.MaxHeight ? p.Y : Surface.MaxHeight;
            }
            else
                return;
        }
        else if (resizingSide == 1 || resizingSide == 3 || resizingSide == 7)
        {
            if (ratio != 0 && resizingSide != 7)
            {
                double temp = WindowWidth / ratio;
                if (temp > Surface.MinHeight && temp < Surface.MaxHeight)
                    WindowTop += Surface.ActualHeight - temp;
                else
                    return;
            }
            else
            {
                p.Y -= 5;
                double temp = Surface.ActualHeight - p.Y;
                if (temp > Surface.MinHeight && temp < Surface.MaxHeight)
                {
                    WindowHeight= temp;
                    WindowTop += p.Y;
                }
                else
                    return;
            }
        }

        if (ratio != 0)
        {
            if (resizingSide == 7 || resizingSide == 8)
                WindowWidth = WindowHeight * ratio;
            else
                WindowHeight = WindowWidth / ratio;
        }

        if (WindowWidth >= WindowHeight)
            PreferredLandscapeWidth = (int)WindowWidth;
        else
            PreferredPortraitHeight = (int)WindowHeight;

        WindowLeft  *= DpiX;
        WindowTop   *= DpiY;
        WindowWidth *= DpiX;
        WindowHeight*= DpiY;

        SetWindowPos(SurfaceHandle, IntPtr.Zero,
            (int)WindowLeft,
            (int)WindowTop,
            (int)Math.Ceiling(WindowWidth),
            (int)Math.Ceiling(WindowHeight),
            (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));
    }
    public static int ResizeSides(Window Window, Point p, int ResizeSensitivity, CornerRadius cornerRadius)
    {
        if (p.X <= ResizeSensitivity + (cornerRadius.TopLeft / 2) && p.Y <= ResizeSensitivity + (cornerRadius.TopLeft / 2))
        {
            Window.Cursor = Cursors.SizeNWSE;
            return 1;
        }
        else if (p.X + ResizeSensitivity + (cornerRadius.BottomRight / 2) >= Window.ActualWidth && p.Y + ResizeSensitivity + (cornerRadius.BottomRight / 2) >= Window.ActualHeight)
        {
            Window.Cursor = Cursors.SizeNWSE;
            return 2;
        }
        else if (p.X + ResizeSensitivity + (cornerRadius.TopRight / 2) >= Window.ActualWidth && p.Y <= ResizeSensitivity + (cornerRadius.TopRight / 2))
        {
            Window.Cursor = Cursors.SizeNESW;
            return 3;
        }
        else if (p.X <= ResizeSensitivity + (cornerRadius.BottomLeft / 2) && p.Y + ResizeSensitivity + (cornerRadius.BottomLeft / 2)  >= Window.ActualHeight)
        {
            Window.Cursor = Cursors.SizeNESW;
            return 4;
        }
        else if (p.X <= ResizeSensitivity)
        {
            Window.Cursor = Cursors.SizeWE;
            return 5;
        }
        else if (p.X + ResizeSensitivity >= Window.ActualWidth)
        {
            Window.Cursor = Cursors.SizeWE;
            return 6;
        }
        else if (p.Y <= ResizeSensitivity)
        {
            Window.Cursor = Cursors.SizeNS;
            return 7;
        }
        else if (p.Y + ResizeSensitivity >= Window.ActualHeight)
        {
            Window.Cursor = Cursors.SizeNS;
            return 8;
        }
        else
        {
            Window.Cursor = Cursors.Arrow;
            return 0;
        }
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
        isDesginMode = DesignerProperties.GetIsInDesignMode(this);
        if (isDesginMode)
            return;

        MarginTarget= this;
        Log         = new LogHandler(("[#" + UniqueId + "]").PadRight(8, ' ') + $" [FlyleafHost NP] ");
        Loaded     += Host_Loaded;
    }
    public FlyleafHost(Window standAloneOverlay)
    {
        UniqueId = idGenerator++;
        Log = new LogHandler(("[#" + UniqueId + "]").PadRight(8, ' ') + $" [FlyleafHost NP] ");

        IsStandAlone = true;
        IsAttached = false;

        this.standAloneOverlay = standAloneOverlay;
        standAloneOverlay.Loaded += OverlayStandAlone_Loaded;
        if (standAloneOverlay.IsLoaded)
            OverlayStandAlone_Loaded(null, null);
    }
    #endregion

    #region Methods
    public virtual void SetReplicaPlayer(Player oldPlayer)
    {
        if (oldPlayer != null)
        {
            oldPlayer.renderer.SetChildHandle(IntPtr.Zero);
            oldPlayer.Video.PropertyChanged -= ReplicaPlayer_Video_PropertyChanged;
        }

        if (ReplicaPlayer == null)
            return;

        if (Surface != null)
            ReplicaPlayer.renderer.SetChildHandle(SurfaceHandle);

        ReplicaPlayer.Video.PropertyChanged += ReplicaPlayer_Video_PropertyChanged;
    }
    public virtual void SetPlayer(Player oldPlayer)
    {
        // De-assign old Player's Handle/FlyleafHost
        if (oldPlayer != null)
        {
            Log.Debug($"De-assign Player #{oldPlayer.PlayerId}");

            oldPlayer.Video.PropertyChanged -= Player_Video_PropertyChanged;
            oldPlayer.VideoDecoder.DestroySwapChain();
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
        if (Player.renderer != null) // TBR: using as AudioOnly with a Control*
            Player.renderer.CornerRadius = IsFullScreen ? zeroCornerRadius : CornerRadius;

        if (Surface != null)
        {
            if (CornerRadius == zeroCornerRadius)
                Surface.Background = new SolidColorBrush(Player.Config.Video.BackgroundColor);
            //else // TBR: this border probably not required? only when we don't have a renderer?
                //((Border)Surface.Content).Background = new SolidColorBrush(Player.Config.Video.BackgroundColor);

            Player.VideoDecoder.CreateSwapChain(SurfaceHandle);
        }

        Player.Video.PropertyChanged += Player_Video_PropertyChanged;
        UpdateCurRatio();
    }
    public virtual void SetSurface(bool fromSetOverlay = false)
    {
        if (Surface != null)
            return;

        // Required for some reason (WindowStyle.None will not be updated with our style)
        Surface             = new();
        Surface.Name        = $"Surface_{UniqueId}";
        Surface.Width       = Surface.Height = 1; // Will be set on loaded
        Surface.WindowStyle = WindowStyle.None;
        Surface.ResizeMode  = ResizeMode.NoResize;
        Surface.ShowInTaskbar = false;

        // CornerRadius must be set initially to AllowsTransparency!
        if (CornerRadius == zeroCornerRadius)
            Surface.Background = Player != null ? new SolidColorBrush(Player.Config.Video.BackgroundColor) : Brushes.Black;
        else
        {
            Surface.AllowsTransparency  = true;
            Surface.Background          = Brushes.Transparent;
            SetCornerRadiusBorder();
        }

        // When using ItemsControl with ObservableCollection<Player> to fill DataTemplates with FlyleafHost EnsureHandle will call Host_loaded
        if (IsAttached) Loaded -= Host_Loaded;
        SurfaceHandle = new WindowInteropHelper(Surface).EnsureHandle();
        if (IsAttached) Loaded += Host_Loaded;

        if (IsAttached)
        {
            SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE, NONE_STYLE | (nint)WindowStyles.WS_CHILD);
            SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_EXSTYLE, (nint)WindowStylesEx.WS_EX_LAYERED);
        }
        else // Detached || StandAlone
        {
            SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE, NONE_STYLE);
            if (DetachedShowInTaskbar || IsStandAlone)
                SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_EXSTYLE, (nint)(WindowStylesEx.WS_EX_APPWINDOW | WindowStylesEx.WS_EX_LAYERED));
            else
                SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_EXSTYLE, (nint)WindowStylesEx.WS_EX_LAYERED);
        }

        if (Player != null)
            Player.VideoDecoder.CreateSwapChain(SurfaceHandle);

        if (ReplicaPlayer != null)
            ReplicaPlayer.renderer.SetChildHandle(SurfaceHandle);

        Surface.Closed      += Surface_Closed;
        Surface.Closing     += Surface_Closing;
        Surface.KeyDown     += Surface_KeyDown;
        Surface.KeyUp       += Surface_KeyUp;
        Surface.Drop        += Surface_Drop;
        Surface.DragEnter   += Surface_DragEnter;
        Surface.StateChanged+= Surface_StateChanged;
        Surface.SizeChanged += SetRectOverlay;

        SetMouseSurface();

        Surface.AllowDrop =
            OpenOnDrop == AvailableWindows.Surface || OpenOnDrop == AvailableWindows.Both ||
            SwapOnDrop == AvailableWindows.Surface || SwapOnDrop == AvailableWindows.Both;

        if (IsAttached && IsLoaded && Owner == null && !fromSetOverlay)
            Host_Loaded(null, null);

        SurfaceCreated?.Invoke(this, new());
    }
    public virtual void SetOverlay()
    {
        if (Overlay == null)
            return;

        SetSurface(true);

        if (IsAttached) Loaded -= Host_Loaded;
        OverlayHandle = new WindowInteropHelper(Overlay).EnsureHandle();
        if (IsAttached) Loaded += Host_Loaded;

        if (IsStandAlone)
        {
            if (PreferredLandscapeWidth == 0)
                PreferredLandscapeWidth = (int)Overlay.Width;

            if (PreferredPortraitHeight == 0)
                PreferredPortraitHeight = (int)Overlay.Height;

            SetWindowPos(SurfaceHandle, IntPtr.Zero, (int)Math.Round(Overlay.Left * DpiX), (int)Math.Round(Overlay.Top * DpiY), (int)Math.Round(Overlay.ActualWidth * DpiX), (int)Math.Round(Overlay.ActualHeight * DpiY),
                (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));

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

        SetWindowPos(OverlayHandle, IntPtr.Zero, 0, 0, (int)Surface.ActualWidth, (int)Surface.ActualHeight,
                (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));

        Overlay.Name            = $"Overlay_{UniqueId}";
        Overlay.Background      = Brushes.Transparent;
        Overlay.ShowInTaskbar   = false;
        Overlay.Owner           = Surface;
        SetParent(OverlayHandle, SurfaceHandle);
        SetWindowLong(OverlayHandle, (int)WindowLongFlags.GWL_STYLE, NONE_STYLE | (nint)(WindowStyles.WS_CHILD | WindowStyles.WS_MAXIMIZE)); // TBR: WS_MAXIMIZE required? (possible better for DWM on fullscreen?)

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

        if (IsAttached && IsLoaded && Owner == null)
            Host_Loaded(null, null);

        OverlayCreated?.Invoke(this, new());
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
            Surface.MouseWheel          += Surface_MouseWheel;
            Surface.MouseMove           += Surface_MouseMove;
            Surface.MouseLeave          += Surface_MouseLeave;
            Surface.MouseDoubleClick    += Surface_MouseDoubleClick;
            isMouseBindingsSubscribedSurface = true;
        }
        else if (isMouseBindingsSubscribedSurface)
        {
            Surface.LostMouseCapture    -= Surface_LostMouseCapture;
            Surface.MouseLeftButtonDown -= Surface_MouseLeftButtonDown;
            Surface.MouseLeftButtonUp   -= Surface_MouseLeftButtonUp;
            Surface.MouseWheel          -= Surface_MouseWheel;
            Surface.MouseMove           -= Surface_MouseMove;
            Surface.MouseLeave          -= Surface_MouseLeave;
            Surface.MouseDoubleClick    -= Surface_MouseDoubleClick;
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
            Overlay.MouseWheel          += Overlay_MouseWheel;
            Overlay.MouseMove           += Overlay_MouseMove;
            Overlay.MouseLeave          += Overlay_MouseLeave;
            Overlay.MouseDoubleClick    += Overlay_MouseDoubleClick;
            isMouseBindingsSubscribedOverlay = true;
        }
        else if (isMouseBindingsSubscribedOverlay)
        {
            Overlay.LostMouseCapture    -= Overlay_LostMouseCapture;
            Overlay.MouseLeftButtonDown -= Overlay_MouseLeftButtonDown;
            Overlay.MouseLeftButtonUp   -= Overlay_MouseLeftButtonUp;
            Overlay.MouseWheel          -= Overlay_MouseWheel;
            Overlay.MouseMove           -= Overlay_MouseMove;
            Overlay.MouseLeave          -= Overlay_MouseLeave;
            Overlay.MouseDoubleClick    -= Overlay_MouseDoubleClick;
            isMouseBindingsSubscribedOverlay = false;
        }
    }

    public virtual void Attach(bool ignoreRestoreRect = false)
    {
        Window wasFocus = Overlay != null && Overlay.IsKeyboardFocusWithin ? Overlay : Surface;

        if (IsFullScreen)
        {
            IsFullScreen = false;
            return;
        }
        if (!ignoreRestoreRect)
            rectDetachedLast= new(Surface.Left, Surface.Top, Surface.Width, Surface.Height);

        Surface.Topmost     = false;
        Surface.MinWidth    = MinWidth;
        Surface.MinHeight   = MinHeight;
        Surface.MaxWidth    = MaxWidth;
        Surface.MaxHeight   = MaxHeight;

        SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE, NONE_STYLE | (nint)WindowStyles.WS_CHILD);
        Surface.Owner = Owner;
        SetParent(SurfaceHandle, OwnerHandle);

        rectInitLast = rectIntersectLast = rectRandom;
        Host_LayoutUpdated(null, null);
        Owner.Activate();
        wasFocus.Focus();
    }
    public virtual void Detach()
    {
        if (IsFullScreen)
            IsFullScreen = false;

        Surface.MinWidth    = DetachedMinWidth;
        Surface.MinHeight   = DetachedMinHeight;
        Surface.MaxWidth    = DetachedMaxWidth;
        Surface.MaxHeight   = DetachedMaxHeight;

        // Calculate Size
        var newSize = DetachedRememberSize && rectDetachedLast != Rect.Empty
            ? new Size(rectDetachedLast.Width, rectDetachedLast.Height)
            : DetachedFixedSize;

        // Calculate Position
        Point newPos;
        if (DetachedRememberPosition && rectDetachedLast != Rect.Empty)
            newPos = new Point(rectDetachedLast.X, rectDetachedLast.Y);
        else
        {
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)Surface.Top, (int)Surface.Left)).Bounds;

            // Drop Dpi to work with screen (no Dpi)
            newSize.Width   *= DpiX;
            newSize.Height  *= DpiY;

            switch (DetachedPosition)
            {
                case DetachedPositionOptions.TopLeft:
                    newPos = new Point(screen.Left, screen.Top);
                    break;
                case DetachedPositionOptions.TopCenter:
                    newPos = new Point(screen.Left + (screen.Width / 2) - (newSize.Width / 2), screen.Top);
                    break;

                case DetachedPositionOptions.TopRight:
                    newPos = new Point(screen.Left + screen.Width - newSize.Width, screen.Top);
                    break;

                case DetachedPositionOptions.CenterLeft:
                    newPos = new Point(screen.Left, screen.Top + (screen.Height / 2) - (newSize.Height / 2));
                    break;

                case DetachedPositionOptions.CenterCenter:
                    newPos = new Point(screen.Left + (screen.Width / 2) - (newSize.Width / 2), screen.Top + (screen.Height / 2) - (newSize.Height / 2));
                    break;

                case DetachedPositionOptions.CenterRight:
                    newPos = new Point(screen.Left + screen.Width - newSize.Width, screen.Top + (screen.Height / 2) - (newSize.Height / 2));
                    break;

                case DetachedPositionOptions.BottomLeft:
                    newPos = new Point(screen.Left, screen.Top + screen.Height - newSize.Height);
                    break;

                case DetachedPositionOptions.BottomCenter:
                    newPos = new Point(screen.Left + (screen.Width / 2) - (newSize.Width / 2), screen.Top + screen.Height - newSize.Height);
                    break;

                case DetachedPositionOptions.BottomRight:
                    newPos = new Point(screen.Left + screen.Width - newSize.Width, screen.Top + screen.Height - newSize.Height);
                    break;

                case DetachedPositionOptions.Custom:
                    newPos = DetachedFixedPosition;
                    break;

                default:
                    newPos = new(); //satisfy the compiler
                    break;
            }

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
        SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE, NONE_STYLE); // TBR (also in Attach/FullScren): Needs to be after SetParent. when detached and trying to close the owner will take two clicks (like mouse capture without release) //SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE, GetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE) & ~(nint)WindowStyles.WS_CHILD);
        Surface.Owner = DetachedNoOwner ? null : Owner;
        Surface.Topmost = DetachedTopMost;

        SetRect(ref final);
        ResetVisibleRect();

        if (Surface.IsVisible) // Initially detached will not be visible yet and activate not required (in case of multiple)
            Surface.Activate();
    }

    public void RefreshNormalFullScreen()
    {
        if (IsFullScreen)
        {
            if (IsAttached)
            {
                // When we set the parent to null we don't really know in which left/top will be transfered and maximized into random screen
                GetWindowRect(SurfaceHandle, ref curRect);

                ResetVisibleRect();
                SetParent(SurfaceHandle, IntPtr.Zero);
                SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE, NONE_STYLE); // TBR (also in Attach/FullScren): Needs to be after SetParent. when detached and trying to close the owner will take two clicks (like mouse capture without release) //SetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE, GetWindowLong(SurfaceHandle, (int)WindowLongFlags.GWL_STYLE) & ~(nint)WindowStyles.WS_CHILD);
                Surface.Owner   = DetachedNoOwner ? null : Owner;
                Surface.Topmost = DetachedTopMost;

                SetWindowPos(SurfaceHandle, IntPtr.Zero, curRect.Left, curRect.Top, 0, 0, (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_NOSIZE));
            }

            if (Player != null)
                Player.renderer.CornerRadius = zeroCornerRadius;

            if (CornerRadius != zeroCornerRadius)
                ((Border)Surface.Content).CornerRadius = zeroCornerRadius;

            Surface.WindowState = WindowState.Maximized;

            // If it was above the borders and double click (mouse didn't move to refresh)
            Surface.Cursor = Cursors.Arrow;
            if (Overlay != null)
                Overlay.Cursor = Cursors.Arrow;
        }
        else
        {
            if (IsStandAlone)
                Surface.WindowState = WindowState.Normal;

            if (IsAttached)
            {
                Attach(true);
                InvalidateVisual(); // To force the FlyleafSharedOverlay (if any) redraw on-top
            }
            else if (Surface.Topmost || DetachedTopMost) // Bring to front (in Desktop, above windows bar)
            {
                Surface.Topmost = false;
                Surface.Topmost = true;
            }

            UpdateCurRatio();

            // TBR: CornerRadius background has issue it's like a mask color?
            if (Player != null)
                Player.renderer.CornerRadius = CornerRadius;

            if (CornerRadius != zeroCornerRadius)
                ((Border)Surface.Content).CornerRadius = CornerRadius;

            if (!IsStandAlone) //when play with alpha video and not standalone, we need to set window state to normal last, otherwise it will be lost the background
                Surface.WindowState = WindowState.Normal;
        }
    }
    public void SetRect(ref Rect rect)
        => SetWindowPos(SurfaceHandle, IntPtr.Zero, (int)Math.Round(rect.X * DpiX), (int)Math.Round(rect.Y * DpiY), (int)Math.Round(rect.Width * DpiX), (int)Math.Round(rect.Height * DpiY),
            (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE));

    private void SetRectOverlay(object sender, SizeChangedEventArgs e)
    {
        if (Overlay != null)
            SetWindowPos(OverlayHandle, IntPtr.Zero, 0, 0, (int)Math.Round(Surface.ActualWidth * DpiX), (int)Math.Round(Surface.ActualHeight * DpiY),
                (uint)(SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOACTIVATE));
    }

    public void ResetVisibleRect()
    {
        SetWindowRgn(SurfaceHandle, IntPtr.Zero, true);
        if (Overlay != null)
            SetWindowRgn(OverlayHandle, IntPtr.Zero, true);
    }
    public void SetVisibleRect(ref Rect rect)
    {
        SetWindowRgn(SurfaceHandle, CreateRectRgn((int)Math.Round(rect.X * DpiX), (int)Math.Round(rect.Y * DpiY), (int)Math.Round(rect.Right * DpiX), (int)Math.Round(rect.Bottom * DpiY)), true);
        if (Overlay != null)
            SetWindowRgn(OverlayHandle, CreateRectRgn((int)Math.Round(rect.X * DpiX), (int)Math.Round(rect.Y * DpiY), (int)Math.Round(rect.Right * DpiX), (int)Math.Round(rect.Bottom * DpiY)), true);
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
            Player = null;
            ReplicaPlayer = null;
            Disposed = true;

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

                Surface.Closed      -= Surface_Closed;
                Surface.Closing     -= Surface_Closing;
                Surface.KeyDown     -= Surface_KeyDown;
                Surface.KeyUp       -= Surface_KeyUp;
                Surface.Drop        -= Surface_Drop;
                Surface.DragEnter   -= Surface_DragEnter;
                Surface.StateChanged-= Surface_StateChanged;
                Surface.SizeChanged -= SetRectOverlay;

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
                Owner.SizeChanged -= Owner_SizeChanged;

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
    public bool Player_GetFullScreen() => IsFullScreen;
    public void Player_SetFullScreen(bool value) => IsFullScreen = value;
    public void Player_Disposed() => UIInvokeIfRequired(() => Player = null);
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

using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Pipboy.Avalonia.Fx.Controls;

/// <summary>
/// A Pip-Boy themed in-app popup: a full-bleed backdrop plus a centred <see cref="PipboyPanel"/>
/// that owns the whole look (bracket title bar, <c>[X]</c> button, <c>.accent</c> / <c>.warning</c>
/// variants and the optional footer).
/// </summary>
/// <remarks>
/// <para>
/// The control covers the structure and behaviour of a <b>single</b> popup. Stacking,
/// single-instance reuse and "which root must be blocked" are host concerns
/// (FastLabel: <c>ShellOverlayHost</c>).
/// </para>
/// <para>
/// <see cref="IsOpen"/> is the single driver of visibility (default <c>false</c> = not visible):
/// the template root is bound to it, so no other API can make the popup appear.
/// </para>
/// <para>
/// The backdrop eats pointers only while its background is non-null, i.e. only in the
/// <c>:modal</c> state. In non-modal mode (<see cref="IsModal"/> = <c>false</c>) there is no scrim
/// and pointer input passes through to whatever is underneath.
/// </para>
/// <para>
/// <see cref="Draggable"/> moves the <b>popup</b> (a <see cref="TranslateTransform"/> on
/// <c>PART_Panel</c>, clamped to its host area) and only from the panel's title bar. It must not be
/// confused with <see cref="PipboyTitleBar"/>'s <c>PART_DragArea</c>: that one moves the host window
/// (<c>Window.BeginMoveDrag</c>), which would be wrong for an in-app overlay.
/// </para>
/// </remarks>
[TemplatePart("PART_Backdrop", typeof(Border))]
[TemplatePart("PART_Panel", typeof(PipboyPanel))]
[PseudoClasses(":open", ":modal", ":draggable", ":closable")]
public class ModalPopup : ContentControl
{
    // ── IsOpen ──────────────────────────────────────────────────────────────
    /// <summary>Single driver of visibility: <c>false</c> (the default) hides the whole popup.</summary>
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<ModalPopup, bool>(nameof(IsOpen));

    // ── Header / Footer ─────────────────────────────────────────────────────
    /// <summary>
    /// Optional header forwarded to the <c>PART_Panel</c> header. A plain
    /// <see cref="ContentControl"/> has no header, so the popup registers its own.
    /// </summary>
    public static readonly StyledProperty<object?> HeaderProperty =
        AvaloniaProperty.Register<ModalPopup, object?>(nameof(Header));

    /// <summary>Optional footer forwarded to the <c>PART_Panel</c> footer (action buttons).</summary>
    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<ModalPopup, object?>(nameof(Footer));

    // ── IsClosable / IsModal / Draggable ────────────────────────────────────
    /// <summary>
    /// When <c>true</c>, both the backdrop click and the panel's <c>[X]</c> button close the popup
    /// through the single <see cref="Close"/> path (which raises <see cref="Closed"/> exactly once).
    /// </summary>
    public static readonly StyledProperty<bool> IsClosableProperty =
        AvaloniaProperty.Register<ModalPopup, bool>(nameof(IsClosable), defaultValue: false);

    /// <summary>
    /// <c>true</c> (the default) lays a scrim over everything underneath and blocks pointer input;
    /// <c>false</c> turns the popup into a non-modal floating panel.
    /// </summary>
    public static readonly StyledProperty<bool> IsModalProperty =
        AvaloniaProperty.Register<ModalPopup, bool>(nameof(IsModal), defaultValue: true);

    /// <summary>
    /// When <c>true</c>, dragging the panel's title bar moves the popup inside its host area
    /// (non-modal floating popups; modal ones stay centred).
    /// </summary>
    public static readonly StyledProperty<bool> DraggableProperty =
        AvaloniaProperty.Register<ModalPopup, bool>(nameof(Draggable), defaultValue: false);

    // ── MaxPanelWidth / MaxPanelHeight ──────────────────────────────────────
    /// <summary>Upper bound for the panel width, forwarded to <c>PART_Panel</c>.</summary>
    public static readonly StyledProperty<double> MaxPanelWidthProperty =
        AvaloniaProperty.Register<ModalPopup, double>(nameof(MaxPanelWidth), defaultValue: 640d);

    /// <summary>Upper bound for the panel height, forwarded to <c>PART_Panel</c>.</summary>
    public static readonly StyledProperty<double> MaxPanelHeightProperty =
        AvaloniaProperty.Register<ModalPopup, double>(nameof(MaxPanelHeight), defaultValue: 480d);

    // ── Routed events ───────────────────────────────────────────────────────
    /// <summary>Raised (bubbling) when the popup becomes open.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> OpenedEvent =
        RoutedEvent.Register<ModalPopup, RoutedEventArgs>(nameof(Opened), RoutingStrategies.Bubble);

    /// <summary>Raised (bubbling) when the popup becomes closed.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ClosedEvent =
        RoutedEvent.Register<ModalPopup, RoutedEventArgs>(nameof(Closed), RoutingStrategies.Bubble);

    private Border? _backdrop;
    private PipboyPanel? _panel;
    private INotifyCollectionChanged? _classesNotifications;

    // Header drag state (see OnPointerPressed / OnPointerMoved).
    private TranslateTransform? _panelOffset;
    private Point _dragOrigin;
    private Point _dragOffsetOrigin;
    private bool _dragArmed;
    private bool _dragging;

    /// <summary>Creates the popup, wires the header drag and syncs the pseudo-classes.</summary>
    public ModalPopup()
    {
        // One set of bubble handlers covers every template state: the header drag looks its hit
        // area up lazily (see IsHeaderHit) because PART_Panel's own template is applied by the
        // panel itself, not by us.
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;

        UpdatePseudoClasses();
    }

    static ModalPopup()
    {
        IsOpenProperty.Changed.AddClassHandler<ModalPopup>(
            (x, e) => x.OnIsOpenChanged(e));
        IsClosableProperty.Changed.AddClassHandler<ModalPopup>(
            (x, _) => x.UpdatePseudoClasses());
        IsModalProperty.Changed.AddClassHandler<ModalPopup>(
            (x, _) => x.UpdatePseudoClasses());
        DraggableProperty.Changed.AddClassHandler<ModalPopup>(
            (x, _) => x.UpdatePseudoClasses());
    }

    /// <summary>Gets or sets whether the popup is open. The single driver of visibility.</summary>
    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>Gets or sets optional header content shown by the inner panel.</summary>
    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Gets or sets optional footer content shown by the inner panel (e.g. action buttons).</summary>
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>Gets or sets whether the backdrop click and the panel's <c>[X]</c> may close the popup.</summary>
    public bool IsClosable
    {
        get => GetValue(IsClosableProperty);
        set => SetValue(IsClosableProperty, value);
    }

    /// <summary>Gets or sets whether the popup is modal (scrim + blocked input) or a floating panel.</summary>
    public bool IsModal
    {
        get => GetValue(IsModalProperty);
        set => SetValue(IsModalProperty, value);
    }

    /// <summary>
    /// Gets or sets whether dragging the panel's title bar moves the popup. Modal popups leave this
    /// <c>false</c> and therefore stay centred.
    /// </summary>
    public bool Draggable
    {
        get => GetValue(DraggableProperty);
        set => SetValue(DraggableProperty, value);
    }

    /// <summary>Gets or sets the maximum width of the inner panel.</summary>
    public double MaxPanelWidth
    {
        get => GetValue(MaxPanelWidthProperty);
        set => SetValue(MaxPanelWidthProperty, value);
    }

    /// <summary>Gets or sets the maximum height of the inner panel.</summary>
    public double MaxPanelHeight
    {
        get => GetValue(MaxPanelHeightProperty);
        set => SetValue(MaxPanelHeightProperty, value);
    }

    /// <summary>Raised (bubbling) when the popup becomes open.</summary>
    public event EventHandler<RoutedEventArgs>? Opened
    {
        add => AddHandler(OpenedEvent, value);
        remove => RemoveHandler(OpenedEvent, value);
    }

    /// <summary>
    /// Raised (bubbling) exactly once per open/close cycle — after the unified close path
    /// (<see cref="Close"/>, backdrop click or panel <c>[X]</c>) has set <see cref="IsOpen"/> to
    /// <c>false</c>. The host is responsible for hiding or removing the popup in response.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? Closed
    {
        add => AddHandler(ClosedEvent, value);
        remove => RemoveHandler(ClosedEvent, value);
    }

    /// <summary>
    /// The unified close path used by the backdrop click, the panel's <c>[X]</c> and hosts:
    /// sets <see cref="IsOpen"/> to <c>false</c> and therefore raises <see cref="Closed"/> once.
    /// Repeated calls (or closing an already closed popup) do nothing — no duplicate event.
    /// </summary>
    public void Close()
    {
        if (!IsOpen)
            return;

        SetCurrentValue(IsOpenProperty, false);
    }

    // ── Template ────────────────────────────────────────────────────────────

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        UnhookTemplateParts();

        _backdrop = e.NameScope.Find<Border>("PART_Backdrop");
        _panel = e.NameScope.Find<PipboyPanel>("PART_Panel");

        if (_backdrop is not null)
            _backdrop.PointerPressed += OnBackdropPointerPressed;

        if (_panel is not null)
        {
            // The panel's own [X] raises PipboyPanel.Closed; the popup turns that into the same
            // unified close path as a backdrop click (idempotent, so a double close raises once).
            _panel.Closed += OnPanelClosed;
        }

        if (Classes is INotifyCollectionChanged notifications)
        {
            notifications.CollectionChanged += OnClassesChanged;
            _classesNotifications = notifications;
        }

        SyncPanelVariants();
        UpdatePseudoClasses();
    }

    // ── Behaviour ───────────────────────────────────────────────────────────

    private void OnIsOpenChanged(AvaloniaPropertyChangedEventArgs e)
    {
        UpdatePseudoClasses();
        RaiseEvent(new RoutedEventArgs(e.NewValue is true ? OpenedEvent : ClosedEvent));
    }

    /// <summary>
    /// Backdrop click = cancel, but only when the popup is closable. The backdrop is only
    /// hit-testable while it has a background (the <c>:modal</c> style in ModalPopup.axaml), so in
    /// non-modal mode this handler never sees a click that was meant for the content underneath.
    /// </summary>
    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsClosable)
            return;

        e.Handled = true;
        Close();
    }

    private void OnPanelClosed(object? sender, RoutedEventArgs e) => Close();

    // ── Header drag ─────────────────────────────────────────────────────────

    /// <summary>
    /// Arms a drag when the left button goes down inside the panel's <b>header</b> (V3: only the
    /// header is a drag handle — the content area and everything behind the popup must stay fully
    /// interactive). Nothing is captured here: a plain click has to keep reaching the panel's own
    /// buttons, <c>[X]</c> included.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!Draggable || _panel is null)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (!IsHeaderHit(e.GetPosition(_panel)))
            return;

        _dragArmed = true;
        _dragging = false;
        _dragOrigin = e.GetPosition(_panel);
        _dragOffsetOrigin = _panelOffset is { } offset ? new Point(offset.X, offset.Y) : default;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || _panel is null)
            return;

        if (!_dragging)
        {
            // Capture once the pointer actually moves; from here on the drag also survives leaving
            // the panel (the pointer follows the popup).
            _dragging = true;
            e.Pointer.Capture(_panel);
        }

        var current = e.GetPosition(_panel);
        SetPanelOffset(
            _dragOffsetOrigin.X + (current.X - _dragOrigin.X),
            _dragOffsetOrigin.Y + (current.Y - _dragOrigin.Y));

        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragArmed)
            return;

        _dragArmed = false;

        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragArmed = false;
        _dragging = false;
    }

    /// <summary>
    /// Applies the popup's offset as a <see cref="TranslateTransform"/> on <c>PART_Panel</c> (never a
    /// window move) and clamps it so the panel stays inside its host area — the overlay root, which
    /// fills the window (归档设计 §3.4「夹在窗口范围内」).
    /// </summary>
    private void SetPanelOffset(double x, double y)
    {
        if (_panel is null)
            return;

        // Bounds are the untransformed layout rect in the host's coordinates; a render transform
        // does not invalidate layout, so the clamp stays stable while dragging.
        var bounds = _panel.Bounds;
        var host = (_panel.Parent as Visual)?.Bounds.Size ?? bounds.Size;
        var minX = -bounds.Left;
        var maxX = host.Width - bounds.Right;
        var minY = -bounds.Top;
        var maxY = host.Height - bounds.Bottom;

        _panelOffset ??= new TranslateTransform();
        if (!ReferenceEquals(_panel.RenderTransform, _panelOffset))
            _panel.RenderTransform = _panelOffset;

        _panelOffset.X = ClampOffset(x, minX, maxX);
        _panelOffset.Y = ClampOffset(y, minY, maxY);
    }

    /// <summary>Clamps into the host area; an oversized panel keeps its leading edge visible.</summary>
    private static double ClampOffset(double value, double min, double max) =>
        max < min ? min : Math.Clamp(value, min, max);

    private bool IsHeaderHit(Point positionInPanel)
    {
        if (_panel is null)
            return false;

        var titleBar = FindTitleBar(_panel);
        if (titleBar is null || titleBar.TranslatePoint(new Point(0, 0), _panel) is not { } origin)
            return false;

        return new Rect(origin, titleBar.Bounds.Size).Contains(positionInPanel);
    }

    /// <summary>
    /// Locates <c>PART_Panel</c>'s title bar, i.e. the row carrying the header.
    /// <see cref="PipboyPanel"/>'s own theme renders the header as the first content presenter
    /// inside the top-docked title bar, so the drag handle is that presenter's first
    /// <see cref="Border"/> ancestor. Returns <c>null</c> while the panel is untemplated — which
    /// simply means "no drag" (fail safe, never a jump).
    /// </summary>
    private static Border? FindTitleBar(PipboyPanel panel)
    {
        var presenter = panel.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();

        for (var visual = (Visual?)presenter; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, panel))
                break;

            if (visual is Border border)
                return border;
        }

        return null;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void UnhookTemplateParts()
    {
        if (_backdrop is not null)
            _backdrop.PointerPressed -= OnBackdropPointerPressed;

        if (_panel is not null)
            _panel.Closed -= OnPanelClosed;

        if (_classesNotifications is not null)
        {
            _classesNotifications.CollectionChanged -= OnClassesChanged;
            _classesNotifications = null;
        }

        _backdrop = null;
        _panel = null;
    }

    private void OnClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncPanelVariants();

    /// <summary>
    /// Forwards the <c>.accent</c> / <c>.warning</c> variant classes from the popup (where hosts such
    /// as FastLabel's <c>ShellOverlayHost</c> add them) to the inner <c>PART_Panel</c>, which owns the
    /// variant visuals. This is deliberately a pass-through instead of a second copy of the
    /// <c>BorderBrush</c> setters in <c>ModalPopup.axaml</c>: <see cref="PipboyPanel"/>'s own theme
    /// stays the single source of truth for what a variant looks like.
    /// </summary>
    private void SyncPanelVariants()
    {
        if (_panel is null)
            return;

        _panel.Classes.Set("accent", Classes.Contains("accent"));
        _panel.Classes.Set("warning", Classes.Contains("warning"));
    }

    private void UpdatePseudoClasses()
    {
        PseudoClasses.Set(":open", IsOpen);
        PseudoClasses.Set(":modal", IsModal);
        PseudoClasses.Set(":draggable", Draggable);
        PseudoClasses.Set(":closable", IsClosable);
    }
}
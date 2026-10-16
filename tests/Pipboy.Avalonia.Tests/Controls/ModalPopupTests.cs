using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pipboy.Avalonia.Fx.Controls;
using Xunit;

namespace Pipboy.Avalonia.Tests.Controls;

/// <summary>
/// Contract tests for <see cref="ModalPopup"/>: template parts (the V9 guard against a control theme
/// that was never mounted), IsOpen as the single driver of visibility, variant class pass-through,
/// the unified close path (backdrop / panel [X], exactly one <c>Closed</c>), the header-only drag
/// (TranslateTransform, clamped) and the "open → close → open again" regression (V10: the double-open
/// crash was only caught after a false PASS that tested open → close).
/// </summary>
/// <remarks>
/// Every test hosts the popup in a real <see cref="Window"/>, so the stock style pipeline runs: the
/// <c>PipboyFxTheme → Index.axaml → StyleInclude</c> chain has to be intact for the template (and for
/// the inner <see cref="PipboyPanel"/>'s own template) to materialise at all.
/// </remarks>
public class ModalPopupTests
{
    // ── V9: the control theme must be reachable through the mounted theme chain ──────────────

    [AvaloniaFact]
    public void ControlTheme_IsMountedThroughIndexAndTargetsModalPopup()
    {
        using var host = new PopupHost();

        Assert.True(
            Application.Current!.TryFindResource(typeof(ModalPopup), out var resource),
            "No implicit ControlTheme for ModalPopup is reachable from Application.Styles - "
            + "check that its style is mounted in Pipboy.Avalonia.Fx/Index.axaml.");

        Assert.Equal(typeof(ModalPopup), Assert.IsType<ControlTheme>(resource).TargetType);
    }

    [AvaloniaFact]
    public void OnApplyTemplate_ResolvesBackdropAndPanelParts()
    {
        var content = new TextBlock { Text = "payload" };
        var popup = new ModalPopup
        {
            IsOpen = true,
            Header = "TITLE",
            Footer = "FOOTER",
            Content = content,
            MaxPanelWidth = 300,
            MaxPanelHeight = 200,
        };
        using var host = new PopupHost(popup);

        var backdrop = Backdrop(popup);
        var panel = Panel(popup);

        Assert.NotNull(backdrop.Name);

        // The popup forwards everything the panel owns: header / content / footer / bounds.
        Assert.Equal("TITLE", panel.Header);
        Assert.Same(content, panel.Content);
        Assert.Equal("FOOTER", panel.Footer);
        Assert.Equal(300d, panel.MaxWidth);
        Assert.Equal(200d, panel.MaxHeight);

        // The inner panel runs its own theme too (real control tree, no hand-wired template).
        Assert.NotNull(CloseButton(popup));
    }

    // ── IsOpen is the single driver of visibility ────────────────────────────────────────────

    [AvaloniaFact]
    public void IsOpen_IsTheSingleDriverOfVisibility()
    {
        var popup = new ModalPopup();
        using var host = new PopupHost(popup);

        Assert.False(popup.IsOpen);
        Assert.False(Root(popup).IsVisible);

        popup.IsOpen = true;
        host.Pump();
        Assert.True(Root(popup).IsVisible);

        popup.IsOpen = false;
        host.Pump();
        Assert.False(Root(popup).IsVisible);
    }

    // ── Variants: the host adds accent / warning on the popup, the panel owns the visuals ────

    [AvaloniaFact]
    public void VariantClasses_AreForwardedToTheInnerPanel()
    {
        var popup = new ModalPopup { IsOpen = true };
        popup.Classes.Add("accent");
        using var host = new PopupHost(popup);

        var panel = Panel(popup);

        // Set before the template was applied (the host's order).
        Assert.Contains("accent", panel.Classes);

        // Set and removed after the template was applied.
        popup.Classes.Add("warning");
        Assert.Contains("warning", panel.Classes);

        popup.Classes.Remove("accent");
        popup.Classes.Remove("warning");
        Assert.DoesNotContain("accent", panel.Classes);
        Assert.DoesNotContain("warning", panel.Classes);
    }

    // ── The unified close path ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Close_SetsIsOpenFalse_AndRaisesClosedExactlyOnce()
    {
        var popup = new ModalPopup();
        using var host = new PopupHost(popup);
        var opened = 0;
        var closed = 0;
        popup.Opened += (_, _) => opened++;
        popup.Closed += (_, _) => closed++;

        popup.IsOpen = true;
        Assert.Equal(1, opened);
        Assert.Equal(0, closed);

        popup.Close();
        Assert.False(popup.IsOpen);
        Assert.Equal(1, closed);

        // Idempotent: a second close must not raise again.
        popup.Close();
        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public void BackdropClick_ClosesOnlyWhenClosable_AndRaisesClosedOnce()
    {
        // IsClosable = false: a click on the backdrop must not close anything.
        var notClosable = new ModalPopup { IsOpen = true, IsModal = true };
        using (var host = new PopupHost(notClosable))
        {
            var closed = 0;
            notClosable.Closed += (_, _) => closed++;

            ClickOnBackdrop(host);

            Assert.True(notClosable.IsOpen);
            Assert.Equal(0, closed);
        }

        // IsClosable = true: the backdrop click closes through the unified path, once.
        var popup = new ModalPopup { IsOpen = true, IsModal = true, IsClosable = true };
        using (var host = new PopupHost(popup))
        {
            var closed = 0;
            popup.Closed += (_, _) => closed++;

            ClickOnBackdrop(host);
            Assert.False(popup.IsOpen);
            Assert.Equal(1, closed);

            // A repeated click (and an explicit close) must stay a no-op.
            ClickOnBackdrop(host);
            popup.Close();
            Assert.Equal(1, closed);
        }
    }

    [AvaloniaFact]
    public void PanelCloseButton_ClosesThroughTheUnifiedPath()
    {
        var popup = new ModalPopup { IsOpen = true, Header = "TITLE", IsClosable = true };
        using var host = new PopupHost(popup);
        var closed = 0;
        popup.Closed += (_, _) => closed++;

        var closeButton = CloseButton(popup);
        Click(host.Window, Center(closeButton, host.Window));

        Assert.False(popup.IsOpen);
        Assert.Equal(1, closed);
    }

    // ── V10: open → close → open again ──────────────────────────────────────────────────────

    [AvaloniaFact]
    public void OpenCloseOpenAgain_DoesNotThrow_AndKeepsThePartsAlive()
    {
        var popup = new ModalPopup { Header = "TITLE", IsClosable = true };
        using var host = new PopupHost(popup);
        var opened = 0;
        var closed = 0;
        popup.Opened += (_, _) => opened++;
        popup.Closed += (_, _) => closed++;

        for (var cycle = 0; cycle < 3; cycle++)
        {
            popup.IsOpen = true;
            host.Pump();
            Assert.True(popup.IsOpen);
            Assert.True(Root(popup).IsVisible);
            Assert.NotNull(Backdrop(popup));
            Assert.NotNull(Panel(popup));
            Assert.NotNull(CloseButton(popup));

            popup.Close();
            host.Pump();
            Assert.False(popup.IsOpen);
            Assert.False(Root(popup).IsVisible);
        }

        Assert.Equal(3, opened);
        Assert.Equal(3, closed);
    }

    // ── Header drag (V3: header hit area only, never a window move) ─────────────────────────

    [AvaloniaFact]
    public void Draggable_HeaderDrag_MovesThePopupByTranslateTransform()
    {
        var popup = FloatingDraggable();
        using var host = new PopupHost(popup);
        var panel = Panel(popup);
        var start = Center(HeaderRow(popup), host.Window);

        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseMove(start + new Vector(40, 25));
        host.Window.MouseUp(start + new Vector(40, 25), MouseButton.Left);
        host.Pump();

        var offset = Assert.IsType<TranslateTransform>(panel.RenderTransform);
        Assert.Equal(40, offset.X, 1);
        Assert.Equal(25, offset.Y, 1);

        // Only PART_Panel is translated: neither the popup nor the host window moves.
        Assert.Null(popup.RenderTransform);
    }

    [AvaloniaFact]
    public void Draggable_False_DoesNotMoveThePopup()
    {
        var popup = FloatingDraggable();
        popup.Draggable = false;
        using var host = new PopupHost(popup);
        var panel = Panel(popup);
        var start = Center(HeaderRow(popup), host.Window);

        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseMove(start + new Vector(40, 25));
        host.Window.MouseUp(start + new Vector(40, 25), MouseButton.Left);
        host.Pump();

        Assert.Null(panel.RenderTransform);
    }

    [AvaloniaFact]
    public void Draggable_PressOnTheContent_DoesNotMoveThePopup()
    {
        var popup = FloatingDraggable();
        popup.Content = new TextBlock { Text = "payload" };
        using var host = new PopupHost(popup);
        var panel = Panel(popup);
        var start = Center(ContentRow(popup), host.Window);

        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseMove(start + new Vector(40, 25));
        host.Window.MouseUp(start + new Vector(40, 25), MouseButton.Left);
        host.Pump();

        Assert.Null(panel.RenderTransform);
    }

    [AvaloniaFact]
    public void Draggable_Offset_IsClampedToTheHostArea_BottomRight()
    {
        AssertClampedAtExtreme(new Vector(5000, 5000), assertLeadingEdge: false);
    }

    [AvaloniaFact]
    public void Draggable_Offset_IsClampedToTheHostArea_TopLeft()
    {
        AssertClampedAtExtreme(new Vector(-5000, -5000), assertLeadingEdge: true);
    }

    // ── Field-level defaults from the library contract ──────────────────────────────────────

    [AvaloniaFact]
    public void Defaults_MatchTheModalPopupContract()
    {
        var popup = new ModalPopup();

        Assert.False(popup.IsOpen);
        Assert.Null(popup.Header);
        Assert.Null(popup.Footer);
        Assert.False(popup.IsClosable);
        Assert.True(popup.IsModal);
        Assert.False(popup.Draggable);
        Assert.Equal(640d, popup.MaxPanelWidth);
        Assert.Equal(480d, popup.MaxPanelHeight);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Hosts a popup (or nothing) in a real window with both Pipboy themes mounted.</summary>
    private sealed class PopupHost : IDisposable
    {
        private readonly global::Pipboy.Avalonia.PipboyTheme _core = new();
        private readonly global::Pipboy.Avalonia.Fx.PipboyFxTheme _fx = new();

        public PopupHost(ModalPopup? popup = null)
        {
            Application.Current!.Styles.Add(_core);
            Application.Current.Styles.Add(_fx);

            Window = new Window { Width = 800, Height = 600 };
            if (popup is not null)
                Window.Content = popup;

            Window.Show();
            Pump();
        }

        public Window Window { get; }

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            Application.Current!.Styles.Remove(_fx);
            Application.Current.Styles.Remove(_core);
            _core.Dispose();
        }
    }

    private static ModalPopup FloatingDraggable() => new()
    {
        IsOpen = true,
        Header = "PALETTE",
        IsModal = false,
        Draggable = true,
    };

    private static void AssertClampedAtExtreme(Vector delta, bool assertLeadingEdge)
    {
        var popup = FloatingDraggable();
        using var host = new PopupHost(popup);
        var panel = Panel(popup);
        var hostArea = Assert.IsAssignableFrom<Visual>(panel.Parent);
        var start = Center(HeaderRow(popup), host.Window);

        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseMove(start + delta);
        host.Window.MouseUp(start + delta, MouseButton.Left);
        host.Pump();

        var offset = Assert.IsType<TranslateTransform>(panel.RenderTransform);

        if (assertLeadingEdge)
        {
            Assert.True(panel.Bounds.Left + offset.X >= -0.5, "the left edge must stay inside the host");
            Assert.True(panel.Bounds.Top + offset.Y >= -0.5, "the top edge must stay inside the host");
        }
        else
        {
            Assert.True(
                panel.Bounds.Right + offset.X <= hostArea.Bounds.Width + 0.5,
                "the right edge must stay inside the host");
            Assert.True(
                panel.Bounds.Bottom + offset.Y <= hostArea.Bounds.Height + 0.5,
                "the bottom edge must stay inside the host");
        }
    }

    /// <summary>Clicks a corner of the popup that only the backdrop covers.</summary>
    private static void ClickOnBackdrop(PopupHost host) => Click(host.Window, new Point(4, 4));

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static Point Center(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), relativeTo)
        ?? throw new InvalidOperationException($"{visual} is not in the same visual tree as {relativeTo}.");

    private static Visual Root(ModalPopup popup) => Assert.Single(popup.GetVisualChildren());

    private static Border Backdrop(ModalPopup popup) => Assert.Single(
        popup.GetVisualDescendants().OfType<Border>(), b => b.Name == "PART_Backdrop");

    private static PipboyPanel Panel(ModalPopup popup) => Assert.Single(
        popup.GetVisualDescendants().OfType<PipboyPanel>(), p => p.Name == "PART_Panel");

    private static Button CloseButton(ModalPopup popup) => Assert.Single(
        Panel(popup).GetVisualDescendants().OfType<Button>(), b => b.Name == "PART_CloseButton");

    /// <summary>The presenter that shows the popup's header - i.e. a point inside the title bar.</summary>
    private static ContentPresenter HeaderRow(ModalPopup popup) => Assert.Single(
        Panel(popup).GetVisualDescendants().OfType<ContentPresenter>(),
        presenter => ReferenceEquals(presenter.Content, popup.Header));

    /// <summary>The presenter that shows the popup's content - i.e. a point outside the title bar.</summary>
    private static ContentPresenter ContentRow(ModalPopup popup) => Assert.Single(
        Panel(popup).GetVisualDescendants().OfType<ContentPresenter>(),
        presenter => ReferenceEquals(presenter.Content, popup.Content));
}
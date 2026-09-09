using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Asura.App.Tests;

/// <summary>
/// The side-docked tab strip's box model, measured rather than eyeballed: rows
/// fill the strip's width, and the close button holds the row's trailing edge
/// no matter how long the title is. This geometry shipped broken twice because
/// nothing measured it.
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class RuntimeTabStripSideDockHeadlessTests
{
    private sealed record FakeTab(
        string Title,
        bool IsActive,
        bool CanClose,
        bool HasAttention,
        string Icon = "terminal",
        Symbol IconSymbol = Symbol.WindowConsole) : IRuntimeTabStripItem
    {
        public string AgentActivity => string.Empty;

        public bool HasAgentActivity => false;
    }

    [Fact]
    public Task Active_tab_sticks_to_both_overflow_edges() =>
        RunHeadlessAsync(() =>
        {
            var tabs = Enumerable.Range(0, 6)
                .Select(index => new FakeTab(
                    $"tab-{index}",
                    IsActive: index == 2,
                    CanClose: true,
                    HasAttention: false))
                .ToArray();
            var strip = new RuntimeTabStripView
            {
                Width = 360,
                Orientation = Orientation.Horizontal,
                Tabs = tabs,
            };
            var window = new Window
            {
                Width = 360,
                Height = 80,
                Content = strip,
            };
            window.Show();
            window.UpdateLayout();

            var scroll = strip.GetVisualDescendants().OfType<ScrollViewer>().Single();
            scroll.Offset = new Vector(1, 0);
            window.UpdateLayout();
            var active = strip.GetVisualDescendants()
                .OfType<Grid>()
                .Single(grid => grid.Classes.Contains("RuntimeTabDropTarget")
                    && ReferenceEquals(grid.DataContext, tabs[2]));
            var activeContainer = active.GetVisualAncestors().OfType<ContentPresenter>().First();
            var right = active.TranslatePoint(
                new Point(active.Bounds.Width, 0),
                scroll);
            Assert.NotNull(right);
            Assert.InRange(scroll.Viewport.Width - right.Value.X, -1, 1);
            Assert.Equal(1, activeContainer.ZIndex);
            Assert.All(
                strip.GetVisualDescendants()
                    .OfType<Grid>()
                    .Where(grid => grid.Classes.Contains("RuntimeTabDropTarget")
                        && !ReferenceEquals(grid, active))
                    .Select(grid => grid.GetVisualAncestors().OfType<ContentPresenter>().First()),
                sibling => Assert.True(activeContainer.ZIndex > sibling.ZIndex));

            scroll.Offset = new Vector(scroll.Extent.Width, 0);
            window.UpdateLayout();
            var left = active.TranslatePoint(default, scroll);
            Assert.NotNull(left);
            Assert.InRange(left.Value.X, -1, 1);
            Assert.Equal(1, activeContainer.ZIndex);

            window.Close();
            return Task.CompletedTask;
        });

    [Fact]
    public Task Active_tab_is_cut_out_of_fade_without_revealing_siblings() =>
        RunHeadlessAsync(() =>
        {
            var tabs = Enumerable.Range(0, 6)
                .Select(index => new FakeTab(
                    $"tab-{index}",
                    IsActive: index == 2,
                    CanClose: true,
                    HasAttention: false))
                .ToArray();
            var strip = new RuntimeTabStripView
            {
                Width = 360,
                Orientation = Orientation.Horizontal,
                Tabs = tabs,
            };
            var window = new Window
            {
                Width = 360,
                Height = 80,
                Content = strip,
            };
            window.Show();
            window.UpdateLayout();

            var scroll = strip.GetVisualDescendants().OfType<ScrollViewer>().Single();
            var active = strip.GetVisualDescendants()
                .OfType<Grid>()
                .Single(grid => grid.Classes.Contains("RuntimeTabDropTarget")
                    && ReferenceEquals(grid.DataContext, tabs[2]));
            var activeContainer = active.GetVisualAncestors().OfType<ContentPresenter>().First();
            var fadeExtent = Math.Min(56, scroll.Viewport.Width / 3);
            var maxOffset = scroll.Extent.Width - scroll.Viewport.Width;

            var leadingBandOffset = activeContainer.Bounds.X - (fadeExtent / 2);
            Assert.InRange(leadingBandOffset, 1, maxOffset - 1);
            scroll.Offset = new Vector(leadingBandOffset, 0);
            window.UpdateLayout();
            var leading = active.TranslatePoint(default, scroll);
            Assert.NotNull(leading);
            Assert.InRange(leading.Value.X, 1, fadeExtent - 1);
            AssertActiveMaskCutout(
                scroll,
                leading.Value.X,
                leadingEdge: true);

            var trailingBandOffset = activeContainer.Bounds.X
                + activeContainer.Bounds.Width
                - scroll.Viewport.Width
                + (fadeExtent / 2);
            Assert.InRange(trailingBandOffset, 1, maxOffset - 1);
            scroll.Offset = new Vector(trailingBandOffset, 0);
            window.UpdateLayout();
            var trailing = active.TranslatePoint(
                new Point(active.Bounds.Width, 0),
                scroll);
            Assert.NotNull(trailing);
            Assert.InRange(
                scroll.Viewport.Width - trailing.Value.X,
                1,
                fadeExtent - 1);
            AssertActiveMaskCutout(
                scroll,
                trailing.Value.X,
                leadingEdge: false);

            window.Close();
            return Task.CompletedTask;
        });

    [Fact]
    public Task The_tab_surface_is_both_the_activation_and_drag_target() =>
        RunHeadlessAsync(() =>
        {
            var tab = new FakeTab(
                "production-api",
                IsActive: true,
                CanClose: true,
                HasAttention: false);
            var strip = new RuntimeTabStripView
            {
                Orientation = Orientation.Horizontal,
                Tabs = new[] { tab },
            };
            var window = new Window
            {
                Width = 320,
                Height = 80,
                Content = strip,
            };
            object? activatedBy = null;
            object? closedBy = null;
            object? dragStartedBy = null;
            object? dragMovedBy = null;
            object? dragReleasedBy = null;
            strip.ActivateRequested += (sender, _) => activatedBy = sender;
            strip.CloseRequested += (sender, _) => closedBy = sender;
            strip.ReorderPointerPressed += (sender, _) => dragStartedBy = sender;
            strip.ReorderPointerMoved += (sender, _) => dragMovedBy = sender;
            strip.ReorderPointerReleased += (sender, _) => dragReleasedBy = sender;
            window.Show();
            window.UpdateLayout();

            var activator = strip.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Classes.Contains("RuntimeTabActivator"));
            var centre = activator.TranslatePoint(
                new Point(activator.Bounds.Width / 2, activator.Bounds.Height / 2),
                window);
            Assert.NotNull(centre);
            window.MouseDown(centre.Value, MouseButton.Left);
            window.MouseUp(centre.Value, MouseButton.Left);

            Assert.Same(activator, dragStartedBy);
            Assert.Same(activator, dragReleasedBy);
            Assert.Same(activator, activatedBy);

            window.MouseDown(centre.Value, MouseButton.Left);
            window.MouseMove(
                centre.Value + new Vector(8, 0),
                RawInputModifiers.LeftMouseButton);
            window.MouseUp(
                centre.Value + new Vector(8, 0),
                MouseButton.Left,
                RawInputModifiers.None);
            Assert.Same(activator, dragMovedBy);

            dragStartedBy = null;
            dragMovedBy = null;
            dragReleasedBy = null;
            var close = strip.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => string.Equals(ToolTip.GetTip(button) as string, "Close tab", StringComparison.Ordinal));
            var closeCentre = close.TranslatePoint(
                new Point(close.Bounds.Width / 2, close.Bounds.Height / 2),
                window);
            Assert.NotNull(closeCentre);
            window.MouseDown(closeCentre.Value, MouseButton.Left);
            window.MouseUp(closeCentre.Value, MouseButton.Left);

            Assert.Same(close, closedBy);
            Assert.Null(dragStartedBy);
            Assert.Null(dragMovedBy);
            Assert.Null(dragReleasedBy);
            window.Close();
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_double_tap_on_the_title_edits_it_inline() =>
        RunHeadlessAsync(() =>
        {
            var tab = new FakeTab(
                "production-api",
                IsActive: true,
                CanClose: true,
                HasAttention: false);
            var strip = new RuntimeTabStripView
            {
                Orientation = Orientation.Horizontal,
                Tabs = new[] { tab },
            };
            var window = new Window
            {
                Width = 320,
                Height = 80,
                Content = strip,
            };
            object? requestedTab = null;
            string? requestedTitle = null;
            strip.TitleEditRequested += (_, e) =>
            {
                requestedTab = e.Tab;
                requestedTitle = e.Title;
            };
            window.Show();
            window.UpdateLayout();

            var title = strip.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(candidate => candidate.Classes.Contains("RuntimeTabTitle"));
            title.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
            var editor = strip.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(candidate => candidate.Classes.Contains("RuntimeTabTitleEditor"));
            Assert.True(editor.IsVisible);
            Assert.Equal(tab.Title, editor.Text);

            editor.Text = "renamed production";
            editor.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));

            Assert.Same(tab, requestedTab);
            Assert.Equal("renamed production", requestedTitle);
            Assert.False(editor.IsVisible);
            window.Close();
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_double_tap_on_the_icon_opens_the_picker_and_reports_the_choice() =>
        RunHeadlessAsync(() =>
        {
            var tab = new FakeTab(
                "production-api",
                IsActive: true,
                CanClose: true,
                HasAttention: false);
            var strip = new RuntimeTabStripView
            {
                Orientation = Orientation.Horizontal,
                IconPickerPlacement = PlacementMode.TopEdgeAlignedLeft,
                Tabs = new[] { tab },
            };
            var window = new Window
            {
                Width = 420,
                Height = 360,
                Content = strip,
            };
            object? requestedTab = null;
            string? requestedIcon = null;
            strip.IconEditRequested += (_, e) =>
            {
                requestedTab = e.Tab;
                requestedIcon = e.Icon;
            };
            window.Show();
            window.UpdateLayout();

            var iconTarget = strip.GetVisualDescendants()
                .OfType<Border>()
                .Single(candidate => string.Equals(ToolTip.GetTip(candidate) as string
, "Double-click to change icon", StringComparison.Ordinal));
            iconTarget.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));

            var flyout = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(iconTarget));
            Assert.True(flyout.IsOpen);
            Assert.Equal(PlacementMode.TopEdgeAlignedLeft, flyout.Placement);
            var picker = Assert.IsType<IconPicker>(flyout.Content);
            window.UpdateLayout();
            var rocket = picker.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => string.Equals(ToolTip.GetTip(button) as string, "Rocket", StringComparison.Ordinal));
            rocket.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Same(tab, requestedTab);
            Assert.Equal("rocket", requestedIcon);
            Assert.False(flyout.IsOpen);
            window.Close();
            return Task.CompletedTask;
        });

    [Fact]
    public Task Icon_picker_show_all_uses_the_same_tile_shape_as_icon_choices() =>
        RunHeadlessAsync(() =>
        {
            var picker = new IconPicker
            {
                Width = 330,
                ItemsSource = Array.Empty<object>(),
                TotalCount = 36,
            };
            var container = new Border
            {
                Width = 338,
                Height = 290,
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(20),
                Child = picker,
            };
            var window = new Window
            {
                Width = 338,
                Height = 290,
                Content = container,
            };
            window.Show();
            window.UpdateLayout();

            var showAll = picker.GetVisualDescendants()
                .OfType<ToggleButton>()
                .Single(button => button.Classes.Contains("IconChoice"));
            var (_, derivedRadius) = ConcentricCorners.DeriveFor(
                showAll,
                Concentric.GetMinimumRadius(showAll));
            Assert.True(Concentric.GetIsEnabled(showAll));
            Assert.NotNull(derivedRadius);
            Assert.Equal(derivedRadius.Value, showAll.CornerRadius);
            Assert.Equal(38, showAll.Width);
            Assert.Equal(38, showAll.Height);

            var initialRadius = showAll.CornerRadius;
            container.CornerRadius = new CornerRadius(24);
            var (_, updatedRadius) = ConcentricCorners.DeriveFor(
                showAll,
                Concentric.GetMinimumRadius(showAll));
            Assert.NotNull(updatedRadius);
            Assert.NotEqual(initialRadius, updatedRadius.Value);
            Assert.Equal(updatedRadius.Value, showAll.CornerRadius);

            window.Close();
            return Task.CompletedTask;
        });

    [Fact]
    public Task Side_docked_tabs_fill_the_strip_and_pin_their_close_buttons() =>
        RunHeadlessAsync(() =>
        {
            var strip = new RuntimeTabStripView
            {
                Orientation = Orientation.Vertical,
                Tabs = new object[]
                {
                    new FakeTab("production-api", IsActive: true, CanClose: true, HasAttention: false),
                    new FakeTab("db", IsActive: false, CanClose: true, HasAttention: false),
                },
            };
            var window = new Window
            {
                Width = 190,
                Height = 400,
                Content = strip,
            };
            window.Show();
            window.UpdateLayout();

            var rows = strip.GetVisualDescendants()
                .OfType<Grid>()
                .Where(grid => grid.Classes.Contains("RuntimeTabDropTarget"))
                .ToArray();
            Assert.Equal(2, rows.Length);

            var scroll = strip.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .First();
            foreach (var row in rows)
            {
                // The row fills the strip's width: a content-sized row leaves a
                // ragged right edge and a close button that trails the title.
                Assert.True(
                    Math.Abs(strip.Bounds.Width - row.Bounds.Width) <= 2,
                    $"strip={strip.Bounds.Width} scroll={scroll.Bounds.Width} "
                    + $"extent={scroll.Extent.Width} viewport={scroll.Viewport.Width} "
                    + $"h={scroll.HorizontalScrollBarVisibility} v={scroll.VerticalScrollBarVisibility} "
                    + $"row={row.Bounds.Width}");

                var close = Assert.Single(
                    row.GetVisualDescendants().OfType<Button>(),
                    button => button.Classes.Contains("IconButton"));
                var closeRight = close.TranslatePoint(
                    new Point(close.Bounds.Width, 0),
                    row);
                Assert.NotNull(closeRight);
                // The close column is the row's last 20 device pixels.
                Assert.InRange(row.Bounds.Width - closeRight.Value.X, 0, 3);
            }

            // Both rows end at the same edge — the long and the short title.
            Assert.InRange(
                Math.Abs(rows[0].Bounds.Width - rows[1].Bounds.Width),
                0,
                1);

            var chip = rows[0].GetVisualDescendants()
                .OfType<Button>()
                .First(button => button.Classes.Contains("RuntimeTabActivator"));
            Assert.DoesNotContain(
                rows[0].GetVisualDescendants().OfType<SymbolIcon>(),
                icon => icon.Symbol == Symbol.ReOrderDotsVertical);
            var chipLeft = chip.TranslatePoint(default, rows[0]);
            Assert.NotNull(chipLeft);
            Assert.InRange(chipLeft.Value.X, -1, 1);
            var title = chip.GetVisualDescendants()
                .OfType<TextBlock>()
                .First();
            var titleTop = title.TranslatePoint(default, rows[0]);
            Assert.NotNull(titleTop);
            // The title sits on the row's vertical centre.
            var titleCentreOffset = titleTop.Value.Y + (title.Bounds.Height / 2)
                - (rows[0].Bounds.Height / 2);
            Assert.True(
                Math.Abs(titleCentreOffset) <= 1.5,
                $"title centre off by {titleCentreOffset} "
                + $"(row={rows[0].Bounds.Height} chip={chip.Bounds.Height} "
                + $"titleY={titleTop.Value.Y} titleH={title.Bounds.Height})");
            window.Close();
            return Task.CompletedTask;
        });

    private static async Task RunHeadlessAsync(Func<Task> assertion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            var completed = await session.Dispatch(
                async () =>
                {
                    await assertion();
                    return true;
                },
                timeout.Token);
            Assert.True(completed);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private static void AssertActiveMaskCutout(
        ScrollViewer scroll,
        double activeBoundary,
        bool leadingEdge)
    {
        var mask = Assert.IsType<LinearGradientBrush>(scroll.OpacityMask);
        var expectedOffset = leadingEdge ? 0 : 1;
        var edge = mask.GradientStops
            .OrderBy(stop => Math.Abs(stop.Offset - expectedOffset))
            .First();
        Assert.Equal(expectedOffset, edge.Offset, precision: 6);
        Assert.Equal(0, edge.Color.A);

        var boundaryOffset = activeBoundary / scroll.Viewport.Width;
        var boundaryStops = mask.GradientStops
            .Where(stop => Math.Abs(stop.Offset - boundaryOffset) < 0.000001)
            .ToArray();
        Assert.Equal(2, boundaryStops.Length);
        Assert.Equal(byte.MaxValue, boundaryStops[leadingEdge ? 1 : 0].Color.A);
        Assert.True(boundaryStops[leadingEdge ? 0 : 1].Color.A < byte.MaxValue);
    }
}

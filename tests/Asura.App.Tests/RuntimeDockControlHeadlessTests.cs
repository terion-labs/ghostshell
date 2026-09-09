using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.App.Views;
using Asura.App.Views.Components;
using Asura.App.Views.Overlays;
using Asura.App.Views.RuntimePanels;
using Asura.Core;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Controls.ProportionalStackPanel;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Inpc.Controls;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class RuntimeDockControlHeadlessTests
{
    [Fact]
    public Task Runtime_tab_is_initialized_and_published_immediately() =>
        RunHeadlessAsync(() =>
        {
            var tab = NewTab();
            var canvas = new RuntimeDockControl
            {
                RuntimeTab = tab,
                InitializeFactory = true,
                InitializeLayout = false,
            };

            Assert.Same(tab.DockFactory, canvas.Factory);
            Assert.Same(tab.DockLayout, canvas.Layout);
            Assert.Same(tab.DockFactory, tab.DockLayout.Factory);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Changing_tabs_publishes_the_initialized_replacement_layout() =>
        RunHeadlessAsync(() =>
        {
            var first = NewTab();
            var second = NewTab();
            var canvas = new RuntimeDockControl
            {
                RuntimeTab = first,
                InitializeFactory = true,
                InitializeLayout = false,
            };

            canvas.RuntimeTab = second;

            Assert.Same(second.DockFactory, canvas.Factory);
            Assert.Same(second.DockLayout, canvas.Layout);
            Assert.Same(second.DockFactory, second.DockLayout.Factory);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Clearing_the_runtime_tab_clears_the_dock_model() =>
        RunHeadlessAsync(() =>
        {
            var canvas = new RuntimeDockControl
            {
                RuntimeTab = NewTab(),
                InitializeFactory = true,
                InitializeLayout = false,
            };

            canvas.RuntimeTab = null;

            Assert.Null(canvas.Layout);
            Assert.Null(canvas.Factory);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Rebuilt_splitter_cannot_collapse_below_the_styled_gap() =>
        RunHeadlessAsync(() =>
        {
            var splitter = new ProportionalStackPanelSplitter
            {
                Thickness = 8,
                Width = 8,
            };

            // Dock reapplies its initial axis while rebuilding a changed
            // layout, after the styled thickness has already resolved.
            splitter.Width = 1;

            Assert.Equal(8, splitter.Width);
            Assert.True(double.IsNaN(splitter.Height));
            return Task.CompletedTask;
        });

    [Fact]
    public Task Main_window_routes_are_deferred_and_each_template_materializes() =>
        RunHeadlessAsync(() =>
        {
            var window = new MainWindow();
            foreach (var hostName in new[]
                     {
                         "SettingsRouteHost",
                         "CommandPaletteOverlayHost",
                         "NewPanelOverlayHost",
                         "LayoutDesignerOverlayHost",
                         "DefinitionEditorOverlayHost",
                     })
            {
                var host = window.FindControl<ContentControl>(hostName);
                Assert.NotNull(host);
                Assert.Null(host.Content);
            }

            Assert.IsType<SettingsView>(Build(window, "SettingsRouteTemplate"));
            Assert.IsType<CommandPaletteView>(Build(window, "CommandPaletteOverlayTemplate"));
            Assert.IsType<NewPanelChooserView>(Build(window, "NewPanelOverlayTemplate"));
            Assert.IsType<LayoutDesignerView>(Build(window, "LayoutDesignerOverlayTemplate"));
            var definitionEditor = Assert.IsType<SurfaceCard>(
                Build(window, "DefinitionEditorOverlayTemplate"));
            Assert.Single(
                definitionEditor.GetLogicalDescendants().OfType<WorkspaceEditorView>());
            return Task.CompletedTask;

            static Control Build(MainWindow owner, string key)
            {
                var template = Assert.IsAssignableFrom<IDataTemplate>(owner.Resources[key]);
                return Assert.IsAssignableFrom<Control>(template.Build(null));
            }
        });

    [Fact]
    public Task Presentation_discards_documents_that_have_no_live_runtime_panel() =>
        RunHeadlessAsync(() =>
        {
            var tab = NewTab();
            var staleDocument = new Document
            {
                Id = "stale-recovery-panel",
                Title = "Stale recovery panel",
            };
            var staleLeaf = new DocumentDock
            {
                Id = "stale-recovery-leaf",
                ActiveDockable = staleDocument,
                VisibleDockables = tab.DockFactory.CreateList<IDockable>(staleDocument),
            };
            tab.DockFactory.AddDockable(tab.DockLayout, staleLeaf);
            tab.DockLayout.ActiveDockable = staleLeaf;

            tab.InitializeDockLayoutForPresentation();

            var dockables = EnumerateDockables(tab.DockLayout).ToArray();
            Assert.DoesNotContain(staleDocument, dockables);
            Assert.Contains(
                dockables.OfType<Document>(),
                document => ReferenceEquals(document.Context, tab.ActivePanel));
            Assert.NotSame(staleLeaf, tab.DockLayout.ActiveDockable);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Runtime_canvas_theme_materializes_the_root_without_a_deferred_host() =>
        RunHeadlessAsync(() =>
        {
            var resources = new WorkspaceView().Resources;
            var theme = Assert.IsType<ControlTheme>(resources["RuntimeDockControlTheme"]);
            var tab = NewTab();
            var canvas = new RuntimeDockControl
            {
                Theme = theme,
                RuntimeTab = tab,
                InitializeFactory = true,
                InitializeLayout = false,
            };
            var window = new Window
            {
                Width = 800,
                Height = 600,
                Content = canvas,
            };
            foreach (var template in new MainWindow().DataTemplates)
            {
                window.DataTemplates.Add(template);
            }

            window.Show();
            try
            {
                canvas.ApplyTemplate();
                window.UpdateLayout();

                var contentHost = Assert.Single(
                    canvas.GetVisualDescendants().OfType<ContentControl>(),
                    control => string.Equals(control.Name, "PART_ContentControl", StringComparison.Ordinal));
                Assert.IsType<ContentControl>(contentHost);
                Assert.Contains(canvas.GetVisualDescendants(), visual => visual is RootDockControl);
                var rootContentHost = Assert.Single(
                    canvas.GetVisualDescendants().OfType<ContentControl>(),
                    control => string.Equals(control.Name, "PART_MainContent", StringComparison.Ordinal));
                Assert.IsType<ContentControl>(rootContentHost);
                var panelContentHost = Assert.Single(
                    canvas.GetVisualDescendants().OfType<ContentControl>(),
                    control => string.Equals(control.Name, "PART_ContentPresenter", StringComparison.Ordinal));
                Assert.IsType<RuntimePanelContentControl>(panelContentHost);
                Assert.Same(tab.ActivePanel, panelContentHost.Content);
                Assert.Contains(
                    canvas.GetVisualDescendants(),
                    visual => visual is UnavailableRuntimePanelView);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    [Fact]
    public Task Closing_the_right_branch_expands_the_surviving_nested_column() =>
        RunHeadlessAsync(() =>
        {
            var tab = NewTab();
            var upperLeft = Assert.IsType<UnavailableRuntimePanelViewModel>(tab.ActivePanel);
            var right = Panel("right-panel", "Right");
            Assert.True(tab.SplitActivePanel(right, PanelSplitOrientation.LeftRight));
            Assert.True(tab.ActivatePanel(upperLeft.Id));
            var lowerLeft = Panel("lower-left-panel", "Lower left");
            Assert.True(tab.SplitActivePanel(lowerLeft, PanelSplitOrientation.TopBottom));

            var canvas = new RuntimeDockControl
            {
                Theme = Assert.IsType<ControlTheme>(
                    new WorkspaceView().Resources["RuntimeDockControlTheme"]),
                RuntimeTab = tab,
                InitializeFactory = true,
                InitializeLayout = false,
            };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = canvas,
            };
            foreach (var template in new MainWindow().DataTemplates)
            {
                window.DataTemplates.Add(template);
            }

            window.Show();
            try
            {
                canvas.ApplyTemplate();
                window.UpdateLayout();

                var widthBeforeClose = PanelHost(canvas, upperLeft).Bounds.Width;
                Assert.InRange(widthBeforeClose, 500, 700);

                Assert.True(tab.RemovePanel(right.Id));
                window.UpdateLayout();

                var upperHost = PanelHost(canvas, upperLeft);
                var lowerHost = PanelHost(canvas, lowerLeft);
                Assert.InRange(upperHost.Bounds.Width, 1_100, 1_200);
                Assert.InRange(lowerHost.Bounds.Width, 1_100, 1_200);
                Assert.DoesNotContain(
                    canvas.GetVisualDescendants().OfType<RuntimePanelContentControl>(),
                    host => ReferenceEquals(host.Content, right));
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    [Fact]
    public Task Closing_a_nested_right_column_then_adding_right_leaves_no_gap() =>
        RunHeadlessAsync(() =>
        {
            var tab = NewTab();
            var upperLeft = Assert.IsType<UnavailableRuntimePanelViewModel>(tab.ActivePanel);
            var lowerLeft = Panel("nested-gap-lower-left", "Lower left");
            Assert.True(tab.SplitActivePanel(lowerLeft, PanelSplitOrientation.TopBottom));

            var rightPlaceholder = tab.AddPlaceholder(PanelSide.Right);
            tab.ReplaceTarget = rightPlaceholder.Id;
            var upperRight = Panel("nested-gap-upper-right", "Upper right");
            tab.AddPanel(upperRight);
            var lowerRight = Panel("nested-gap-lower-right", "Lower right");
            Assert.True(tab.SplitActivePanel(lowerRight, PanelSplitOrientation.TopBottom));
            // Dock restores saved proportional containers as non-collapsible.
            foreach (var dock in EnumerateDockables(tab.DockLayout).OfType<IProportionalDock>())
            {
                dock.IsCollapsable = false;
            }

            var canvas = new RuntimeDockControl
            {
                Theme = Assert.IsType<ControlTheme>(
                    new WorkspaceView().Resources["RuntimeDockControlTheme"]),
                RuntimeTab = tab,
                InitializeFactory = true,
                InitializeLayout = false,
            };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = canvas,
            };
            foreach (var template in new MainWindow().DataTemplates)
            {
                window.DataTemplates.Add(template);
            }

            window.Show();
            try
            {
                canvas.ApplyTemplate();
                window.UpdateLayout();

                Assert.True(tab.RemovePanel(lowerRight.Id));
                Assert.True(tab.RemovePanel(upperRight.Id));
                var replacementPlaceholder = tab.AddPlaceholder(PanelSide.Right);
                tab.ReplaceTarget = replacementPlaceholder.Id;
                var replacementRight = Panel("nested-gap-replacement-right", "Replacement right");
                tab.AddPanel(replacementRight);
                window.UpdateLayout();

                var leftHost = PanelHost(canvas, upperLeft);
                var lowerLeftHost = PanelHost(canvas, lowerLeft);
                var rightHost = PanelHost(canvas, replacementRight);
                var leftOrigin = Assert.NotNull(
                    Avalonia.VisualExtensions.TranslatePoint(leftHost, default, canvas));
                var lowerLeftOrigin = Assert.NotNull(
                    Avalonia.VisualExtensions.TranslatePoint(lowerLeftHost, default, canvas));
                var rightOrigin = Assert.NotNull(
                    Avalonia.VisualExtensions.TranslatePoint(rightHost, default, canvas));
                var gap = rightOrigin.X - (leftOrigin.X + leftHost.Bounds.Width);
                Assert.Equal(
                    3,
                    canvas.GetVisualDescendants()
                        .OfType<RuntimePanelContentControl>()
                        .Count());
                Assert.DoesNotContain(
                    canvas.GetVisualDescendants().OfType<RuntimePanelContentControl>(),
                    host => ReferenceEquals(host.Content, upperRight)
                        || ReferenceEquals(host.Content, lowerRight));
                Assert.InRange(Math.Abs(leftOrigin.X - lowerLeftOrigin.X), 0, 1);
                Assert.InRange(
                    Math.Abs(leftHost.Bounds.Width - lowerLeftHost.Bounds.Width),
                    0,
                    1);
                Assert.InRange(gap, 0, 12);
                Assert.True(leftHost.Bounds.Width > canvas.Bounds.Width * 0.35);
                Assert.True(rightHost.Bounds.Width > canvas.Bounds.Width * 0.35);
                Assert.True(
                    leftHost.Bounds.Width + rightHost.Bounds.Width
                    >= canvas.Bounds.Width - 32);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    private static RuntimePanelContentControl PanelHost(
        RuntimeDockControl canvas,
        RuntimePanelViewModel panel) =>
        Assert.Single(
            canvas.GetVisualDescendants().OfType<RuntimePanelContentControl>(),
            host => ReferenceEquals(host.Content, panel));

    private static UnavailableRuntimePanelViewModel Panel(string id, string title) =>
        new(
            new PanelInstanceId(id),
            PanelKind.Terminal,
            title,
            "LOCAL",
            "Unavailable in this test.");

    private static RuntimeTabViewModel NewTab()
    {
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Local terminal",
            "test");
        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            PanelInstanceId.New(),
            PanelKind.Terminal,
            "Local terminal",
            "LOCAL",
            "Unavailable in this test."));
        return tab;
    }

    private static IEnumerable<IDockable> EnumerateDockables(IDockable root)
    {
        var pending = new Stack<IDockable>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (current is IDock { VisibleDockables: { } children })
            {
                foreach (var child in children)
                {
                    pending.Push(child);
                }
            }
        }
    }

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
}

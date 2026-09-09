using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.App.Views.RuntimePanels;
using Asura.Application;
using Asura.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class RedisRuntimePanelViewHeadlessTests
{
    [Fact]
    public Task ConnectionButtonsKeepTheirLabelsInsideTheToolbarHeight() =>
        RunHeadlessAsync(() =>
        {
            using var panel = new RedisRuntimePanelViewModel(
                PanelInstanceId.New(),
                "Redis",
                new UnusedSessionFactory(),
                new RedisCatalogStub());
            var view = new RedisRuntimePanelView { DataContext = panel };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                var connect = view.FindControl<Button>("ConnectButton")!;
                var disconnect = view.FindControl<Button>("DisconnectButton")!;
                disconnect.IsVisible = true;
                window.UpdateLayout();

                AssertButtonContentFits(connect);
                AssertButtonContentFits(disconnect);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// Every value table in the shell is the same hairline table. The Redis
    /// grids used to be stock Fluent grids beside the database viewer's, which
    /// is two different products in one panel; the chrome now lives in the
    /// theme, and this is the check that it reaches here.
    /// </summary>
    [Fact]
    public Task ValueTablesWearTheSharedDatabaseGridChrome() =>
        RunHeadlessAsync(() =>
        {
            using var panel = NewPanel();
            // The message table is the one value table a panel with no session
            // still realises, so it is the one this fixture can measure.
            panel.Perspective = RedisWorkspacePerspective.PubSub;
            var view = new RedisRuntimePanelView { DataContext = panel };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                var grids = view.GetVisualDescendants().OfType<DataGrid>().ToArray();
                Assert.NotEmpty(grids);
                foreach (var grid in grids)
                {
                    Assert.Contains("DatabaseGrid", grid.Classes, StringComparer.Ordinal);
                    Assert.Equal(28, grid.ColumnHeaderHeight);
                    Assert.Equal(30, grid.RowHeight);
                    Assert.Equal(DataGridGridLinesVisibility.All, grid.GridLinesVisibility);
                }
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// A panel with nothing in it says so. Before this, an unconnected Redis
    /// panel showed an empty list beside an empty table with no explanation.
    /// </summary>
    [Fact]
    public Task EmptySurfacesExplainThemselves() =>
        RunHeadlessAsync(() =>
        {
            using var panel = NewPanel();
            var view = new RedisRuntimePanelView { DataContext = panel };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.False(panel.HasKeys);
                var states = view
                    .GetVisualDescendants()
                    .OfType<EmptyStatePanel>()
                    .Where(state => state.IsVisible)
                    .Select(state => state.Heading)
                    .ToArray();
                Assert.Contains("No keys yet", states, StringComparer.Ordinal);
                Assert.Contains("Select a Redis key", states, StringComparer.Ordinal);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// The create-key form is a sheet inside the panel, opened from the list's
    /// own toolbar. It has to open, its fields have to be laid out, and — the
    /// reason it stopped being a flyout — it has to stay within the panel it
    /// belongs to rather than being drawn as a window of its own.
    /// </summary>
    [Fact]
    public Task TheCreateKeySheetOpensWithItsFieldsLaidOut() =>
        RunHeadlessAsync(() =>
        {
            using var panel = NewPanel();
            var view = new RedisRuntimePanelView { DataContext = panel };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                panel.BeginCreateKey();
                window.UpdateLayout();

                var sheet = view.FindControl<SurfaceCard>("NewKeySheet")!;
                var fields = sheet
                    .GetVisualDescendants()
                    .OfType<LabeledField>()
                    .Where(field => field.IsVisible)
                    .ToArray();
                // A string needs no field or score, and every key can be given
                // a deadline as it is created.
                Assert.Equal(
                    ["Key", "Type", "Value", "TTL seconds"],
                    fields.Select(field => field.Label), StringComparer.Ordinal);
                foreach (var field in fields)
                {
                    Assert.True(
                        field.Bounds.Width > 0 && field.Bounds.Height > 0,
                        $"The {field.Label} field was not laid out: {field.Bounds}.");
                }

                var create = Assert.Single(
                    sheet.GetVisualDescendants().OfType<Button>(),
                    button => Equals(button.Content, "Create key"));
                Assert.True(create.Bounds.Width > 0);

                // The sheet is inside the panel: a popup could be drawn outside
                // the window, and was.
                var corner = sheet.TranslatePoint(default, view);
                Assert.NotNull(corner);
                Assert.InRange(corner.Value.X, 0, view.Bounds.Width - sheet.Bounds.Width);
                Assert.InRange(corner.Value.Y, 0, view.Bounds.Height - sheet.Bounds.Height);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });



    /// <summary>
    /// Corners step down with depth. A card sitting inside the panel takes a
    /// tighter corner than the panel's, and the controls inside the card take
    /// a tighter one again — nested rounded rectangles read as one surface only
    /// when the inner curve is the outer one less the distance between them.
    /// </summary>
    [Fact]
    public Task CornersTightenWithEveryNesting() =>
        RunHeadlessAsync(async () =>
        {
            using var panel = RedisPanelFixtures.Panel(
                new RedisPanelFixtures.StubSession(null, type: "hash"));
            await panel.Initialization;
            panel.SelectedKey = panel.Keys[0];
            panel.SelectedValueEntry = panel.ValueEntries[0];

            var view = new RedisRuntimePanelView { DataContext = panel };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                // The rule reconciles on layout, so it lands a pass later than
                // the first arrange.
                window.UpdateLayout();
                window.UpdateLayout();

                var card = view
                    .GetVisualDescendants()
                    .OfType<SurfaceCard>()
                    .First(surface => surface.IsVisible
                        && surface.GetVisualDescendants().OfType<TextBox>().Any());
                var input = card.GetVisualDescendants().OfType<TextBox>().First();

                Assert.True(
                    card.CornerRadius.TopLeft > input.CornerRadius.TopLeft,
                    $"A card at {card.CornerRadius.TopLeft} holds inputs at "
                    + $"{input.CornerRadius.TopLeft}: the inner corner must be the tighter one.");
                Assert.True(input.CornerRadius.TopLeft > 0);
            }
            finally
            {
                window.Close();
            }
        });





    /// <summary>
    /// A string has nothing to add to, so its one form takes the width. A Grid
    /// spaces its columns whether or not one of them holds anything, which left
    /// a column's worth of air down the right of every whole-value key.
    /// </summary>
    [Theory]
    [InlineData("string", false)]
    [InlineData("hash", true)]
    public Task TheValueFormsFillTheirPane(string type, bool hasAddForm) =>
        RunHeadlessAsync(async () =>
        {
            using var panel = RedisPanelFixtures.Panel(new RedisPanelFixtures.StubSession(null, type: type));
            await panel.Initialization;
            panel.SelectedKey = panel.Keys[0];
            panel.SelectedValueEntry = panel.ValueEntries[0];
            Assert.Equal(hasAddForm, panel.MutationForm.HasAddForm);

            var view = new RedisRuntimePanelView { DataContext = panel };
            var window = new Window
            {
                Width = 1_400,
                Height = 950,
                Content = view,
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                var cards = view
                    .GetVisualDescendants()
                    .OfType<SurfaceCard>()
                    .Where(card => card.IsVisible && card.Bounds.Width > 0)
                    .ToArray();
                // The panel is a card too, so the table's own card is the
                // smallest one holding it.
                var table = cards
                    .Where(card => card.GetVisualDescendants().OfType<DataGrid>().Any())
                    .OrderBy(card => card.Bounds.Width)
                    .First();
                var forms = cards
                    // The panel is a card holding everything, including these.
                    .Where(card => !card.GetVisualDescendants().OfType<DataGrid>().Any())
                    .Where(card => card.GetVisualDescendants().OfType<Button>().Any(
                        button => Equals(button.Content, "Set value")
                            || Equals(button.Content, "Save field")
                            || Equals(button.Content, "Add fields")))
                    .ToArray();

                // Whatever the type, the forms span the same width the table
                // above them does.
                var right = forms.Max(card =>
                    card.TranslatePoint(new Point(card.Bounds.Width, 0), view)!.Value.X);
                var tableRight = table.TranslatePoint(new Point(table.Bounds.Width, 0), view)!.Value.X;
                Assert.InRange(right, tableRight - 1, tableRight + 1);
            }
            finally
            {
                window.Close();
            }
        });

    private static RedisRuntimePanelViewModel NewPanel() =>
        new(
            PanelInstanceId.New(),
            "Redis",
            new UnusedSessionFactory(),
            new RedisCatalogStub());

    private static void AssertButtonContentFits(Button button)
    {
        Assert.Equal(26, button.Bounds.Height);
        Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);
        var content = Assert.Single(
            button.GetVisualDescendants().OfType<ContentPresenter>(),
            presenter => Equals(presenter.Content, button.Content));
        var top = content.TranslatePoint(default, button);
        Assert.NotNull(top);
        Assert.InRange(top.Value.Y, 0, button.Bounds.Height);
        Assert.InRange(
            top.Value.Y + content.Bounds.Height,
            0,
            button.Bounds.Height);
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

    private sealed class UnusedSessionFactory : IRedisPanelSessionFactory
    {
        public Task<IRedisPanelSession> OpenAsync(
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This layout fixture has no connection target.");
    }

    private sealed class RedisCatalogStub : IDatabaseConnectionCatalog
    {
        public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
            [RedisDatabase.Descriptor];

        public DatabaseConnectionDetails ParseConnectionDetails(
            string driverId,
            string connectionString) =>
            new("localhost", 6379);

        public string BuildConnectionString(
            string driverId,
            DatabaseConnectionDetails details) =>
            "localhost:6379";
    }
}

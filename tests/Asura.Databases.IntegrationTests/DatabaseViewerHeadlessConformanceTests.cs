using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Asura.App.Views.RuntimePanels;
using Asura.Application;
using Asura.Core;
using Asura.Infrastructure;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.CodeCompletion;

namespace Asura.Databases.IntegrationTests;

public sealed partial class DatabaseViewerConformanceTests
{
    private static async Task AssertHeadlessViewJourneyAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        CancellationToken cancellationToken)
    {
        ISqlLanguageService sqlLanguageService = new CalciteSqlLanguageService();
        var nativeWorkerRequired = string.Equals(
            Environment.GetEnvironmentVariable("ASURA_RUN_SQL_LANGUAGE_NATIVE"),
            "1",
            StringComparison.Ordinal);
        Assert.True(
            !nativeWorkerRequired || sqlLanguageService.IsAvailable,
            "Rendered native SQL coverage was required, but "
                + "ASURA_SQL_LANGUAGE_WORKER could not be resolved.");
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            $"{environment.Provider.DisplayName} headless view conformance",
            client,
            driverId: environment.Provider.Id,
            connectionString: environment.ConnectionString,
            sqlLanguageService: sqlLanguageService);
        await AwaitPanelOperationAsync(panel, panel.Initialization, cancellationToken);
        await AwaitPanelOperationAsync(
            panel,
            panel.SqlLanguageInitialization,
            cancellationToken);

        await RunHeadlessFixtureSessionAsync(
            panel,
            environment.Provider.Seed.RowsTable,
            async fixture =>
            {
                Assert.True(panel.IsDatabaseObjectsOverview);
                Assert.True(fixture.ObjectsOverviewButton.IsEffectivelyVisible);
                Assert.True(fixture.ObjectsOverviewButton.IsChecked);
                if (sqlLanguageService.IsAvailable)
                {
                    await AssertRenderedSqlIntelligenceAsync(
                        client,
                        environment,
                        objects,
                        panel,
                        fixture,
                        cancellationToken);
                }
                fixture.OpenObject(environment.Provider.Seed.RowsTable);
                await WaitOnDispatcherAsync(
                    () => !panel.IsBusy
                        && string.Equals(panel.SelectedObject?.Descriptor.Name
, environment.Provider.Seed.RowsTable
, StringComparison.Ordinal) && panel.ResultRows.Count > 0,
                    cancellationToken);

                fixture.AssertResultColumns();
                fixture.AssertRunButtonBinding();
                fixture.AssertAllWorkspaceModes(environment.Provider.Expectations.HasIndexes);
                await AssertHeadlessFilterAndPagingAsync(
                    panel,
                    fixture,
                    cancellationToken);
                if (environment.Provider.Expectations.CanEdit)
                {
                    await AssertEditableHeadlessJourneyAsync(
                        client,
                        environment,
                        objects,
                        panel,
                        fixture,
                        cancellationToken);
                }
                else
                {
                    await AssertReadOnlyHeadlessJourneyAsync(
                        panel,
                        fixture);
                }
            },
            cancellationToken);

        await RunHeadlessFixtureSessionAsync(
            panel,
            environment.Provider.Seed.RowsTable,
            async fixture =>
            {
                fixture.OpenObject(environment.Provider.Seed.RowsTable);
                await WaitForSelectedObjectAsync(
                    panel,
                    environment.Provider.Seed.RowsTable,
                    cancellationToken);
                await AssertHeadlessColumnSortingAsync(
                    environment,
                    panel,
                    fixture,
                    cancellationToken);
                await AssertHeadlessRawQueryJourneyAsync(
                    client,
                    environment,
                    objects,
                    panel,
                    fixture,
                    cancellationToken);
            },
            cancellationToken);

        // Popup visuals are hosted outside the DataGrid tree. Isolate the raw
        // arbitrary-result context menu from the preceding runtime column-shape
        // replacement so its teardown cannot poison a following header layout.
        await RunHeadlessFixtureSessionAsync(
            panel,
            environment.Provider.Seed.RowsTable,
            fixture =>
            {
                Assert.Null(panel.SelectedObject);
                Assert.False(panel.CanEditRows);
                fixture.AssertRawResultControlsVisibleAndReadOnly();
                fixture.OpenCellContextMenu(FindRow(panel, "alpha"), "code");
                fixture.AssertContextMenuCapabilities(canEdit: false);
                fixture.CloseContextMenu();
                return Task.CompletedTask;
            },
            cancellationToken);

        await RunHeadlessFixtureSessionAsync(
            panel,
            environment.Provider.Seed.RowsTable,
            async fixture =>
            {
                fixture.OpenObject(environment.Provider.Seed.RowsTable);
                await WaitForSelectedObjectAsync(
                    panel,
                    environment.Provider.Seed.RowsTable,
                    cancellationToken);
                await AssertHeadlessContextMenuReadJourneyAsync(
                    environment,
                    panel,
                    fixture,
                    cancellationToken);
                await AssertReadOnlyContextMenusAsync(
                    environment,
                    panel,
                    fixture,
                    cancellationToken);
            },
            cancellationToken);

        if (environment.Provider.Expectations.CanEdit)
        {
            await AssertHeadlessContextMenuDuplicateAsync(
                client,
                environment,
                objects,
                panel,
                cancellationToken);
            await AssertHeadlessMutationControlsAsync(
                client,
                environment,
                objects,
                panel,
                cancellationToken);
        }

        // The diagram owns a separate source surface. Exercise it after row
        // interactions so this journey ends in the same database-overview
        // state a person deliberately selected.
        await RunHeadlessFixtureSessionAsync(
            panel,
            environment.Provider.Seed.RowsTable,
            fixture => AssertHeadlessDatabaseDiagramAsync(panel, fixture, cancellationToken),
            cancellationToken);
    }

    private static async Task AssertRenderedSqlIntelligenceAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CancellationToken cancellationToken)
    {
        var editor = fixture.QueryEditor;
        Assert.True(panel.HasSqlLanguageSession, panel.SqlLanguageStatus);
        Assert.Same(panel.SqlLanguageSession, editor.SqlLanguageSession);
        Assert.Equal(TimeSpan.FromMilliseconds(20), editor.CompletionDebounceForTesting);
        editor.DiagnosticDebounceForTesting = TimeSpan.Zero;
        await AssertRenderedKeywordInsertionAsync(
            fixture,
            editor,
            cancellationToken);

        var quote = BuiltInDatabaseDrivers.All
            .Single(driver => string.Equals(driver.Descriptor.Id, environment.Provider.Id, StringComparison.Ordinal))
            .QuoteIdentifier;
        var catalog = await client.GetSqlCatalogAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            cancellationToken);
        var table = Assert.Single(catalog.Objects, item => string.Equals(item.Id.Name, objects.Rows.Name, StringComparison.Ordinal) && item.Kind == DatabaseTableKind.Table);
        var keylessTable = Assert.Single(catalog.Objects, item => string.Equals(item.Id.Name, objects.Keyless.Name
, StringComparison.Ordinal) && item.Kind == objects.Keyless.Kind);
        await AssertRenderedSelectedObjectSqlContextAsync(
            environment,
            panel,
            fixture,
            editor,
            catalog,
            table,
            keylessTable,
            quote,
            cancellationToken);

        var objectName = catalog.DefaultCatalog is not null || catalog.DefaultSchema is not null
            ? quote(table.Id.Name)
            : QuoteSqlLanguageObject(catalog.DriverId, table.Id);
        const string alias = SqlLanguageAlias;
        var completionSql = $"SELECT {alias} FROM {objectName} {alias}";
        editor.Text = completionSql;
        editor.FocusEditor();
        editor.EditorForTesting.CaretOffset =
            completionSql.IndexOf(alias, StringComparison.Ordinal) + alias.Length;
        fixture.Window.KeyTextInput(".");
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);

        var completionWindow = Assert.IsType<CompletionWindow>(
            editor.ActiveCompletionWindowForTesting);
        var completionItems = completionWindow.CompletionList.CompletionData
            .Cast<SqlCompletionData>()
            .ToArray();
        Assert.Contains(completionItems, item => string.Equals(item.Label, "id", StringComparison.Ordinal));
        Assert.Contains(completionItems, item => string.Equals(item.Label, "title", StringComparison.Ordinal));
        completionWindow.Hide();

        var clauseSql = $"SELECT * FROM {objectName} ";
        editor.Text = clauseSql;
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput("W");
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);

        var clauseCompletion = Assert.IsType<CompletionWindow>(
            editor.ActiveCompletionWindowForTesting);
        Assert.Contains(
            clauseCompletion.CompletionList.CompletionData.Cast<SqlCompletionData>(),
            item => item.Kind == SqlCompletionItemKind.Keyword && string.Equals(item.Label, "WHERE", StringComparison.Ordinal));
        clauseCompletion.Hide();

        var expressionSql = $"SELECT * FROM {objectName} WHERE ";
        editor.Text = expressionSql;
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput("t");
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);

        var expressionCompletion = Assert.IsType<CompletionWindow>(
            editor.ActiveCompletionWindowForTesting);
        Assert.Contains(
            expressionCompletion.CompletionList.CompletionData.Cast<SqlCompletionData>(),
            item => item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "title", StringComparison.Ordinal));
        Assert.DoesNotContain(
            expressionCompletion.CompletionList.CompletionData.Cast<SqlCompletionData>(),
            item => item.Kind is SqlCompletionItemKind.Catalog
                or SqlCompletionItemKind.Schema
                or SqlCompletionItemKind.Table
                or SqlCompletionItemKind.View);
        expressionCompletion.Hide();

        await AssertRenderedValueAndFunctionInsertionAsync(
            panel,
            fixture,
            editor,
            catalog,
            table,
            objectName,
            quote,
            cancellationToken);
        if (string.Equals(catalog.DriverId, "postgres", StringComparison.Ordinal))
        {
            await AssertRenderedPostgresRoutineInsertionAsync(
                panel,
                fixture,
                editor,
                table,
                objectName,
                quote,
                cancellationToken);
        }

        const string missingColumn = "asura_rendered_column_that_does_not_exist";
        var invalidSql = $"SELECT {alias}.{quote(missingColumn)} "
            + $"FROM {objectName} {alias}";
        editor.Text = invalidSql;
        await editor.PendingDiagnosticsForTesting.WaitAsync(cancellationToken);

        var diagnostic = Assert.Single(editor.Diagnostics, item =>
            item.Severity == SqlDiagnosticSeverity.Error
            && item.Message.Contains(missingColumn, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("1 error", editor.DiagnosticStatus);
        Assert.True(editor.IsDiagnosticRendererAttachedForTesting);
        var renderedSegment = SqlDiagnosticBackgroundRenderer.CreateVisibleSegment(
            diagnostic,
            editor.Text!.Length);
        Assert.NotNull(renderedSegment);
        Assert.Equal(diagnostic.Start, renderedSegment!.StartOffset);
        Assert.Equal(diagnostic.Length, renderedSegment.Length);

        var runnableSql = client.BuildTablePreviewQuery(
            environment.Provider.Id,
            objects.Rows.Id,
            limit: 1);
        editor.Text = runnableSql;
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyPress(
            Key.Enter,
            RawInputModifiers.Control,
            PhysicalKey.Enter,
            keySymbol: null);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy && panel.ResultRows.Count == 1,
            cancellationToken);
        Assert.Equal(runnableSql, panel.QueryText);
        Assert.False(panel.HasError, panel.ErrorMessage);
    }

    private static async Task AssertRenderedKeywordInsertionAsync(
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        CancellationToken cancellationToken)
    {
        editor.Text = string.Empty;
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput("s");
        fixture.Window.KeyTextInput("e");
        fixture.Window.KeyTextInput("l");
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);

        AcceptRenderedCompletion(
            fixture,
            editor,
            replacementStart: 0,
            replacementLength: 3,
            "SELECT",
            SqlCompletionItemKind.Keyword,
            "select");
        Assert.Equal("select", editor.Text);
        Assert.Null(editor.ActiveCompletionWindowForTesting);

        editor.Text = "SELECT * ";
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput("f");
        fixture.Window.KeyTextInput("r");
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);

        AcceptRenderedCompletion(
            fixture,
            editor,
            replacementStart: "SELECT * ".Length,
            replacementLength: 2,
            "FROM",
            SqlCompletionItemKind.Keyword,
            "from");
        Assert.Equal("SELECT * from", editor.Text);
        Assert.Null(editor.ActiveCompletionWindowForTesting);
    }

    private static async Task AssertRenderedValueAndFunctionInsertionAsync(
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        SqlCatalogSnapshot catalog,
        SqlCatalogObject table,
        string objectName,
        Func<string, string> quote,
        CancellationToken cancellationToken)
    {
        var timestampColumn = Assert.Single(
            table.Columns,
            column => string.Equals(column.Name, "created_at", StringComparison.Ordinal));
        Assert.True(
            timestampColumn.ValueKind is DatabaseValueKind.Timestamp
                or DatabaseValueKind.TimestampWithZone,
            $"Expected created_at to be a timestamp, got {timestampColumn.ValueKind} "
                + $"({timestampColumn.DataTypeName}).");
        var expressionPrefix =
            $"SELECT * FROM {objectName} WHERE {quote(timestampColumn.Name)} < ";

        editor.Text = $"{expressionPrefix}cu";
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput("r");
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);
        var valueCompletionWindow = editor.ActiveCompletionWindowForTesting;
        var valueItems = valueCompletionWindow?.CompletionList.CompletionData
            .Cast<SqlCompletionData>()
            .Where(item => item.Label is
                "CURRENT_DATE" or "CURRENT_TIME" or "CURRENT_TIMESTAMP")
            .ToArray() ?? [];
        Assert.All(valueItems, item =>
        {
            Assert.Equal(SqlCompletionItemKind.Keyword, item.Kind);
            Assert.Equal(item.Label, item.InsertText);
            Assert.Contains(catalog.IntrinsicSymbols, symbol =>
                symbol.Name.Equals(item.Label, StringComparison.OrdinalIgnoreCase));
        });
        var hasCurrentTimestampEvidence = catalog.IntrinsicSymbols.Any(symbol =>
            symbol.Name.Equals(
                "CURRENT_TIMESTAMP",
                StringComparison.OrdinalIgnoreCase));
        if (!hasCurrentTimestampEvidence)
        {
            Assert.DoesNotContain(
                valueItems,
                item => string.Equals(item.Label, "CURRENT_TIMESTAMP", StringComparison.Ordinal));
            valueCompletionWindow?.Hide();
        }
        else
        {
            Assert.Contains(valueItems, item => string.Equals(item.Label, "CURRENT_TIMESTAMP", StringComparison.Ordinal));
            AcceptRenderedCompletion(
                fixture,
                editor,
                expressionPrefix.Length,
                replacementLength: 3,
                "CURRENT_TIMESTAMP",
                SqlCompletionItemKind.Keyword,
                "CURRENT_TIMESTAMP");
            var currentTimestampSql = $"{expressionPrefix}CURRENT_TIMESTAMP";
            Assert.Equal(currentTimestampSql, editor.Text);
            Assert.Equal(editor.Text.Length, editor.EditorForTesting.CaretOffset);
            await AssertRenderedQueryRunsAsync(
                panel,
                fixture,
                editor,
                currentTimestampSql,
                cancellationToken);
        }

        foreach (var valueItem in valueItems.Where(item => !string.Equals(item.Label, "CURRENT_TIMESTAMP", StringComparison.Ordinal)))
        {
            await AssertRenderedQueryRunsAsync(
                panel,
                fixture,
                editor,
                $"SELECT {valueItem.InsertText} FROM {objectName}",
                cancellationToken);
        }

        var hasCallableFunctionEvidence = catalog.DriverId is not ("sqlserver" or "redshift");
        if (hasCallableFunctionEvidence)
        {
            await AssertRenderedFunctionInsertionAsync(
                fixture,
                editor,
                expressionPrefix,
                "co",
                "COALESCE",
                "COALESCE(",
                cancellationToken);
            await AssertRenderedFunctionInsertionAsync(
                fixture,
                editor,
                "SELECT ",
                "cou",
                "COUNT",
                "COUNT(",
                cancellationToken);
        }
        else
        {
            await AssertRenderedFunctionAvailabilityAsync(
                fixture,
                editor,
                $"{expressionPrefix}co",
                "COALESCE",
                expected: false,
                cancellationToken);
            await AssertRenderedFunctionAvailabilityAsync(
                fixture,
                editor,
                "SELECT cou",
                "COUNT",
                expected: false,
                cancellationToken);
        }

        await AssertRenderedFunctionAvailabilityAsync(
            fixture,
            editor,
            $"SELECT {quote(timestampColumn.Name)} FROM {objectName} "
                + $"GROUP BY {quote(timestampColumn.Name)} HAVING cou",
            "COUNT",
            expected: hasCallableFunctionEvidence,
            cancellationToken);
        await AssertRenderedFunctionAvailabilityAsync(
            fixture,
            editor,
            $"SELECT * FROM {objectName} ORDER BY cou",
            "COUNT",
            expected: hasCallableFunctionEvidence,
            cancellationToken);
        await AssertRenderedFunctionAvailabilityAsync(
            fixture,
            editor,
            $"SELECT * FROM {objectName} WHERE cou",
            "COUNT",
            expected: false,
            cancellationToken);
        await AssertRenderedFunctionAvailabilityAsync(
            fixture,
            editor,
            $"SELECT * FROM {objectName} {SqlLanguageAlias} "
                + $"JOIN {objectName} joined ON cou",
            "COUNT",
            expected: false,
            cancellationToken);
        await AssertRenderedFunctionAvailabilityAsync(
            fixture,
            editor,
            $"SELECT {quote(timestampColumn.Name)} FROM {objectName} GROUP BY cou",
            "COUNT",
            expected: false,
            cancellationToken);

        await AssertNoRenderedValueOrFunctionCompletionAsync(
            fixture,
            editor,
            "SELECT * FROM fu",
            cancellationToken);
        await AssertNoRenderedValueOrFunctionCompletionAsync(
            fixture,
            editor,
            $"SELECT * FROM {objectName} {SqlLanguageAlias} "
                + "WHERE missing.fu",
            cancellationToken);
    }

    private static async Task AssertRenderedPostgresRoutineInsertionAsync(
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        SqlCatalogObject table,
        string objectName,
        Func<string, string> quote,
        CancellationToken cancellationToken)
    {
        var timestampColumn = Assert.Single(table.Columns, column => string.Equals(column.Name, "created_at", StringComparison.Ordinal));
        var expressionPrefix = $"SELECT * FROM {objectName} WHERE "
            + $"{quote(timestampColumn.Name)} < ";

        editor.Text = $"{expressionPrefix}date";
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput("_");
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);
        AcceptRenderedCompletion(
            fixture,
            editor,
            expressionPrefix.Length,
            replacementLength: 5,
            "date_add",
            SqlCompletionItemKind.Function,
            "date_add(");
        Assert.Equal($"{expressionPrefix}date_add(", editor.Text);

        var typePrefix = $"{expressionPrefix}"
            + "date_add(CURRENT_TIMESTAMP, '1 day'::in";
        editor.Text = typePrefix;
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput("t");
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);
        AcceptRenderedCompletion(
            fixture,
            editor,
            typePrefix.Length - 2,
            replacementLength: 3,
            "INTERVAL",
            SqlCompletionItemKind.DataType,
            "INTERVAL");

        var completedSql = $"{editor.Text})";
        editor.Text = completedSql;
        await editor.PendingDiagnosticsForTesting.WaitAsync(cancellationToken);
        Assert.DoesNotContain(editor.Diagnostics, diagnostic =>
            diagnostic.Severity == SqlDiagnosticSeverity.Error);
        Assert.Equal(
            $"{expressionPrefix}date_add(CURRENT_TIMESTAMP, '1 day'::INTERVAL)",
            completedSql);
        await AssertRenderedQueryRunsAsync(
            panel,
            fixture,
            editor,
            completedSql,
            cancellationToken);
    }

    private static async Task AssertRenderedQueryRunsAsync(
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        string sql,
        CancellationToken cancellationToken)
    {
        editor.Text = sql;
        editor.FocusEditor(caretToEnd: true);
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedBusy = false;
        void ObserveBusy(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            _ = sender;
            if (!string.Equals(args.PropertyName, nameof(panel.IsBusy), StringComparison.Ordinal))
            {
                return;
            }

            if (panel.IsBusy)
            {
                observedBusy = true;
            }
            else if (observedBusy)
            {
                completed.TrySetResult();
            }
        }

        panel.PropertyChanged += ObserveBusy;
        try
        {
            fixture.Window.KeyPress(
                Key.Enter,
                RawInputModifiers.Control,
                PhysicalKey.Enter,
                keySymbol: null);
            await completed.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            panel.PropertyChanged -= ObserveBusy;
        }

        Assert.Equal(sql, panel.QueryText);
        Assert.False(panel.HasError, panel.ErrorMessage);
    }

    private static async Task AssertRenderedFunctionInsertionAsync(
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        string expressionPrefix,
        string typedPrefix,
        string label,
        string insertText,
        CancellationToken cancellationToken)
    {
        editor.Text = $"{expressionPrefix}{typedPrefix[..^1]}";
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput(typedPrefix[^1].ToString());
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);
        AcceptRenderedCompletion(
            fixture,
            editor,
            expressionPrefix.Length,
            typedPrefix.Length,
            label,
            SqlCompletionItemKind.Function,
            insertText);
        Assert.Equal($"{expressionPrefix}{insertText}", editor.Text);
        Assert.Equal(editor.Text.Length, editor.EditorForTesting.CaretOffset);
    }

    private static async Task AssertRenderedFunctionAvailabilityAsync(
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        string sql,
        string expectedLabel,
        bool expected,
        CancellationToken cancellationToken)
    {
        editor.Text = sql[..^1];
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput(sql[^1].ToString());
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);

        var completionWindow = editor.ActiveCompletionWindowForTesting;
        var items = completionWindow?.CompletionList.CompletionData
            .Cast<SqlCompletionData>()
            .ToArray() ?? [];
        if (expected)
        {
            Assert.Contains(items, item => string.Equals(item.Label, expectedLabel
, StringComparison.Ordinal) && item.Kind == SqlCompletionItemKind.Function
                && string.Equals(item.InsertText, $"{expectedLabel}(", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(items, item => string.Equals(item.Label, expectedLabel, StringComparison.Ordinal));
        }

        completionWindow?.Hide();
    }

    private static async Task AssertNoRenderedValueOrFunctionCompletionAsync(
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        string sql,
        CancellationToken cancellationToken)
    {
        editor.Text = sql[..^1];
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput(sql[^1].ToString());
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);

        if (editor.ActiveCompletionWindowForTesting is not { } completionWindow)
        {
            return;
        }

        var items = completionWindow.CompletionList.CompletionData
            .Cast<SqlCompletionData>()
            .ToArray();
        Assert.DoesNotContain(items, item => item.Kind == SqlCompletionItemKind.Function);
        Assert.DoesNotContain(items, item => item.Kind == SqlCompletionItemKind.Keyword
            && item.Label.StartsWith("CURRENT_", StringComparison.Ordinal));
        completionWindow.Hide();
    }

    private static void AcceptRenderedCompletion(
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        int replacementStart,
        int replacementLength,
        string label,
        SqlCompletionItemKind kind,
        string insertText)
    {
        var completionWindow = Assert.IsType<CompletionWindow>(
            editor.ActiveCompletionWindowForTesting);
        Assert.Equal(replacementStart, completionWindow.StartOffset);
        Assert.Equal(replacementStart + replacementLength, completionWindow.EndOffset);
        var item = Assert.Single(
            completionWindow.CompletionList.CompletionData.Cast<SqlCompletionData>(),
            candidate => candidate.Kind == kind
                && string.Equals(candidate.Label, label
, StringComparison.Ordinal) && string.Equals(candidate.InsertText, insertText, StringComparison.Ordinal));
        completionWindow.CompletionList.SelectedItem = item;
        fixture.Window.KeyPress(
            Key.Enter,
            RawInputModifiers.None,
            PhysicalKey.Enter,
            keySymbol: null);
        Assert.Null(editor.ActiveCompletionWindowForTesting);
    }

    private static async Task AssertRenderedSelectedObjectSqlContextAsync(
        DatabaseTestEnvironment environment,
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        SqlCatalogSnapshot catalog,
        SqlCatalogObject rowsTable,
        SqlCatalogObject keylessTable,
        Func<string, string> quote,
        CancellationToken cancellationToken)
    {
        Assert.Null(panel.SqlLanguageCompletionContext.PreferredObject);
        Assert.Equal(panel.SqlLanguageCompletionContext, editor.SqlCompletionContext);

        fixture.OpenObject(environment.Provider.Seed.RowsTable);
        await WaitForSelectedObjectAsync(
            panel,
            environment.Provider.Seed.RowsTable,
            cancellationToken);
        Assert.Equal(
            panel.SelectedObject?.Descriptor.Id,
            panel.SqlLanguageCompletionContext.PreferredObject);
        Assert.Equal(panel.SqlLanguageCompletionContext, editor.SqlCompletionContext);

        var selectedPrefix = rowsTable.Id.Name;
        editor.Text = $"SELECT {selectedPrefix[..^1]}";
        editor.FocusEditor(caretToEnd: true);
        fixture.Window.KeyTextInput(selectedPrefix[^1].ToString());
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);
        var selectedPrefixWindow = Assert.IsType<CompletionWindow>(
            editor.ActiveCompletionWindowForTesting);
        var selectedPrefixCompletion = selectedPrefixWindow.CompletionList.CompletionData
            .Cast<SqlCompletionData>()
            .ToArray();
        Assert.Contains(selectedPrefixCompletion, item =>
            IsSelectedObjectColumnCompletion(
                item,
                catalog.DriverId,
                selectedPrefix,
                "id",
                quote));
        Assert.Contains(selectedPrefixCompletion, item =>
            IsSelectedObjectColumnCompletion(
                item,
                catalog.DriverId,
                selectedPrefix,
                "title",
                quote));
        selectedPrefixWindow.Hide();

        var rowsMemberQualifier = catalog.DriverId is "oracle" or "firebird"
            ? quote(rowsTable.Id.Name)
            : rowsTable.Id.Name;
        var rowsCompletion = await CompleteRenderedMemberAsync(
            fixture,
            editor,
            $"SELECT {rowsMemberQualifier}",
            cancellationToken);
        Assert.Contains(rowsCompletion, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "id", StringComparison.Ordinal));
        Assert.Contains(rowsCompletion, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "title", StringComparison.Ordinal));

        fixture.OpenObject(environment.Provider.Seed.KeylessTable);
        await WaitForSelectedObjectAsync(
            panel,
            environment.Provider.Seed.KeylessTable,
            cancellationToken);
        Assert.Equal(
            panel.SelectedObject?.Descriptor.Id,
            panel.SqlLanguageCompletionContext.PreferredObject);
        Assert.Equal(panel.SqlLanguageCompletionContext, editor.SqlCompletionContext);

        var keylessMemberQualifier = catalog.DriverId is "oracle" or "firebird"
            ? quote(keylessTable.Id.Name)
            : keylessTable.Id.Name;
        var keylessCompletion = await CompleteRenderedMemberAsync(
            fixture,
            editor,
            $"SELECT {keylessMemberQualifier}",
            cancellationToken);
        Assert.Contains(keylessCompletion, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "position", StringComparison.Ordinal));
        Assert.Contains(keylessCompletion, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "label", StringComparison.Ordinal));
        Assert.DoesNotContain(keylessCompletion, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "title", StringComparison.Ordinal));

        var rowsObjectName = catalog.DefaultCatalog is not null
            || catalog.DefaultSchema is not null
                ? quote(rowsTable.Id.Name)
                : QuoteSqlLanguageObject(catalog.DriverId, rowsTable.Id);
        const string explicitAlias = "gsother";
        var explicitSql = $"SELECT {explicitAlias} FROM {rowsObjectName} {explicitAlias}";
        var explicitCompletion = await CompleteRenderedMemberAsync(
            fixture,
            editor,
            explicitSql,
            explicitSql.IndexOf(explicitAlias, StringComparison.Ordinal) + explicitAlias.Length,
            cancellationToken);
        Assert.Contains(explicitCompletion, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "id", StringComparison.Ordinal));
        Assert.Contains(explicitCompletion, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "title", StringComparison.Ordinal));
        Assert.DoesNotContain(explicitCompletion, item =>
            item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "position", StringComparison.Ordinal));

        const string unresolvedAlias = "gsmissing";
        var unresolvedSql = $"SELECT {unresolvedAlias} "
            + $"FROM asura_relation_that_does_not_exist {unresolvedAlias}";
        var unresolvedCompletion = await CompleteRenderedMemberAsync(
            fixture,
            editor,
            unresolvedSql,
            unresolvedSql.IndexOf(unresolvedAlias, StringComparison.Ordinal)
                + unresolvedAlias.Length,
            cancellationToken);
        Assert.DoesNotContain(unresolvedCompletion, item =>
            item.Kind == SqlCompletionItemKind.Column
                && item.Label is "position" or "label");

        var keylessObjectName = catalog.DefaultCatalog is not null
            || catalog.DefaultSchema is not null
                ? quote(keylessTable.Id.Name)
                : QuoteSqlLanguageObject(catalog.DriverId, keylessTable.Id);
        var invalidPredicateSql = $"SELECT * FROM {keylessObjectName} "
            + $"WHERE {keylessTable.Id.Name}";
        editor.Text = invalidPredicateSql;
        await editor.PendingDiagnosticsForTesting.WaitAsync(cancellationToken);
        Assert.Contains(editor.Diagnostics, item =>
            item.Severity == SqlDiagnosticSeverity.Error
                && item.Message.Contains(
                    keylessTable.Id.Name,
                    StringComparison.OrdinalIgnoreCase));

        fixture.Click(fixture.DatabaseOverviewButton);
        await WaitOnDispatcherAsync(
            () => panel.IsDatabaseObjectsOverview,
            cancellationToken);
        Assert.Null(panel.SqlLanguageCompletionContext.PreferredObject);
        Assert.Equal(panel.SqlLanguageCompletionContext, editor.SqlCompletionContext);
    }

    private static Task<IReadOnlyList<SqlCompletionData>> CompleteRenderedMemberAsync(
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        string sql,
        CancellationToken cancellationToken) =>
        CompleteRenderedMemberAsync(
            fixture,
            editor,
            sql,
            sql.Length,
            cancellationToken);

    private static async Task<IReadOnlyList<SqlCompletionData>> CompleteRenderedMemberAsync(
        HeadlessViewFixture fixture,
        CodeEditBox editor,
        string sql,
        int cursorOffset,
        CancellationToken cancellationToken)
    {
        editor.Text = sql;
        editor.FocusEditor();
        editor.EditorForTesting.CaretOffset = cursorOffset;
        fixture.Window.KeyTextInput(".");
        await editor.PendingCompletionForTesting.WaitAsync(cancellationToken);

        if (editor.ActiveCompletionWindowForTesting is not { } completionWindow)
        {
            return [];
        }

        var items = completionWindow.CompletionList.CompletionData
            .Cast<SqlCompletionData>()
            .ToArray();
        completionWindow.Hide();
        return items;
    }

    private static bool IsSelectedObjectColumnCompletion(
        SqlCompletionData item,
        string driverId,
        string objectName,
        string columnName,
        Func<string, string> quote)
    {
        var unquoted = $"{objectName}.{columnName}";
        var quoted = $"{quote(objectName)}.{quote(columnName)}";
        var expectedInsertText = driverId is "oracle" or "firebird"
            ? quoted
            : unquoted;
        return item.Kind == SqlCompletionItemKind.Column
            && string.Equals(item.Label, unquoted
, StringComparison.Ordinal) && string.Equals(item.InsertText, expectedInsertText, StringComparison.Ordinal);
    }

    private static async Task AssertHeadlessDatabaseDiagramAsync(
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CancellationToken cancellationToken)
    {
        fixture.Click(fixture.DatabaseOverviewButton);
        await WaitOnDispatcherAsync(
            () => panel.IsDatabaseObjectsOverview,
            cancellationToken);
        Assert.True(fixture.ObjectsOverviewButton.IsEffectivelyVisible);
        Assert.True(fixture.ObjectsOverviewButton.IsChecked);

        fixture.Click(fixture.DiagramOverviewButton);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && panel.IsDatabaseDiagramOverview
                && panel.HasMermaidDiagram
                && fixture.MermaidDiagramView.HasRenderedDiagram,
            cancellationToken);

        Assert.True(fixture.DiagramOverviewButton.IsChecked);
        Assert.True(fixture.MermaidDiagramView.IsEffectivelyVisible);
        Assert.True(fixture.MermaidDiagramView.HasRenderedDiagram);
        Assert.Contains("<svg", fixture.MermaidDiagramView.RenderedSvg, StringComparison.Ordinal);
        Assert.DoesNotContain("erDiagram", fixture.MermaidDiagramView.RenderedSvg, StringComparison.Ordinal);
        await fixture.SetClipboardTextAsync("diagram clipboard sentinel");
        fixture.InvokeClickHandler(fixture.CopyMermaidDiagramButton);
        await WaitForClipboardTextAsync(
            fixture,
            panel.MermaidDiagramText,
            cancellationToken);
        fixture.InvokeClickHandler(fixture.CopyMermaidSvgButton);
        await WaitForClipboardTextAsync(
            fixture,
            fixture.MermaidDiagramView.RenderedSvg,
            cancellationToken);

        fixture.Click(fixture.ObjectsOverviewButton);
        await WaitOnDispatcherAsync(
            () => panel.IsDatabaseObjectsOverview,
            cancellationToken);
        Assert.True(fixture.ObjectsOverviewButton.IsChecked);
        Assert.False(fixture.MermaidDiagramView.IsEffectivelyVisible);
    }

    private static async Task RunHeadlessFixtureSessionAsync(
        DatabaseRuntimePanelViewModel panel,
        string rowsTableName,
        Func<HeadlessViewFixture, Task> action,
        CancellationToken cancellationToken)
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
        try
        {
            var completed = await session.Dispatch(
                async () =>
                {
                    using var fixture = HeadlessViewFixture.Create(panel, rowsTableName);
                    await action(fixture);
                    return true;
                },
                cancellationToken);
            Assert.True(completed);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private static async Task AssertHeadlessContextMenuReadJourneyAsync(
        DatabaseTestEnvironment environment,
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CancellationToken cancellationToken)
    {
        var alpha = FindRow(panel, "alpha");
        fixture.OpenCellContextMenu(alpha, "code");
        Assert.Same(alpha, panel.SelectedRow);
        Assert.Equal("code", fixture.CurrentColumnName);
        fixture.AssertInsertInspectorActionAvailable();
        fixture.AssertContextMenuCapabilities(environment.Provider.Expectations.CanEdit);

        await fixture.SetClipboardTextAsync("clipboard sentinel");
        fixture.InvokeContextMenuItem("Copy the selected database row as INSERT");
        await WaitForClipboardContainingAsync(
            fixture,
            "INSERT INTO",
            cancellationToken);

        await fixture.SetClipboardTextAsync("clipboard sentinel");
        fixture.InvokeContextMenuItem("Copy the active database cell value");
        await WaitForClipboardTextAsync(fixture, "alpha", cancellationToken);

        fixture.OpenCellContextMenu(alpha, "code");
        fixture.InvokeContextMenuItem("Quick-filter code using Equals");
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && panel.ResultRows.Count == 1
                && string.Equals(Cell(panel.ResultRows[0], "code").Text, "alpha", StringComparison.Ordinal),
            cancellationToken);

        fixture.OpenCellContextMenu(panel.ResultRows[0], "code");
        fixture.InvokeContextMenuItem("Refresh the current database page");
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && panel.ResultRows.Count == 1
                && string.Equals(Cell(panel.ResultRows[0], "code").Text, "alpha", StringComparison.Ordinal),
            cancellationToken);
        Assert.False(panel.HasError, panel.ErrorMessage);

        fixture.InvokeClickHandler(fixture.ClearFilterButton);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy && panel.ResultRows.Count == 200,
            cancellationToken);
        await fixture.AssertQuickLookOpenCloseAsync(
            FindRow(panel, "alpha"),
            "title",
            isReadOnly: !environment.Provider.Expectations.CanEdit,
            cancellationToken);
    }

    private static async Task AssertHeadlessColumnSortingAsync(
        DatabaseTestEnvironment environment,
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CancellationToken cancellationToken)
    {
        fixture.SelectFilter("score", DatabaseFilterOperator.GreaterThan, "1200");
        fixture.InvokeClickHandler(fixture.ApplyFilterButton);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy && panel.ResultRows.Count == 5,
            cancellationToken);

        fixture.ClickColumnHeader("score");
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && SortDescending(panel, "score") == false
                && RowCodes(panel).SequenceEqual(
                    ["row-201", "row-202", "row-203", "row-204", "row-205"], StringComparer.Ordinal),
            cancellationToken);
        fixture.AssertColumnHeaderSortState("score", descending: false);
        AssertActiveScoreFilter(panel);

        fixture.ClickColumnHeader("score");
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && SortDescending(panel, "score") == true
                && RowCodes(panel).SequenceEqual(
                    ["row-205", "row-204", "row-203", "row-202", "row-201"], StringComparer.Ordinal),
            cancellationToken);
        fixture.AssertColumnHeaderSortState("score", descending: true);
        AssertActiveScoreFilter(panel);

        if (environment.Provider.Expectations.CanEdit)
        {
            var firstRow = panel.ResultRows[0];
            var originalTitle = Cell(firstRow, "title").Text;
            fixture.EditTextCell(firstRow, "title", originalTitle + " pending sort guard");
            fixture.CommitGridEdit();
            Assert.True(panel.HasPendingChanges);
            Assert.False(panel.CanSortTable);
            fixture.AssertColumnCannotSort("score");

            fixture.ClickColumnHeader("score");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            Assert.True(panel.HasPendingChanges);
            Assert.True(SortDescending(panel, "score"));
            Assert.Equal(
                ["row-205", "row-204", "row-203", "row-202", "row-201"],
                RowCodes(panel));

            fixture.Click(fixture.RevertButton);
            await WaitOnDispatcherAsync(
                () => !panel.IsBusy && !panel.HasPendingChanges,
                cancellationToken);
            Assert.False(panel.HasError, panel.ErrorMessage);
        }

        // Reopening the object restores the generated, unsorted preview before
        // the raw-query portion of the rendered journey.
        fixture.OpenObject(environment.Provider.Seed.RowsTable);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && panel.ResultRows.Count == 200
                && SortDescending(panel, "score") is null,
            cancellationToken);
    }

    private static async Task AssertHeadlessRawQueryJourneyAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CancellationToken cancellationToken)
    {
        var generatedPreview = panel.QueryText;
        var orderedPreview = AddOrderByToGeneratedPreview(
            generatedPreview,
            environment.Provider.Id,
            "id",
            descending: true);
        fixture.RunSql(orderedPreview);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && (panel.HasError
                    || (panel.ResultRows.Count == 200
                        && string.Equals(Cell(panel.ResultRows[0], "code").Text, "row-205", StringComparison.Ordinal))),
            cancellationToken);
        Assert.False(panel.HasError, panel.ErrorMessage);

        if (environment.Provider.Expectations.CanEdit && !panel.CanEditRows)
        {
            var provenancePage = await client.QueryWithProvenanceAsync(
                environment.Provider.Id,
                environment.ConnectionString,
                tunnel: null,
                orderedPreview,
                DatabaseRuntimePanelViewModel.MaxRows,
                cancellationToken);
            var candidateDetails = await client.GetObjectDetailsAsync(
                environment.Provider.Id,
                environment.ConnectionString,
                tunnel: null,
                objects.Rows,
                cancellationToken);
            Assert.True(
                panel.CanEditRows,
                $"{environment.Provider.Id} did not prove the edited generated query. "
                + $"candidate={objects.Rows.Id}; "
                + $"page-columns={provenancePage.Columns.Count}; "
                + $"detail-columns={candidateDetails.Columns.Count}; "
                + $"descriptors=[{string.Join("; ", provenancePage.Columns.Select(FormatProvenanceColumn))}].");
        }

        Assert.Equal(environment.Provider.Expectations.CanEdit, panel.CanEditRows);
        if (environment.Provider.Expectations.CanEdit)
        {
            Assert.Equal(
                environment.Provider.Seed.RowsTable,
                panel.SelectedObject?.Descriptor.Name);
            Assert.NotEmpty(panel.StructureColumns);
        }
        fixture.OpenCellContextMenu(panel.ResultRows[0], "code");
        fixture.AssertContextMenuCapabilities(environment.Provider.Expectations.CanEdit);
        fixture.CloseContextMenu();

        if (environment.Provider.Expectations.CanEdit)
        {
            const string rowCode = "row-205";
            var firstRow = panel.ResultRows[0];
            var originalTitle = Cell(firstRow, "title").Text;
            var editedTitle = $"Raw query save through {environment.Provider.Id}";
            fixture.EditTextCell(firstRow, "title", editedTitle);
            await ClickSaveAsync(panel, fixture, cancellationToken);
            Assert.False(panel.HasError, panel.ErrorMessage);
            Assert.Equal(rowCode, Cell(panel.ResultRows[0], "code").Text);
            Assert.Equal(editedTitle, Cell(panel.ResultRows[0], "title").Text);
            Assert.Equal(orderedPreview, panel.QueryText);

            var saved = await LoadSingleRowAsync(
                client,
                environment,
                objects.Rows,
                rowCode,
                cancellationToken);
            Assert.Equal(editedTitle, Value(saved, "title").DisplayText);

            fixture.UpdateLayout();
            fixture.EditTextCell(panel.ResultRows[0], "title", originalTitle);
            await ClickSaveAsync(panel, fixture, cancellationToken);
            Assert.False(panel.HasError, panel.ErrorMessage);
            Assert.Equal(rowCode, Cell(panel.ResultRows[0], "code").Text);
            var restored = await LoadSingleRowAsync(
                client,
                environment,
                objects.Rows,
                rowCode,
                cancellationToken);
            Assert.Equal(originalTitle, Value(restored, "title").DisplayText);
        }
        else
        {
            fixture.UpdateLayout();
            Assert.False(panel.CanEditRows);
            Assert.True(fixture.RowsGrid.IsReadOnly);
            Assert.False(fixture.SaveButton.IsEffectivelyEnabled);
            Assert.False(fixture.AddRowButton.IsEffectivelyEnabled);
            Assert.False(fixture.DeleteRowButton.IsEffectivelyEnabled);
            Assert.False(fixture.CanBeginTextCellEdit("row-205", "title"));
        }

        if (string.Equals(environment.Provider.Id, "sqlserver", StringComparison.Ordinal))
        {
            await AssertSqlServerUnsafeRawProjectionsStayReadOnlyAsync(
                objects,
                panel,
                fixture,
                cancellationToken);
        }

        var completeGeneratedPreview = client.BuildTablePreviewQuery(
            environment.Provider.Id,
            objects.Rows.Id,
            DatabaseRuntimePanelViewModel.MaxRows);
        var expressionQuery = AddAliasedExpressionToGeneratedPreview(
            completeGeneratedPreview,
            environment.Provider.Id);
        if (string.Equals(environment.Provider.Id, "sqlite", StringComparison.Ordinal))
        {
            // A final line comment must not consume the generated outer
            // filter/order/page clauses. A semicolon before the comment stays
            // deliberately fail-closed because it can introduce another
            // statement; the ordinary single-SELECT form remains browsable.
            expressionQuery += " -- trailing raw-query comment";
        }
        fixture.RunSql(expressionQuery);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && !panel.HasError
                && panel.ResultRows.Count == 205
                && panel.ResultColumns.Any(column => string.Equals(column.Name, "asura_expression", StringComparison.Ordinal)),
            cancellationToken);

        Assert.Null(panel.SelectedObject);
        Assert.False(panel.CanEditRows);
        Assert.Contains("does not map exactly", panel.ReadOnlyReason, StringComparison.Ordinal);
        fixture.AssertRawResultControlsVisibleAndReadOnly();
        await AssertHeadlessRawBrowseJourneyAsync(
            panel,
            fixture,
            expressionQuery,
            cancellationToken);
    }

    private static async Task AssertHeadlessRawBrowseJourneyAsync(
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        string expressionQuery,
        CancellationToken cancellationToken)
    {
        Assert.Equal(expressionQuery, panel.QueryText);
        Assert.Null(panel.SelectedObject);
        Assert.False(panel.CanEditRows);
        fixture.AssertRawResultControlsVisibleAndReadOnly();
        fixture.ClickColumnHeader("id");
        await WaitOnDispatcherAsync(
            () => panel.HasError
                || (!panel.IsBusy
                    && SortDescending(panel, "id") == false
                    && RowCodes(panel).Take(5).SequenceEqual(
                        ["alpha", "beta", "literal", "omega-a", "omega-b"], StringComparer.Ordinal)),
            cancellationToken);
        Assert.False(panel.HasError, panel.ErrorMessage);
        fixture.AssertColumnHeaderSortState("id", descending: false);

        fixture.ClickColumnHeader("id");
        await WaitOnDispatcherAsync(
            () => panel.HasError
                || (!panel.IsBusy
                    && SortDescending(panel, "id") == true
                    && RowCodes(panel).Take(5).SequenceEqual(
                        ["row-205", "row-204", "row-203", "row-202", "row-201"], StringComparer.Ordinal)),
            cancellationToken);
        Assert.False(panel.HasError, panel.ErrorMessage);
        fixture.AssertColumnHeaderSortState("id", descending: true);
        Assert.Equal(expressionQuery, panel.QueryText);

        var alpha = FindRow(panel, "alpha");
        fixture.OpenCellContextMenu(alpha, "code");
        fixture.InvokeContextMenuItem("Quick-filter code using Equals");
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && panel.ResultRows.Count == 1
                && string.Equals(Cell(panel.ResultRows[0], "code").Text, "alpha"
, StringComparison.Ordinal) && SortDescending(panel, "id") == true,
            cancellationToken);
        fixture.AssertColumnHeaderSortState("id", descending: true);

        fixture.OpenCellContextMenu(panel.ResultRows[0], "code");
        fixture.InvokeContextMenuItem("Refresh the current database page");
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && panel.ResultRows.Count == 1
                && string.Equals(Cell(panel.ResultRows[0], "code").Text, "alpha", StringComparison.Ordinal),
            cancellationToken);
        Assert.False(panel.HasError, panel.ErrorMessage);

        fixture.InvokeClickHandler(fixture.ClearFilterButton);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && panel.ResultRows.Count == 205
                && string.Equals(Cell(panel.ResultRows[0], "code").Text, "row-205"
, StringComparison.Ordinal) && SortDescending(panel, "id") == true,
            cancellationToken);
        Assert.Equal(expressionQuery, panel.QueryText);
    }

    private static async Task AssertSqlServerUnsafeRawProjectionsStayReadOnlyAsync(
        DatabaseObjects objects,
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CancellationToken cancellationToken)
    {
        var table = string.Join(
            '.',
            new[]
            {
                objects.Rows.Id.Catalog,
                objects.Rows.Id.Schema,
                objects.Rows.Id.Name,
            }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => QuoteIdentifier("sqlserver", part!)));
        var projectedWithoutKey = objects.RowsDetails.Columns
            .Where(column => !column.IsPrimaryKey)
            .Select(column => QuoteIdentifier("sqlserver", column.Name))
            .ToArray();
        var missingKeySql = $"SELECT {string.Join(", ", projectedWithoutKey)} "
            + $"FROM {table} ORDER BY {QuoteIdentifier("sqlserver", "code")};";

        fixture.RunSql(missingKeySql);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && (panel.HasError || panel.ResultRows.Count == 205),
            cancellationToken);
        Assert.False(panel.HasError, panel.ErrorMessage);
        AssertUnsafeRawProjectionReadOnly(panel, fixture);
        Assert.All(
            objects.RowsDetails.Columns.Where(column => !column.IsPrimaryKey),
            column => Assert.Contains(
                panel.ResultColumns,
                projected => string.Equals(projected.Name, column.Name, StringComparison.Ordinal)));

        const string replacedColumnName = "title";
        var replacedProjection = objects.RowsDetails.Columns
            .Select(column => string.Equals(
                    column.Name,
                    replacedColumnName,
                    StringComparison.Ordinal)
                ? $"UPPER({QuoteIdentifier("sqlserver", column.Name)}) "
                    + $"AS {QuoteIdentifier("sqlserver", column.Name)}"
                : QuoteIdentifier("sqlserver", column.Name));
        var computedReplacementSql = $"SELECT {string.Join(", ", replacedProjection)} "
            + $"FROM {table} ORDER BY {QuoteIdentifier("sqlserver", "id")};";

        fixture.RunSql(computedReplacementSql);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && (panel.HasError || panel.ResultRows.Count == 205),
            cancellationToken);
        Assert.False(panel.HasError, panel.ErrorMessage);
        Assert.Equal(
            objects.RowsDetails.Columns.Select(column => column.Name),
            panel.ResultColumns.Select(column => column.Name), StringComparer.Ordinal);
        Assert.Equal("ALPHA", Cell(FindRow(panel, "alpha"), replacedColumnName).Text);
        AssertUnsafeRawProjectionReadOnly(panel, fixture);

        const string swappedColumnName = "note";
        var swappedProjection = objects.RowsDetails.Columns
            .Select(column => column.Name switch
            {
                replacedColumnName => $"{QuoteIdentifier("sqlserver", swappedColumnName)} "
                    + $"AS {QuoteIdentifier("sqlserver", replacedColumnName)}",
                swappedColumnName => $"{QuoteIdentifier("sqlserver", replacedColumnName)} "
                    + $"AS {QuoteIdentifier("sqlserver", swappedColumnName)}",
                _ => QuoteIdentifier("sqlserver", column.Name),
            });
        var swappedAliasesSql = $"SELECT {string.Join(", ", swappedProjection)} "
            + $"FROM {table} ORDER BY {QuoteIdentifier("sqlserver", "id")};";

        fixture.RunSql(swappedAliasesSql);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && (panel.HasError || panel.ResultRows.Count == 205),
            cancellationToken);
        Assert.False(panel.HasError, panel.ErrorMessage);
        Assert.Equal(
            objects.RowsDetails.Columns.Select(column => column.Name),
            panel.ResultColumns.Select(column => column.Name), StringComparer.Ordinal);
        var alpha = FindRow(panel, "alpha");
        Assert.Equal("one", Cell(alpha, replacedColumnName).Text);
        Assert.Equal("Alpha", Cell(alpha, swappedColumnName).Text);
        AssertUnsafeRawProjectionReadOnly(panel, fixture);
    }

    private static void AssertUnsafeRawProjectionReadOnly(
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture)
    {
        Assert.Null(panel.SelectedObject);
        Assert.False(panel.CanEditRows);
        Assert.Contains("does not map exactly", panel.ReadOnlyReason, StringComparison.Ordinal);
        fixture.AssertRawResultControlsVisibleAndReadOnly();
        Assert.True(fixture.RowsGrid.IsReadOnly);
    }

    private static void AssertActiveScoreFilter(DatabaseRuntimePanelViewModel panel)
    {
        Assert.Equal("score", panel.FilterColumn?.Name);
        Assert.Equal(DatabaseFilterOperator.GreaterThan, panel.FilterOperator?.Operator);
        Assert.Equal("1200", panel.FilterValue);
        Assert.All(panel.ResultRows, row =>
            Assert.True(decimal.Parse(
                Cell(row, "score").Text,
                System.Globalization.CultureInfo.InvariantCulture) > 1200m));
    }

    private static bool? SortDescending(
        DatabaseRuntimePanelViewModel panel,
        string columnName) => Assert.Single(
            panel.ResultColumns,
            column => string.Equals(column.Name, columnName, StringComparison.Ordinal)).SortDescending;

    private static IReadOnlyList<string> RowCodes(DatabaseRuntimePanelViewModel panel) =>
        [.. panel.ResultRows.Select(row => Cell(row, "code").Text)];

    private static string AddOrderByToGeneratedPreview(
        string generatedPreview,
        string providerId,
        string columnName,
        bool descending)
    {
        var ordering = $"ORDER BY {QuoteIdentifier(providerId, columnName)}"
            + (descending ? " DESC" : " ASC");
        if (string.Equals(providerId, "sqlserver", StringComparison.Ordinal))
        {
            return generatedPreview.Replace(
                "ORDER BY (SELECT NULL)",
                ordering,
                StringComparison.Ordinal);
        }

        if (string.Equals(providerId, "oracle", StringComparison.Ordinal))
        {
            return generatedPreview.Replace(
                "ORDER BY 1",
                ordering,
                StringComparison.Ordinal);
        }

        var pageMarker = string.Equals(providerId, "firebird", StringComparison.Ordinal) ? " ROWS " : " LIMIT ";
        var markerIndex = generatedPreview.LastIndexOf(pageMarker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Generated preview did not contain '{pageMarker.Trim()}'.");
        return generatedPreview.Insert(markerIndex, $" {ordering}");
    }

    private static string AddAliasedExpressionToGeneratedPreview(
        string generatedPreview,
        string providerId)
    {
        var source = generatedPreview.TrimEnd();
        if (source.EndsWith(';'))
        {
            source = source[..^1].TrimEnd();
        }

        var alias = QuoteIdentifier(providerId, "asura_base");
        var aliasJoiner = string.Equals(providerId, "oracle", StringComparison.Ordinal) ? " " : " AS ";
        return $"SELECT {alias}.*, {alias}.{QuoteIdentifier(providerId, "score")} + 1 AS "
            + $"{QuoteIdentifier(providerId, "asura_expression")} "
            + $"FROM ({source}){aliasJoiner}{alias}";
    }

    private static string QuoteIdentifier(string providerId, string identifier) =>
        providerId switch
        {
            "mysql" or "mariadb" or "clickhouse" => $"`{identifier}`",
            "sqlserver" => $"[{identifier}]",
            _ => $"\"{identifier}\"",
        };

    private static string FormatProvenanceColumn(DatabaseColumnDescriptor column)
    {
        var source = column.BaseObject;
        return $"{column.Name}|baseColumn={column.BaseColumnName ?? "<null>"}"
            + $"|catalog={source?.Catalog ?? "<null>"}"
            + $"|schema={source?.Schema ?? "<null>"}"
            + $"|table={source?.Name ?? "<null>"}";
    }

    private static async Task WaitForClipboardTextAsync(
        HeadlessViewFixture fixture,
        string expected,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!string.Equals(
            await fixture.GetClipboardTextAsync(),
            expected,
            StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static async Task WaitForClipboardContainingAsync(
        HeadlessViewFixture fixture,
        string expectedFragment,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while ((await fixture.GetClipboardTextAsync())?.Contains(
                   expectedFragment,
                   StringComparison.Ordinal) != true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static async Task AssertHeadlessFilterAndPagingAsync(
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CancellationToken cancellationToken)
    {
        Assert.Equal("200", fixture.PageLimitBox.Text);
        Assert.Equal("205", fixture.TotalRowsText.Text);
        fixture.EnterPageLimit("75");
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && panel.ResultRows.Count == 75
                && string.Equals(panel.PageLimitText, "75", StringComparison.Ordinal),
            cancellationToken);
        Assert.Equal(205, panel.TotalRows);
        Assert.Equal("205", fixture.TotalRowsText.Text);
        fixture.EnterPageLimit("200");
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && panel.ResultRows.Count == 200
                && string.Equals(panel.PageLimitText, "200", StringComparison.Ordinal),
            cancellationToken);

        fixture.SelectFilter("code", DatabaseFilterOperator.Equal, "alpha");
        fixture.InvokeClickHandler(fixture.ApplyFilterButton);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && panel.ResultRows.Count == 1
                && string.Equals(Cell(panel.ResultRows[0], "code").Text, "alpha", StringComparison.Ordinal),
            cancellationToken);

        fixture.InvokeClickHandler(fixture.ClearFilterButton);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy && panel.ResultRows.Count == 200,
            cancellationToken);

        Assert.True(fixture.NextPageButton.IsEffectivelyEnabled);
        fixture.InvokeClickHandler(fixture.NextPageButton);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy,
            cancellationToken);
        Assert.True(panel.HasPreviousPage);
        Assert.Equal(5, panel.ResultRows.Count);

        Assert.True(fixture.PreviousPageButton.IsEffectivelyEnabled);
        fixture.InvokeClickHandler(fixture.PreviousPageButton);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy,
            cancellationToken);
        Assert.False(panel.HasPreviousPage);
        Assert.Equal(200, panel.ResultRows.Count);
    }

    private static async Task AssertEditableHeadlessJourneyAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CancellationToken cancellationToken)
    {
        var originalTitle = Cell(FindRow(panel, "alpha"), "title").Text;
        var editedTitle = $"Alpha saved through {environment.Provider.Id} DataGrid";
        var alpha = FindRow(panel, "alpha");
        var originalScore = Cell(alpha, "score").EditText;
        fixture.EditTextCell(alpha, "score", "not-a-number");
        Assert.NotNull(Cell(alpha, "score").ValidationError);
        Assert.False(fixture.SaveButton.IsEffectivelyEnabled);
        fixture.EditTextCell(alpha, "score", originalScore);
        Assert.Null(Cell(alpha, "score").ValidationError);

        fixture.EditTextCell("alpha", "title", editedTitle);
        Assert.True(panel.HasPendingChanges);
        Assert.True(fixture.SaveButton.IsEffectivelyEnabled);
        Assert.False(fixture.ObjectsList.IsEffectivelyEnabled);
        Assert.False(fixture.RowsObjectButton.IsEffectivelyEnabled);
        Assert.False(fixture.ConnectButton.IsEffectivelyEnabled);

        await ClickSaveAsync(
            panel,
            fixture,
            cancellationToken);
        fixture.UpdateLayout();
        Assert.False(panel.HasError, panel.ErrorMessage);
        Assert.False(fixture.SaveButton.IsEffectivelyEnabled);
        Assert.True(fixture.ObjectsList.IsEffectivelyEnabled);
        Assert.True(fixture.RowsObjectButton.IsEffectivelyEnabled);
        Assert.True(fixture.ConnectButton.IsEffectivelyEnabled);

        var saved = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            "alpha",
            cancellationToken);
        Assert.Equal(editedTitle, Value(saved, "title").DisplayText);

        fixture.OpenObject(environment.Provider.Seed.KeylessTable);
        await WaitForSelectedObjectAsync(
            panel,
            environment.Provider.Seed.KeylessTable,
            cancellationToken);
        fixture.AssertSelectedObjectIsReadOnly();
        fixture.OpenObject(environment.Provider.Seed.View);
        await WaitForSelectedObjectAsync(
            panel,
            environment.Provider.Seed.View,
            cancellationToken);
        fixture.AssertSelectedObjectIsReadOnly();
        fixture.OpenObject(environment.Provider.Seed.RowsTable);
        await WaitForSelectedObjectAsync(
            panel,
            environment.Provider.Seed.RowsTable,
            cancellationToken);

        fixture.EditTextCell("alpha", "title", originalTitle);
        await ClickSaveAsync(
            panel,
            fixture,
            cancellationToken);
        Assert.False(panel.HasError, panel.ErrorMessage);

        var restored = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            "alpha",
            cancellationToken);
        Assert.Equal(originalTitle, Value(restored, "title").DisplayText);

        var alphaAfterRestore = FindRow(panel, "alpha");
        var enabled = Cell(alphaAfterRestore, "enabled").BooleanValue;
        fixture.EditBooleanCell(alphaAfterRestore, "enabled", enabled != true);
        Assert.True(panel.HasPendingChanges);
        Assert.True(fixture.RevertButton.IsEffectivelyEnabled);
        fixture.Click(fixture.RevertButton);
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy && !panel.HasPendingChanges,
            cancellationToken);
        Assert.False(panel.HasError, panel.ErrorMessage);
        Assert.Equal(
            enabled,
            Cell(FindRow(panel, "alpha"), "enabled").BooleanValue);

        if (environment.Provider.Id is "mysql" or "mariadb")
        {
            fixture.OpenObject("viewer_nontransactional");
            await WaitForSelectedObjectAsync(
                panel,
                "viewer_nontransactional",
                cancellationToken);
            fixture.AssertSelectedObjectIsReadOnly();
            Assert.Contains("InnoDB", panel.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task AssertHeadlessContextMenuDuplicateAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        DatabaseRuntimePanelViewModel panel,
        CancellationToken cancellationToken)
    {
        var duplicateCode = $"context-{environment.Provider.Id}-{Guid.NewGuid():N}";
        const string sourceCode = "row-006";
        var sourceTitle = string.Empty;
        await RunHeadlessFixtureSessionAsync(
            panel,
            environment.Provider.Seed.RowsTable,
            async fixture =>
            {
                fixture.OpenObject(environment.Provider.Seed.RowsTable);
                await WaitForSelectedObjectAsync(
                    panel,
                    environment.Provider.Seed.RowsTable,
                    cancellationToken);
                fixture.SelectFilter("code", DatabaseFilterOperator.Equal, sourceCode);
                fixture.InvokeClickHandler(fixture.ApplyFilterButton);
                await WaitOnDispatcherAsync(
                    () => !panel.IsBusy && panel.ResultRows.Count == 1,
                    cancellationToken);
                var source = FindRow(panel, sourceCode);
                sourceTitle = Cell(source, "title").Text;
                await fixture.SetClipboardTextAsync(duplicateCode);
                fixture.OpenCellContextMenu(source, "code");
                fixture.InvokeContextMenuItem(
                    "Paste a database cell value from the clipboard");
                await WaitOnDispatcherAsync(
                    () => string.Equals(Cell(source, "code").Text, duplicateCode, StringComparison.Ordinal),
                    cancellationToken);
                fixture.OpenCellContextMenu(source, "code");
                fixture.InvokeContextMenuItem("Duplicate the selected database row");
                var duplicate = Assert.Single(panel.ResultRows, row => row.IsNew);
                Assert.Same(duplicate, panel.SelectedRow);
                Assert.Equal(sourceTitle, Cell(duplicate, "title").Text);
                Assert.Equal(duplicateCode, Cell(duplicate, "code").Text);
                await fixture.SetClipboardTextAsync(sourceCode);
                fixture.OpenCellContextMenu(source, "code");
                fixture.InvokeContextMenuItem(
                    "Paste a database cell value from the clipboard");
                await WaitOnDispatcherAsync(
                    () => string.Equals(Cell(source, "code").Text, sourceCode, StringComparison.Ordinal),
                    cancellationToken);
                Assert.True(fixture.SaveButton.IsEffectivelyEnabled);
            },
            cancellationToken);

        await SaveContextChangesAsync(panel);
        var inserted = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            duplicateCode,
            cancellationToken);
        Assert.Equal(sourceTitle, Value(inserted, "title").DisplayText);

        await RunHeadlessFixtureSessionAsync(
            panel,
            environment.Provider.Seed.RowsTable,
            async fixture =>
            {
                fixture.SelectFilter("code", DatabaseFilterOperator.Equal, duplicateCode);
                fixture.InvokeClickHandler(fixture.ApplyFilterButton);
                await WaitOnDispatcherAsync(
                    () => !panel.IsBusy && panel.ResultRows.Count == 1,
                    cancellationToken);
                fixture.OpenCellContextMenu(panel.ResultRows[0], "code");
                fixture.InvokeContextMenuItem(
                    "Delete the selected database row from the context menu");
                Assert.True(panel.HasPendingChanges);
            },
            cancellationToken);

        await SaveContextChangesAsync(panel);
        Assert.Empty((await LoadRowsByCodeAsync(
            client,
            environment,
            objects.Rows,
            duplicateCode,
            cancellationToken)).ValueRows);
        await panel.ClearFilterAsync();
        Assert.Equal(200, panel.ResultRows.Count);
    }

    private static async Task AssertHeadlessMutationControlsAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        DatabaseRuntimePanelViewModel panel,
        CancellationToken cancellationToken)
    {
        var insertedCode = $"ui-{environment.Provider.Id}-{Guid.NewGuid():N}";
        await RunHeadlessFixtureSessionAsync(
            panel,
            environment.Provider.Seed.RowsTable,
            async fixture =>
            {
                Assert.Equal(
                    environment.Provider.Seed.RowsTable,
                    panel.SelectedObject?.Descriptor.Name);

                fixture.Click(fixture.AddRowButton);
                var toolbarRow = Assert.Single(panel.ResultRows, row => row.IsNew);
                await WaitOnDispatcherAsync(
                    () => fixture.IsRowRealized(toolbarRow),
                    cancellationToken);
                fixture.AssertRowInsideGridViewport(toolbarRow);
                Assert.Same(toolbarRow, panel.SelectedRow);
                fixture.Click(fixture.RevertButton);
                await WaitOnDispatcherAsync(
                    () => panel.ResultRows.All(row => !row.IsNew),
                    cancellationToken);

                fixture.OpenCellContextMenu(FindRow(panel, "alpha"), "code");
                fixture.InvokeContextMenuItem("Add a database row from the context menu");
                var newRow = Assert.Single(panel.ResultRows, row => row.IsNew);
                await WaitOnDispatcherAsync(
                    () => fixture.IsRowRealized(newRow),
                    cancellationToken);
                fixture.AssertRowInsideGridViewport(newRow);
                Assert.Same(newRow, panel.SelectedRow);

                fixture.EditTextCell(newRow, "code", insertedCode);
                fixture.EditTextCell(newRow, "title", "Inserted through real DataGrid");
                fixture.EditTextCell(newRow, "score", "77.25");
                fixture.EditBooleanCell(newRow, "enabled", value: true);
                fixture.EditTextCell(newRow, "note", "temporary note");
                fixture.CommitGridEdit();
                fixture.OpenCellContextMenu(newRow, "note");
                fixture.InvokeContextMenuItem(
                    "Set the active database cell to NULL from the context menu");
                Assert.Equal(DatabaseEditValueState.Null, Cell(newRow, "note").State);

                fixture.EditTextCell(newRow, "status", "temporary status");
                fixture.CommitGridEdit();
                fixture.OpenCellContextMenu(newRow, "status");
                fixture.InvokeContextMenuItem(
                    "Set the active database cell to DEFAULT from the context menu");
                Assert.Equal(DatabaseEditValueState.Default, Cell(newRow, "status").State);
                Assert.True(fixture.SaveButton.IsEffectivelyEnabled);
            },
            cancellationToken);

        await SaveContextChangesAsync(panel);
        var inserted = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            insertedCode,
            cancellationToken);
        Assert.True(Value(inserted, "note").IsNull);
        Assert.Equal("draft", Value(inserted, "status").DisplayText);

        await FilterRowsAsync(
            panel,
            "code",
            DatabaseFilterOperator.Equal,
            insertedCode);
        panel.SelectRow(Assert.Single(panel.ResultRows));
        Assert.True(panel.CanDeleteSelectedRow);
        panel.DeleteSelectedRow();
        await SaveContextChangesAsync(panel);
        Assert.Empty((await LoadRowsByCodeAsync(
            client,
            environment,
            objects.Rows,
            insertedCode,
            cancellationToken)).ValueRows);

        await panel.ClearFilterAsync();
        Assert.Equal(200, panel.ResultRows.Count);
    }

    private static Task AssertReadOnlyHeadlessJourneyAsync(
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture)
    {
        fixture.SelectCell(FindRow(panel, "alpha"), "title");
        Assert.False(panel.CanEditRows);
        Assert.True(fixture.RowsGrid.IsReadOnly);
        Assert.False(fixture.SaveButton.IsEffectivelyEnabled);
        Assert.False(fixture.AddRowButton.IsEffectivelyEnabled);
        Assert.False(fixture.DeleteRowButton.IsEffectivelyEnabled);
        Assert.False(fixture.RevertButton.IsEffectivelyEnabled);
        Assert.True(fixture.ObjectsList.IsEffectivelyEnabled);
        Assert.True(fixture.ConnectButton.IsEffectivelyEnabled);
        Assert.False(fixture.CanBeginTextCellEdit("alpha", "title"));
        Assert.False(panel.HasPendingChanges);

        Assert.False(panel.IsBusy);
        Assert.False(panel.HasPendingChanges);

        return Task.CompletedTask;
    }

    private static async Task AssertReadOnlyContextMenusAsync(
        DatabaseTestEnvironment environment,
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CancellationToken cancellationToken)
    {
        fixture.OpenObject(environment.Provider.Seed.KeylessTable);
        await WaitForSelectedObjectAsync(
            panel,
            environment.Provider.Seed.KeylessTable,
            cancellationToken);
        fixture.AssertSelectedObjectIsReadOnly();
        fixture.AssertReadOnlyContextMenu(panel.ResultRows[0]);
        fixture.OpenObject(environment.Provider.Seed.View);
        await WaitForSelectedObjectAsync(
            panel,
            environment.Provider.Seed.View,
            cancellationToken);
        fixture.AssertSelectedObjectIsReadOnly();
        fixture.AssertReadOnlyContextMenu(panel.ResultRows[0]);

        if (environment.Provider.Id is "mysql" or "mariadb")
        {
            fixture.OpenObject("viewer_nontransactional");
            await WaitForSelectedObjectAsync(
                panel,
                "viewer_nontransactional",
                cancellationToken);
            fixture.AssertSelectedObjectIsReadOnly();
            Assert.Contains("InnoDB", panel.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);
            fixture.AssertReadOnlyContextMenu(panel.ResultRows[0]);
        }
    }

    private static Task WaitForSelectedObjectAsync(
        DatabaseRuntimePanelViewModel panel,
        string objectName,
        CancellationToken cancellationToken) => WaitOnDispatcherAsync(
            () => !panel.IsBusy
                && string.Equals(panel.SelectedObject?.Descriptor.Name, objectName, StringComparison.Ordinal),
            cancellationToken);

    private static async Task ClickSaveAsync(
        DatabaseRuntimePanelViewModel panel,
        HeadlessViewFixture fixture,
        CancellationToken cancellationToken)
    {
        fixture.Click(fixture.SaveButton);
        Assert.True(
            panel.IsBusy || !panel.HasPendingChanges,
            "The rendered Save button did not start its handler.");
        await WaitOnDispatcherAsync(
            () => !panel.IsBusy && !panel.HasPendingChanges,
            cancellationToken);
    }

    private static async Task SaveContextChangesAsync(
        DatabaseRuntimePanelViewModel panel)
    {
        Assert.True(panel.HasPendingChanges);
        Assert.True(panel.CanSaveChanges);
        await panel.SaveChangesAsync();
        Assert.False(panel.IsBusy);
        Assert.False(panel.HasError, panel.ErrorMessage);
        Assert.False(panel.HasPendingChanges);
    }

    private static async Task FilterRowsAsync(
        DatabaseRuntimePanelViewModel panel,
        string columnName,
        DatabaseFilterOperator filterOperator,
        string value)
    {
        panel.FilterColumn = Assert.Single(
            panel.FilterColumns,
            candidate => string.Equals(candidate.Name, columnName, StringComparison.Ordinal));
        panel.FilterOperator = Assert.Single(
            panel.FilterOperators,
            candidate => candidate.Operator == filterOperator);
        panel.FilterValue = value;
        await panel.ApplyFilterAsync();
        Assert.False(panel.HasError, panel.ErrorMessage);
    }

    private static async Task WaitOnDispatcherAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private sealed class HeadlessViewFixture : IDisposable
    {
        private HeadlessViewFixture(
            Window window,
            DatabaseRuntimePanelViewModel panel,
            DatabaseRuntimePanelView view,
            string rowsTableName)
        {
            Window = window;
            Panel = panel;
            View = view;
            RowsTableName = rowsTableName;
        }

        internal Window Window { get; }

        private DatabaseRuntimePanelViewModel Panel { get; }

        private DatabaseRuntimePanelView View { get; }

        private string RowsTableName { get; }

        public Button SaveButton => NamedControl<Button>("Save database changes");

        public Button DatabaseOverviewButton =>
            NamedControl<Button>("Show the database overview");

        public ToggleButton ObjectsOverviewButton =>
            NamedControl<ToggleButton>("Show database objects");

        public ToggleButton DiagramOverviewButton =>
            NamedControl<ToggleButton>("Show database Mermaid ER diagram");

        public DatabaseMermaidDiagramView MermaidDiagramView =>
            NamedControl<DatabaseMermaidDiagramView>("Rendered Mermaid ER diagram");

        public Button CopyMermaidDiagramButton =>
            NamedControl<Button>("Copy the Mermaid ER diagram source");

        public Button CopyMermaidSvgButton =>
            NamedControl<Button>("Copy the rendered Mermaid ER diagram SVG");

        public Button RowsObjectButton => ObjectButton(RowsTableName);

        public Button AddRowButton => NamedControl<Button>("Add a database row");

        public Button DeleteRowButton =>
            NamedControl<Button>("Delete the selected database row");

        public Button RevertButton =>
            NamedControl<Button>("Revert unsaved database changes");

        public Button ApplyFilterButton =>
            NamedControl<Button>("Apply the table filter");

        public Button ClearFilterButton =>
            NamedControl<Button>("Clear the table filter");

        public Button NextPageButton =>
            NamedControl<Button>("Show the next database page");

        public Button PreviousPageButton =>
            NamedControl<Button>("Show the previous database page");

        public TextBox PageLimitBox =>
            NamedControl<TextBox>("Database page row limit");

        public TextBlock TotalRowsText =>
            NamedControl<TextBlock>("Total matching database rows");

        public Button RunQueryButton =>
            NamedControl<Button>("Run the SQL statement");

        public CodeEditBox QueryEditor => NamedControl<CodeEditBox>("SQL statement");

        public DataGrid RowsGrid => NamedControl<DataGrid>("Database rows");

        public string? CurrentColumnName =>
            (RowsGrid.CurrentColumn?.Tag as DatabaseResultColumnViewModel)?.Name;

        public ItemsControl ObjectsList =>
            RowsObjectButton.GetVisualAncestors().OfType<ItemsControl>().First();

        public Button ConnectButton =>
            NamedControl<Button>("Connect to the database");

        public static HeadlessViewFixture Create(
            DatabaseRuntimePanelViewModel panel,
            string rowsTableName)
        {
            var view = new DatabaseRuntimePanelView
            {
                DataContext = panel,
            };
            var window = new Window
            {
                Width = 1280,
                Height = 800,
                Content = view,
            };
            window.Show();
            window.UpdateLayout();
            return new HeadlessViewFixture(window, panel, view, rowsTableName);
        }

        public void OpenObject(string name)
        {
            Click(ObjectButton(name));
        }

        public void AssertAllWorkspaceModes(bool hasIndexes)
        {
            var structureButton = NamedControl<ToggleButton>("Show table structure");
            var structureGrid = NamedControl<DataGrid>("Database object structure");
            InvokeClickHandler(structureButton);
            Assert.Equal(DatabaseWorkspaceMode.Structure, Panel.SelectedMode);
            Assert.True(structureButton.IsChecked);
            Assert.True(structureGrid.IsEffectivelyVisible);
            Assert.Same(Panel.StructureColumns, structureGrid.ItemsSource);
            Assert.NotEmpty(Panel.StructureColumns);

            var indexesButton = NamedControl<ToggleButton>("Show table indexes");
            var indexesGrid = NamedControl<DataGrid>("Database object indexes");
            InvokeClickHandler(indexesButton);
            Assert.Equal(DatabaseWorkspaceMode.Indexes, Panel.SelectedMode);
            Assert.True(indexesButton.IsChecked);
            Assert.True(indexesGrid.IsEffectivelyVisible);
            Assert.Same(Panel.Indexes, indexesGrid.ItemsSource);
            Assert.Equal(hasIndexes, Panel.Indexes.Count > 0);

            var dataButton = NamedControl<ToggleButton>("Show table data");
            InvokeClickHandler(dataButton);
            Assert.Equal(DatabaseWorkspaceMode.Data, Panel.SelectedMode);
            Assert.True(dataButton.IsChecked);
            Assert.True(RowsGrid.IsEffectivelyVisible);
        }

        public void AssertSelectedObjectIsReadOnly()
        {
            UpdateLayout();
            Assert.False(Panel.CanEditRows);
            Assert.True(RowsGrid.IsReadOnly);
            Assert.False(SaveButton.IsEffectivelyEnabled);
            Assert.False(AddRowButton.IsEffectivelyEnabled);
            Assert.False(DeleteRowButton.IsEffectivelyEnabled);
            Assert.False(RevertButton.IsEffectivelyEnabled);
            Assert.False(string.IsNullOrWhiteSpace(Panel.ReadOnlyReason));
            var reason = NamedControl<TextBlock>("Database read-only reason");
            Assert.True(reason.IsEffectivelyVisible);
            Assert.Equal(Panel.ReadOnlyReason, reason.Text);
        }

        public void AssertRawResultControlsVisibleAndReadOnly()
        {
            UpdateLayout();
            Assert.Null(Panel.SelectedObject);
            Assert.True(Panel.HasResults);
            Assert.True(NamedControl<ComboBox>("Filter column").IsEffectivelyVisible);
            Assert.True(NamedControl<ComboBox>("Filter operator").IsEffectivelyVisible);
            Assert.True(ApplyFilterButton.IsEffectivelyVisible);
            Assert.True(ClearFilterButton.IsEffectivelyVisible);

            Assert.True(AddRowButton.IsEffectivelyVisible);
            Assert.True(DeleteRowButton.IsEffectivelyVisible);
            Assert.True(RevertButton.IsEffectivelyVisible);
            Assert.True(SaveButton.IsEffectivelyVisible);
            Assert.False(AddRowButton.IsEffectivelyEnabled);
            Assert.False(DeleteRowButton.IsEffectivelyEnabled);
            Assert.False(RevertButton.IsEffectivelyEnabled);
            Assert.False(SaveButton.IsEffectivelyEnabled);

            var reason = NamedControl<TextBlock>("Database read-only reason");
            Assert.True(reason.IsEffectivelyVisible);
            Assert.Equal(Panel.ReadOnlyReason, reason.Text);
        }

        public void AssertResultColumns()
        {
            Assert.Equal(Panel.ResultColumns.Count, RowsGrid.Columns.Count);
            foreach (var column in RowsGrid.Columns)
            {
                var descriptor = Assert.IsType<DatabaseResultColumnViewModel>(column.Tag);
                Assert.Same(
                    Assert.Single(
                        Panel.ResultColumns,
                        candidate => string.Equals(candidate.Name, descriptor.Name, StringComparison.Ordinal)),
                    descriptor);
                Assert.Equal(!descriptor.IsEditable, column.IsReadOnly);
            }
        }

        public void AssertRunButtonBinding()
        {
            var command = Assert.IsAssignableFrom<System.Windows.Input.ICommand>(
                RunQueryButton.Command);
            Assert.True(command.CanExecute(RunQueryButton.CommandParameter));
        }

        public void SelectFilter(
            string columnName,
            DatabaseFilterOperator filterOperator,
            string value)
        {
            // Provider reads can complete on a non-UI continuation immediately
            // before this helper resumes. Force the bound ItemsControl to
            // realize its new filter-row template before addressing controls.
            UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            UpdateLayout();
            var column = Assert.Single(
                Panel.FilterColumns,
                candidate => string.Equals(candidate.Name, columnName, StringComparison.Ordinal));
            var filter = Assert.Single(
                Panel.FilterOperators,
                candidate => candidate.Operator == filterOperator);
            NamedControl<ComboBox>("Filter column").SelectedItem = column;
            NamedControl<ComboBox>("Filter operator").SelectedItem = filter;
            NamedControl<TextBox>("Filter value").Text = value;
            UpdateLayout();
            Assert.Same(column, Panel.FilterColumn);
            Assert.Same(filter, Panel.FilterOperator);
            Assert.Equal(value, Panel.FilterValue);
        }

        public void EnterPageLimit(string value)
        {
            PageLimitBox.Text = value;
            PageLimitBox.Focus();
            UpdateLayout();
            Window.KeyPress(
                Key.Enter,
                RawInputModifiers.None,
                PhysicalKey.Enter,
                keySymbol: null);
            UpdateLayout();
        }

        public void Click(Button button)
        {
            ClickControl(button);
        }

        public bool IsRowRealized(DatabaseResultRowViewModel row)
        {
            UpdateLayout();
            return RowsGrid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Any(candidate => ReferenceEquals(candidate.DataContext, row));
        }

        public void AssertRowInsideGridViewport(DatabaseResultRowViewModel row)
        {
            UpdateLayout();
            var container = RowsGrid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Single(candidate => ReferenceEquals(candidate.DataContext, row));
            var top = container.TranslatePoint(default, RowsGrid)
                ?? throw new InvalidOperationException("The staged row has no grid position.");
            var bottom = container.TranslatePoint(
                    new Point(0, container.Bounds.Height),
                    RowsGrid)
                ?? throw new InvalidOperationException("The staged row has no grid bounds.");
            Assert.True(top.Y < RowsGrid.Bounds.Height);
            Assert.True(bottom.Y > 0);
        }

        public void ClickColumnHeader(string columnName)
        {
            var column = ResultColumn(columnName);
            if (Panel.ResultRows.FirstOrDefault() is { } row)
            {
                RowsGrid.ScrollIntoView(row, column);
            }

            UpdateLayout();
            ClickControl(ColumnHeader(columnName));
        }

        public void AssertColumnHeaderSortState(string columnName, bool descending)
        {
            UpdateLayout();
            var expected = descending ? "descending" : "ascending";
            var headerContent = ColumnHeaderContent(columnName);
            Assert.Equal(
                $"Sort database column {columnName}, {expected}",
                AutomationProperties.GetName(headerContent));
            Assert.Contains(
                expected,
                AutomationProperties.GetHelpText(headerContent),
                StringComparison.Ordinal);
            Assert.Equal(descending, Assert.IsType<DatabaseResultColumnViewModel>(
                ResultColumn(columnName).Tag).SortDescending);
        }

        public void AssertColumnCannotSort(string columnName)
        {
            Assert.False(ResultColumn(columnName).CanUserSort);
            Assert.False(Panel.CanSortTable);
        }

        public void RunSql(string sql)
        {
            Assert.NotEmpty(sql);
            QueryEditor.EditorForTesting.Document.Text = sql;
            Dispatcher.UIThread.RunJobs();
            UpdateLayout();
            Click(RunQueryButton);
            Assert.Equal(sql, Panel.QueryText);
        }

        public void CloseContextMenu()
        {
            ResultContextMenu.Close();
            UpdateLayout();
            Assert.False(ResultContextMenu.IsOpen);
        }

        private void ClickControl(Control control)
        {
            control.BringIntoView();
            UpdateLayout();
            var point = control.TranslatePoint(
                new Point(control.Bounds.Width / 2, control.Bounds.Height / 2),
                Window)
                ?? throw new InvalidOperationException(
                    $"Could not locate '{AutomationProperties.GetName(control)}' in the headless window.");
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
            UpdateLayout();
        }

        public void InvokeClickHandler(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UpdateLayout();
        }

        public void CommitGridEdit()
        {
            RowsGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
            RowsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
            UpdateLayout();
        }

        public void EditTextCell(string code, string columnName, string value)
        {
            var row = Assert.Single(Panel.ResultRows, candidate => string.Equals(Cell(candidate, "code").Text, code, StringComparison.Ordinal));
            EditTextCell(row, columnName, value);
        }

        public void EditTextCell(
            DatabaseResultRowViewModel row,
            string columnName,
            string value)
        {
            var column = Assert.Single(
                RowsGrid.Columns,
                candidate => candidate.Tag is DatabaseResultColumnViewModel descriptor
                    && string.Equals(descriptor.Name, columnName, StringComparison.Ordinal));
            var grid = RowsGrid;
            grid.SelectedItem = row;
            grid.CurrentColumn = column;
            grid.ScrollIntoView(row, column);
            Window.UpdateLayout();
            Assert.True(
                grid.BeginEdit(),
                $"DataGrid refused to edit '{columnName}' "
                + $"(grid.IsReadOnly={grid.IsReadOnly}, "
                + $"column.IsReadOnly={column.IsReadOnly}, "
                + $"cell.IsEditable={Cell(row, columnName).IsEditable}, "
                + $"selected={ReferenceEquals(grid.SelectedItem, row)}, "
                + $"current={ReferenceEquals(grid.CurrentColumn, column)}).");
            Window.UpdateLayout();

            var editor = NamedControl<TextBox>($"Edit {columnName}");
            editor.Text = value;
            Assert.Equal(value, Cell(row, columnName).EditText);
        }

        public void EditBooleanCell(
            DatabaseResultRowViewModel row,
            string columnName,
            bool value)
        {
            SelectCell(row, columnName);
            Assert.True(RowsGrid.BeginEdit(), $"DataGrid refused to edit '{columnName}'.");
            UpdateLayout();
            var editor = NamedControl<CheckBox>($"Edit {columnName}");
            editor.IsChecked = value;
            Assert.Equal(value, Cell(row, columnName).BooleanValue);
        }

        public void SelectCell(
            DatabaseResultRowViewModel row,
            string columnName)
        {
            var column = Assert.Single(
                RowsGrid.Columns,
                candidate => candidate.Tag is DatabaseResultColumnViewModel descriptor
                    && string.Equals(descriptor.Name, columnName, StringComparison.Ordinal));
            RowsGrid.SelectedItem = row;
            RowsGrid.CurrentColumn = column;
            RowsGrid.ScrollIntoView(row, column);
            UpdateLayout();
        }

        public void OpenCellContextMenu(
            DatabaseResultRowViewModel row,
            string columnName)
        {
            SelectCell(row, columnName);
            var cell = Cell(row, columnName);
            var cellContainer = RowsGrid.GetVisualDescendants()
                .OfType<DataGridCell>()
                .FirstOrDefault(candidate => candidate
                    .GetVisualDescendants()
                    .Any(descendant => ReferenceEquals(descendant.DataContext, cell)))
                ?? throw new InvalidOperationException(
                    $"The real DataGrid did not realize the '{columnName}' cell.");

            cellContainer.RaiseEvent(new ContextRequestedEventArgs
            {
                RoutedEvent = InputElement.ContextRequestedEvent,
            });
            UpdateLayout();
            Assert.True(ResultContextMenu.IsOpen);
        }

        public void AssertContextMenuCapabilities(bool canEdit)
        {
            AssertMenuItemAvailable("Refresh the current database page");
            AssertMenuItemAvailable("Copy the selected database row");
            AssertMenuItemAvailable("Copy the active database cell value");
            AssertMenuItemAvailable("Copy all values in the active database column");
            var copyInsert = ContextMenuItem("Copy the selected database row as INSERT");
            Assert.True(copyInsert.IsVisible);
            Assert.Equal(
                Panel.CanCopySelectedRowAsInsert,
                copyInsert.IsEffectivelyEnabled);
            AssertMenuItemAvailable("Quick-filter the active database cell");
            AssertMenuItemAvailable($"Quick-filter {CurrentColumnName} using Equals");

            var mutations = new[]
            {
                "Paste a database cell value from the clipboard",
                "Add a database row from the context menu",
                "Duplicate the selected database row",
                "Import database rows",
                "Delete the selected database row from the context menu",
            };
            foreach (var automationName in mutations)
            {
                var item = ContextMenuItem(automationName);
                var expectedEnabled = canEdit
                    && (!string.Equals(
                            automationName,
                            "Import database rows",
                            StringComparison.Ordinal)
                        || TopLevel.GetTopLevel(View)?.StorageProvider?.CanOpen == true);
                if (expectedEnabled)
                {
                    Assert.True(
                        item.IsEffectivelyVisible,
                        $"'{automationName}' should be reachable in the open menu.");
                    Assert.True(
                        item.IsEffectivelyEnabled,
                        $"'{automationName}' should be enabled for an editable result.");
                }
                else
                {
                    Assert.True(
                        item.IsEffectivelyVisible,
                        $"'{automationName}' should remain reachable while unavailable.");
                    Assert.False(
                        item.IsEffectivelyEnabled,
                        $"'{automationName}' should not allow this unavailable mutation "
                        + $"(visible={item.IsVisible}, enabled={item.IsEnabled}, "
                        + $"effective={item.IsEffectivelyEnabled}, "
                        + $"panel.CanMutateRows={Panel.CanMutateRows}).");
                }
            }

            var selectedColumn = Assert.IsType<DatabaseResultColumnViewModel>(
                RowsGrid.CurrentColumn?.Tag);
            var ordinal = Panel.ResultColumns
                .Select((column, index) => (column, index))
                .Single(item => ReferenceEquals(item.column, selectedColumn))
                .index;
            var selectedCell = Panel.SelectedRow?.Cells[ordinal];
            var canSetValue = canEdit && selectedCell is not null
                && (Panel.CanSetSelectedCellEmpty(ordinal)
                    || selectedCell.CanSetNull
                    || selectedCell.CanSetDefault
                    || selectedCell.CanSetBinary);
            var setValue = ContextMenuItem("Set the active database cell value");
            Assert.Equal(canSetValue, setValue.IsVisible && setValue.IsEnabled);
            var setNull = ContextMenuItem(
                "Set the active database cell to NULL from the context menu");
            Assert.Equal(selectedCell?.CanSetNull == true, setNull.IsVisible);
            Assert.Equal(
                canEdit && selectedCell?.CanSetNull == true,
                setNull.IsEnabled);
            var setDefault = ContextMenuItem(
                "Set the active database cell to DEFAULT from the context menu");
            Assert.Equal(selectedCell?.CanSetDefault == true, setDefault.IsVisible);
            Assert.Equal(
                canEdit
                    && Panel.SelectedRow?.IsNew == true
                    && selectedCell?.CanSetDefault == true,
                setDefault.IsEnabled);

            if (canEdit)
            {
                Assert.True(ContextMenuItem(
                    "Duplicate the selected database row").IsEnabled);
                Assert.True(ContextMenuItem(
                    "Delete the selected database row from the context menu").IsEnabled);
            }
        }

        public void AssertInsertInspectorActionAvailable()
        {
            var button = NamedControl<Button>("Copy the row as INSERT");
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.IsEffectivelyEnabled);
        }

        public void AssertReadOnlyContextMenu(DatabaseResultRowViewModel row)
        {
            var columnName = Panel.ResultColumns[0].Name;
            OpenCellContextMenu(row, columnName);
            AssertContextMenuCapabilities(canEdit: false);
            ResultContextMenu.Close();
            UpdateLayout();
        }

        public async Task AssertQuickLookOpenCloseAsync(
            DatabaseResultRowViewModel row,
            string columnName,
            bool isReadOnly,
            CancellationToken cancellationToken)
        {
            Window.Width = 720;
            Window.Height = 560;
            Dispatcher.UIThread.RunJobs();
            UpdateLayout();

            (TextBox Editor, Visual Host, FlyoutPresenter Popup, Grid Dialog) OpenQuickLook()
            {
                OpenCellContextMenu(row, columnName);
                InvokeContextMenuItem("Open the active database cell in Quick Look");
                Dispatcher.UIThread.RunJobs();

                var quickLookEditor = Assert.IsType<TextBox>(FocusedControl);
                Assert.Equal(
                    $"Quick Look value for {columnName}",
                    AutomationProperties.GetName(quickLookEditor));
                var host = quickLookEditor.GetVisualAncestors().Last();
                var presenter = host.GetVisualDescendants()
                    .OfType<FlyoutPresenter>()
                    .Single();
                var dialog = host.GetVisualDescendants()
                    .OfType<Grid>()
                    .Single(candidate => string.Equals(
                        AutomationProperties.GetName(candidate),
                        "Database cell Quick Look dialog",
                        StringComparison.Ordinal));
                return (quickLookEditor, host, presenter, dialog);
            }

            var originalText = Cell(row, columnName).EditText;
            var first = OpenQuickLook();
            Assert.Equal(originalText, first.Editor.Text);
            Assert.Equal(isReadOnly, first.Editor.IsReadOnly);
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, first.Editor.TextWrapping);

            // Exercise the layout with content large enough to wrap and scroll.
            // The short canonical seed value alone cannot reproduce the popup
            // overflow that motivated this regression journey.
            first.Editor.Text = string.Join(
                ' ',
                Enumerable.Repeat("quick-look-overflow-probe", 400));
            Dispatcher.UIThread.RunJobs();
            UpdateLayout();
            Assert.True(
                first.Dialog.Bounds.Width <= RowsGrid.Bounds.Width,
                $"Quick Look content width {first.Dialog.Bounds.Width} exceeded grid width {RowsGrid.Bounds.Width}.");
            Assert.True(
                first.Dialog.Bounds.Height <= RowsGrid.Bounds.Height,
                $"Quick Look content height {first.Dialog.Bounds.Height} exceeded grid height {RowsGrid.Bounds.Height}.");
            Assert.True(
                first.Popup.Bounds.Width <= RowsGrid.Bounds.Width,
                $"Quick Look popup width {first.Popup.Bounds.Width} exceeded grid width {RowsGrid.Bounds.Width}.");
            Assert.True(
                first.Popup.Bounds.Height <= RowsGrid.Bounds.Height,
                $"Quick Look popup height {first.Popup.Bounds.Height} exceeded grid height {RowsGrid.Bounds.Height}.");
            AssertContained(first.Popup, first.Host, "Quick Look popup");
            AssertContained(first.Dialog, first.Host, "Quick Look dialog");

            var close = first.Popup.GetVisualDescendants()
                .OfType<Button>()
                .Single(candidate => string.Equals(
                    AutomationProperties.GetName(candidate),
                    "Close the database cell Quick Look",
                    StringComparison.Ordinal));
            first.Editor.Text = originalText + " cancelled";
            var closeRaised = false;
            close.Click += (_, _) => closeRaised = true;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(closeRaised);
            Dispatcher.UIThread.RunJobs();
            await WaitOnDispatcherAsync(
                () => !first.Host.GetVisualDescendants().Contains(first.Editor),
                cancellationToken);
            Assert.Equal(originalText, Cell(row, columnName).EditText);

            var second = OpenQuickLook();
            if (isReadOnly)
            {
                var quickLookTopLevel = TopLevel.GetTopLevel(second.Editor)
                    ?? throw new InvalidOperationException("Quick Look has no input root.");
                quickLookTopLevel.KeyPress(
                    Key.Escape,
                    RawInputModifiers.None,
                    PhysicalKey.Escape,
                    keySymbol: null);
                Dispatcher.UIThread.RunJobs();
                await WaitOnDispatcherAsync(
                    () => !second.Host.GetVisualDescendants().Contains(second.Editor),
                    cancellationToken);
            }
            else
            {
                var apply = second.Popup.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(candidate => string.Equals(
                        AutomationProperties.GetName(candidate),
                        "Apply the Quick Look database cell value",
                        StringComparison.Ordinal));
                Assert.True(apply.IsEffectivelyVisible);
                var applyBottomRight = apply.TranslatePoint(
                    new Point(apply.Bounds.Width, apply.Bounds.Height),
                    second.Dialog);
                Assert.NotNull(applyBottomRight);
                Assert.InRange(applyBottomRight.Value.X, 0, second.Dialog.Bounds.Width);
                Assert.InRange(applyBottomRight.Value.Y, 0, second.Dialog.Bounds.Height);

                var appliedText = originalText + " applied";
                second.Editor.Text = appliedText;
                var quickLookTopLevel = TopLevel.GetTopLevel(second.Editor)
                    ?? throw new InvalidOperationException("Quick Look has no input root.");
                var accelerator = OperatingSystem.IsMacOS()
                    ? RawInputModifiers.Meta
                    : RawInputModifiers.Control;
                quickLookTopLevel.KeyPress(
                    Key.Enter,
                    accelerator,
                    PhysicalKey.Enter,
                    keySymbol: null);
                Dispatcher.UIThread.RunJobs();
                await WaitOnDispatcherAsync(
                    () => !second.Host.GetVisualDescendants().Contains(second.Editor),
                    cancellationToken);
                Assert.Equal(appliedText, Cell(row, columnName).EditText);

                await Panel.RevertChangesAsync();
                Assert.False(Panel.HasPendingChanges);
            }

            Window.Width = 1280;
            Window.Height = 800;
            Dispatcher.UIThread.RunJobs();
            UpdateLayout();
        }

        private static void AssertContained(
            Visual child,
            Visual container,
            string description)
        {
            var topLeft = child.TranslatePoint(default, container);
            var bottomRight = child.TranslatePoint(
                new Point(child.Bounds.Width, child.Bounds.Height),
                container);
            Assert.NotNull(topLeft);
            Assert.NotNull(bottomRight);
            Assert.InRange(topLeft.Value.X, 0, container.Bounds.Width);
            Assert.InRange(topLeft.Value.Y, 0, container.Bounds.Height);
            Assert.InRange(bottomRight.Value.X, 0, container.Bounds.Width);
            Assert.InRange(bottomRight.Value.Y, 0, container.Bounds.Height);
            Assert.True(
                bottomRight.Value.X >= topLeft.Value.X
                    && bottomRight.Value.Y >= topLeft.Value.Y,
                $"{description} had invalid translated bounds "
                + $"({topLeft.Value} to {bottomRight.Value}) inside {container.Bounds}.");
        }

        public void InvokeContextMenuItem(string automationName)
        {
            var menu = ResultContextMenu;
            var item = ContextMenuItem(automationName);
            Assert.True(item.IsVisible, $"'{automationName}' is not visible.");
            Assert.True(item.IsEnabled, $"'{automationName}' is not enabled.");

            // Async handlers can replace rows. Close the popup before invoking the
            // real MenuItem so Avalonia does not lay out stale DataGrid containers.
            menu.Close();
            UpdateLayout();
            Assert.False(menu.IsOpen);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }

        public Task SetClipboardTextAsync(string value) =>
            Clipboard.SetTextAsync(value);

        public Task<string?> GetClipboardTextAsync() =>
            Clipboard.TryGetTextAsync();

        public bool CanBeginTextCellEdit(string code, string columnName)
        {
            var row = Assert.Single(Panel.ResultRows, candidate => string.Equals(Cell(candidate, "code").Text, code, StringComparison.Ordinal));
            var column = Assert.Single(
                RowsGrid.Columns,
                candidate => candidate.Tag is DatabaseResultColumnViewModel descriptor
                    && string.Equals(descriptor.Name, columnName, StringComparison.Ordinal));
            RowsGrid.SelectedItem = row;
            RowsGrid.CurrentColumn = column;
            RowsGrid.ScrollIntoView(row, column);
            UpdateLayout();
            return RowsGrid.BeginEdit();
        }

        public void UpdateLayout() => Window.UpdateLayout();

        public void Dispose()
        {
            // A panel intentionally lives across independent headless sessions.
            // Detach bindings on their owning dispatcher so an old view cannot
            // observe the shared panel from a disposed session thread.
            View.DataContext = null;
            Window.Content = null;
            Window.Close();
        }

        private IClipboard Clipboard =>
            TopLevel.GetTopLevel(View)?.Clipboard
            ?? throw new InvalidOperationException(
                "The headless database viewer did not provide a clipboard.");

        private Control? FocusedControl =>
            TopLevel.GetTopLevel(View)?.FocusManager?.GetFocusedElement() as Control;

        private ContextMenu ResultContextMenu =>
            RowsGrid.ContextMenu
            ?? throw new InvalidOperationException(
                "The real database viewer did not attach its context menu.");

        private void AssertMenuItemAvailable(string automationName)
        {
            var item = ContextMenuItem(automationName);
            Assert.True(item.IsVisible, $"'{automationName}' is not visible.");
            Assert.True(item.IsEnabled, $"'{automationName}' is not enabled.");
        }

        private MenuItem ContextMenuItem(string automationName) =>
            EnumerateMenuItems(ResultContextMenu)
                .SingleOrDefault(candidate => string.Equals(
                    AutomationProperties.GetName(candidate),
                    automationName,
                    StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The real database viewer did not render '{automationName}'.");

        private static IEnumerable<MenuItem> EnumerateMenuItems(ItemsControl owner)
        {
            foreach (var item in owner.Items.OfType<MenuItem>())
            {
                yield return item;
                foreach (var descendant in EnumerateMenuItems(item))
                {
                    yield return descendant;
                }
            }
        }

        private DataGridColumn ResultColumn(string columnName) =>
            Assert.Single(
                RowsGrid.Columns,
                candidate => candidate.Tag is DatabaseResultColumnViewModel descriptor
                    && string.Equals(descriptor.Name, columnName, StringComparison.Ordinal));

        private DataGridColumnHeader ColumnHeader(string columnName) =>
            ColumnHeaderContent(columnName)
                .GetVisualAncestors()
                .OfType<DataGridColumnHeader>()
                .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"The real DataGrid did not realize the '{columnName}' column header.");

        private Control ColumnHeaderContent(string columnName) =>
            RowsGrid.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(candidate => AutomationProperties.GetName(candidate)?.StartsWith(
                    $"Sort database column {columnName},",
                    StringComparison.Ordinal) == true)
            ?? throw new InvalidOperationException(
                $"The real DataGrid did not render the '{columnName}' accessible sort header.");

        private Button ObjectButton(string name) =>
            View.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(candidate =>
                    candidate.DataContext is DatabaseTableItemViewModel table
                    && string.Equals(table.Descriptor.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The real database viewer did not render the '{name}' object button.");

        private T NamedControl<T>(string automationName)
            where T : Control =>
            View.GetVisualDescendants()
                .OfType<T>()
                .FirstOrDefault(candidate => string.Equals(
                    AutomationProperties.GetName(candidate),
                    automationName,
                    StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The real database viewer did not render '{automationName}'.");
    }
}

public static class HeadlessTestApplication
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Asura.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

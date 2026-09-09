using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.Databases.IntegrationTests;

public sealed partial class DatabaseViewerConformanceTests
{
    private static readonly TimeSpan PanelOperationTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PanelCancellationGracePeriod = TimeSpan.FromSeconds(10);

    private static async Task AssertViewModelJourneyAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        CancellationToken cancellationToken)
    {
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            $"{environment.Provider.DisplayName} conformance",
            client,
            driverId: environment.Provider.Id,
            connectionString: environment.ConnectionString);
        await AwaitPanelOperationAsync(panel, panel.Initialization, cancellationToken);

        Assert.True(panel.IsConnected);
        Assert.False(panel.IsBusy);
        Assert.False(panel.HasError, panel.ErrorMessage);
        Assert.True(panel.CanChangeConnection);

        panel.TableFilter = "viewer_rows";
        Assert.Contains(
            panel.Tables,
            table => string.Equals(table.Descriptor.Name, environment.Provider.Seed.RowsTable, StringComparison.Ordinal));
        panel.TableFilter = string.Empty;

        var rowsTable = FindTable(panel, environment.Provider.Seed.RowsTable);
        await AwaitPanelOperationAsync(
            panel,
            panel.PreviewTableAsync(rowsTable),
            cancellationToken);
        Assert.Equal(200, panel.ResultRows.Count);
        Assert.True(panel.HasNextPage);
        Assert.Equal(205, panel.TotalRows);
        Assert.Equal("200", panel.PageLimitText);
        if (string.Equals(environment.Provider.Id, "postgres", StringComparison.Ordinal))
        {
            Assert.IsType<int>(Cell(panel.ResultRows[0], "id").RawValue);
        }

        Assert.DoesNotContain(panel.ResultRows, row => row.IsDirty);
        Assert.False(panel.HasPendingChanges, panel.ErrorMessage);
        Assert.NotEmpty(panel.StructureColumns);
        Assert.Equal(environment.Provider.Expectations.HasIndexes, panel.Indexes.Count > 0);
        Assert.Equal(environment.Provider.Expectations.CanEdit, panel.CanEditRows);
        AssertRenderedMetadata(panel, environment.Provider.Expectations);

        panel.SetMode(DatabaseWorkspaceMode.Structure);
        Assert.True(panel.ShowStructure);
        panel.SetMode(DatabaseWorkspaceMode.Indexes);
        Assert.True(panel.ShowIndexes);
        panel.SetMode(DatabaseWorkspaceMode.Data);
        Assert.True(panel.ShowData);

        panel.PageLimitText = "75";
        await AwaitPanelOperationAsync(panel, panel.ApplyPageLimitAsync(), cancellationToken);
        Assert.Equal(75, panel.ResultRows.Count);
        Assert.Equal(205, panel.TotalRows);
        Assert.Equal("75", panel.PageLimitText);
        await AwaitPanelOperationAsync(panel, panel.NextPageAsync(), cancellationToken);
        Assert.Equal(75, panel.ResultRows.Count);
        Assert.True(panel.HasPreviousPage);
        panel.PageLimitText = "200";
        await AwaitPanelOperationAsync(panel, panel.ApplyPageLimitAsync(), cancellationToken);
        Assert.Equal(200, panel.ResultRows.Count);
        Assert.False(panel.HasPreviousPage);

        await AwaitPanelOperationAsync(panel, panel.NextPageAsync(), cancellationToken);
        Assert.True(panel.HasPreviousPage);
        Assert.Equal(5, panel.ResultRows.Count);
        await AwaitPanelOperationAsync(panel, panel.PreviousPageAsync(), cancellationToken);
        Assert.False(panel.HasPreviousPage);
        Assert.Equal(200, panel.ResultRows.Count);

        panel.FilterColumn = Assert.Single(panel.FilterColumns, column => string.Equals(column.Name, "code", StringComparison.Ordinal));
        panel.FilterOperator = Assert.Single(
            panel.FilterOperators,
            item => item.Operator == DatabaseFilterOperator.Equal);
        panel.FilterValue = "alpha";
        await AwaitPanelOperationAsync(panel, panel.ApplyFilterAsync(), cancellationToken);
        Assert.Single(panel.ResultRows);
        Assert.Equal("alpha", Cell(panel.ResultRows[0], "code").Text);
        await AwaitPanelOperationAsync(panel, panel.ClearFilterAsync(), cancellationToken);
        Assert.Equal(200, panel.ResultRows.Count);

        if (!environment.Provider.Expectations.CanEdit)
        {
            var count = panel.ResultRows.Count;
            panel.AddRow();
            Assert.Equal(count, panel.ResultRows.Count);
            Assert.False(panel.CanSaveChanges);
            Assert.True(panel.CanChangeConnection);
        }
        else
        {
            await AssertViewModelSaveAndNavigationAsync(
                client,
                environment,
                objects,
                panel,
                cancellationToken);
            await AssertViewModelAddDeleteAndRevertAsync(
                client,
                environment,
                objects,
                panel,
                cancellationToken);
            await AssertViewModelConflictRecoveryAsync(
                client,
                environment,
                objects,
                panel,
                cancellationToken);
        }

        await AssertViewModelQueryFailureRecoveryAsync(
            panel,
            environment.Provider.ReadySql,
            cancellationToken);
    }

    private static async Task AssertViewModelSaveAndNavigationAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        DatabaseRuntimePanelViewModel panel,
        CancellationToken cancellationToken)
    {
        var alpha = FindRow(panel, "alpha");
        Cell(alpha, "title").EditText = "Alpha saved by VM";
        Assert.Single(panel.ResultRows, row => row.IsDirty);
        Assert.True(panel.HasPendingChanges);
        Assert.True(panel.CanSaveChanges);

        await AwaitPanelOperationAsync(panel, panel.SaveChangesAsync(), cancellationToken);
        Assert.False(panel.IsBusy);
        Assert.False(panel.HasError, panel.ErrorMessage);
        Assert.False(panel.HasPendingChanges);
        Assert.False(panel.CanSaveChanges);
        Assert.True(panel.CanChangeConnection);
        Assert.True(panel.CanChangeSelectedObject);

        var saved = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            "alpha",
            cancellationToken);
        Assert.Equal("Alpha saved by VM", Value(saved, "title").DisplayText);

        await AwaitPanelOperationAsync(
            panel,
            panel.PreviewTableAsync(FindTable(panel, environment.Provider.Seed.KeylessTable)),
            cancellationToken);
        Assert.Equal(
            environment.Provider.Seed.KeylessTable,
            panel.SelectedObject?.Descriptor.Name);

        await AwaitPanelOperationAsync(
            panel,
            panel.PreviewTableAsync(FindTable(panel, environment.Provider.Seed.RowsTable)),
            cancellationToken);
        alpha = FindRow(panel, "alpha");
        Cell(alpha, "title").EditText = "Alpha";
        await AwaitPanelOperationAsync(panel, panel.SaveChangesAsync(), cancellationToken);
        Assert.False(panel.HasPendingChanges);
        Assert.True(panel.CanChangeConnection);
    }

    private static async Task AssertViewModelAddDeleteAndRevertAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        DatabaseRuntimePanelViewModel panel,
        CancellationToken cancellationToken)
    {
        var insertedCode = $"vm-{Guid.NewGuid():N}";
        panel.AddRow();
        var newRow = Assert.Single(panel.ResultRows, row => row.IsNew);
        Cell(newRow, "code").EditText = insertedCode;
        Cell(newRow, "title").EditText = "VM inserted";
        Cell(newRow, "score").EditText = "77.25";
        Cell(newRow, "enabled").BooleanValue = true;
        Cell(newRow, "note").EditText = "replace with NULL";
        panel.SetSelectedCellNull(ColumnOrdinal(panel, "note"));
        Assert.True(Cell(newRow, "note").IsNull);
        Cell(newRow, "status").EditText = "replace with DEFAULT";
        panel.SetSelectedCellDefault(ColumnOrdinal(panel, "status"));
        Assert.True(Cell(newRow, "status").IsDefault);
        Assert.True(newRow.IsValid, string.Join("; ", newRow.Cells
            .Where(cell => !cell.IsValid)
            .Select(cell => cell.ValidationError)));
        Assert.True(panel.CanSaveChanges);

        await AwaitPanelOperationAsync(panel, panel.SaveChangesAsync(), cancellationToken);
        Assert.False(panel.HasPendingChanges, panel.ErrorMessage);
        Assert.False(panel.HasError, panel.ErrorMessage);
        var inserted = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            insertedCode,
            cancellationToken);
        Assert.True(Value(inserted, "note").IsNull);
        Assert.Equal("draft", Value(inserted, "status").DisplayText);

        panel.FilterColumn = Assert.Single(panel.FilterColumns, column => string.Equals(column.Name, "code", StringComparison.Ordinal));
        panel.FilterOperator = Assert.Single(
            panel.FilterOperators,
            item => item.Operator == DatabaseFilterOperator.Equal);
        panel.FilterValue = insertedCode;
        await AwaitPanelOperationAsync(panel, panel.ApplyFilterAsync(), cancellationToken);
        var insertedRow = Assert.Single(panel.ResultRows);
        panel.SelectRow(insertedRow);
        panel.DeleteSelectedRow();
        Assert.True(panel.CanSaveChanges);
        await AwaitPanelOperationAsync(panel, panel.SaveChangesAsync(), cancellationToken);
        Assert.False(panel.HasPendingChanges);
        Assert.Empty((await LoadRowsByCodeAsync(
            client,
            environment,
            objects.Rows,
            insertedCode,
            cancellationToken)).ValueRows);

        await AwaitPanelOperationAsync(panel, panel.ClearFilterAsync(), cancellationToken);
        var beta = FindRow(panel, "beta");
        Cell(beta, "title").EditText = "Must be reverted";
        Assert.True(panel.HasPendingChanges);
        await AwaitPanelOperationAsync(panel, panel.RevertChangesAsync(), cancellationToken);
        Assert.False(panel.HasPendingChanges);
        Assert.True(panel.CanChangeConnection);
        var unchanged = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            "beta",
            cancellationToken);
        Assert.Equal("Beta", Value(unchanged, "title").DisplayText);
    }

    private static async Task AssertViewModelConflictRecoveryAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        DatabaseRuntimePanelViewModel panel,
        CancellationToken cancellationToken)
    {
        var alpha = FindRow(panel, "alpha");
        Cell(alpha, "title").EditText = "VM stale loser";
        var snapshot = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            "alpha",
            cancellationToken);
        var external = await ApplyAsync(
            client,
            environment,
            objects.Rows,
            new DatabaseTableChanges(
                [],
                [BuildUpdate(objects.RowsDetails, snapshot, "title", "External winner")],
                []),
            cancellationToken);
        Assert.Equal(1, external.Updated);

        await AwaitPanelOperationAsync(panel, panel.SaveChangesAsync(), cancellationToken);
        Assert.False(panel.IsBusy);
        Assert.True(panel.HasError);
        Assert.True(panel.HasPendingChanges);
        Assert.True(panel.CanSaveChanges);
        Assert.False(panel.CanChangeConnection);

        var selectedObject = panel.SelectedObject;
        var conflictMessage = panel.ErrorMessage;
        await AwaitPanelOperationAsync(
            panel,
            panel.PreviewTableAsync(FindTable(panel, environment.Provider.Seed.KeylessTable)),
            cancellationToken);
        Assert.Equal(selectedObject, panel.SelectedObject);
        Assert.Equal(conflictMessage, panel.ErrorMessage);

        await AwaitPanelOperationAsync(panel, panel.RevertChangesAsync(), cancellationToken);
        Assert.False(panel.IsBusy);
        Assert.False(panel.HasPendingChanges);
        Assert.True(panel.CanChangeConnection);
        Assert.False(panel.HasError, panel.ErrorMessage);

        alpha = FindRow(panel, "alpha");
        Assert.Equal("External winner", Cell(alpha, "title").Text);
        Cell(alpha, "title").EditText = "Alpha";
        await AwaitPanelOperationAsync(panel, panel.SaveChangesAsync(), cancellationToken);
        Assert.False(panel.HasPendingChanges);
        Assert.False(panel.HasError, panel.ErrorMessage);
    }

    private static async Task AssertViewModelQueryFailureRecoveryAsync(
        DatabaseRuntimePanelViewModel panel,
        string readySql,
        CancellationToken cancellationToken)
    {
        panel.QueryText = "SELECT * FROM asura_table_that_does_not_exist";
        await AwaitPanelOperationAsync(panel, panel.RunQueryAsync(), cancellationToken);
        Assert.False(panel.IsBusy);
        Assert.True(panel.HasError);
        Assert.False(string.IsNullOrWhiteSpace(panel.ErrorMessage));
        Assert.True(panel.CanChangeConnection);

        panel.QueryText = readySql;
        await AwaitPanelOperationAsync(panel, panel.RunQueryAsync(), cancellationToken);
        Assert.False(panel.IsBusy);
        Assert.False(panel.HasError, panel.ErrorMessage);
        Assert.True(panel.CanChangeConnection);
    }

    private static DatabaseTableItemViewModel FindTable(
        DatabaseRuntimePanelViewModel panel,
        string name) =>
        Assert.Single(panel.Tables, table => string.Equals(table.Descriptor.Name, name, StringComparison.Ordinal));

    private static DatabaseResultRowViewModel FindRow(
        DatabaseRuntimePanelViewModel panel,
        string code) =>
        Assert.Single(panel.ResultRows, row => string.Equals(Cell(row, "code").Text, code, StringComparison.Ordinal));

    private static DatabaseResultCellViewModel Cell(
        DatabaseResultRowViewModel row,
        string columnName) =>
        Assert.Single(row.Cells, cell => string.Equals(cell.Column.Name, columnName, StringComparison.Ordinal));

    private static int ColumnOrdinal(
        DatabaseRuntimePanelViewModel panel,
        string columnName) =>
        panel.ResultColumns
            .Select((column, ordinal) => (column, ordinal))
            .Single(pair => string.Equals(pair.column.Descriptor.Name, columnName, StringComparison.Ordinal))
            .ordinal;

    private static void AssertRenderedMetadata(
        DatabaseRuntimePanelViewModel panel,
        DatabaseProviderExpectations expectations)
    {
        var note = Assert.Single(panel.StructureColumns, column => string.Equals(column.Name, "note", StringComparison.Ordinal));
        var status = Assert.Single(panel.StructureColumns, column => string.Equals(column.Name, "status", StringComparison.Ordinal));
        var generated = Assert.Single(
            panel.StructureColumns,
            column => string.Equals(column.Name, "computed_label", StringComparison.Ordinal));
        Assert.Equal("Yes", note.Nullable);
        Assert.Equal("No", status.Nullable);
        Assert.Contains("draft", status.Default, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            expectations.HasGeneratedColumn,
            generated.Flags.Contains("generated", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            expectations.HasGeneratedColumn,
            generated.Flags.Contains("read-only", StringComparison.OrdinalIgnoreCase));

        if (expectations.ExpectedCodeLength is { } codeLength)
        {
            var code = Assert.Single(panel.StructureColumns, column => string.Equals(column.Name, "code", StringComparison.Ordinal));
            Assert.Contains(codeLength.ToString(System.Globalization.CultureInfo.InvariantCulture), code.Type, StringComparison.Ordinal);
        }

        if (expectations.ExpectedScorePrecision is { } precision
            && expectations.ExpectedScoreScale is { } scale)
        {
            var score = Assert.Single(panel.StructureColumns, column => string.Equals(column.Name, "score", StringComparison.Ordinal));
            var compactType = score.Type.Replace(" ", string.Empty, StringComparison.Ordinal);
            Assert.Contains($"({precision},{scale})", compactType, StringComparison.Ordinal);
        }

        if (!expectations.HasIndexes)
        {
            Assert.Empty(panel.Indexes);
            return;
        }

        Assert.NotNull(expectations.ScoreIndex);
        var indexExpectations = expectations.ScoreIndex;
        var index = Assert.Single(
            panel.Indexes,
            item => string.Equals(item.Name, "idx_viewer_rows_score", StringComparison.Ordinal));
        Assert.Equal("No", index.Unique);
        Assert.Equal("Valid", index.Status);
        Assert.Contains("score", index.Columns, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            indexExpectations.FirstColumnDescending,
            index.Columns.Contains("score DESC", StringComparison.OrdinalIgnoreCase));
        if (indexExpectations.IncludedColumn is { } includedColumn)
        {
            Assert.Contains(
                $"{includedColumn} INCLUDE",
                index.Columns,
                StringComparison.OrdinalIgnoreCase);
        }

        if (indexExpectations.PredicateFragment is { } predicateFragment)
        {
            Assert.Contains(
                predicateFragment,
                index.Predicate,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task AwaitPanelOperationAsync(
        DatabaseRuntimePanelViewModel panel,
        Task operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(PanelOperationTimeout, cancellationToken);
        }
        catch (TimeoutException timeout) when (!operation.IsCompleted)
        {
            // WaitAsync leaves its wrapped task running. Disposing the panel is
            // its lifetime-cancellation boundary, then this grace period lets a
            // cooperative database operation finish before shared teardown.
            try
            {
                panel.Dispose();
                await operation.WaitAsync(
                    PanelCancellationGracePeriod,
                    CancellationToken.None);
            }
            catch (Exception shutdownFailure)
            {
                timeout.Data["Panel shutdown failure"] = shutdownFailure.ToString();
            }

            throw;
        }
    }
}

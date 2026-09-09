using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class DatabaseRuntimePanelViewModelTests
{
    [Theory]
    [InlineData("unchanged", true)]
    [InlineData("target", false)]
    [InlineData("route", false)]
    [InlineData("password", false)]
    [InlineData("failed", false)]
    public async Task FirstRecoverySavePreservesStartupOnlyForTheAcceptedInitialBinding(string change, bool preserves)
    {
        using var vault = new DatabaseRecoveryStateTests.TestVault();
        var route = new ConnectionProfile(new ConnectionId("original-route"), 1, "Route",
            new ConnectionEndpoint.Ssh("original.invalid", username: "user"),
            new ConnectionAuthentication.None(), ConnectionStartup.Default, ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.AcceptNew);
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", client,
            "postgres", "Host=fixture.invalid", route, sessionPassword: "fixture", recovery: new DatabaseRecoveryState(vault));
        var source = new ScreenPanelDefinition(ScreenPanelId.New(), new LayoutSlotId("database"),
            ScreenPanelKind.DatabaseViewer, "Database", route.Id,
            new PanelStartupBehavior("postgres:Host=fixture.invalid", ["select 1"],
                StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure));
        panel.SourceDefinition = source;
        Assert.False(panel.CanPreserveInitialSourceConnection);
        panel.StartInitialization();
        await panel.Initialization;
        Assert.True(panel.CanPreserveInitialSourceConnection);
        switch (change)
        {
            case "target": panel.ConnectionString = "Host=other.invalid"; break;
            case "route":
                panel.SetTunnel(new ConnectionProfile(route.Id, 1, "Route",
                    new ConnectionEndpoint.Ssh("changed.invalid", username: "user"), route.Authentication,
                    route.Startup, route.KeepAlive, route.HostKeyPolicy));
                break;
            case "password": panel.SetSessionPassword("replacement"); break;
            case "failed": client.FailWith = "fixture failure"; break;
        }
        while (panel.IsBusy) { await Task.Yield(); }
        await panel.ConnectAsync();
        Assert.Equal(preserves, panel.CanPreserveInitialSourceConnection);
        var captured = WorkspaceAutoSaveCoordinatorTests.CaptureDatabasePanel(panel, source);
        Assert.Null(captured.ConnectionId);
        Assert.NotNull(DatabaseRecoveryToken.TryParse(captured.Startup.Location));
        Assert.Equal(preserves ? source.Startup.Commands : [], captured.Startup.Commands);
        if (preserves)
        {
            Assert.Equal(source.Startup.DeliveryFailurePolicy, captured.Startup.DeliveryFailurePolicy);
            Assert.Equal(source.Id, captured.Id);
            Assert.Equal(source.Startup.Commands,
                WorkspaceAutoSaveCoordinatorTests.CaptureDatabasePanel(panel, captured).Startup.Commands);
        }
    }

    [Fact]
    public async Task ReplacingQueryPageAndClosingViewReleaseTheirDetachedContent()
    {
        var stores = new List<ResultOwnershipFixture>();
        var client = new FakeDatabasePanelClient
        {
            ResultContentFactory = () =>
            {
                var store = new ResultOwnershipFixture();
                stores.Add(store);
                return store;
            },
            TableHasMore = true,
            TableTotalRows = 1000,
        };
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", client,
            driverId: "sqlite", connectionString: "Data Source=fixture.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        Assert.Equal(1, Assert.Single(stores).Owners);
        Assert.True(stores[0].PrimaryReleased);
        await panel.NextPageAsync();
        Assert.Equal(2, stores.Count);
        Assert.Equal(0, stores[0].Owners);
        Assert.Equal(1, stores[1].Owners);
        panel.Dispose();
        Assert.Equal(0, stores[1].Owners);
    }

    private sealed class ResultOwnershipFixture : DatabaseValueContentStore
    {
        public int Owners { get; private set; } = 1;
        public bool PrimaryReleased { get; private set; }
        public override IDisposable Retain()
        {
            Assert.True(Owners > 0);
            Owners++;
            return new Lease(this);
        }
        public override Task<DatabaseValueContent> StoreAsync(DatabaseValueKind kind,
            Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !PrimaryReleased)
            {
                PrimaryReleased = true;
                Owners--;
            }
        }
        private sealed class Lease(ResultOwnershipFixture owner) : IDisposable
        {
            private ResultOwnershipFixture? _owner = owner;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _owner, null) is { } retained)
                {
                    retained.Owners--;
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncClipboardSnapshotRunsOffThreadAndCannotPublishAfterSupersessionOrClose(bool close)
    {
        var store = new ResultOwnershipFixture();
        var client = new FakeDatabasePanelClient { ResultContentFactory = () => store };
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", client,
            driverId: "sqlite", connectionString: "Data Source=fixture.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        using var gate = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = panel.ResultRows[0];
        var content = new BlockedClipboardContent(gate, started);
        var row = new DatabaseResultRowViewModel(1,
            [.. original.Cells.Select((cell, index) => index == 1
                ? new DatabaseValue(content, DatabaseValueKind.Text, "preview", true)
                : new DatabaseValue(cell.RawValue, cell.Column.ValueKind, cell.Text))],
            [.. original.Cells.Select(cell => cell.Column)], [.. original.Cells.Select(cell => cell.Width)], canEdit: false);
        var oldCopy = panel.BuildCellValueAsync(row, 1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(panel.IsBusy);
        Assert.Equal(2, store.Owners);
        try
        {
            if (close) { panel.Dispose(); }
            else
            {
                var newer = await panel.BuildCellValueAsync(panel.ResultRows[1], 0);
                Assert.True(panel.IsClipboardRequestCurrent(newer.Revision));
                Assert.False(panel.HasInterchangeNotice);
            }
        }
        finally { gate.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldCopy);
        Assert.Equal(close ? 0 : 1, store.Owners);
        Assert.False(panel.IsBusy);
    }

    private sealed class BlockedClipboardContent(ManualResetEventSlim gate, TaskCompletionSource started) : DatabaseValueContent
    {
        public override long Length => 8;
        public override DatabaseValueKind Kind => DatabaseValueKind.Text;
        public override Stream OpenRead()
        {
            started.TrySetResult();
            if (!gate.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("Clipboard test was not released."); }
            return new MemoryStream(" =1+1"u8.ToArray(), writable: false);
        }
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    [InlineData("insert")]
    public async Task InspectorCopyFormatsUseCompleteSnapshotAndOriginalDriverFormatter(string format)
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", client,
            driverId: "sqlite", connectionString: "Data Source=fixture.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        using var gate = new ManualResetEventSlim(true);
        var original = panel.ResultRows[0];
        var content = new BlockedClipboardContent(gate, new TaskCompletionSource());
        var row = new DatabaseResultRowViewModel(1,
            [.. original.Cells.Select((cell, index) => index == 1
                ? new DatabaseValue(content, DatabaseValueKind.Text, "preview", true)
                : new DatabaseValue(cell.RawValue, cell.Column.ValueKind, cell.Text))],
            [.. original.Cells.Select(cell => cell.Column)], [.. original.Cells.Select(cell => cell.Width)], canEdit: false);
        var snapshot = format switch
        {
            "csv" => await panel.BuildRowCsvAsync(row),
            "json" => await panel.BuildRowJsonAsync(row),
            _ => await panel.BuildRowSqlInsertAsync(row),
        };
        Assert.True(panel.IsClipboardRequestCurrent(snapshot.Revision));
        Assert.Contains(" =1+1", snapshot.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("preview", snapshot.Text, StringComparison.Ordinal);
        if (format == "insert")
        {
            Assert.Equal(" =1+1", Assert.Single(client.LastFormattedInsert!.Values, value => value.ColumnName == "name").Value);
        }
        Assert.Equal(format == "csv", panel.HasInterchangeNotice);
    }

    [Fact]
    public async Task TemporaryPasswordOverrideDoesNotReuseTheSavedProfilesOlderCredential()
    {
        using var vault = new DatabaseRecoveryStateTests.TestVault();
        var state = new DatabaseRecoveryState(vault);
        var profile = new DatabaseConnectionProfile(DatabaseConnectionProfileId.New(), 1, "Saved", "postgres",
            "Host=fixture.invalid", SecretRef.New());
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", new FakeDatabasePanelClient(),
            savedConnection: profile, sessionPassword: "temporary-fixture", recovery: state);
        Assert.Null(panel.RecoveryTarget);
        panel.StartInitialization();
        await panel.Initialization;
        Assert.NotNull(DatabaseRecoveryToken.TryParse(panel.RecoveryTarget));
        Assert.Equal("temporary-fixture", (await state.RestoreAsync(CancellationToken.None))!.SessionPassword);
    }

    [Fact]
    public async Task AdHocRecoveryWaitsForAcceptanceAndRetainsSessionPassword()
    {
        using var vault = new DatabaseRecoveryStateTests.TestVault();
        var state = new DatabaseRecoveryState(vault);
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", client,
            "postgres", "Host=fixture.invalid", sessionPassword: "session-fixture", recovery: state);
        Assert.Null(panel.RecoveryTarget);
        Assert.Null(client.LastConnectionString);
        Assert.Equal(0, vault.Creates);
        panel.StartInitialization();
        await panel.Initialization;

        Assert.Equal("Host=fixture.invalid;Password=session-fixture", client.LastConnectionString);
        Assert.NotNull(DatabaseRecoveryToken.TryParse(panel.RecoveryTarget));
        Assert.Equal(client.LastConnectionString, (await state.RestoreAsync(CancellationToken.None))!.ConnectionString);
        Assert.Equal(1, vault.Creates);
    }

    [Fact]
    public async Task LiveSavedTargetChangeUsesTokenAndFailedWriteKeepsWorkingConfiguration()
    {
        using var vault = new DatabaseRecoveryStateTests.TestVault();
        var state = new DatabaseRecoveryState(vault);
        var profile = new DatabaseConnectionProfile(DatabaseConnectionProfileId.New(), 1, "Saved", "postgres", "Host=first;Password=fixture");
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", client,
            savedConnection: profile, recovery: state);
        Assert.Equal("saved:" + profile.Id.Value, panel.RecoveryTarget);
        panel.ConnectionString = "Host=second;Password=fixture";
        await panel.ConnectAsync();
        var target = panel.RecoveryTarget;
        Assert.NotNull(DatabaseRecoveryToken.TryParse(target));
        vault.FailWrites = true;
        panel.ConnectionString = "Host=third;Password=fixture";
        await panel.ConnectAsync();

        Assert.True(panel.IsConnected);
        Assert.Equal("Host=third;Password=fixture", panel.ConnectionString);
        Assert.Equal(target, panel.RecoveryTarget);
        Assert.Contains("could not be saved", panel.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal("Host=second;Password=fixture", (await state.RestoreAsync(CancellationToken.None))!.ConnectionString);
    }

    [Fact]
    public void Provider_typed_rows_stay_clean_until_the_user_changes_text()
    {
        var timestamp = new DateTime(2026, 8, 8, 12, 30, 0, DateTimeKind.Utc);
        var row = new DatabaseResultRowViewModel(
            1,
            [
                new DatabaseValue(7, DatabaseValueKind.SignedInteger, "7"),
                new DatabaseValue(timestamp, DatabaseValueKind.TimestampWithZone, timestamp.ToString("O")),
            ],
            [
                new DatabaseColumnDescriptor(
                    "id",
                    "integer",
                    DatabaseValueKind.SignedInteger,
                    typeof(int).FullName,
                    IsNullable: false,
                    IsKey: true),
                new DatabaseColumnDescriptor(
                    "updated_at",
                    "timestamp with time zone",
                    DatabaseValueKind.TimestampWithZone,
                    typeof(DateTime).FullName,
                    IsNullable: false),
            ],
            [100d, 220d],
            canEdit: true);

        Assert.False(row.IsDirty);
        Assert.IsType<int>(row.Cells[0].RawValue);

        row.Cells[0].EditText = "8";
        Assert.True(row.IsDirty);

        row.Cells[0].EditText = "7";
        Assert.False(row.IsDirty);
    }

    [Fact]
    public void Cells_preserve_provider_display_text_and_reset_file_bytes()
    {
        var originalBytes = Enumerable.Range(0, 40).Select(value => (byte)value).ToArray();
        var cell = new DatabaseResultCellViewModel(
            new DatabaseValue(
                originalBytes,
                DatabaseValueKind.Binary,
                "0x00010203… (40 bytes)",
                IsTruncated: true),
            new DatabaseColumnDescriptor(
                "payload",
                "BLOB",
                DatabaseValueKind.Binary,
                typeof(byte[]).FullName,
                IsNullable: true),
            width: 180,
            canEdit: true);

        Assert.Equal("0x00010203… (40 bytes)", cell.Text);
        Assert.Equal($"0x{Convert.ToHexString(originalBytes)}", cell.FullText);
        Assert.False(cell.IsEditable);
        Assert.True(cell.CanSetBinary);
        Assert.True(cell.CanSetNull);

        var replacement = new byte[] { 0x0A, 0x0B };
        cell.SetBinary(replacement);
        replacement[0] = 0xFF;

        Assert.Equal("0x0A0B", cell.Text);
        Assert.Equal("0x0A0B", cell.FullText);
        Assert.Equal(new byte[] { 0x0A, 0x0B }, Assert.IsType<byte[]>(cell.RawValue));
        Assert.True(cell.IsDirty);

        cell.Reset();

        Assert.Equal("0x00010203… (40 bytes)", cell.Text);
        Assert.Equal($"0x{Convert.ToHexString(originalBytes)}", cell.FullText);
        Assert.Same(originalBytes, cell.RawValue);
        Assert.False(cell.IsDirty);
    }

    [Fact]
    public void Full_cell_text_formats_complete_collections_and_uses_display_for_other_values()
    {
        var collection = Enumerable.Range(0, 40).ToArray();
        var collectionCell = new DatabaseResultCellViewModel(
            new DatabaseValue(
                collection,
                DatabaseValueKind.Collection,
                "[0, 1, 2, 3]… (40 values)",
                IsTruncated: true),
            new DatabaseColumnDescriptor("tags", "ARRAY", DatabaseValueKind.Collection),
            width: 180,
            canEdit: false);
        var otherCell = new DatabaseResultCellViewModel(
            new DatabaseValue("provider-owned", DatabaseValueKind.Other, "safe display"),
            new DatabaseColumnDescriptor("opaque", "OBJECT", DatabaseValueKind.Other),
            width: 180,
            canEdit: false);

        Assert.Equal(
            $"[{string.Join(", ", collection)}]",
            collectionCell.FullText);
        Assert.Equal("safe display", otherCell.FullText);
    }

    [Fact]
    public void Duplicate_row_preserves_safe_states_and_detaches_binary_values()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var source = new DatabaseResultRowViewModel(
            1,
            [
                new DatabaseValue(7L, DatabaseValueKind.SignedInteger, "7"),
                new DatabaseValue(9L, DatabaseValueKind.SignedInteger, "9"),
                new DatabaseValue(11, DatabaseValueKind.SignedInteger, "11"),
                new DatabaseValue("Ada", DatabaseValueKind.Text, "Ada"),
                new DatabaseValue(bytes, DatabaseValueKind.Binary, "0x010203"),
                new DatabaseValue("computed", DatabaseValueKind.Text, "computed"),
            ],
            [
                new DatabaseColumnDescriptor(
                    "generated_id",
                    "BIGINT",
                    DatabaseValueKind.SignedInteger,
                    IsIdentity: true),
                new DatabaseColumnDescriptor(
                    "default_id",
                    "BIGINT",
                    DatabaseValueKind.SignedInteger,
                    IsNullable: false,
                    IsKey: true,
                    DefaultExpression: "nextval('items_id_seq')"),
                new DatabaseColumnDescriptor(
                    "manual_id",
                    "INTEGER",
                    DatabaseValueKind.SignedInteger,
                    IsNullable: false,
                    IsKey: true),
                new DatabaseColumnDescriptor(
                    "name",
                    "TEXT",
                    DatabaseValueKind.Text,
                    IsNullable: true),
                new DatabaseColumnDescriptor(
                    "payload",
                    "BLOB",
                    DatabaseValueKind.Binary,
                    IsNullable: true),
                new DatabaseColumnDescriptor(
                    "computed",
                    "TEXT",
                    DatabaseValueKind.Text,
                    IsReadOnly: true),
            ],
            [100d, 100d, 100d, 140d, 180d, 140d],
            canEdit: true);

        var duplicate = source.DuplicateAsNew(2);

        Assert.True(duplicate.IsNew);
        Assert.Equal(DatabaseEditValueState.Default, duplicate.Cells[0].State);
        Assert.Equal(DatabaseEditValueState.Default, duplicate.Cells[1].State);
        Assert.Equal(DatabaseEditValueState.Value, duplicate.Cells[2].State);
        Assert.IsType<int>(duplicate.Cells[2].RawValue);
        Assert.Equal("Ada", duplicate.Cells[3].RawValue);
        Assert.False(duplicate.Cells[3].IsNull);
        Assert.Equal(DatabaseEditValueState.Default, duplicate.Cells[5].State);
        var copiedBytes = Assert.IsType<byte[]>(duplicate.Cells[4].RawValue);
        Assert.Equal(bytes, copiedBytes);
        Assert.NotSame(bytes, copiedBytes);
    }

    [Fact]
    public void Scalar_paste_validates_booleans_without_conflating_null()
    {
        var cell = new DatabaseResultCellViewModel(
            new DatabaseValue(true, DatabaseValueKind.Boolean, "true"),
            new DatabaseColumnDescriptor(
                "enabled",
                "BOOLEAN",
                DatabaseValueKind.Boolean,
                IsNullable: true),
            width: 100,
            canEdit: true);

        cell.SetText("not-a-boolean");
        Assert.False(cell.IsValid);
        Assert.Equal("not-a-boolean", cell.Text);
        Assert.False(cell.IsNull);

        cell.SetText("false");
        Assert.True(cell.IsValid);
        Assert.False(Assert.IsType<bool>(cell.RawValue));

        cell.SetNull();
        Assert.True(cell.IsNull);
        Assert.Null(cell.RawValue);
    }

    [Fact]
    public async Task Connect_lists_tables_and_publishes_the_durable_target()
    {
        var client = new FakeDatabasePanelClient();
        using var vault = new DatabaseRecoveryStateTests.TestVault();
        var recovery = new DatabaseRecoveryState(vault);
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client, recovery: recovery);

        Assert.Equal(PanelKind.DatabaseViewer, panel.Kind);
        Assert.False(panel.IsConnected);
        Assert.Null(panel.RecoveryTarget);

        panel.ConnectionString = "Data Source=demo.db";
        await panel.ConnectAsync();

        Assert.True(panel.IsConnected);
        Assert.Equal(["people", "names"], panel.Tables.Select(table => table.Name), StringComparer.Ordinal);
        Assert.NotNull(DatabaseRecoveryToken.TryParse(panel.RecoveryTarget));
        Assert.Equal("Data Source=demo.db", (await recovery.RestoreAsync(CancellationToken.None))!.ConnectionString);

        // Editing the target drops the connected state until re-probed.
        panel.ConnectionString = "Data Source=other.db";
        Assert.False(panel.IsConnected);
    }

    [Fact]
    public async Task Restored_panel_reconnects_from_its_saved_target()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");

        await panel.Initialization;

        Assert.True(panel.IsConnected);
        Assert.Equal("sqlite", panel.SelectedDriver.Id);
        Assert.Equal(2, panel.Tables.Count);
    }

    [Fact]
    public async Task Query_results_render_null_cells_and_the_summary()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;

        panel.QueryText = "SELECT id, name FROM people;";
        await panel.RunQueryAsync();

        Assert.True(panel.HasResults);
        Assert.Equal(["id", "name"], panel.ResultColumns.Select(column => column.Name), StringComparer.Ordinal);
        var lastRow = panel.ResultRows[^1];
        Assert.True(lastRow.Cells[1].IsNull);
        Assert.Equal("NULL", lastRow.Cells[1].Text);
        Assert.StartsWith("2 rows", panel.ResultSummary, StringComparison.Ordinal);
        Assert.Equal("SELECT id, name FROM people;", client.LastSql);
    }

    [Fact]
    public async Task Failures_surface_inline_and_clear_on_the_next_operation()
    {
        var client = new FakeDatabasePanelClient { FailWith = "no such table: missing" };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client);
        panel.ConnectionString = "Data Source=demo.db";

        await panel.ConnectAsync();

        Assert.False(panel.IsConnected);
        Assert.True(panel.HasError);
        Assert.Equal("no such table: missing", panel.ErrorMessage);

        client.FailWith = null;
        await panel.ConnectAsync();

        Assert.True(panel.IsConnected);
        Assert.False(panel.HasError);
    }

    [Fact]
    public async Task Selecting_a_row_fills_the_field_inspector_and_toggles_off()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        panel.QueryText = "SELECT id, name FROM people;";
        await panel.RunQueryAsync();

        Assert.Equal([1, 2], panel.ResultRows.Select(row => row.Number));
        Assert.False(panel.HasSelectedRow);

        panel.SelectRow(panel.ResultRows[1]);

        Assert.True(panel.HasSelectedRow);
        Assert.True(panel.ResultRows[1].IsSelected);
        Assert.Equal(
            [("id", "2", false), ("name", "NULL", true)],
            panel.SelectedRowFields.Select(field => (field.Name, field.Text, field.IsNull)));

        // Selecting the same row again clears the inspector; a fresh result
        // set also starts unselected.
        panel.SelectRow(panel.ResultRows[1]);
        Assert.False(panel.HasSelectedRow);
        Assert.False(panel.ResultRows[1].IsSelected);

        panel.SelectRow(panel.ResultRows[0]);
        await panel.RunQueryAsync();
        Assert.False(panel.HasSelectedRow);
    }

    [Fact]
    public async Task Switching_the_tunnel_reconnects_through_the_new_route()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        // The pill names the bound connection; a raw target reads as its
        // driver. The tunnel is connection configuration, not the label.
        Assert.Equal("SQLite", panel.ConnectionDisplayName);
        Assert.Null(client.LastTunnel);

        var bastion = new ConnectionProfile(
            new ConnectionId("bastion"),
            ConnectionProfile.CurrentSchemaVersion,
            "bastion-eu",
            new ConnectionEndpoint.Ssh("bastion.example.test", username: "ops"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.AcceptNew);
        panel.SetTunnel(bastion);
        await panel.Initialization;
        // SetTunnel re-probes asynchronously; wait for the busy window to close.
        while (panel.IsBusy)
        {
            await Task.Yield();
        }

        Assert.Equal("SQLite", panel.ConnectionDisplayName);
        Assert.Equal(bastion.Id, panel.TunnelConnectionId);
        Assert.True(panel.IsConnected);
        Assert.Same(bastion, client.LastTunnel);

        // A local connection means direct again.
        var local = new ConnectionProfile(
            new ConnectionId("local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        panel.SetTunnel(local);
        while (panel.IsBusy)
        {
            await Task.Yield();
        }

        Assert.Equal("SQLite", panel.ConnectionDisplayName);
        Assert.Null(panel.TunnelConnectionId);
        Assert.Null(client.LastTunnel);
    }

    [Fact]
    public async Task Saved_connection_shows_its_name_and_injects_the_vault_password()
    {
        var client = new FakeDatabasePanelClient();
        var secret = SecretRef.New();
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "prod-core",
            "postgres",
            "Host=db.internal;Database=app",
            secret);
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            savedConnection: profile,
            passwordResolver: (reference, _) => Task.FromResult<string?>(
                reference == profile ? "vaulted" : null));
        await panel.Initialization;

        Assert.True(panel.IsSavedConnection);
        Assert.Equal("prod-core", panel.AddressBarText);
        Assert.Equal("postgres", panel.SelectedDriver.Id);
        Assert.Equal($"saved:{profile.Id.Value}", panel.RecoveryTarget);
        Assert.True(panel.IsConnected);
        Assert.Equal(
            "Host=db.internal;Database=app;Password=vaulted",
            client.LastConnectionString);
    }

    [Fact]
    public async Task Restored_saved_connection_waits_for_connect_before_resolving_credential()
    {
        var client = new FakeDatabasePanelClient();
        var secret = SecretRef.New();
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "neon",
            "postgres",
            "Host=db.internal;Database=app",
            secret);
        var resolveCount = 0;
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            savedConnection: profile,
            passwordResolver: (reference, _) =>
            {
                Assert.Equal(profile, reference);
                resolveCount++;
                return Task.FromResult<string?>("vaulted");
            },
            deferStoredCredentialAccess: true);

        await panel.Initialization;

        Assert.Equal(0, resolveCount);
        Assert.False(panel.IsConnected);

        var resolvesBeforeConnect = resolveCount;
        await panel.ConnectAsync();

        Assert.True(resolveCount > resolvesBeforeConnect);
        Assert.True(panel.IsConnected);
    }

    [Fact]
    public async Task Saved_connection_without_password_asks_before_connecting()
    {
        var client = new FakeDatabasePanelClient();
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "prod-core",
            "postgres",
            "Host=db.internal;Database=app");
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            savedConnection: profile);
        var prompts = 0;
        panel.PasswordRequested += (_, _) => prompts++;
        await panel.Initialization;

        // Construction must not connect: the prompt needs a view first.
        Assert.False(panel.IsConnected);

        await panel.ConnectAsync();
        Assert.Equal(1, prompts);
        Assert.False(panel.IsConnected);

        panel.SetSessionPassword("typed");
        await panel.ConnectAsync();
        Assert.True(panel.IsConnected);
        Assert.Equal(
            "Host=db.internal;Database=app;Password=typed",
            client.LastConnectionString);
    }

    [Fact]
    public async Task Prompted_password_can_be_persisted_for_the_saved_profile()
    {
        var client = new FakeDatabasePanelClient();
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "prod-core",
            "postgres",
            "Host=db.internal;Database=app");
        DatabaseConnectionProfileId? persistedId = null;
        string? persistedPassword = null;
        var secret = SecretRef.New();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            savedConnection: profile,
            passwordPersister: (profileId, password, _) =>
            {
                persistedId = profileId;
                persistedPassword = password;
                return Task.FromResult<DatabaseConnectionProfile?>(new(
                    profile.Id,
                    profile.SchemaVersion,
                    profile.Name,
                    profile.DriverId,
                    profile.ConnectionString,
                    secret));
            },
            passwordStoreLabel: "Save in macOS Keychain");

        Assert.True(panel.CanStorePassword);
        Assert.Equal("Save in macOS Keychain", panel.PasswordStoreLabel);

        var stored = await panel.StoreSessionPasswordAsync("typed");

        Assert.True(stored);
        Assert.Equal(profile.Id, persistedId);
        Assert.Equal("typed", persistedPassword);
        Assert.False(panel.CanStorePassword);
    }

    [Fact]
    public async Task Editing_details_detaches_the_panel_from_the_saved_connection()
    {
        var client = new FakeDatabasePanelClient();
        using var vault = new DatabaseRecoveryStateTests.TestVault();
        var recovery = new DatabaseRecoveryState(vault);
        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "prod-core",
            "postgres",
            "Host=db.internal;Database=app",
            SecretRef.New());
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            savedConnection: profile,
            passwordResolver: (_, _) => Task.FromResult<string?>("vaulted"), recovery: recovery);
        panel.StartInitialization();
        await panel.Initialization;
        Assert.True(panel.IsSavedConnection);

        await panel.ApplyConnectionDetailsAsync(
            new DatabaseConnectionDetails(Options: "Host=other;Database=app"));

        Assert.False(panel.IsSavedConnection);
        Assert.NotNull(DatabaseRecoveryToken.TryParse(panel.RecoveryTarget));
        Assert.Equal("Host=other;Database=app", (await recovery.RestoreAsync(CancellationToken.None))!.ConnectionString);
        Assert.Equal("Host=other;Database=app", client.LastConnectionString);
    }

    [Fact]
    public async Task Copy_builders_render_json_csv_and_sql_insert()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        var row = panel.ResultRows[^1];

        var json = panel.BuildRowJson(row);
        Assert.Contains("\"id\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"name\": null", json, StringComparison.Ordinal);

        Assert.Equal(
            "id,name" + Environment.NewLine + "2,",
            panel.BuildRowCsv(row));

        Assert.Equal(
            "INSERT INTO \"people\" (\"id\", \"name\") VALUES (2, NULL);",
            panel.BuildRowSqlInsert(row));

        var first = panel.ResultRows[0];
        Assert.Equal(
            "INSERT INTO \"people\" (\"id\", \"name\") VALUES (1, 'Ada');",
            panel.BuildRowSqlInsert(first));
    }

    [Fact]
    public async Task Context_copy_and_page_exports_escape_delimiters_and_preserve_types()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        var row = panel.ResultRows[0];
        panel.SelectRow(row);
        const string text = "O'Hara,\t\"quoted\"\nline";
        panel.SetSelectedCellText(1, text);

        Assert.Equal(text, panel.BuildCellValue(row, 1));
        Assert.Equal(
            text + Environment.NewLine + "NULL",
            panel.BuildColumnValues(1));
        Assert.Equal(
            "1\t\"O'Hara,\t\"\"quoted\"\"\nline\"",
            panel.BuildRowTsv(row));

        var rowCsv = DatabaseGridCsv.Parse(panel.BuildRowCsv(row));
        Assert.Equal(["id", "name"], rowCsv.Headers);
        Assert.Equal(text, Assert.Single(rowCsv.Rows)[1]);

        using var rowJson = System.Text.Json.JsonDocument.Parse(panel.BuildRowJson(row));
        Assert.Equal(1, rowJson.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(text, rowJson.RootElement.GetProperty("name").GetString());
        Assert.Contains("'O''Hara", panel.BuildRowSqlInsert(row), StringComparison.Ordinal);

        Assert.StartsWith("id\tname" + Environment.NewLine, panel.BuildCurrentPageTsv());
        using var pageJson = System.Text.Json.JsonDocument.Parse(panel.BuildCurrentPageJson());
        Assert.Equal(2, pageJson.RootElement.GetArrayLength());
        Assert.Equal(2, DatabaseGridCsv.Parse(panel.BuildCurrentPageCsv()).Rows.Count);
        Assert.Equal(
            2,
            panel.BuildCurrentPageSql()
                .Split("INSERT INTO", StringSplitOptions.None)
                .Length - 1);
    }

    [Fact]
    public async Task FormulaLikeCsvAndTsvKeepExactDataAndShowNonBlockingRiskNotice()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(), "Database", client,
            driverId: "sqlite", connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        var row = panel.ResultRows[0];
        panel.SelectRow(row);
        const string formula = "\t=HYPERLINK(\"https://example.invalid\",\"label\")";
        panel.SetSelectedCellText(1, formula);

        var csv = DatabaseGridCsv.Parse(panel.BuildRowCsv(row));
        Assert.Equal(formula, Assert.Single(csv.Rows)[1]);
        Assert.True(panel.HasInterchangeNotice);
        Assert.Equal(DatabaseSpreadsheetRisk.Notice, panel.InterchangeNotice);
        Assert.False(panel.HasError);
        Assert.False(panel.IsBusy);
        Assert.Contains("HYPERLINK", panel.BuildRowTsv(row), StringComparison.Ordinal);
        Assert.True(panel.HasInterchangeNotice);

        using var exported = new MemoryStream();
        await panel.WriteCurrentPageExportAsync(exported, DatabaseGridExportFormat.Csv);
        var file = DatabaseGridCsv.Parse(System.Text.Encoding.UTF8.GetString(exported.ToArray()));
        Assert.Equal(formula, file.Rows[0][1]);
        Assert.True(panel.HasInterchangeNotice);

        panel.SetSelectedCellText(1, "ordinary text");
        _ = panel.BuildRowCsv(row);
        Assert.False(panel.HasInterchangeNotice);
    }

    [Fact]
    public void Json_exports_reject_duplicate_query_column_names_instead_of_dropping_values()
    {
        var descriptors = new[]
        {
            new DatabaseColumnDescriptor("x", "INTEGER", DatabaseValueKind.SignedInteger),
            new DatabaseColumnDescriptor("x", "INTEGER", DatabaseValueKind.SignedInteger),
        };
        var columns = descriptors
            .Select(descriptor => new DatabaseResultColumnViewModel(descriptor, 120))
            .ToArray();
        var row = new DatabaseResultRowViewModel(
            1,
            [
                new DatabaseValue(1L, DatabaseValueKind.SignedInteger, "1"),
                new DatabaseValue(2L, DatabaseValueKind.SignedInteger, "2"),
            ],
            descriptors,
            [120, 120],
            canEdit: false);

        var textError = Assert.Throws<InvalidDataException>(() =>
            DatabaseGridExport.BuildClipboardText(writer =>
                DatabaseGridExport.WriteJsonRow(writer, columns, row)));
        Assert.Contains("unique column names", textError.Message, StringComparison.Ordinal);

        using var content = new MemoryStream();
        using var jsonWriter = new System.Text.Json.Utf8JsonWriter(content);
        var utf8Error = Assert.Throws<InvalidDataException>(() =>
            DatabaseGridExport.WriteJsonRow(jsonWriter, columns, row));
        Assert.Contains("use CSV", utf8Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_and_exports_use_complete_raw_values_instead_of_bounded_display_text()
    {
        var longText = new string('x', 5000);
        var bytes = Enumerable.Range(0, 40).Select(value => (byte)value).ToArray();
        var collection = Enumerable.Range(0, 40).ToArray();
        var client = new FakeDatabasePanelClient
        {
            IncludeBinaryColumn = true,
            IncludeCollectionColumn = true,
            FirstNameValue = new DatabaseValue(
                longText,
                DatabaseValueKind.Text,
                longText[..4095] + "…",
                IsTruncated: true),
            FirstBinaryValue = new DatabaseValue(
                bytes,
                DatabaseValueKind.Binary,
                "0x00010203… (40 bytes)",
                IsTruncated: true),
            FirstCollectionValue = new DatabaseValue(
                collection,
                DatabaseValueKind.Collection,
                "[0, 1, 2, 3]… (40 values)",
                IsTruncated: true),
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        var row = panel.ResultRows[0];
        var fullBinary = $"0x{Convert.ToHexString(bytes)}";
        var fullCollection = $"[{string.Join(", ", collection)}]";

        Assert.Equal(longText, panel.BuildCellValue(row, 1));
        Assert.Equal(fullBinary, panel.BuildCellValue(row, 2));
        Assert.Equal(fullCollection, panel.BuildCellValue(row, 3));

        var csv = DatabaseGridCsv.Parse(panel.BuildRowCsv(row));
        Assert.Equal(longText, csv.Rows[0][1]);
        Assert.Equal(fullBinary, csv.Rows[0][2]);
        Assert.Equal(fullCollection, csv.Rows[0][3]);

        using var json = System.Text.Json.JsonDocument.Parse(panel.BuildRowJson(row));
        Assert.Equal(longText, json.RootElement.GetProperty("name").GetString());
        Assert.Equal(bytes, json.RootElement.GetProperty("payload").GetBytesFromBase64());
        Assert.Equal(collection.Length, json.RootElement.GetProperty("tags").GetArrayLength());

        var sql = panel.BuildRowSqlInsert(row);
        Assert.Contains(longText, sql, StringComparison.Ordinal);
        Assert.Contains($"X'{Convert.ToHexString(bytes)}'", sql, StringComparison.Ordinal);
        Assert.Contains(fullCollection, sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clipboard_builders_reject_single_values_over_the_utf8_budget()
    {
        var oversizedText = new string(
            'é',
            (DatabaseRuntimePanelViewModel.MaximumClipboardUtf8Bytes / 2) + 1);
        var oversizedBinary = new byte[
            (DatabaseRuntimePanelViewModel.MaximumClipboardUtf8Bytes / 2) + 1];
        var client = new FakeDatabasePanelClient
        {
            IncludeBinaryColumn = true,
            FirstNameValue = new DatabaseValue(
                oversizedText,
                DatabaseValueKind.Text,
                "oversized text",
                IsTruncated: true),
            FirstBinaryValue = new DatabaseValue(
                oversizedBinary,
                DatabaseValueKind.Binary,
                "oversized binary",
                IsTruncated: true),
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        var row = panel.ResultRows[0];

        AssertClipboardLimit(() => panel.BuildCellValue(row, 1));
        AssertClipboardLimit(() => panel.BuildCellValue(row, 2));
        AssertClipboardLimit(() => panel.BuildColumnValues(1));
        AssertClipboardLimit(() => panel.BuildRowTsv(row));
        AssertClipboardLimit(() => panel.BuildRowJson(row));
        AssertClipboardLimit(() => panel.BuildRowCsv(row));
        AssertClipboardLimit(() => panel.BuildRowSqlInsert(row));
    }

    [Fact]
    public async Task Current_page_clipboard_builders_enforce_the_aggregate_utf8_budget()
    {
        var halfBudget = new string(
            'x',
            (DatabaseRuntimePanelViewModel.MaximumClipboardUtf8Bytes / 2) + 1);
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.ResultRows[0].Cells[1].EditText = halfBudget;
        panel.ResultRows[1].Cells[1].EditText = halfBudget;

        Assert.Equal(halfBudget, panel.BuildCellValue(panel.ResultRows[0], 1));
        AssertClipboardLimit(panel.BuildCurrentPageTsv);
        AssertClipboardLimit(panel.BuildCurrentPageJson);
        AssertClipboardLimit(panel.BuildCurrentPageCsv);
        AssertClipboardLimit(panel.BuildCurrentPageSql);
    }

    [Fact]
    public async Task Large_current_page_streaming_exports_write_incrementally_past_clipboard_limit()
    {
        var halfBudget = new string(
            'x',
            (DatabaseRuntimePanelViewModel.MaximumClipboardUtf8Bytes / 2) + 1);
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.ResultRows[0].Cells[1].EditText = halfBudget;
        panel.ResultRows[1].Cells[1].EditText = halfBudget;

        var csv = new InspectingWriteStream();
        panel.WriteCurrentPageCsv(csv);
        Assert.True(csv.Length > DatabaseRuntimePanelViewModel.MaximumClipboardUtf8Bytes);
        Assert.Equal(2, csv.NewlineCount);
        Assert.InRange(csv.MaximumWriteSize, 1, 64 * 1024);

        var json = new InspectingWriteStream("\"id\"");
        panel.WriteCurrentPageJson(json);
        Assert.True(json.Length > DatabaseRuntimePanelViewModel.MaximumClipboardUtf8Bytes);
        Assert.Equal(2, json.PatternCount);
        Assert.InRange(json.MaximumWriteSize, 1, 64 * 1024);

        var sql = new InspectingWriteStream("INSERT INTO");
        panel.WriteCurrentPageSql(sql);
        Assert.True(sql.Length > DatabaseRuntimePanelViewModel.MaximumClipboardUtf8Bytes);
        Assert.Equal(2, sql.PatternCount);
        Assert.InRange(sql.MaximumWriteSize, 1, 64 * 1024);
    }

    [Fact]
    public async Task Streaming_exports_chunk_large_binary_hex_and_base64_values()
    {
        var bytes = new byte[12 * 1024 * 1024];
        bytes[0] = 0xA5;
        bytes[^1] = 0x5A;
        var client = new FakeDatabasePanelClient
        {
            IncludeBinaryColumn = true,
            FirstBinaryValue = new DatabaseValue(
                bytes,
                DatabaseValueKind.Binary,
                "large binary",
                IsTruncated: true),
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);

        var csv = new InspectingWriteStream("0xA5");
        panel.WriteCurrentPageCsv(csv);
        Assert.True(csv.Length > DatabaseRuntimePanelViewModel.MaximumClipboardUtf8Bytes);
        Assert.Equal(1, csv.PatternCount);
        Assert.InRange(csv.MaximumWriteSize, 1, 64 * 1024);

        var json = new InspectingWriteStream("\"payload\"");
        panel.WriteCurrentPageJson(json);
        Assert.True(json.Length > DatabaseRuntimePanelViewModel.MaximumClipboardUtf8Bytes);
        Assert.Equal(2, json.PatternCount);
        Assert.InRange(json.MaximumWriteSize, 1, 64 * 1024);

        var sql = new InspectingWriteStream("X'A5");
        panel.WriteCurrentPageSql(sql);
        Assert.True(sql.Length > DatabaseRuntimePanelViewModel.MaximumClipboardUtf8Bytes);
        Assert.Equal(1, sql.PatternCount);
        Assert.InRange(sql.MaximumWriteSize, 1, 64 * 1024);
    }

    [Fact]
    public async Task Streaming_exports_round_trip_hostile_quotes_delimiters_and_newlines()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        const string hostile = "O'Hara,\t\"quoted\"\r\nnext";
        panel.ResultRows[0].Cells[1].EditText = hostile;

        using var csvStream = new MemoryStream();
        panel.WriteCurrentPageCsv(csvStream);
        var csv = DatabaseGridCsv.Parse(System.Text.Encoding.UTF8.GetString(csvStream.ToArray()));
        Assert.Equal(2, csv.Rows.Count);
        Assert.Equal(hostile, csv.Rows[0][1]);
        Assert.Equal(string.Empty, csv.Rows[1][1]);

        using var jsonStream = new MemoryStream();
        panel.WriteCurrentPageJson(jsonStream);
        using var json = System.Text.Json.JsonDocument.Parse(jsonStream.ToArray());
        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Equal(hostile, json.RootElement[0].GetProperty("name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement[1]
            .GetProperty("name").ValueKind);

        using var sqlStream = new MemoryStream();
        panel.WriteCurrentPageSql(sqlStream);
        var sql = System.Text.Encoding.UTF8.GetString(sqlStream.ToArray());
        Assert.Contains("'O''Hara,\t\"quoted\"\r\nnext'", sql, StringComparison.Ordinal);
        Assert.Equal(2, sql.Split("INSERT INTO", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task Async_streaming_export_locks_interactions_and_releases_them_after_write()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        using var destination = new BlockingWriteStream();

        var export = panel.WriteCurrentPageExportAsync(
            destination,
            DatabaseGridExportFormat.Csv);
        await destination.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(panel.IsBusy);
        Assert.False(panel.CanChangeConnection);
        Assert.False(panel.CanMutateRows);

        destination.Release();
        await export.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(panel.IsBusy);
        Assert.True(panel.CanChangeConnection);
        Assert.True(destination.Length > 0);
    }

    private static void AssertClipboardLimit(Func<string> build)
    {
        var exception = Assert.Throws<InvalidDataException>(() => build());
        Assert.Contains("16 MiB UTF-8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_paste_and_csv_import_bound_only_the_grid_display_preview()
    {
        var value = new string('x', 2 * 1024 * 1024);
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.SelectRow(panel.ResultRows[0]);

        panel.SetSelectedCellText(0, value);
        Assert.NotNull(panel.SelectedRow!.Cells[0].ValidationError);
        Assert.InRange(panel.SelectedRow.Cells[0].ValidationError!.Length, 1, 256);
        await panel.RevertChangesAsync();
        panel.SelectRow(panel.ResultRows[0]);

        panel.SetSelectedCellText(1, value);

        var pasted = panel.SelectedRow!.Cells[1];
        Assert.Equal(DatabaseResultCellViewModel.MaximumDisplayCharacters, pasted.Text.Length);
        Assert.EndsWith("…", pasted.Text, StringComparison.Ordinal);
        Assert.Equal(value, pasted.EditText);
        Assert.Equal(value, pasted.RawValue);
        Assert.Equal(value, pasted.FullText);
        Assert.Equal(value, panel.BuildCellValue(panel.SelectedRow, 1));

        await panel.RevertChangesAsync();
        Assert.True(panel.ImportCsv($"name\n{value}\n"));

        var imported = panel.SelectedRow!.Cells[1];
        Assert.Equal(DatabaseResultCellViewModel.MaximumDisplayCharacters, imported.Text.Length);
        Assert.EndsWith("…", imported.Text, StringComparison.Ordinal);
        Assert.Equal(value, imported.EditText);
        Assert.Equal(value, imported.RawValue);
        Assert.Equal(value, imported.FullText);
    }

    [Fact]
    public async Task Sql_insert_export_refuses_arbitrary_query_results()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        panel.QueryText = "SELECT 1 AS id, 'Ada' AS name";
        await panel.RunQueryAsync();

        Assert.Null(panel.SelectedObject);
        Assert.Throws<InvalidOperationException>(() =>
            panel.BuildRowSqlInsert(panel.ResultRows[0]));
        Assert.Throws<InvalidOperationException>(() => panel.BuildCurrentPageSql());
    }

    [Fact]
    public async Task Csv_import_is_atomic_exact_case_and_preserves_missing_column_states()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);

        Assert.True(panel.ImportCsv("name\nGrace\n"));
        var grace = Assert.IsType<DatabaseResultRowViewModel>(panel.SelectedRow);
        Assert.Equal(DatabaseEditValueState.Default, grace.Cells[0].State);
        Assert.Equal("Grace", grace.Cells[1].RawValue);

        Assert.True(panel.ImportCsv("id\n5\n"));
        var unnamed = Assert.IsType<DatabaseResultRowViewModel>(panel.SelectedRow);
        Assert.Equal(5L, unnamed.Cells[0].RawValue);
        Assert.True(unnamed.Cells[1].IsNull);

        Assert.True(panel.ImportCsv("name\n\"\"\n"));
        var empty = Assert.IsType<DatabaseResultRowViewModel>(panel.SelectedRow);
        Assert.Equal(DatabaseEditValueState.Value, empty.Cells[1].State);
        Assert.Equal(string.Empty, empty.Cells[1].RawValue);

        var beforeFailure = panel.ResultRows.Count;
        Assert.False(panel.ImportCsv("id,name\n6,valid\nnot-a-number,invalid\n"));
        Assert.Equal(beforeFailure, panel.ResultRows.Count);
        Assert.Contains("row 3", panel.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        Assert.False(panel.ImportCsv("Name\nwrong case\n"));
        Assert.Equal(beforeFailure, panel.ResultRows.Count);
        Assert.Contains("does not exist", panel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_import_rejects_server_owned_columns_before_staging_rows()
    {
        var client = new FakeDatabasePanelClient { IdIsIdentity = true };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        var before = panel.ResultRows.Count;

        Assert.False(panel.ImportCsv("id,name\n3,Grace\n"));

        Assert.Equal(before, panel.ResultRows.Count);
        Assert.Contains("owned by the database", panel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_string_display_masks_password_values()
    {
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            new FakeDatabasePanelClient());

        panel.ConnectionString = "Host=db;Username=ops;Password=s3cret;SSL Mode=Require";
        Assert.Equal(
            "Host=db;Username=ops;Password=••••••;SSL Mode=Require",
            panel.MaskedConnectionString);

        panel.ConnectionString = "Server=db;Pwd=x";
        Assert.Equal("Server=db;Pwd=••••••", panel.MaskedConnectionString);

        // A bare file path has nothing to hide and stays untouched.
        panel.ConnectionString = "/data/app.db";
        Assert.Equal("/data/app.db", panel.MaskedConnectionString);
    }

    [Fact]
    public void Dispose_is_idempotent_across_tab_and_window_teardown()
    {
        var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            new FakeDatabasePanelClient());

        panel.Dispose();
        panel.Dispose();
    }

    [Fact]
    public async Task Table_preview_uses_typed_rows_and_exposes_structure_and_indexes()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;

        await panel.PreviewTableAsync(panel.Tables[0]);

        Assert.Equal("SELECT * FROM \"people\" LIMIT 200;", panel.QueryText);
        Assert.Null(client.LastSql);
        Assert.Equal(panel.Tables[0].Descriptor, client.LastDetailsObject);
        Assert.Equal(panel.Tables[0].Descriptor, client.LastReadTable);
        Assert.Equal(DatabaseWorkspaceMode.Data, panel.SelectedMode);
        Assert.True(panel.ShowData);
        Assert.True(panel.HasResults);
        Assert.Equal(DatabaseValueKind.SignedInteger, panel.ResultColumns[0].ValueKind);
        Assert.Equal(1L, panel.ResultRows[0].Cells[0].RawValue);
        Assert.True(panel.CanEditRows);
        Assert.Null(panel.ReadOnlyReason);
        Assert.Equal(["id", "name"], panel.StructureColumns.Select(column => column.Name), StringComparer.Ordinal);
        Assert.Equal(
            ["pk_people", "ix_people_name"],
            panel.Indexes.Select(index => index.Name), StringComparer.Ordinal);

        panel.SetMode(DatabaseWorkspaceMode.Structure);
        Assert.True(panel.ShowStructure);
        Assert.False(panel.ShowData);

        panel.SetMode(DatabaseWorkspaceMode.Indexes);
        Assert.True(panel.ShowIndexes);
        Assert.False(panel.ShowStructure);
    }

    [Fact]
    public async Task Catalog_kind_cannot_override_a_display_only_materialized_primary_key()
    {
        var client = new FakeDatabasePanelClient
        {
            ReturnDisplayOnlyPrimaryKey = true,
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;

        await panel.PreviewTableAsync(panel.Tables[0]);

        Assert.False(panel.CanEditRows);
        Assert.Equal("This primary-key value cannot be edited safely.", panel.ReadOnlyReason);
        Assert.Equal(DatabaseValueKind.Other, panel.ResultColumns[0].ValueKind);
        Assert.False(panel.ResultRows[0].Cells[0].IsEditable);
    }

    [Fact]
    public async Task Table_filter_is_parsed_to_the_column_kind_before_it_reaches_the_client()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);

        panel.FilterColumn = panel.FilterColumns.Single(column => string.Equals(column.Name, "id", StringComparison.Ordinal));
        panel.FilterOperator = panel.FilterOperators.Single(option =>
            option.Operator == DatabaseFilterOperator.GreaterThan);
        panel.FilterValue = "40";

        await panel.ApplyFilterAsync();

        var query = Assert.IsType<DatabaseTableQuery>(client.LastTableQuery);
        Assert.Equal(0, query.Offset);
        var filter = Assert.Single(query.Filters);
        Assert.Equal("id", filter.ColumnName);
        Assert.Equal(DatabaseFilterOperator.GreaterThan, filter.Operator);
        Assert.Equal(40L, filter.Value);
    }

    [Fact]
    public async Task Stacked_filter_rows_apply_together_and_unchecked_rows_stay_out()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);

        var first = panel.FilterRows[0];
        first.Column = first.Columns.Single(column => string.Equals(column.Name, "id", StringComparison.Ordinal));
        first.Operator = first.Operators.Single(option =>
            option.Operator == DatabaseFilterOperator.GreaterThan);
        first.Value = "40";
        panel.AddFilterRow(first);
        var second = panel.FilterRows[1];
        second.Column = second.Columns.Single(column => string.Equals(column.Name, "name", StringComparison.Ordinal));
        second.Operator = second.Operators.Single(option =>
            option.Operator == DatabaseFilterOperator.Contains);
        second.Value = "Ada";
        panel.AddFilterRow(second);
        var third = panel.FilterRows[2];
        third.Column = third.Columns.Single(column => string.Equals(column.Name, "name", StringComparison.Ordinal));
        third.Operator = third.Operators.Single(option =>
            option.Operator == DatabaseFilterOperator.Contains);
        third.Value = "ignored";
        third.IsIncluded = false;

        await panel.ApplyFilterAsync();

        var query = Assert.IsType<DatabaseTableQuery>(client.LastTableQuery);
        Assert.Equal(2, query.Filters.Count);
        Assert.Equal("id", query.Filters[0].ColumnName);
        Assert.Equal(40L, query.Filters[0].Value);
        Assert.Equal("name", query.Filters[1].ColumnName);
        Assert.Equal("Ada", query.Filters[1].Value);

        // The applied conditions survive the reload as fresh rows. Removing
        // down to nothing leaves one blank row, never an empty bar.
        Assert.Equal(3, panel.FilterRows.Count);
        foreach (var row in panel.FilterRows.ToArray())
        {
            panel.RemoveFilterRow(row);
        }

        var remaining = Assert.Single(panel.FilterRows);
        Assert.Null(remaining.Column);
    }

    [Fact]
    public async Task Inspector_fields_edit_the_same_cell_the_grid_does()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.SelectRow(panel.ResultRows[0]);

        var field = panel.SelectedRowFields.Single(candidate => string.Equals(candidate.Name, "name", StringComparison.Ordinal));
        Assert.True(field.CanEdit);
        Assert.False(panel.HasPendingChanges);

        // Apply stages into the cell — the same move as typing in the grid.
        field.BeginEdit();
        field.Draft = "edited through the inspector";
        field.ApplyEdit();
        Assert.False(field.IsEditing);
        Assert.Equal("edited through the inspector", field.Text);
        Assert.True(panel.HasPendingChanges);
        Assert.Equal(
            "edited through the inspector",
            panel.ResultRows[0].Cells[1].EditText);

        // Revert abandons the draft without touching the cell.
        field.BeginEdit();
        field.Draft = "discarded";
        field.CancelEdit();
        Assert.Equal("edited through the inspector", field.Text);

        // The field is a live window: a grid-side edit shows up in it.
        panel.ResultRows[0].Cells[1].EditText = "edited through the grid";
        Assert.Equal("edited through the grid", field.Text);
    }

    [Fact]
    public async Task A_panel_born_pointing_at_an_object_opens_straight_onto_it()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "people",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db",
            initialObject: new DatabaseObjectId(null, null, "people"));
        await panel.Initialization;

        Assert.True(panel.IsConnected);
        Assert.Equal("people", panel.SelectedObject?.Name);
        Assert.False(panel.IsDatabaseOverview);
    }

    [Fact]
    public async Task Switching_perspective_hides_the_data_surface_and_says_so()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        Assert.True(panel.ShowDataSurface);

        var raised = new List<string>();
        panel.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        panel.SetMode(DatabaseWorkspaceMode.Structure);

        // The view binds the derived surface flag; if the mode change does not
        // raise it, the data grid stays visible under the structure view.
        Assert.False(panel.ShowDataSurface);
        Assert.Contains(nameof(DatabaseRuntimePanelViewModel.ShowDataSurface), raised, StringComparer.Ordinal);
        Assert.True(panel.ShowStructure);
    }

    [Fact]
    public async Task The_database_overview_lists_every_object_read_only()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);

        panel.ShowDatabaseOverview();

        Assert.Null(panel.SelectedObject);
        Assert.Equal(
            ["Schema", "Name", "Kind"],
            [.. panel.ResultColumns.Select(column => column.Name)]);
        Assert.Equal(panel.Tables.Count, panel.ResultRows.Count);
        Assert.Contains("objects", panel.ResultSummary, StringComparison.Ordinal);
        // A catalog is a fact sheet: no editing, no paging, no filtering.
        Assert.False(panel.CanMutateRows);
        Assert.False(panel.CanFilterTable);
    }

    [Fact]
    public async Task Successful_initial_connection_selects_the_database_overview()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");

        await panel.Initialization;

        Assert.True(panel.IsDatabaseOverview);
        Assert.True(panel.IsDatabaseObjectsOverview);
        Assert.Null(panel.SelectedObject);
        Assert.Equal(panel.Tables.Count, panel.ResultRows.Count);
        Assert.Equal(["Schema", "Name", "Kind"], panel.ResultColumns.Select(column => column.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Database_overview_owns_a_worker_and_closes_it_when_leaving_the_diagram()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;

        panel.ShowDatabaseOverview();
        Assert.True(panel.IsDatabaseObjectsOverview);
        Assert.False(panel.IsDatabaseDiagramOverview);

        await panel.ShowDatabaseDiagramAsync();

        Assert.True(panel.IsDatabaseDiagramOverview);
        Assert.False(panel.ShowQueryEditor);
        Assert.False(panel.ShowDataSurface);
        Assert.True(panel.HasMermaidDiagram);
        Assert.Empty(panel.MermaidDiagramSource);
        Assert.Empty(panel.MermaidDiagramText);
        var first = Assert.IsType<FakeDiagramSession>(panel.DiagramSession);
        Assert.Equal(1, client.SchemaGraphCallCount);

        panel.ShowDatabaseOverview();
        Assert.True(first.Disposed);
        Assert.True(panel.IsDatabaseObjectsOverview);
        Assert.True(panel.ShowDataSurface);
        await panel.ShowDatabaseDiagramAsync();
        Assert.Equal(2, client.SchemaGraphCallCount);

        panel.ShowDatabaseOverview();
        panel.QueryText = "SELECT id, name FROM people";
        await panel.RunQueryAsync();
        panel.ShowDatabaseOverview();
        await panel.ShowDatabaseDiagramAsync();
        Assert.Equal(3, client.SchemaGraphCallCount);

        panel.ShowDatabaseOverview();
        panel.QueryText = "CREATE TABLE added (id INTEGER)";
        await panel.RunQueryAsync();
        panel.ShowDatabaseOverview();
        await panel.ShowDatabaseDiagramAsync();
        Assert.Equal(4, client.SchemaGraphCallCount);
    }

    [Fact]
    public async Task Leaving_the_diagram_cancels_metadata_loading_without_blocking_navigation()
    {
        var client = new FakeDatabasePanelClient { HoldDiagram = true };
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", client,
            "sqlite", "Data Source=demo.db");
        await panel.Initialization;
        var loading = panel.ShowDatabaseDiagramAsync();
        Assert.True(panel.IsBusy);

        panel.ShowDatabaseOverview();
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(client.DiagramCanceled);
        Assert.False(panel.IsBusy);
        Assert.True(panel.IsDatabaseObjectsOverview);
        Assert.False(panel.HasMermaidDiagram);
    }

    [Fact]
    public async Task Hidden_diagram_releases_its_worker_but_retains_the_selected_view()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", client,
            "sqlite", "Data Source=demo.db");
        await panel.Initialization;
        await panel.ShowDatabaseDiagramAsync();
        var first = Assert.IsType<FakeDiagramSession>(panel.DiagramSession);

        panel.SuspendDatabaseDiagram();

        Assert.True(first.Disposed);
        Assert.True(panel.IsDatabaseDiagramOverview);
        Assert.False(panel.HasMermaidDiagram);
        await panel.ShowDatabaseDiagramAsync();
        Assert.True(panel.HasMermaidDiagram);
        Assert.Equal(2, client.SchemaGraphCallCount);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("replace")]
    [InlineData("newer-copy")]
    public async Task DiagramClipboardRejectsSupersededExportEvenWhenSessionIgnoresCancellation(string action)
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", client,
            "sqlite", "Data Source=demo.db");
        await panel.Initialization;
        await panel.ShowDatabaseDiagramAsync();
        var session = Assert.IsType<FakeDiagramSession>(panel.DiagramSession);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ExportGate = release.Task;
        var copy = panel.BuildDiagramClipboardAsync(session, DatabaseDiagramExport.Svg);
        if (action == "close") { panel.Dispose(); }
        else if (action == "replace") { panel.SuspendDatabaseDiagram(); }
        else
        {
            session.ExportGate = null;
            var newer = await panel.BuildDiagramClipboardAsync(session, DatabaseDiagramExport.MermaidMarkdown);
            Assert.Equal("complete diagram", newer.Text);
            Assert.True(panel.IsClipboardRequestCurrent(newer.Revision));
        }
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
    }

    [Fact]
    public async Task CompletedDiagramClipboardRevisionIsInvalidAfterDiagramReplacement()
    {
        using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database", new FakeDatabasePanelClient(),
            "sqlite", "Data Source=demo.db");
        await panel.Initialization;
        await panel.ShowDatabaseDiagramAsync();
        var copy = await panel.BuildDiagramClipboardAsync(panel.DiagramSession!, DatabaseDiagramExport.Svg);
        Assert.Equal("complete diagram", copy.Text);
        Assert.True(panel.IsClipboardRequestCurrent(copy.Revision));
        panel.SuspendDatabaseDiagram();
        Assert.False(panel.IsClipboardRequestCurrent(copy.Revision));
    }

    [Fact]
    public async Task Header_sort_toggles_direction_and_preserves_filter_and_page_size()
    {
        var client = new FakeDatabasePanelClient { TableHasMore = true };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.FilterColumn = panel.FilterColumns.Single(column => string.Equals(column.Name, "name", StringComparison.Ordinal));
        panel.FilterOperator = panel.FilterOperators.Single(option =>
            option.Operator == DatabaseFilterOperator.NotContains);
        panel.FilterValue = "other";
        await panel.ApplyFilterAsync();
        await panel.NextPageAsync();
        var pagedQuery = Assert.IsType<DatabaseTableQuery>(client.LastTableQuery);

        await panel.ToggleTableSortAsync("id");

        var ascending = Assert.IsType<DatabaseTableQuery>(client.LastTableQuery);
        Assert.Equal(0, ascending.Offset);
        Assert.Equal(pagedQuery.Limit, ascending.Limit);
        Assert.Equal(pagedQuery.Filters, ascending.Filters);
        var ascendingSort = Assert.Single(ascending.Sorts);
        Assert.Equal("id", ascendingSort.ColumnName);
        Assert.False(ascendingSort.Descending);
        Assert.Equal(false, panel.ResultColumns.Single(column => string.Equals(column.Name, "id", StringComparison.Ordinal)).SortDescending);

        await panel.ToggleTableSortAsync("id");

        var descending = Assert.IsType<DatabaseTableQuery>(client.LastTableQuery);
        Assert.Equal(0, descending.Offset);
        Assert.Equal(ascending.Limit, descending.Limit);
        Assert.Equal(ascending.Filters, descending.Filters);
        var descendingSort = Assert.Single(descending.Sorts);
        Assert.Equal("id", descendingSort.ColumnName);
        Assert.True(descendingSort.Descending);
        Assert.Equal(true, panel.ResultColumns.Single(column => string.Equals(column.Name, "id", StringComparison.Ordinal)).SortDescending);
    }

    [Fact]
    public async Task Page_limit_is_bounded_resets_offset_and_preserves_filter_sort_and_total()
    {
        var client = new FakeDatabasePanelClient
        {
            TableHasMore = true,
            TableTotalRows = 987,
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        Assert.Equal("200", panel.PageLimitText);
        Assert.Equal(987, panel.TotalRows);
        Assert.Equal("987", panel.TotalRowsText);

        panel.FilterColumn = panel.FilterColumns.Single(column => string.Equals(column.Name, "name", StringComparison.Ordinal));
        panel.FilterOperator = panel.FilterOperators.Single(option =>
            option.Operator == DatabaseFilterOperator.Contains);
        panel.FilterValue = "Ada";
        await panel.ApplyFilterAsync();
        await panel.ToggleTableSortAsync("id");
        await panel.NextPageAsync();

        panel.PageLimitText = "75";
        await panel.ApplyPageLimitAsync();

        var resized = Assert.IsType<DatabaseTableQuery>(client.LastTableQuery);
        Assert.Equal(0, resized.Offset);
        Assert.Equal(75, resized.Limit);
        Assert.Single(resized.Filters);
        Assert.Single(resized.Sorts);
        Assert.Equal("75", panel.PageLimitText);
        Assert.Equal(987, panel.TotalRows);

        var reads = client.ReadTableCallCount;
        panel.PageLimitText = "0";
        await panel.ApplyPageLimitAsync();
        Assert.Equal(reads, client.ReadTableCallCount);
        Assert.Contains("between 1 and 5000", panel.ErrorMessage, StringComparison.Ordinal);

        panel.PageLimitText = "50";
        client.ReadTableFailure = "page failed";
        await panel.ApplyPageLimitAsync();
        Assert.Equal("75", panel.PageLimitText);
        Assert.Equal("page failed", panel.ErrorMessage);
    }

    [Fact]
    public async Task Failed_header_sort_keeps_the_previous_page_query_and_indicator()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        await panel.ToggleTableSortAsync("id");
        var successfulQuery = Assert.IsType<DatabaseTableQuery>(client.LastTableQuery);
        var successfulRows = panel.ResultRows;
        var successfulColumns = panel.ResultColumns;
        client.ReadTableFailure = "sort failed";

        await panel.ToggleTableSortAsync("id");

        Assert.Equal("sort failed", panel.ErrorMessage);
        Assert.Equal(successfulQuery, client.LastTableQuery);
        Assert.Same(successfulRows, panel.ResultRows);
        Assert.Same(successfulColumns, panel.ResultColumns);
        Assert.Equal(false, panel.ResultColumns.Single(column => string.Equals(column.Name, "id", StringComparison.Ordinal)).SortDescending);
    }

    [Fact]
    public async Task Header_sort_is_gated_while_changes_are_pending_or_a_page_is_loading()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.ResultRows[0].Cells[1].EditText = "pending";
        var readCalls = client.ReadTableCallCount;

        Assert.False(panel.CanSortTable);
        await panel.ToggleTableSortAsync("id");
        Assert.Equal(readCalls, client.ReadTableCallCount);

        await panel.RevertChangesAsync();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ReadTableStarted = started;
        client.ReadTableRelease = release;
        var refresh = panel.RefreshTableAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            readCalls = client.ReadTableCallCount;
            Assert.True(panel.IsBusy);
            Assert.False(panel.CanSortTable);

            await panel.ToggleTableSortAsync("id");

            Assert.Equal(readCalls, client.ReadTableCallCount);
        }
        finally
        {
            release.TrySetResult(true);
            await refresh;
        }
    }

    [Fact]
    public async Task Empty_editable_table_exposes_its_columns_and_can_add_the_first_row()
    {
        var client = new FakeDatabasePanelClient { ReturnEmptyTable = true };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;

        await panel.PreviewTableAsync(panel.Tables[0]);

        Assert.True(panel.HasResults);
        Assert.False(panel.ShowEmptyHint);
        Assert.Empty(panel.ResultRows);
        Assert.NotEmpty(panel.ResultColumns);
        Assert.True(panel.CanEditRows);
        Assert.True(panel.CanMutateRows);

        panel.AddRow();

        Assert.Single(panel.ResultRows);
        Assert.True(panel.HasPendingChanges);
    }

    [Fact]
    public async Task Arbitrary_query_results_filter_sort_and_refresh_through_the_raw_source()
    {
        var client = new FakeDatabasePanelClient { TableHasMore = true };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        const string sql =
            "SELECT id, name FROM people WHERE name <> 'DELETE; UPDATE' -- safe comment";
        panel.QueryText = sql;
        await panel.RunQueryAsync();

        Assert.False(panel.CanEditRows);
        Assert.False(panel.CanMutateRows);
        Assert.Null(panel.SelectedObject);
        Assert.True(panel.CanFilterTable);
        Assert.True(panel.CanSortTable);
        Assert.True(panel.CanRefreshTable);

        panel.FilterColumn = panel.FilterColumns.Single(column => string.Equals(column.Name, "name", StringComparison.Ordinal));
        panel.FilterOperator = panel.FilterOperators.Single(option =>
            option.Operator == DatabaseFilterOperator.Contains);
        panel.FilterValue = "Ada";
        await panel.ApplyFilterAsync();

        var filtered = Assert.IsType<DatabaseTableQuery>(client.LastReadQuery);
        Assert.Equal(sql, client.LastReadQuerySql);
        Assert.Equal(["id", "name"], client.LastReadQueryColumns!.Select(column => column.Name), StringComparer.Ordinal);
        var condition = Assert.Single(filtered.Filters);
        Assert.Equal("name", condition.ColumnName);
        Assert.Equal(DatabaseFilterOperator.Contains, condition.Operator);
        Assert.Equal("Ada", condition.Value);

        await panel.ToggleTableSortAsync("id");

        var sorted = Assert.IsType<DatabaseTableQuery>(client.LastReadQuery);
        Assert.Equal(filtered.Filters, sorted.Filters);
        Assert.Equal(0, sorted.Offset);
        var sort = Assert.Single(sorted.Sorts);
        Assert.Equal("id", sort.ColumnName);
        Assert.False(sort.Descending);
        Assert.Equal(false, panel.ResultColumns.Single(column => string.Equals(column.Name, "id", StringComparison.Ordinal)).SortDescending);
        Assert.True(panel.HasNextPage);
        Assert.False(panel.CanGoToNextPage);

        var readCalls = client.ReadQueryCallCount;
        await panel.RefreshTableAsync();

        Assert.Equal(readCalls + 1, client.ReadQueryCallCount);
        Assert.Equal(sorted, client.LastReadQuery);
        Assert.Equal(sql, client.LastReadQuerySql);
        Assert.Equal(1, client.QueryCallCount);
        Assert.False(panel.CanEditRows);
    }

    [Theory]
    [InlineData("UPDATE people SET name = 'changed' RETURNING id, name")]
    [InlineData("SELECT id, name FROM people; DELETE FROM audit_log")]
    [InlineData("SELECT id, name FROM people; -- trailing comment")]
    [InlineData("SELECT 1--1; DELETE FROM audit_log")]
    [InlineData("SELECT id, name INTO archived_people FROM people")]
    public async Task Data_changing_result_statements_are_never_rerun_by_browser_actions(
        string sql)
    {
        var client = new FakeDatabasePanelClient
        {
            // Even convincing provider lineage must not turn a data-changing
            // statement into a refreshable/editable table projection.
            QueryProvenance = QueryProvenanceShape.Exact,
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.QueryText = sql;

        await panel.RunQueryAsync();

        Assert.True(panel.HasResults);
        Assert.Null(panel.SelectedObject);
        Assert.False(panel.CanEditRows);
        Assert.False(panel.CanFilterTable);
        Assert.False(panel.CanSortTable);
        Assert.False(panel.CanRefreshTable);
        Assert.Contains("not rerun", panel.ReadOnlyReason, StringComparison.Ordinal);
        var queryCalls = client.QueryCallCount;

        await panel.RefreshTableAsync();
        await panel.ToggleTableSortAsync("id");

        Assert.Equal(queryCalls, client.QueryCallCount);
        Assert.Equal(0, client.ReadQueryCallCount);
        Assert.False(panel.HasPendingChanges);
    }

    [Fact]
    public async Task Rerunning_the_same_sql_refreshes_the_schema_used_by_raw_result_actions()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        const string sql = "SELECT * FROM changing_shape";
        panel.QueryText = sql;
        await panel.RunQueryAsync();
        Assert.Equal(["id", "name"], panel.ResultColumns.Select(column => column.Name), StringComparer.Ordinal);

        client.IncludeQueryExtraColumn = true;
        await panel.RunQueryAsync();

        Assert.Equal(
            ["id", "name", "extra"],
            panel.ResultColumns.Select(column => column.Name), StringComparer.Ordinal);
        panel.FilterColumn = panel.FilterColumns.Single(column => string.Equals(column.Name, "extra", StringComparison.Ordinal));
        panel.FilterOperator = panel.FilterOperators.Single(option =>
            option.Operator == DatabaseFilterOperator.Equal);
        panel.FilterValue = "Ada";
        await panel.ApplyFilterAsync();

        Assert.Equal(
            ["id", "name", "extra"],
            client.LastReadQueryColumns!.Select(column => column.Name), StringComparer.Ordinal);
        Assert.Equal("extra", Assert.Single(client.LastReadQuery!.Filters).ColumnName);
    }

    [Fact]
    public async Task Exact_query_provenance_keeps_mutations_and_save_reloads_the_exact_raw_source()
    {
        var client = new FakeDatabasePanelClient
        {
            QueryProvenance = QueryProvenanceShape.Exact,
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        var tableReads = client.ReadTableCallCount;
        const string sourceSql = "SELECT id, name FROM people ORDER BY id DESC";
        panel.QueryText = sourceSql;

        await panel.RunQueryAsync();

        Assert.Equal(panel.Tables[0].Descriptor, panel.SelectedObject?.Descriptor);
        Assert.True(panel.CanEditRows);
        Assert.True(panel.CanMutateRows);
        var queryCalls = client.QueryCallCount;
        var readQueryCalls = client.ReadQueryCallCount;
        panel.SelectRow(panel.ResultRows[0]);
        Assert.True(panel.CanDuplicateSelectedRow);
        Assert.True(panel.CanDeleteSelectedRow);
        panel.ResultRows[0].Cells[1].EditText = "Ada Lovelace";
        Assert.True(panel.CanSaveChanges);
        panel.QueryText = "SELECT name FROM names";

        await panel.SaveChangesAsync();

        Assert.Equal(1, client.ApplyChangesCallCount);
        Assert.Equal(tableReads, client.ReadTableCallCount);
        Assert.Equal(queryCalls + 1, client.QueryCallCount);
        Assert.Equal(sourceSql, client.LastSql);
        Assert.Equal(readQueryCalls, client.ReadQueryCallCount);
        Assert.False(panel.HasPendingChanges);
        Assert.True(panel.CanEditRows);
        Assert.Equal(panel.Tables[0].Descriptor, client.LastChangedTable);
    }

    [Fact]
    public async Task Returning_from_an_unproven_query_to_the_exact_table_restores_capabilities()
    {
        var client = new FakeDatabasePanelClient
        {
            QueryProvenance = QueryProvenanceShape.Missing,
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.QueryText = "SELECT id, name || '!' AS name FROM people";
        await panel.RunQueryAsync();
        Assert.Null(panel.SelectedObject);
        Assert.False(panel.CanEditRows);

        client.QueryProvenance = QueryProvenanceShape.Exact;
        panel.QueryText = "SELECT id, name FROM people ORDER BY id DESC";
        await panel.RunQueryAsync();

        Assert.Equal(panel.Tables[0].Descriptor, panel.SelectedObject?.Descriptor);
        Assert.True(panel.CanEditRows);
        Assert.True(panel.CanMutateRows);
        Assert.NotEmpty(panel.StructureColumns);
        Assert.NotEmpty(panel.Indexes);
    }

    [Theory]
    [InlineData(QueryProvenanceShape.Missing)]
    [InlineData(QueryProvenanceShape.Aliased)]
    [InlineData(QueryProvenanceShape.Incomplete)]
    public async Task Unproven_query_projection_downgrades_the_result_to_read_only(
        QueryProvenanceShape provenance)
    {
        var client = new FakeDatabasePanelClient { QueryProvenance = provenance };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.QueryText = "SELECT result FROM people";

        await panel.RunQueryAsync();

        Assert.Null(panel.SelectedObject);
        Assert.False(panel.CanEditRows);
        Assert.False(panel.CanMutateRows);
        Assert.Equal(
            "This query result does not map exactly to one editable table.",
            panel.ReadOnlyReason);
        Assert.All(panel.ResultRows.SelectMany(row => row.Cells), cell => Assert.False(cell.IsEditable));
        Assert.True(panel.CanFilterTable);
        Assert.True(panel.CanSortTable);
    }

    [Fact]
    public async Task Refresh_reloads_the_current_table_page_without_rerunning_query_text()
    {
        var client = new FakeDatabasePanelClient { TableHasMore = true };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.FilterColumn = panel.FilterColumns.Single(column => string.Equals(column.Name, "name", StringComparison.Ordinal));
        panel.FilterOperator = panel.FilterOperators.Single(option =>
            option.Operator == DatabaseFilterOperator.NotContains);
        panel.FilterValue = "other";
        await panel.ApplyFilterAsync();
        await panel.NextPageAsync();
        var before = Assert.IsType<DatabaseTableQuery>(client.LastTableQuery);
        var readCalls = client.ReadTableCallCount;
        var detailCalls = client.GetObjectDetailsCallCount;
        panel.QueryText = "DELETE FROM people;";

        await panel.RefreshTableAsync();

        var refreshed = Assert.IsType<DatabaseTableQuery>(client.LastTableQuery);
        Assert.Equal(before.Offset, refreshed.Offset);
        Assert.Equal(before.Limit, refreshed.Limit);
        Assert.Equal(before.Filters, refreshed.Filters);
        Assert.Equal(before.Sorts, refreshed.Sorts);
        Assert.Equal(readCalls + 1, client.ReadTableCallCount);
        Assert.Equal(detailCalls + 1, client.GetObjectDetailsCallCount);
        Assert.Equal(0, client.QueryCallCount);
        Assert.Null(client.LastSql);

        panel.ResultRows[0].Cells[1].EditText = "pending";
        readCalls = client.ReadTableCallCount;
        Assert.False(panel.CanRefreshTable);

        await panel.RefreshTableAsync();

        Assert.Equal(readCalls, client.ReadTableCallCount);
        Assert.Contains("Save or revert", panel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_selected_row_is_a_new_pending_insert_and_can_be_removed()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.SelectRow(panel.ResultRows[0]);

        Assert.True(panel.CanDuplicateSelectedRow);
        panel.DuplicateSelectedRow();

        var duplicate = Assert.IsType<DatabaseResultRowViewModel>(panel.SelectedRow);
        Assert.Equal(3, panel.ResultRows.Count);
        Assert.True(duplicate.IsNew);
        Assert.Equal(DatabaseEditValueState.Default, duplicate.Cells[0].State);
        Assert.Null(duplicate.Cells[0].RawValue);
        Assert.Equal("Ada", duplicate.Cells[1].RawValue);
        Assert.True(panel.HasPendingChanges);
        Assert.False(panel.CanRefreshTable);

        panel.DeleteSelectedRow();

        Assert.Equal(2, panel.ResultRows.Count);
        Assert.False(panel.HasPendingChanges);
        Assert.True(panel.CanRefreshTable);
    }

    [Fact]
    public async Task Empty_text_paste_and_null_remain_distinct_and_oracle_disables_empty()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.SelectRow(panel.ResultRows[1]);
        Assert.True(panel.ResultRows[1].Cells[1].IsNull);
        Assert.True(panel.CanSetSelectedCellEmpty(1));

        panel.SetSelectedCellEmpty(1);

        Assert.Equal(DatabaseEditValueState.Value, panel.SelectedRow!.Cells[1].State);
        Assert.Equal(string.Empty, panel.SelectedRow.Cells[1].RawValue);
        Assert.False(panel.SelectedRow.Cells[1].IsNull);

        panel.SetSelectedCellText(0, "not-an-integer");
        Assert.False(panel.SelectedRow.Cells[0].IsValid);
        Assert.False(panel.CanSaveChanges);
        panel.SetSelectedCellText(0, "42");
        Assert.True(panel.SelectedRow.Cells[0].IsValid);
        Assert.Equal(42L, panel.SelectedRow.Cells[0].RawValue);

        using var oracle = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "oracle",
            connectionString: "Data Source=demo");
        await oracle.Initialization;
        await oracle.PreviewTableAsync(oracle.Tables[0]);
        oracle.SelectRow(oracle.ResultRows[1]);

        Assert.False(oracle.CanSetSelectedCellEmpty(1));
        oracle.SetSelectedCellEmpty(1);
        Assert.True(oracle.SelectedRow!.Cells[1].IsNull);
    }

    [Fact]
    public async Task Oracle_rejects_implicit_empty_text_but_saves_explicit_null()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "oracle",
            connectionString: "Data Source=demo");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.SelectRow(panel.ResultRows[0]);
        var name = panel.SelectedRow!.Cells[1];

        panel.SetSelectedCellText(1, string.Empty);

        Assert.Equal("Ada", name.RawValue);
        Assert.False(name.IsDirty);
        Assert.Contains("Oracle stores empty text as SQL NULL", panel.ErrorMessage);

        var rowCount = panel.ResultRows.Count;
        Assert.False(panel.ImportCsv("name\n\"\"\n"));
        Assert.Equal(rowCount, panel.ResultRows.Count);
        Assert.False(panel.HasPendingChanges);
        Assert.Contains("CSV row 2", panel.ErrorMessage);
        Assert.Contains("Oracle stores empty text as SQL NULL", panel.ErrorMessage);

        name.EditText = string.Empty;
        Assert.True(panel.CanSaveChanges);

        await panel.SaveChangesAsync();

        Assert.Equal(0, client.ApplyChangesCallCount);
        Assert.True(panel.HasPendingChanges);
        Assert.Contains("Oracle stores empty text as SQL NULL", panel.ErrorMessage);

        await panel.RevertChangesAsync();
        panel.SelectRow(panel.ResultRows[0]);
        panel.SetSelectedCellNull(1);
        Assert.True(panel.SelectedRow!.Cells[1].IsNull);

        await panel.SaveChangesAsync();

        Assert.Equal(1, client.ApplyChangesCallCount);
        var update = Assert.Single(Assert.IsType<DatabaseTableChanges>(client.LastChanges).Updates);
        var change = Assert.Single(update.Changes);
        Assert.Equal("name", change.ColumnName);
        Assert.Equal(DatabaseEditValueState.Null, change.State);
    }

    [Fact]
    public async Task Binary_file_value_is_staged_as_detached_bytes_and_saved()
    {
        var client = new FakeDatabasePanelClient { IncludeBinaryColumn = true };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.SelectRow(panel.ResultRows[0]);
        var cell = panel.SelectedRow!.Cells[2];
        Assert.False(panel.ResultColumns[2].IsEditable);
        Assert.True(cell.CanSetBinary);
        var bytes = new byte[] { 0x10, 0x20 };

        panel.SetSelectedCellBinary(2, bytes);
        bytes[0] = 0xFF;

        Assert.Equal(new byte[] { 0x10, 0x20 }, Assert.IsType<byte[]>(cell.RawValue));
        Assert.Equal("0x1020", cell.Text);
        Assert.True(panel.CanSaveChanges);

        await panel.SaveChangesAsync();

        var update = Assert.Single(Assert.IsType<DatabaseTableChanges>(client.LastChanges).Updates);
        var change = Assert.Single(update.Changes);
        Assert.Equal("payload", change.ColumnName);
        Assert.Equal(new byte[] { 0x10, 0x20 }, Assert.IsType<byte[]>(change.Value));
    }

    [Fact]
    public async Task Quick_filter_uses_typed_cell_values_and_list_filter_resets_offset()
    {
        var client = new FakeDatabasePanelClient { TableHasMore = true };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        await panel.NextPageAsync();
        panel.SelectRow(panel.ResultRows[0]);

        Assert.Contains(
            panel.GetQuickFilterOperators(0),
            option => option.Operator == DatabaseFilterOperator.In);
        Assert.DoesNotContain(
            panel.GetQuickFilterOperators(0),
            option => option.Operator == DatabaseFilterOperator.Contains);

        await panel.ApplyQuickFilterAsync(0, DatabaseFilterOperator.In);

        var query = Assert.IsType<DatabaseTableQuery>(client.LastTableQuery);
        Assert.Equal(0, query.Offset);
        var filter = Assert.Single(query.Filters);
        Assert.Equal("id", filter.ColumnName);
        Assert.Equal(DatabaseFilterOperator.In, filter.Operator);
        Assert.Equal([1L], Assert.IsType<object?[]>(filter.Value));
        Assert.Equal(DatabaseFilterOperator.In, panel.FilterOperator?.Operator);

        panel.FilterColumn = panel.FilterColumns.Single(column => string.Equals(column.Name, "id", StringComparison.Ordinal));
        panel.FilterOperator = panel.FilterOperators.Single(option =>
            option.Operator == DatabaseFilterOperator.NotIn);
        panel.FilterValue = "1, 2,\"3\"";
        await panel.ApplyFilterAsync();

        filter = Assert.Single(Assert.IsType<DatabaseTableQuery>(client.LastTableQuery).Filters);
        Assert.Equal(DatabaseFilterOperator.NotIn, filter.Operator);
        Assert.Equal(
            [1L, 2L, 3L],
            Assert.IsType<object?[]>(filter.Value));
    }

    [Fact]
    public async Task Text_quick_filter_orders_like_the_menu_and_round_trips_one_csv_list_value()
    {
        var client = new FakeDatabasePanelClient
        {
            FirstNameValue = new DatabaseValue(
                " a,b ",
                DatabaseValueKind.Text,
                " a,b "),
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.SelectRow(panel.ResultRows[0]);

        Assert.Equal(
            [
                DatabaseFilterOperator.Equal,
                DatabaseFilterOperator.NotEqual,
                DatabaseFilterOperator.LessThan,
                DatabaseFilterOperator.GreaterThan,
                DatabaseFilterOperator.LessThanOrEqual,
                DatabaseFilterOperator.GreaterThanOrEqual,
                DatabaseFilterOperator.Contains,
                DatabaseFilterOperator.NotContains,
                DatabaseFilterOperator.StartsWith,
                DatabaseFilterOperator.EndsWith,
                DatabaseFilterOperator.In,
                DatabaseFilterOperator.NotIn,
                DatabaseFilterOperator.IsNull,
                DatabaseFilterOperator.IsNotNull,
            ],
            panel.GetQuickFilterOperators(1).Select(option => option.Operator));

        await panel.ApplyQuickFilterAsync(1, DatabaseFilterOperator.In);

        Assert.Equal("\" a,b \"", panel.FilterValue);
        var filter = Assert.Single(Assert.IsType<DatabaseTableQuery>(client.LastTableQuery).Filters);
        Assert.Equal(
            [" a,b "],
            Assert.IsType<object?[]>(filter.Value));

        await panel.ApplyFilterAsync();

        filter = Assert.Single(Assert.IsType<DatabaseTableQuery>(client.LastTableQuery).Filters);
        Assert.Equal(
            [" a,b "],
            Assert.IsType<object?[]>(filter.Value));

        panel.FilterValue = " left , right ";
        await panel.ApplyFilterAsync();
        filter = Assert.Single(Assert.IsType<DatabaseTableQuery>(client.LastTableQuery).Filters);
        Assert.Equal(
            [" left ", " right "],
            Assert.IsType<object?[]>(filter.Value));
    }

    [Fact]
    public async Task Null_quick_filter_only_offers_null_predicates()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.SelectRow(panel.ResultRows[1]);

        Assert.Equal(
            [DatabaseFilterOperator.IsNull, DatabaseFilterOperator.IsNotNull],
            panel.GetQuickFilterOperators(1).Select(option => option.Operator));

        await panel.ApplyQuickFilterAsync(1, DatabaseFilterOperator.IsNull);

        var filter = Assert.Single(Assert.IsType<DatabaseTableQuery>(client.LastTableQuery).Filters);
        Assert.Equal(DatabaseFilterOperator.IsNull, filter.Operator);
        Assert.Null(filter.Value);
    }

    [Fact]
    public async Task Failed_page_load_does_not_advance_the_retry_offset()
    {
        var client = new FakeDatabasePanelClient { TableHasMore = true };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        Assert.True(panel.HasNextPage);
        Assert.Equal(0, client.LastTableQuery?.Offset);

        client.ReadTableFailure = "page load failed";
        await panel.NextPageAsync();

        Assert.Equal("page load failed", panel.ErrorMessage);
        Assert.Equal(0, client.LastTableQuery?.Offset);
        Assert.True(panel.HasNextPage);

        client.ReadTableFailure = null;
        await panel.NextPageAsync();

        Assert.Equal(200, client.LastTableQuery?.Offset);
    }

    [Fact]
    public async Task New_rows_save_default_null_and_typed_value_states()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);

        panel.AddRow();
        var nullName = Assert.IsType<DatabaseResultRowViewModel>(panel.SelectedRow);
        panel.SetSelectedCellDefault(0);
        panel.SetSelectedCellNull(1);
        Assert.Equal(DatabaseEditValueState.Default, nullName.Cells[0].State);
        Assert.Equal(DatabaseEditValueState.Null, nullName.Cells[1].State);

        panel.AddRow();
        var named = Assert.IsType<DatabaseResultRowViewModel>(panel.SelectedRow);
        panel.SetSelectedCellDefault(0);
        named.Cells[1].EditText = "Lin";
        Assert.Equal(DatabaseEditValueState.Value, named.Cells[1].State);
        Assert.True(panel.CanSaveChanges);

        await panel.SaveChangesAsync();

        var changes = Assert.IsType<DatabaseTableChanges>(client.LastChanges);
        Assert.Equal(panel.Tables[0].Descriptor, client.LastChangedTable);
        Assert.Empty(changes.Updates);
        Assert.Empty(changes.Deletes);
        Assert.Collection(
            changes.Inserts,
            insert => Assert.Equal(
                [
                    ("id", DatabaseEditValueState.Default, null),
                    ("name", DatabaseEditValueState.Null, null),
                ],
                insert.Values.Select(value =>
                    (value.ColumnName, value.State, value.Value))),
            insert => Assert.Equal(
                [
                    ("id", DatabaseEditValueState.Default, null),
                    ("name", DatabaseEditValueState.Value, "Lin"),
                ],
                insert.Values.Select(value =>
                    (value.ColumnName, value.State, value.Value))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task New_rows_with_unsupported_required_values_cannot_be_saved(
        bool? nullability)
    {
        var client = new FakeDatabasePanelClient
        {
            IncludeUnsupportedRequiredColumn = true,
            UnsupportedRequiredColumnNullability = nullability,
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);

        panel.AddRow();

        var row = Assert.IsType<DatabaseResultRowViewModel>(panel.SelectedRow);
        var payload = Assert.Single(row.Cells, cell => string.Equals(cell.Column.Name, "payload", StringComparison.Ordinal));
        Assert.False(payload.IsEditable);
        Assert.False(payload.IsValid);
        Assert.Contains("requires a value this viewer cannot edit safely", payload.ValidationError);
        Assert.False(panel.CanSaveChanges);

        await panel.SaveChangesAsync();

        Assert.Equal(0, client.ApplyChangesCallCount);
        Assert.Null(client.LastChanges);
    }

    [Fact]
    public async Task Optimistic_conflict_keeps_the_users_edit_available_for_retry()
    {
        var client = new FakeDatabasePanelClient
        {
            MutationResult = new DatabaseMutationResult(
                Inserted: 0,
                Updated: 0,
                Deleted: 0,
                HasConflict: true,
                Message: "The row changed after it was loaded."),
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        var editedRow = panel.ResultRows[0];
        panel.SelectRow(editedRow);
        editedRow.Cells[1].EditText = "Ada Lovelace";

        await panel.SaveChangesAsync();

        Assert.Equal("The row changed after it was loaded.", panel.ErrorMessage);
        Assert.Same(editedRow, panel.ResultRows[0]);
        Assert.Equal("Ada Lovelace", editedRow.Cells[1].EditText);
        Assert.True(editedRow.IsDirty);
        Assert.True(panel.HasPendingChanges);
        var update = Assert.Single(Assert.IsType<DatabaseTableChanges>(client.LastChanges).Updates);
        Assert.Equal(1L, Assert.Single(update.Keys).Value);
        Assert.Equal("Ada Lovelace", Assert.Single(update.Changes).Value);
    }

    [Fact]
    public async Task Successful_insert_is_not_replayed_when_the_followup_refresh_fails()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        panel.AddRow();
        var inserted = Assert.IsType<DatabaseResultRowViewModel>(panel.SelectedRow);
        panel.SetSelectedCellDefault(0);
        inserted.Cells[1].EditText = "Grace";
        Assert.True(panel.CanSaveChanges);
        client.ReadTableFailure = "saved, but refresh failed";

        await panel.SaveChangesAsync();

        Assert.Equal(1, client.ApplyChangesCallCount);
        Assert.Equal("saved, but refresh failed", panel.ErrorMessage);
        Assert.False(panel.HasPendingChanges);
        Assert.False(panel.CanSaveChanges);
        Assert.True(panel.CanChangeSelectedObject);

        await panel.SaveChangesAsync();

        Assert.Equal(1, client.ApplyChangesCallCount);
    }

    [Fact]
    public async Task Successful_update_clears_pending_changes_and_allows_table_navigation()
    {
        var client = new FakeDatabasePanelClient { ReturnPostgreSqlInt32Values = true };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "postgres",
            connectionString: "Host=localhost;Database=demo");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        Assert.False(panel.HasPendingChanges);
        panel.ResultRows[0].Cells[1].EditText = "Ada Lovelace";

        await panel.SaveChangesAsync();

        var update = Assert.Single(Assert.IsType<DatabaseTableChanges>(client.LastChanges).Updates);
        Assert.Single(update.Changes);
        Assert.False(panel.HasPendingChanges);
        Assert.False(panel.CanSaveChanges);

        await panel.PreviewTableAsync(panel.Tables[1]);

        Assert.Equal(panel.Tables[1], panel.SelectedObject);
        Assert.Null(panel.ErrorMessage);
    }

    [Fact]
    public async Task Failed_save_error_remains_visible_when_pending_changes_block_navigation()
    {
        var client = new FakeDatabasePanelClient
        {
            ApplyChangesFailure = "update failed",
        };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;
        await panel.PreviewTableAsync(panel.Tables[0]);
        var selectedObject = panel.SelectedObject;
        panel.ResultRows[0].Cells[1].EditText = "Ada Lovelace";

        await panel.SaveChangesAsync();
        await panel.PreviewTableAsync(panel.Tables[1]);

        Assert.Equal("update failed", panel.ErrorMessage);
        Assert.Equal(selectedObject, panel.SelectedObject);
        Assert.True(panel.HasPendingChanges);
        Assert.True(panel.CanSaveChanges);
        Assert.False(panel.CanChangeSelectedObject);
    }

    [Fact]
    public async Task View_metadata_explains_why_rows_are_read_only()
    {
        var client = new FakeDatabasePanelClient();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db");
        await panel.Initialization;

        await panel.PreviewTableAsync(panel.Tables[1]);

        Assert.False(panel.CanEditRows);
        Assert.Equal("Views are read-only.", panel.ReadOnlyReason);
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly MemoryStream _content = new();
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public TaskCompletionSource WriteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _content.Length;

        public override long Position
        {
            get => _content.Position;
            set => throw new NotSupportedException();
        }

        public void Release() => _release.Set();

        public override void Flush() => _content.Flush();

        public override void Write(byte[] buffer, int offset, int count)
        {
            WaitForRelease();
            _content.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WaitForRelease();
            _content.Write(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
                _release.Dispose();
                _content.Dispose();
            }

            base.Dispose(disposing);
        }

        private void WaitForRelease()
        {
            WriteStarted.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The export test did not release its destination.");
            }
        }
    }

    private sealed class InspectingWriteStream : Stream
    {
        private readonly byte[] _pattern;
        private readonly int[] _patternPrefix;
        private int _matchedPatternBytes;
        private long _length;

        public InspectingWriteStream(string? pattern = null)
        {
            _pattern = pattern is null
                ? []
                : System.Text.Encoding.UTF8.GetBytes(pattern);
            _patternPrefix = BuildPrefixTable(_pattern);
        }

        public int MaximumWriteSize { get; private set; }

        public int NewlineCount { get; private set; }

        public int PatternCount { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            MaximumWriteSize = Math.Max(MaximumWriteSize, buffer.Length);
            _length += buffer.Length;
            foreach (var value in buffer)
            {
                if (value == (byte)'\n')
                {
                    NewlineCount++;
                }

                CountPatternByte(value);
            }
        }

        private void CountPatternByte(byte value)
        {
            if (_pattern.Length == 0)
            {
                return;
            }

            while (_matchedPatternBytes > 0
                   && value != _pattern[_matchedPatternBytes])
            {
                _matchedPatternBytes = _patternPrefix[_matchedPatternBytes - 1];
            }

            if (value == _pattern[_matchedPatternBytes])
            {
                _matchedPatternBytes++;
            }

            if (_matchedPatternBytes == _pattern.Length)
            {
                PatternCount++;
                _matchedPatternBytes = _patternPrefix[_matchedPatternBytes - 1];
            }
        }

        private static int[] BuildPrefixTable(IReadOnlyList<byte> pattern)
        {
            var prefix = new int[pattern.Count];
            for (var index = 1; index < pattern.Count; index++)
            {
                var matched = prefix[index - 1];
                while (matched > 0 && pattern[index] != pattern[matched])
                {
                    matched = prefix[matched - 1];
                }

                if (pattern[index] == pattern[matched])
                {
                    matched++;
                }

                prefix[index] = matched;
            }

            return prefix;
        }
    }

    public enum QueryProvenanceShape
    {
        Missing,
        Exact,
        Aliased,
        Incomplete,
    }

    internal sealed class FakeDiagramSession : IDatabaseDiagramSession
    {
        public bool Disposed { get; private set; }
        public Task? ExportGate { get; set; }
        public Task<byte[]> RenderViewportAsync(DatabaseDiagramViewport viewport, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<byte>());
        public async Task ExportAsync(Stream destination, DatabaseDiagramExport format, CancellationToken cancellationToken)
        {
            if (ExportGate is { } gate) { await gate; }
            // Intentionally ignores cancellation: publication must still check ownership.
            await destination.WriteAsync("complete diagram"u8.ToArray(), CancellationToken.None);
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeDatabasePanelClient : IDatabasePanelClient
    {
        public DatabaseInsertedRow? LastFormattedInsert { get; private set; }
        public Func<DatabaseValueContentStore>? ResultContentFactory { get; init; }
        public bool HoldDiagram { get; init; }
        public bool DiagramCanceled { get; private set; }
        public string? FailWith { get; set; }

        public string? LastSql { get; private set; }

        public ConnectionProfile? LastTunnel { get; private set; }

        public DatabaseTableDescriptor? LastDetailsObject { get; private set; }

        public DatabaseTableDescriptor? LastReadTable { get; private set; }

        public DatabaseTableQuery? LastTableQuery { get; private set; }

        public DatabaseTableDescriptor? LastChangedTable { get; private set; }

        public DatabaseTableChanges? LastChanges { get; private set; }

        public DatabaseMutationResult? MutationResult { get; set; }

        public string? ApplyChangesFailure { get; set; }

        public string? ReadTableFailure { get; set; }

        public string? ReadQueryFailure { get; set; }

        public int ApplyChangesCallCount { get; private set; }

        public int QueryCallCount { get; private set; }

        public int GetObjectDetailsCallCount { get; private set; }

        public int SchemaGraphCallCount { get; private set; }

        public int ReadTableCallCount { get; private set; }

        public int ReadQueryCallCount { get; private set; }

        public string? LastReadQuerySql { get; private set; }

        public IReadOnlyList<DatabaseColumnDescriptor>? LastReadQueryColumns { get; private set; }

        public DatabaseTableQuery? LastReadQuery { get; private set; }

        public QueryProvenanceShape QueryProvenance { get; set; }

        public TaskCompletionSource<bool>? ReadTableStarted { get; set; }

        public TaskCompletionSource<bool>? ReadTableRelease { get; set; }

        public bool IncludeUnsupportedRequiredColumn { get; set; }

        public bool IncludeBinaryColumn { get; set; }

        public bool IncludeCollectionColumn { get; set; }

        public bool IncludeQueryExtraColumn { get; set; }

        public DatabaseValue? FirstNameValue { get; set; }

        public DatabaseValue? FirstBinaryValue { get; set; }

        public DatabaseValue? FirstCollectionValue { get; set; }

        public bool IdIsIdentity { get; set; }

        public bool? UnsupportedRequiredColumnNullability { get; set; }

        public bool ReturnDisplayOnlyPrimaryKey { get; set; }

        public bool ReturnPostgreSqlInt32Values { get; set; }

        public bool ReturnEmptyTable { get; set; }

        public bool TableHasMore { get; set; }

        public long TableTotalRows { get; set; } = 2;

        public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
        [
            new("sqlite", "SQLite", "Data Source=…"),
            new("postgres", "PostgreSQL", "Host=…"),
            new("oracle", "Oracle", "Data Source=…"),
        ];

        public string? LastConnectionString { get; private set; }

        public Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken)
        {
            LastTunnel = tunnel;
            LastConnectionString = connectionString;
            ThrowIfConfigured();
            return Task.FromResult<IReadOnlyList<DatabaseTableDescriptor>>(
            [
                new("people", DatabaseTableKind.Table),
                new("names", DatabaseTableKind.View),
            ]);
        }

        public Task<DatabaseQueryPage> QueryAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            string sql,
            int maxRows,
            CancellationToken cancellationToken)
        {
            ThrowIfConfigured();
            QueryCallCount++;
            LastTunnel = tunnel;
            LastConnectionString = connectionString;
            LastSql = sql;
            return Task.FromResult(BuildQueryPage(QueryColumns()));
        }

        public Task<DatabaseTablePage> ReadQueryAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            string sourceSql,
            IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
            DatabaseTableQuery query,
            CancellationToken cancellationToken)
        {
            ThrowIfConfigured();
            ReadQueryCallCount++;
            if (ReadQueryFailure is { } message)
            {
                throw new InvalidOperationException(message);
            }

            LastTunnel = tunnel;
            LastConnectionString = connectionString;
            LastReadQuerySql = sourceSql;
            LastReadQueryColumns = [.. sourceColumns];
            LastReadQuery = query;
            return Task.FromResult(new DatabaseTablePage(
                BuildQueryPage(sourceColumns),
                query.Offset,
                query.Limit,
                HasMore: TableHasMore,
                TotalRows: TableTotalRows));
        }

        public Task<long> CountQueryRowsAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            string sourceSql,
            IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
            IReadOnlyList<DatabaseFilterCondition> filters,
            CancellationToken cancellationToken)
        {
            ThrowIfConfigured();
            LastTunnel = tunnel;
            LastConnectionString = connectionString;
            LastReadQuerySql = sourceSql;
            LastReadQueryColumns = [.. sourceColumns];
            return Task.FromResult(TableTotalRows);
        }

        public Task<DatabaseObjectDetails> GetObjectDetailsAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            DatabaseTableDescriptor databaseObject,
            CancellationToken cancellationToken)
        {
            ThrowIfConfigured();
            GetObjectDetailsCallCount++;
            LastTunnel = tunnel;
            LastConnectionString = connectionString;
            LastDetailsObject = databaseObject;
            var columns = new List<DatabaseColumnSchema>
            {
                new(
                    "id",
                    0,
                    "INTEGER",
                    DatabaseValueKind.SignedInteger,
                    typeof(long).FullName,
                    IsNullable: false,
                    IsPrimaryKey: true,
                    PrimaryKeyOrdinal: 1,
                    IsIdentity: IdIsIdentity,
                    DefaultExpression: "next_person_id()"),
                new(
                    "name",
                    1,
                    "TEXT",
                    DatabaseValueKind.Text,
                    typeof(string).FullName,
                    IsNullable: true),
            };
            if (IncludeBinaryColumn)
            {
                columns.Add(new DatabaseColumnSchema(
                    "payload",
                    2,
                    "BLOB",
                    DatabaseValueKind.Binary,
                    typeof(byte[]).FullName,
                    IsNullable: true));
            }

            if (IncludeCollectionColumn)
            {
                columns.Add(new DatabaseColumnSchema(
                    "tags",
                    columns.Count,
                    "ARRAY",
                    DatabaseValueKind.Collection,
                    typeof(int[]).FullName,
                    IsNullable: true));
            }

            if (IncludeUnsupportedRequiredColumn)
            {
                columns.Add(new DatabaseColumnSchema(
                    "payload",
                    2,
                    "JSON",
                    DatabaseValueKind.Json,
                    typeof(string).FullName,
                    IsNullable: UnsupportedRequiredColumnNullability));
            }

            return Task.FromResult(new DatabaseObjectDetails(
                databaseObject,
                columns,
                [
                    new DatabaseIndexSchema(
                        "pk_people",
                        "PRIMARY KEY",
                        IsUnique: true,
                        IsPrimary: true,
                        IsValid: true,
                        [new DatabaseIndexColumn("id", 0)]),
                    new DatabaseIndexSchema(
                        "ix_people_name",
                        "INDEX",
                        IsUnique: false,
                        IsPrimary: false,
                        IsValid: true,
                        [new DatabaseIndexColumn("name", 0)]),
                ],
                CanEdit: databaseObject.Kind == DatabaseTableKind.Table,
                ReadOnlyReason: databaseObject.Kind == DatabaseTableKind.View
                    ? "Views are read-only."
                    : null));
        }

        public async Task<IDatabaseDiagramSession> OpenDatabaseDiagramAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken)
        {
            SchemaGraphCallCount++;
            if (HoldDiagram)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    DiagramCanceled = true;
                    throw;
                }
            }

            return new FakeDiagramSession();
        }

        public Task<DatabaseSchemaGraph> GetDatabaseSchemaGraphAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken)
        {
            ThrowIfConfigured();
            SchemaGraphCallCount++;
            return Task.FromResult(new DatabaseSchemaGraph(
            [
                new DatabaseSchemaTable(
                    new DatabaseTableDescriptor("people", DatabaseTableKind.Table),
                    [
                        new DatabaseColumnSchema(
                            "id",
                            1,
                            "INTEGER",
                            DatabaseValueKind.SignedInteger,
                            IsNullable: false,
                            IsPrimaryKey: true,
                            PrimaryKeyOrdinal: 1),
                    ],
                    []),
            ]));
        }

        public async Task<DatabaseTablePage> ReadTableAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            DatabaseTableDescriptor table,
            DatabaseTableQuery query,
            CancellationToken cancellationToken)
        {
            ThrowIfConfigured();
            ReadTableCallCount++;
            if (ReadTableFailure is { } message)
            {
                throw new InvalidOperationException(message);
            }

            LastTunnel = tunnel;
            LastConnectionString = connectionString;
            LastReadTable = table;
            LastTableQuery = query;
            ReadTableStarted?.TrySetResult(true);
            if (ReadTableRelease is { } release)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            var columns = new List<DatabaseColumnDescriptor>
            {
                new(
                    "id",
                    "INTEGER",
                    DatabaseValueKind.SignedInteger,
                    typeof(long).FullName,
                    IsNullable: false,
                    IsKey: true,
                    IsIdentity: IdIsIdentity,
                    IsReadOnly: false),
                new(
                    "name",
                    "TEXT",
                    DatabaseValueKind.Text,
                    typeof(string).FullName,
                    IsNullable: true),
            };
            var firstName = FirstNameValue
                ?? new DatabaseValue("Ada", DatabaseValueKind.Text, "Ada");
            var rows = new List<IReadOnlyList<string?>>
            {
                new string?[] { "1", firstName.IsNull ? null : firstName.DisplayText },
                new string?[] { "2", null },
            };
            var typedRows = new List<IReadOnlyList<DatabaseValue>>
            {
                new DatabaseValue[]
                {
                    ReturnDisplayOnlyPrimaryKey
                        ? new DatabaseValue("provider key 1", DatabaseValueKind.Other, "provider key 1")
                        : ReturnPostgreSqlInt32Values
                            ? new DatabaseValue(1, DatabaseValueKind.SignedInteger, "1")
                            : new DatabaseValue(1L, DatabaseValueKind.SignedInteger, "1"),
                    firstName,
                },
                new DatabaseValue[]
                {
                    ReturnDisplayOnlyPrimaryKey
                        ? new DatabaseValue("provider key 2", DatabaseValueKind.Other, "provider key 2")
                        : ReturnPostgreSqlInt32Values
                            ? new DatabaseValue(2, DatabaseValueKind.SignedInteger, "2")
                            : new DatabaseValue(2L, DatabaseValueKind.SignedInteger, "2"),
                    new(null, DatabaseValueKind.Text, "NULL"),
                },
            };
            if (IncludeBinaryColumn)
            {
                var binary = FirstBinaryValue
                    ?? new DatabaseValue(
                        new byte[] { 0x01, 0x02 },
                        DatabaseValueKind.Binary,
                        "0x0102");
                columns.Add(new DatabaseColumnDescriptor(
                    "payload",
                    "BLOB",
                    DatabaseValueKind.Binary,
                    typeof(byte[]).FullName,
                    IsNullable: true));
                rows[0] = [.. rows[0], binary.DisplayText];
                rows[1] = [.. rows[1], null];
                typedRows[0] =
                [
                    .. typedRows[0],
                    binary,
                ];
                typedRows[1] =
                [
                    .. typedRows[1],
                    new DatabaseValue(null, DatabaseValueKind.Binary, "NULL"),
                ];
            }

            if (IncludeCollectionColumn)
            {
                var values = Enumerable.Range(0, 40).ToArray();
                var collection = FirstCollectionValue
                    ?? new DatabaseValue(
                        values,
                        DatabaseValueKind.Collection,
                        "[0, 1, 2, 3]… (40 values)",
                        IsTruncated: true);
                columns.Add(new DatabaseColumnDescriptor(
                    "tags",
                    "ARRAY",
                    DatabaseValueKind.Collection,
                    typeof(int[]).FullName,
                    IsNullable: true));
                rows[0] = [.. rows[0], collection.DisplayText];
                rows[1] = [.. rows[1], null];
                typedRows[0] = [.. typedRows[0], collection];
                typedRows[1] =
                [
                    .. typedRows[1],
                    new DatabaseValue(null, DatabaseValueKind.Collection, "NULL"),
                ];
            }

            if (IncludeUnsupportedRequiredColumn)
            {
                columns.Add(new DatabaseColumnDescriptor(
                    "payload",
                    "JSON",
                    DatabaseValueKind.Json,
                    typeof(string).FullName,
                    IsNullable: UnsupportedRequiredColumnNullability));
                rows[0] = [.. rows[0], "{}"];
                rows[1] = [.. rows[1], "{}"];
                typedRows[0] =
                [
                    .. typedRows[0],
                    new DatabaseValue("{}", DatabaseValueKind.Json, "{}"),
                ];
                typedRows[1] =
                [
                    .. typedRows[1],
                    new DatabaseValue("{}", DatabaseValueKind.Json, "{}"),
                ];
            }

            if (ReturnEmptyTable)
            {
                rows.Clear();
                typedRows.Clear();
            }

            var result = new DatabaseQueryPage(
                columns,
                rows,
                Truncated: false,
                RowsAffected: 0,
                TimeSpan.FromMilliseconds(2),
                typedRows,
                ResultContentFactory?.Invoke());
            return new DatabaseTablePage(
                result,
                query.Offset,
                query.Limit,
                HasMore: TableHasMore,
                TotalRows: TableTotalRows);
        }

        public Task<DatabaseMutationResult> ApplyTableChangesAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            DatabaseTableDescriptor table,
            DatabaseTableChanges changes,
            CancellationToken cancellationToken)
        {
            ThrowIfConfigured();
            if (ApplyChangesFailure is { } message)
            {
                throw new InvalidOperationException(message);
            }

            ApplyChangesCallCount++;
            LastTunnel = tunnel;
            LastConnectionString = connectionString;
            LastChangedTable = table;
            LastChanges = changes;
            return Task.FromResult(MutationResult ?? new DatabaseMutationResult(
                changes.Inserts.Count,
                changes.Updates.Count,
                changes.Deletes.Count));
        }

        public string BuildTablePreviewQuery(string driverId, string tableName, int limit) =>
            $"SELECT * FROM \"{tableName}\" LIMIT {limit};";

        public string BuildInsertStatement(
            string driverId,
            DatabaseObjectDetails details,
            DatabaseInsertedRow row,
            int maximumUtf8Bytes = int.MaxValue)
        {
            LastFormattedInsert = row;
            var values = row.Values
                .Where(value => value.State != DatabaseEditValueState.Default)
                .ToArray();
            var names = string.Join(", ", values.Select(value =>
                $"\"{value.ColumnName.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
            var literals = string.Join(", ", values.Select(FormatInsertValue));
            return $"INSERT INTO \"{details.Object.Name}\" ({names}) VALUES ({literals});";
        }

        public DatabaseConnectionDetails ParseConnectionDetails(
            string driverId,
            string connectionString) =>
            connectionString.Contains("Password=", StringComparison.OrdinalIgnoreCase)
                ? new(Options: connectionString, Password: "present")
                : new(Options: connectionString);

        public string BuildConnectionString(string driverId, DatabaseConnectionDetails details) =>
            details.Password is { } password
                ? $"{details.FilePath ?? details.Options};Password={password}"
                : details.FilePath ?? details.Options ?? string.Empty;

        private static string FormatInsertValue(DatabaseColumnEdit edit)
        {
            if (edit.State == DatabaseEditValueState.Null)
            {
                return "NULL";
            }

            return edit.Value switch
            {
                null => "NULL",
                string value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'",
                byte[] value => $"X'{Convert.ToHexString(value)}'",
                bool value => value ? "1" : "0",
                System.Collections.IEnumerable value => $"'{FormatCollection(value)}'",
                _ => Convert.ToString(
                        edit.Value,
                        System.Globalization.CultureInfo.InvariantCulture)
                    ?? "NULL",
            };
        }

        private static string FormatCollection(System.Collections.IEnumerable values) =>
            "[" + string.Join(", ", values.Cast<object?>().Select(value => Convert.ToString(
                value,
                System.Globalization.CultureInfo.InvariantCulture))) + "]";

        private IReadOnlyList<DatabaseColumnDescriptor> QueryColumns()
        {
            var table = new DatabaseObjectId(Catalog: null, Schema: null, Name: "people");
            var source = QueryProvenance == QueryProvenanceShape.Missing ? null : table;
            var idName = QueryProvenance == QueryProvenanceShape.Aliased
                ? "person_id"
                : "id";
            var columns = new List<DatabaseColumnDescriptor>
            {
                new(
                    idName,
                    "INTEGER",
                    DatabaseValueKind.SignedInteger,
                    typeof(long).FullName,
                    IsNullable: false,
                    IsKey: true,
                    BaseColumnName: "id",
                    BaseObject: source),
            };
            if (QueryProvenance != QueryProvenanceShape.Incomplete)
            {
                columns.Add(new DatabaseColumnDescriptor(
                    "name",
                    "TEXT",
                    DatabaseValueKind.Text,
                    typeof(string).FullName,
                    IsNullable: true,
                    BaseColumnName: "name",
                    BaseObject: source));
            }

            if (IncludeQueryExtraColumn)
            {
                columns.Add(new DatabaseColumnDescriptor(
                    "extra",
                    "TEXT",
                    DatabaseValueKind.Text,
                    typeof(string).FullName,
                    IsNullable: true));
            }

            return columns;
        }

        private static DatabaseQueryPage BuildQueryPage(
            IReadOnlyList<DatabaseColumnDescriptor> columns)
        {
            var first = columns.Select(column => ValueFor(column, 1)).ToArray();
            var second = columns.Select(column => ValueFor(column, 2)).ToArray();
            IReadOnlyList<IReadOnlyList<DatabaseValue>> typedRows = [first, second];
            var displayRows = typedRows
                .Select(row => (IReadOnlyList<string?>)[.. row.Select(value => value.IsNull ? null : value.DisplayText)])
                .ToArray();
            return new DatabaseQueryPage(
                columns,
                displayRows,
                Truncated: false,
                RowsAffected: 0,
                TimeSpan.FromMilliseconds(3),
                typedRows);
        }

        private static DatabaseValue ValueFor(DatabaseColumnDescriptor column, int rowNumber) =>
            string.Equals(
                column.BaseColumnName ?? column.Name,
                "id",
                StringComparison.Ordinal)
                ? new DatabaseValue(
                    (long)rowNumber,
                    DatabaseValueKind.SignedInteger,
                    rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : rowNumber == 1
                    ? new DatabaseValue("Ada", DatabaseValueKind.Text, "Ada")
                    : new DatabaseValue(null, DatabaseValueKind.Text, "NULL");

        private void ThrowIfConfigured()
        {
            if (FailWith is { } message)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

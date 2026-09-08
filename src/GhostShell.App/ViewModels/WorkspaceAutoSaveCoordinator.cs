using System.Runtime.CompilerServices;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns durable write-back of live workspace layouts. Navigation queues or
/// flushes autosave for the active workspace; manual saves target an open
/// workspace without changing which one is active.
/// </summary>
public sealed class WorkspaceAutoSaveCoordinator : IDisposable
{
    private const int WorkspaceAutoSaveDebounceMilliseconds = 1500;

    private readonly IDefinitionCatalog _catalog;
    private readonly Func<RuntimeWorkspaceViewModel?> _runtimeWorkspace;
    private readonly Func<RuntimeHistorySource?> _historySource;
    private readonly Func<bool> _isShutdown;
    private readonly TimeProvider _timeProvider;
    private readonly ISecretVault? _secretVault;
    private readonly Action<string>? _reportCaptureError;
    private readonly ConditionalWeakTable<RuntimePanelViewModel, DatabaseRecoveryState> _unavailableDatabaseRecovery = [];
    private string? _captureError;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _debounce;
    private bool _sealed;
    private bool _disposed;
    private bool _isSavingLayout;
    private readonly ConditionalWeakTable<RuntimeWorkspaceViewModel, List<(string Title, string Layout)>> _savedLayouts = [];

    // Remember the materialized layout, not generated persistence IDs. Opening
    // a connection-reference workspace must not itself count as an edit.
    public void TrackLayout(RuntimeWorkspaceViewModel runtime) =>
        _savedLayouts.GetValue(runtime, CaptureLayoutShape);

    public bool CanSaveLayout(RuntimeWorkspaceViewModel runtime, WorkspaceId workspaceId) =>
        !_sealed && !_isSavingLayout && !_isShutdown()
        && _catalog.Snapshot.Workspaces.Any(item => item.Value.Id == workspaceId && !item.Value.AutoSave)
        && _savedLayouts.TryGetValue(runtime, out var saved)
        && !saved.SequenceEqual(CaptureLayoutShape(runtime));

    public async Task<string?> SaveLayoutAsync(RuntimeWorkspaceViewModel runtime, WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        if (!CanSaveLayout(runtime, workspaceId))
        {
            return null;
        }

        _isSavingLayout = true;
        try
        {
            var stored = _catalog.Snapshot.Workspaces.Single(item => item.Value.Id == workspaceId);
            var shape = CaptureLayoutShape(runtime);
            var capture = await CaptureWorkspaceAutoSaveAsync(runtime, stored.Value, stored.Revision, cancellationToken);
            if (capture is null)
            {
                return _captureError ?? "The workspace layout is not ready to save. Wait for its panels to finish opening and try again.";
            }

            var error = await _catalog.SaveWorkspaceWithLayoutsAsync(capture.Workspace,
                capture.WorkspaceRevision, capture.Layouts, cancellationToken);
            if (error is not null)
            {
                return error.Message;
            }

            _savedLayouts.Remove(runtime);
            _savedLayouts.Add(runtime, shape);
            await CleanUpOrphanedAutoSaveLayoutsAsync(capture.Workspace);
            return null;
        }
        finally
        {
            _isSavingLayout = false;
        }
    }

    private static List<(string Title, string Layout)> CaptureLayoutShape(RuntimeWorkspaceViewModel runtime) =>
        [.. runtime.Tabs.Where(tab => !IsLauncherTab(tab)).Select(tab => (tab.Title, tab.SerializeDockLayout()))];

    public WorkspaceAutoSaveCoordinator(
        IDefinitionCatalog catalog,
        Func<RuntimeWorkspaceViewModel?> runtimeWorkspace,
        Func<RuntimeHistorySource?> historySource,
        Func<bool> isShutdown,
        TimeProvider? timeProvider = null,
        ISecretVault? secretVault = null,
        Action<string>? reportCaptureError = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtimeWorkspace = runtimeWorkspace
            ?? throw new ArgumentNullException(nameof(runtimeWorkspace));
        _historySource = historySource
            ?? throw new ArgumentNullException(nameof(historySource));
        _isShutdown = isShutdown ?? throw new ArgumentNullException(nameof(isShutdown));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _secretVault = secretVault;
        _reportCaptureError = reportCaptureError;
    }

    private sealed record WorkspaceAutoSaveCapture(
        WorkspaceDefinition Workspace,
        long WorkspaceRevision,
        IReadOnlyList<(LayoutDefinition Definition, long? ExpectedRevision)> Layouts);

    /// <summary>
    /// Schedules a write-back of the live tabs into the open workspace's durable
    /// definition. Piggybacks on the recovery-snapshot triggers, so anything worth
    /// recovering is also worth persisting; the debounce coalesces drag storms
    /// into one save.
    /// </summary>
    public void Queue()
    {
        if (_sealed || _isShutdown() || AutoSaveSourceWorkspace() is null)
        {
            return;
        }

        _debounce?.Cancel();
        var debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _debounce = debounce;
        _ = AutoSaveWorkspaceAsync(debounce);
    }

    private StoredDefinition<WorkspaceDefinition>? AutoSaveSourceWorkspace()
    {
        if (_historySource()?.SourceDefinition is not { } sourceKey
            || sourceKey.Kind != WorkspaceDefinition.Kind)
        {
            return null;
        }

        var stored = _catalog.Snapshot.Workspaces
            .SingleOrDefault(item => string.Equals(item.Value.Id.Value, sourceKey.Value, StringComparison.Ordinal));
        return stored is { Value.AutoSave: true } ? stored : null;
    }

    private async Task AutoSaveWorkspaceAsync(CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(WorkspaceAutoSaveDebounceMilliseconds),
                _timeProvider,
                debounce.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_debounce, debounce))
            {
                _debounce = null;
            }

            debounce.Dispose();
        }

        if (_sealed || _isShutdown())
        {
            return;
        }

        await PersistWorkspaceAutoSaveAsync();
    }

    /// <summary>
    /// Writes the pending autosave now instead of when the debounce elapses.
    ///
    /// Leaving a workspace is exactly when the debounce would be lost: it fires
    /// against whichever workspace is active at the time, so a switch a second
    /// after a change used to save the wrong one — or nothing. Every path that
    /// changes which workspace is in front flushes first.
    /// </summary>
    public async Task FlushAsync()
    {
        var pending = _debounce;
        if (pending is null || pending.IsCancellationRequested || _sealed || _isShutdown())
        {
            return;
        }

        pending.Cancel();
        _debounce = null;
        await PersistWorkspaceAutoSaveAsync();
    }

    private async Task PersistWorkspaceAutoSaveAsync()
    {
        if (AutoSaveSourceWorkspace() is not { } stored || _runtimeWorkspace() is not { } runtime)
        {
            return;
        }

        WorkspaceAutoSaveCapture? capture;
        try
        {
            capture = await CaptureWorkspaceAutoSaveAsync(runtime, stored.Value, stored.Revision, _lifetime.Token);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or FormatException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "autosave.workspace-capture.failed",
                exception);
            return;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }

        if (capture is null)
        {
            return;
        }

        var shape = CaptureLayoutShape(runtime);
        var error = await _catalog.SaveWorkspaceWithLayoutsAsync(
            capture.Workspace,
            capture.WorkspaceRevision,
            capture.Layouts,
            CancellationToken.None);
        if (error is null)
        {
            _savedLayouts.Remove(runtime);
            _savedLayouts.Add(runtime, shape);
            await CleanUpOrphanedAutoSaveLayoutsAsync(capture.Workspace);
            return;
        }

        // A revision conflict means another writer got there first; the next
        // change captures against the fresh revision. Anything else is logged
        // rather than surfaced — autosave must not nag while the user works.
        if (error.Code != DefinitionStoreErrorCode.RevisionConflict)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                $"workspace.autosave.failed.{StoreErrorCode(error.Code)}",
                SecretSafeDiagnosticKind.Unexpected);
        }
    }

    private static string StoreErrorCode(DefinitionStoreErrorCode code) => code switch
    {
        DefinitionStoreErrorCode.NotFound => "not-found",
        DefinitionStoreErrorCode.RevisionConflict => "revision-conflict",
        DefinitionStoreErrorCode.InvalidDefinition => "invalid-definition",
        DefinitionStoreErrorCode.UnsupportedKind => "unsupported-kind",
        DefinitionStoreErrorCode.UnsupportedSchema => "unsupported-schema",
        DefinitionStoreErrorCode.DependencyConflict => "dependency-conflict",
        DefinitionStoreErrorCode.UnsafePayload => "unsafe-payload",
        DefinitionStoreErrorCode.StorageUnavailable => "storage-unavailable",
        DefinitionStoreErrorCode.StorageFailure => "storage-failure",
        DefinitionStoreErrorCode.Cancelled => "cancelled",
        _ => "unknown",
    };

    /// <summary>
    /// Captures the live tabs as workspace-only tab entries plus one auto-saved
    /// layout per tab. Returns null when the runtime is mid-mutation (placeholder
    /// or unavailable panels, dock tree out of step) or when nothing changed —
    /// which also breaks the save→refresh→save loop, since a save's own catalog
    /// refresh re-queues an identical capture.
    /// </summary>
    private async Task<WorkspaceAutoSaveCapture?> CaptureWorkspaceAutoSaveAsync(
        RuntimeWorkspaceViewModel runtime,
        WorkspaceDefinition storedDefinition,
        long storedRevision,
        CancellationToken cancellationToken)
    {
        _captureError = null;
        using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var initialShape = CaptureLayoutShape(runtime);
        if (runtime.Tabs.Count == 0)
        {
            return null;
        }

        var storedTabs = storedDefinition.Entries.OfType<WorkspaceEntry.Tab>().ToList();
        var storedLayouts = _catalog.Snapshot.Layouts
            .ToDictionary(item => item.Value.Id.Value, StringComparer.Ordinal);
        var usedStoredTabs = new HashSet<WorkspaceEntryId>();
        var capturedDatabaseRoutes = new Dictionary<ConnectionId, ConnectionProfile>();
        var layouts = new List<(LayoutDefinition Definition, long? ExpectedRevision)>();
        var entries = new List<WorkspaceEntry>();
        var tabs = runtime.Tabs.ToArray();
        for (var index = 0; index < tabs.Length; index++)
        {
            var tab = tabs[index];
            if (IsLauncherTab(tab))
            {
                // A launcher tab is a question, not content: there is nothing
                // in it to describe. Deferring the whole pass for one froze the
                // definition for as long as it stayed open, and the workspace
                // then reopened from whatever had been saved before it appeared
                // — which reads as closed tabs coming back.
                continue;
            }

            // Dock documents are the durable slot identities: a restored panel
            // keeps its saved document id, so capturing by document keeps slot
            // ids stable across sessions. The document's context is the live
            // panel bound to that slot.
            var panelsBySlot = new Dictionary<string, RuntimePanelViewModel>(StringComparer.Ordinal);
            foreach (var region in DockLayoutProjection.CollectRegions(tab.DockLayout))
            {
                if (region.Document.Context is not RuntimePanelViewModel panel)
                {
                    // A document with no bound panel is an empty layout slot:
                    // it keeps its place in the dock geometry but gets no slot
                    // mapping.
                    continue;
                }

                if (PanelKindForAutoSave(panel) is null)
                {
                    // A placeholder or unavailable panel cannot be described
                    // durably; saving now would drop it from the definition.
                    // Defer the whole pass until the runtime settles.
                    return null;
                }

                panelsBySlot[region.Document.Id] = panel;
            }

            if (panelsBySlot.Count == 0 || panelsBySlot.Count != tab.Panels.Count)
            {
                return null;
            }

            var (grid, projectedSlots) = DockLayoutProjection.ProjectSlots(
                tab.DockLayout,
                id => panelsBySlot.TryGetValue(id, out var panel)
                    ? new LayoutMinimumSize(panel.LayoutMinimumWidth, panel.LayoutMinimumHeight)
                    : new LayoutMinimumSize(220, 140));
            var slots = projectedSlots
                .Where(slot => panelsBySlot.ContainsKey(slot.Id.Value))
                .ToArray();
            if (slots.Length != panelsBySlot.Count)
            {
                return null;
            }
            var layoutId = new LayoutId(
                $"{LayoutDefinition.AutoSaveIdPrefix}{storedDefinition.Id.Value}.tab-{index}");
            var layout = new LayoutDefinition(
                layoutId,
                LayoutDefinition.CurrentSchemaVersion,
                $"{tab.Title} (auto)",
                grid,
                slots,
                tab.SerializeDockLayout());
            layouts.Add((
                layout,
                storedLayouts.TryGetValue(layoutId.Value, out var storedLayout)
                    ? storedLayout.Revision
                    : null));

            var storedTab = storedTabs.FirstOrDefault(candidate =>
                !usedStoredTabs.Contains(candidate.Id)
                && string.Equals(candidate.Name, tab.Title, StringComparison.Ordinal));
            if (storedTab is not null)
            {
                usedStoredTabs.Add(storedTab.Id);
            }

            var usedStoredPanels = new HashSet<ScreenPanelId>();
            var capturedPanels = new List<ScreenPanelDefinition>();
            foreach (var slot in slots)
            {
                var panel = panelsBySlot[slot.Id.Value];
                var protectedTarget = _unavailableDatabaseRecovery.TryGetValue(panel, out var recovery) ? recovery.Target : null;
                var captured = CaptureAutoSavePanel(panel, slot.Id, storedTab, storedTabs, usedStoredPanels, protectedTarget);
                if (panel is UnavailableRuntimePanelViewModel and not PendingDatabaseRecoveryPanelViewModel
                    && captured.Kind == ScreenPanelKind.DatabaseViewer)
                {
                    captured = await ProtectUnavailableDatabaseTargetAsync(panel, captured, capturedDatabaseRoutes, captureCancellation.Token);
                    if (captured is null) { return null; }
                }
                if (_sealed || _isShutdown() || !runtime.Tabs.Contains(tab) || !tab.Panels.Contains(panel)) { return null; }
                capturedPanels.Add(captured);
            }
            entries.Add(new WorkspaceEntry.Tab(
                storedTab?.Id ?? WorkspaceEntryId.New(),
                tab.Title,
                layoutId,
                capturedPanels));
        }

        captureCancellation.Token.ThrowIfCancellationRequested();
        if (!initialShape.SequenceEqual(CaptureLayoutShape(runtime))) { return null; }
        if (capturedDatabaseRoutes.Any(captured =>
            _catalog.Snapshot.Connections.SingleOrDefault(item => item.Value.Id == captured.Key)?.Value != captured.Value))
        {
            _ = CannotProtectDatabaseTarget();
            return null;
        }

        // Every tab is the launcher, so nothing durable is open and the
        // definition says so. Holding the previous entries back instead left the
        // workspace describing tabs the user had closed, and reopening it
        // brought them all back.

        // Connection and saved-screen references materialized into the live tabs
        // above; under autosave the definition is the live state, so the entry
        // list is replaced wholesale.
        var definition = new WorkspaceDefinition(
            storedDefinition.Id,
            WorkspaceDefinition.CurrentSchemaVersion,
            storedDefinition.Name,
            storedDefinition.Description,
            storedDefinition.Accent,
            entries,
            storedDefinition.AgentPolicyOverride,
            storedDefinition.Icon,
            autoSave: storedDefinition.AutoSave,
            storedDefinition.Color,
            storedDefinition.AgentPanelPinned,
            storedDefinition.TerminalMultiplexingOverride,
            storedDefinition.BrowserProfileOverride,
            storedDefinition.HasExplicitAccent,
            storedDefinition.IsIsolated,
            storedDefinition.IsolationMounts,
            storedDefinition.IsolationImageReference,
            storedDefinition.RunAgentInIsolation,
            storedDefinition.NetworkOverride,
            storedDefinition.SortOrder);
        var unchanged = DefinitionPayloadEquals(definition, storedDefinition)
            && layouts.All(item =>
                storedLayouts.TryGetValue(item.Definition.Id.Value, out var existing)
                && DefinitionPayloadEquals(item.Definition, existing.Value));
        return unchanged
            ? null
            : new WorkspaceAutoSaveCapture(definition, storedRevision, layouts);
    }

    private async Task<ScreenPanelDefinition?> ProtectUnavailableDatabaseTargetAsync(
        RuntimePanelViewModel panel, ScreenPanelDefinition captured,
        Dictionary<ConnectionId, ConnectionProfile> capturedRoutes, CancellationToken cancellationToken)
    {
        var target = captured.Startup.Location;
        if (target is null || SafeDatabaseTarget(target) is not null
            || string.Equals(target, DatabaseRecoveryToken.ReconnectTarget, StringComparison.Ordinal))
        {
            return DatabaseRecoveryToken.TryParse(target) is not null ? captured with { ConnectionId = null } : captured;
        }
        var parsed = DatabasePanelTarget.TryParse(target);
        var tunnel = captured.ConnectionId is { } routeId
            ? _catalog.Snapshot.Connections.SingleOrDefault(item => item.Value.Id == routeId)?.Value
            : null;
        if (_secretVault is null || parsed is null || captured.ConnectionId is not null && tunnel is null)
        {
            return CannotProtectDatabaseTarget();
        }
        if (tunnel is not null && !capturedRoutes.TryAdd(tunnel.Id, tunnel) && capturedRoutes[tunnel.Id] != tunnel)
        {
            return CannotProtectDatabaseTarget();
        }
        var recovery = _unavailableDatabaseRecovery.GetValue(panel, _ => new DatabaseRecoveryState(_secretVault));
        try
        {
            if (!await recovery.SaveAsync(new(parsed.DriverId, parsed.ConnectionString, null, tunnel, null), cancellationToken))
            {
                return CannotProtectDatabaseTarget();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return CannotProtectDatabaseTarget(); }
        if (captured.ConnectionId is { } connectionId
            && _catalog.Snapshot.Connections.SingleOrDefault(item => item.Value.Id == connectionId)?.Value != tunnel)
        {
            return CannotProtectDatabaseTarget();
        }
        return captured with
        {
            ConnectionId = null,
            Startup = new PanelStartupBehavior(recovery.Target, captured.Startup.Commands, captured.Startup.DeliveryFailurePolicy),
        };
    }

    private ScreenPanelDefinition? CannotProtectDatabaseTarget()
    {
        _captureError = "The workspace layout was not saved because an unavailable database target could not be protected. Unlock the credential store or restore its connection, then retry. The previous workspace is unchanged.";
        _reportCaptureError?.Invoke(_captureError);
        return null;
    }

    private static bool DefinitionPayloadEquals(
        WorkspaceDefinition left,
        WorkspaceDefinition right) =>
        string.Equals(
            System.Text.Json.JsonSerializer.Serialize(
                left,
                DefinitionBundleJsonContext.Default.WorkspaceDefinition),
            System.Text.Json.JsonSerializer.Serialize(
                right,
                DefinitionBundleJsonContext.Default.WorkspaceDefinition),
            StringComparison.Ordinal);

    private static bool DefinitionPayloadEquals(
        LayoutDefinition left,
        LayoutDefinition right) =>
        string.Equals(
            System.Text.Json.JsonSerializer.Serialize(
                left,
                DefinitionBundleJsonContext.Default.LayoutDefinition),
            System.Text.Json.JsonSerializer.Serialize(
                right,
                DefinitionBundleJsonContext.Default.LayoutDefinition),
            StringComparison.Ordinal);

    /// <summary>
    /// The durable kind a live panel persists as, or null for panels that are
    /// not durable state. Unavailable panels keep their declared kind — their
    /// adapter is missing, not their identity — so autosave does not stall on
    /// them; <see cref="CaptureAutoSavePanel"/> falls back to the stored
    /// definition for the configuration they cannot express.
    /// </summary>
    private static ScreenPanelKind? PanelKindForAutoSave(RuntimePanelViewModel panel) =>
        panel is PanelPlaceholderViewModel
            ? null
            : panel.Kind switch
            {
                PanelKind.Terminal => ScreenPanelKind.Terminal,
                PanelKind.Browser => ScreenPanelKind.Browser,
                PanelKind.FileViewer => ScreenPanelKind.FileViewer,
                PanelKind.Statistics => ScreenPanelKind.Statistics,
                PanelKind.ProcessMonitor => ScreenPanelKind.ProcessMonitor,
                PanelKind.DatabaseViewer => ScreenPanelKind.DatabaseViewer,
                PanelKind.Docker => ScreenPanelKind.Docker,
                PanelKind.Git => ScreenPanelKind.Git,
                _ => null,
            };

    private static ScreenPanelDefinition CaptureAutoSavePanel(
        RuntimePanelViewModel panel,
        LayoutSlotId slotId,
        WorkspaceEntry.Tab? storedTab,
        IReadOnlyList<WorkspaceEntry.Tab> storedTabs,
        HashSet<ScreenPanelId> usedStoredPanels,
        string? protectedUnavailableTarget = null)
    {
        var kind = PanelKindForAutoSave(panel)!.Value;
        ConnectionId? connectionId = panel switch
        {
            TerminalRuntimePanelViewModel terminal => terminal.ConnectionId,
            BrowserRuntimePanelViewModel browser => browser.ConnectionId,
            FileRuntimePanelViewModel file => file.ConnectionId,
            StatisticsRuntimePanelViewModel statistics => statistics.ConnectionId,
            ProcessMonitorRuntimePanelViewModel processes => processes.ConnectionId,
            DatabaseRuntimePanelViewModel or RedisRuntimePanelViewModel or PendingDatabaseRecoveryPanelViewModel => null,
            DockerRuntimePanelViewModel docker => docker.ConnectionId,
            GitRuntimePanelViewModel git => git.ConnectionId,
            UnavailableRuntimePanelViewModel unavailable => unavailable.SourceDefinition?.ConnectionId,
            _ => null,
        };
        var preservesInitialDatabaseBinding = panel switch
        {
            DatabaseRuntimePanelViewModel database => database.CanPreserveInitialSourceConnection,
            RedisRuntimePanelViewModel redis => redis.CanPreserveInitialSourceConnection,
            _ => false,
        };
        bool MatchesConnection(ScreenPanelDefinition candidate) => protectedUnavailableTarget is not null
            && candidate.ConnectionId is null
            && string.Equals(candidate.Startup.Location, protectedUnavailableTarget, StringComparison.Ordinal)
            || (panel is DatabaseRuntimePanelViewModel or RedisRuntimePanelViewModel
            ? preservesInitialDatabaseBinding
                && (candidate.ConnectionId is null || candidate.ConnectionId == panel.SourceDefinition?.ConnectionId)
                && (string.Equals(candidate.Startup.Location, panel.SourceDefinition?.Startup.Location, StringComparison.Ordinal)
                    || string.Equals(candidate.Startup.Location, panel is DatabaseRuntimePanelViewModel database
                        ? database.RecoveryTarget : ((RedisRuntimePanelViewModel)panel).RecoveryTarget, StringComparison.Ordinal))
            : candidate.ConnectionId == connectionId);
        var stored = storedTab?.Panels.FirstOrDefault(candidate =>
            !usedStoredPanels.Contains(candidate.Id)
            && (candidate.SlotId == slotId || candidate.SlotId == panel.SourceDefinition?.SlotId)
            && candidate.Kind == kind
            && MatchesConnection(candidate)
            && (panel.SourceDefinition is null || candidate.Id == panel.SourceDefinition.Id));
        if (stored is null && panel.SourceDefinition is { } source)
        {
            // A renamed tab still resolves the current persisted source, not a
            // stale startup command which the user may since have edited away.
            var candidates = storedTabs.SelectMany(tab => tab.Panels)
                .Where(candidate => candidate.Id == source.Id).ToArray();
            if (candidates.Length == 1 && (candidates[0].SlotId == slotId || candidates[0].SlotId == source.SlotId)
                && candidates[0].Kind == kind && MatchesConnection(candidates[0])
                && !usedStoredPanels.Contains(source.Id))
            {
                stored = candidates[0];
            }
        }
        if (stored is not null)
        {
            usedStoredPanels.Add(stored.Id);
        }

        string? location;
        if (panel is UnavailableRuntimePanelViewModel and not PendingDatabaseRecoveryPanelViewModel)
        {
            // The live panel cannot express its configuration, so the stored
            // definition keeps everything it already knows.
            connectionId ??= stored?.ConnectionId;
            location = stored?.Startup.Location;
        }
        else
        {
            location = panel switch
            {
                TerminalRuntimePanelViewModel terminal => terminal.RecoveryStartupLocation,
                BrowserRuntimePanelViewModel browser => browser.CurrentAddress.ToString(),
                DatabaseRuntimePanelViewModel database =>
                    database.RecoveryTarget ?? SafeDatabaseTarget(stored?.Startup.Location),
                RedisRuntimePanelViewModel redis =>
                    redis.RecoveryTarget ?? SafeDatabaseTarget(stored?.Startup.Location),
                PendingDatabaseRecoveryPanelViewModel pending => pending.Target,
                GitRuntimePanelViewModel { IsRepositoryOpen: true } git =>
                    git.RepositoryRoot,
                _ => stored?.Startup.Location,
            };
        }
        FileProviderProfileId? fileProvider = kind != ScreenPanelKind.FileViewer
            ? null
            : panel is FileRuntimePanelViewModel fileViewer
                && fileViewer.RecoveryProfileId is { } profileId
                ? profileId
                : stored?.FileProviderProfileId;
        // Executable startup belongs to the exact source panel/slot/connection,
        // not another same-kind panel which happens to appear first in a tab.
        return new ScreenPanelDefinition(
            stored?.Id ?? new ScreenPanelId(panel.Id.Value),
            slotId,
            kind,
            panel.Title,
            connectionId,
            new PanelStartupBehavior(
                location,
                stored?.Startup.Commands,
                stored?.Startup.DeliveryFailurePolicy
                    ?? StartupCommandDeliveryFailurePolicy.RetryWhileLive),
            fileProvider);
    }

    /// <summary>
    /// Deletes auto-saved layouts of this workspace that no live tab references
    /// any more — a closed tab leaves its captured layout behind otherwise. Best
    /// effort: a failure here only delays cleanup until the next save.
    /// </summary>
    private async Task CleanUpOrphanedAutoSaveLayoutsAsync(WorkspaceDefinition workspace)
    {
        var prefix = $"{LayoutDefinition.AutoSaveIdPrefix}{workspace.Id.Value}.";
        var referenced = workspace.Entries
            .OfType<WorkspaceEntry.Tab>()
            .Select(tab => tab.LayoutId.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var layout in _catalog.Snapshot.Layouts
            .Where(item => item.Value.Id.Value.StartsWith(prefix, StringComparison.Ordinal)
                && !referenced.Contains(item.Value.Id.Value))
            .ToArray())
        {
            _ = await _catalog.DeleteAsync(
                layout.Value.Key,
                layout.Revision,
                CancellationToken.None);
        }
    }

    private static bool IsLauncherTab(RuntimeTabViewModel tab) =>
        tab.Panels is [PanelPlaceholderViewModel];

    public void Seal()
    {
        if (_sealed)
        {
            return;
        }

        _sealed = true;
        var pending = _debounce;
        _debounce = null;
        pending?.Cancel();
        _lifetime.Cancel();
    }

    private static string? SafeDatabaseTarget(string? target) =>
        target?.StartsWith("saved:", StringComparison.Ordinal) == true
        || DatabaseRecoveryToken.TryParse(target) is not null
            ? target
            : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Seal();
        _lifetime.Dispose();
    }
}

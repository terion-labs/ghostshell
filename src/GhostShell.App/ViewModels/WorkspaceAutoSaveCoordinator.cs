using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns durable write-back of one live runtime workspace. Navigation requests
/// queue or flush operations; capture, revision checks, persistence, and
/// cancellation remain inside this boundary.
/// </summary>
public sealed class WorkspaceAutoSaveCoordinator : IDisposable
{
    private const int WorkspaceAutoSaveDebounceMilliseconds = 1500;

    private readonly IDefinitionCatalog _catalog;
    private readonly Func<RuntimeWorkspaceViewModel?> _runtimeWorkspace;
    private readonly Func<RuntimeHistorySource?> _historySource;
    private readonly Func<bool> _isShutdown;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _debounce;
    private bool _sealed;
    private bool _disposed;

    public WorkspaceAutoSaveCoordinator(
        IDefinitionCatalog catalog,
        Func<RuntimeWorkspaceViewModel?> runtimeWorkspace,
        Func<RuntimeHistorySource?> historySource,
        Func<bool> isShutdown,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtimeWorkspace = runtimeWorkspace
            ?? throw new ArgumentNullException(nameof(runtimeWorkspace));
        _historySource = historySource
            ?? throw new ArgumentNullException(nameof(historySource));
        _isShutdown = isShutdown ?? throw new ArgumentNullException(nameof(isShutdown));
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        if (AutoSaveSourceWorkspace() is not { } stored)
        {
            return;
        }

        WorkspaceAutoSaveCapture? capture;
        try
        {
            capture = CaptureWorkspaceAutoSave(stored.Value, stored.Revision);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or FormatException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "autosave.workspace-capture.failed",
                exception);
            return;
        }

        if (capture is null)
        {
            return;
        }

        var error = await _catalog.SaveWorkspaceWithLayoutsAsync(
            capture.Workspace,
            capture.WorkspaceRevision,
            capture.Layouts,
            CancellationToken.None);
        if (error is null)
        {
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
    private WorkspaceAutoSaveCapture? CaptureWorkspaceAutoSave(
        WorkspaceDefinition storedDefinition,
        long storedRevision)
    {
        if (_runtimeWorkspace() is not { Tabs.Count: > 0 } runtime)
        {
            return null;
        }

        var storedTabs = storedDefinition.Entries.OfType<WorkspaceEntry.Tab>().ToList();
        var storedLayouts = _catalog.Snapshot.Layouts
            .ToDictionary(item => item.Value.Id.Value, StringComparer.Ordinal);
        var usedStoredTabs = new HashSet<WorkspaceEntryId>();
        var layouts = new List<(LayoutDefinition Definition, long? ExpectedRevision)>();
        var entries = new List<WorkspaceEntry>();
        for (var index = 0; index < runtime.Tabs.Count; index++)
        {
            var tab = runtime.Tabs[index];
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
            entries.Add(new WorkspaceEntry.Tab(
                storedTab?.Id ?? WorkspaceEntryId.New(),
                tab.Title,
                layoutId,
                [.. slots
                .Select(slot => CaptureAutoSavePanel(
                    panelsBySlot[slot.Id.Value],
                    slot.Id,
                    storedTab,
                    usedStoredPanels))]));
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
            autoSave: true,
            storedDefinition.Color,
            storedDefinition.AgentPanelPinned,
            storedDefinition.TerminalMultiplexingOverride,
            storedDefinition.BrowserProfileOverride,
            storedDefinition.HasExplicitAccent,
            storedDefinition.IsIsolated,
            storedDefinition.IsolationMounts,
            storedDefinition.IsolationImageReference,
            storedDefinition.RunAgentInIsolation,
            storedDefinition.NetworkOverride);
        var unchanged = DefinitionPayloadEquals(definition, storedDefinition)
            && layouts.All(item =>
                storedLayouts.TryGetValue(item.Definition.Id.Value, out var existing)
                && DefinitionPayloadEquals(item.Definition, existing.Value));
        return unchanged
            ? null
            : new WorkspaceAutoSaveCapture(definition, storedRevision, layouts);
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
        HashSet<ScreenPanelId> usedStoredPanels)
    {
        var kind = PanelKindForAutoSave(panel)!.Value;
        ConnectionId? connectionId = panel switch
        {
            TerminalRuntimePanelViewModel terminal => terminal.ConnectionId,
            BrowserRuntimePanelViewModel browser => browser.ConnectionId,
            FileRuntimePanelViewModel file => file.ConnectionId,
            StatisticsRuntimePanelViewModel statistics => statistics.ConnectionId,
            ProcessMonitorRuntimePanelViewModel processes => processes.ConnectionId,
            DatabaseRuntimePanelViewModel database => database.TunnelConnectionId,
            RedisRuntimePanelViewModel redis => redis.TunnelConnectionId,
            DockerRuntimePanelViewModel docker => docker.ConnectionId,
            GitRuntimePanelViewModel git => git.ConnectionId,
            _ => null,
        };
        var stored = storedTab?.Panels.FirstOrDefault(candidate =>
            !usedStoredPanels.Contains(candidate.Id)
            && candidate.Kind == kind
            && (connectionId is null || candidate.ConnectionId == connectionId));
        if (stored is not null)
        {
            usedStoredPanels.Add(stored.Id);
        }

        string? location;
        if (panel is UnavailableRuntimePanelViewModel)
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
                    database.RecoveryTarget ?? stored?.Startup.Location,
                RedisRuntimePanelViewModel redis =>
                    redis.RecoveryTarget ?? stored?.Startup.Location,
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
        // Startup commands cannot be read back from a live panel, so a matched
        // stored panel keeps the commands the user configured for this tab.
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

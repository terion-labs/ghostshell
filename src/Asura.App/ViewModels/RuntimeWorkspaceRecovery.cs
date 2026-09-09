using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

internal static class RuntimeWorkspaceRecoveryCodec
{
    public const string SnapshotKey = "desktop.main-window";
    public const int SchemaVersion = 10;

    private const int MaximumTabs = WorkspaceInstance.MaximumPanelCount;
    private const int MaximumPanelsPerTab = WorkspaceInstance.MaximumPanelCount;
    private const int MaximumGridDimension = 64;

    public static string Serialize(
        RuntimeWorkspaceViewModel? workspace,
        RuntimeHistorySource? historySource = null)
    {
        var payload = new RuntimeWindowRecoveryPayload(
            workspace is null || workspace.Tabs.Count == 0
                ? null
                : CaptureWorkspace(workspace, historySource));
        if (!TryValidate(payload, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return JsonSerializer.Serialize(
            payload,
            RuntimeWorkspaceRecoveryJsonContext.Default.RuntimeWindowRecoveryPayload);
    }

    public static bool TryDeserialize(
        RuntimeRecoverySnapshot snapshot,
        out RuntimeWindowRecoveryPayload? payload,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        payload = null;
        if (snapshot.SchemaVersion != SchemaVersion)
        {
            error = $"Runtime recovery schema {snapshot.SchemaVersion} is not supported.";
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize(
                snapshot.PayloadJson,
                RuntimeWorkspaceRecoveryJsonContext.Default.RuntimeWindowRecoveryPayload);
        }
        catch (JsonException)
        {
            error = "Runtime recovery data is not valid JSON.";
            return false;
        }
        catch (NotSupportedException)
        {
            error = "Runtime recovery data uses an unsupported shape.";
            return false;
        }

        error = null;
        if (payload is null
            || !TryValidate(payload, out error))
        {
            payload = null;
            error ??= "Runtime recovery data is incomplete.";
            return false;
        }

        return true;
    }

    private static RuntimeWorkspaceRecoveryPayload CaptureWorkspace(
        RuntimeWorkspaceViewModel workspace,
        RuntimeHistorySource? historySource) =>
        new(
            workspace.Name,
            workspace.Accent,
            workspace.ActiveTab?.Id.Value,
            [.. workspace.Connections
                .Select(connection => connection.Id.Value)
                .Distinct(StringComparer.Ordinal)],
            RuntimeAgentPolicyRecoveryPayload.Capture(workspace.AgentPolicy),
            [.. workspace.Tabs.Select(CaptureTab)],
            historySource is null
                ? null
                : RuntimeHistorySourceRecoveryPayload.Capture(historySource),
            workspace.TerminalMultiplexingMode,
            workspace.IsolationBinding is not null,
            workspace.IsolationBinding is { } binding
                ? [.. binding.Mounts.Select(
                    RuntimeWorkspaceIsolationMountRecoveryPayload.Capture)]
                : [],
            workspace.IsolationBinding?.ImageReference);

    private static RuntimeTabRecoveryPayload CaptureTab(RuntimeTabViewModel tab) =>
        new(
            tab.Id.Value,
            tab.Title,
            tab.Source,
            tab.ActivePanelId?.Value,
            tab.ZoomedPanelId?.Value,
            tab.UsesAutomaticLayout,
            tab.SerializeDockLayout(),
            tab.Columns,
            tab.Rows,
            tab.HistorySource is { } historySource
                ? RuntimeHistorySourceRecoveryPayload.Capture(historySource)
                : null,
            RuntimeAgentPolicyRecoveryPayload.Capture(tab.AgentPolicy),
            [.. tab.Panels.Select(CapturePanel)],
            tab.Icon,
            tab.HasChosenTitle,
            tab.HasChosenIcon);

    private static RuntimePanelRecoveryPayload CapturePanel(RuntimePanelViewModel panel)
    {
        var kind = panel switch
        {
            TerminalRuntimePanelViewModel => RuntimePanelRecoveryKind.Terminal,
            BrowserRuntimePanelViewModel => RuntimePanelRecoveryKind.Browser,
            FileRuntimePanelViewModel => RuntimePanelRecoveryKind.FileViewer,
            StatisticsRuntimePanelViewModel => RuntimePanelRecoveryKind.Statistics,
            ProcessMonitorRuntimePanelViewModel => RuntimePanelRecoveryKind.ProcessMonitor,
            DatabaseRuntimePanelViewModel or RedisRuntimePanelViewModel or PendingDatabaseRecoveryPanelViewModel =>
                RuntimePanelRecoveryKind.DatabaseViewer,
            DockerRuntimePanelViewModel => RuntimePanelRecoveryKind.Docker,
            GitRuntimePanelViewModel => RuntimePanelRecoveryKind.Git,
            PanelPlaceholderViewModel => RuntimePanelRecoveryKind.Placeholder,
            _ => RuntimePanelRecoveryKind.Unavailable,
        };
        var terminal = panel as TerminalRuntimePanelViewModel;
        var browser = panel as BrowserRuntimePanelViewModel;
        var database = panel as DatabaseRuntimePanelViewModel;
        var redis = panel as RedisRuntimePanelViewModel;
        var pendingDatabase = panel as PendingDatabaseRecoveryPanelViewModel;
        var file = panel as FileRuntimePanelViewModel;
        var statistics = panel as StatisticsRuntimePanelViewModel;
        var processes = panel as ProcessMonitorRuntimePanelViewModel;
        var docker = panel as DockerRuntimePanelViewModel;
        var git = panel as GitRuntimePanelViewModel;
        return new RuntimePanelRecoveryPayload(
            panel.Id.Value,
            kind,
            panel.Title,
            kind == RuntimePanelRecoveryKind.Unavailable ? panel.KindLabel : null,
            terminal?.ConnectionId.Value
                ?? browser?.ConnectionId.Value
                ?? file?.ConnectionId.Value
                ?? statistics?.ConnectionId.Value
                ?? processes?.ConnectionId.Value
                ?? docker?.ConnectionId.Value
                ?? git?.ConnectionId.Value,
            terminal?.RecoveryStartupLocation
                ?? browser?.CurrentAddress.ToString()
                ?? database?.RecoveryTarget
                ?? redis?.RecoveryTarget
                ?? pendingDatabase?.Target
                ?? (git is { IsRepositoryOpen: true } ? git.RepositoryRoot : null),
            file?.SelectedProfile?.Id ?? file?.CurrentLocation?.ProviderProfileId,
            file?.CurrentLocation is { } location
                ? RuntimeFileLocationRecoveryPayload.Capture(location)
                : null,
            file?.ShowHidden ?? false,
            file?.Filter,
            panel.LayoutColumn,
            panel.LayoutRow,
            panel.LayoutColumnSpan,
            panel.LayoutRowSpan,
            panel.LayoutMinimumWidth,
            panel.LayoutMinimumHeight,
            terminal?.MultiplexerSession is { } multiplexer
                ? new RuntimeTerminalMultiplexerRecoveryPayload(
                    multiplexer.Mode,
                    multiplexer.SessionName,
                    multiplexer.IsEstablished)
                : null,
            browser?.ProfileBinding.Definition.Id.Value,
            browser?.ProfileBinding.Selection.Partition.Kind,
            browser?.ProfileBinding.Selection.Partition.Identity);
    }

    private static bool TryValidate(
        RuntimeWindowRecoveryPayload payload,
        out string? error)
    {
        if (payload.Workspace is not { } workspace)
        {
            error = null;
            return true;
        }

        if (!IsDisplayText(workspace.Name, 256)
            || !IsDisplayText(workspace.Accent, 64)
            || workspace.ConnectionIds is null
            || workspace.ConnectionIds.Length > 512
            || workspace.ConnectionIds.Any(id => !IsIdentifier(id))
            || workspace.ConnectionIds.Distinct(StringComparer.Ordinal).Count()
                != workspace.ConnectionIds.Length
            || workspace.TerminalMultiplexingMode is { } terminalMultiplexingMode
                && !Enum.IsDefined(terminalMultiplexingMode)
            || !TryValidateIsolationMounts(
                workspace.IsIsolated,
                workspace.IsolationMounts)
            || workspace.IsolationImageReference is { } imageReference
                && (imageReference.Length > WorkspaceDefinition.MaximumIsolationImageReferenceLength
                    || imageReference.Any(char.IsWhiteSpace)
                    || imageReference.Any(char.IsControl))
            || workspace.AgentPolicy is { } workspacePolicy
                && (!workspacePolicy.TryValidate()
                    || workspacePolicy.Sources.Any(source => string.Equals(source.Kind, ScreenDefinition.Kind.Value, StringComparison.Ordinal)))
            || workspace.Tabs is null
            || workspace.Tabs.Length is 0 or > MaximumTabs)
        {
            error = "Runtime workspace recovery metadata is invalid.";
            return false;
        }

        error = null;
        var tabKeys = new HashSet<string>(StringComparer.Ordinal);
        var panelCount = 0;
        foreach (var tab in workspace.Tabs)
        {
            if (tab is null
                || !tabKeys.Add(tab.Key)
                || !TryValidate(
                    tab,
                    workspace.AgentPolicy,
                    out error))
            {
                error ??= "Runtime tab recovery metadata is invalid.";
                return false;
            }

            panelCount += tab.Panels!.Length;
            if (panelCount > WorkspaceInstance.MaximumPanelCount)
            {
                error = $"A recovered workspace cannot contain more than {WorkspaceInstance.MaximumPanelCount} panels.";
                return false;
            }
        }

        if (workspace.ActiveTabKey is not null && !tabKeys.Contains(workspace.ActiveTabKey))
        {
            error = "The recovered active tab does not exist.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateIsolationMounts(
        bool isIsolated,
        RuntimeWorkspaceIsolationMountRecoveryPayload[]? mounts)
    {
        if (mounts is null)
        {
            return !isIsolated;
        }

        if (mounts.Length > WorkspaceDefinition.MaximumIsolationMountCount
            || !isIsolated && mounts.Length > 0
            || mounts.Any(mount => mount is null))
        {
            return false;
        }

        try
        {
            _ = new WorkspaceIsolationPrepareRequest(
                new WorkspaceId("runtime-recovery-validation"),
                [.. mounts.Select(mount => mount.ToMount())]);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryValidate(
        RuntimeTabRecoveryPayload tab,
        RuntimeAgentPolicyRecoveryPayload? workspacePolicy,
        out string? error)
    {
        if (!IsIdentifier(tab.Key)
            || !IsDisplayText(tab.Title, 256)
            || !IsDisplayText(tab.Source, 128)
            || tab.Icon is { } icon && !IsDisplayText(icon, 64)
            || tab.HistorySource is { } historySource
                && !TryValidate(historySource)
            || tab.AgentPolicy is { } tabPolicy
                && !tabPolicy.TryValidate()
            || string.IsNullOrWhiteSpace(tab.DockLayoutJson)
            || tab.DockLayoutJson.Length > 4 * 1024 * 1024
            || tab.DockLayoutJson.Contains('\0')
            || tab.Columns is < 1 or > MaximumGridDimension
            || tab.Rows is < 1 or > MaximumGridDimension
            || tab.Panels is null
            || tab.Panels.Length is 0 or > MaximumPanelsPerTab)
        {
            error = "Runtime tab recovery metadata is invalid.";
            return false;
        }

        var screenPolicySource = tab.AgentPolicy?.Sources
            .SingleOrDefault(source => string.Equals(source.Kind, ScreenDefinition.Kind.Value, StringComparison.Ordinal));
        var workspacePolicySource = workspacePolicy?.Sources
            .SingleOrDefault(source => string.Equals(source.Kind, WorkspaceDefinition.Kind.Value, StringComparison.Ordinal));
        var tabWorkspacePolicySource = tab.AgentPolicy?.Sources
            .SingleOrDefault(source => string.Equals(source.Kind, WorkspaceDefinition.Kind.Value, StringComparison.Ordinal));
        if (workspacePolicy is not null && tab.AgentPolicy is null)
        {
            error = "Recovered tab policy is missing its workspace policy lineage.";
            return false;
        }

        if (screenPolicySource is not null
            && (tab.HistorySource is null
                || !string.Equals(tab.HistorySource.SourceKind, screenPolicySource.Kind
, StringComparison.Ordinal) || !string.Equals(tab.HistorySource.SourceValue, screenPolicySource.Value, StringComparison.Ordinal)))
        {
            error = "Recovered screen policy provenance does not match its history source.";
            return false;
        }

        if (string.Equals(tab.HistorySource?.SourceKind, ScreenDefinition.Kind.Value
, StringComparison.Ordinal) && screenPolicySource is null
            && tab.AgentPolicy is not null)
        {
            error = "Recovered saved-screen tab is missing its policy provenance.";
            return false;
        }

        if (workspacePolicy is not null
            && tab.AgentPolicy is not null
            && (screenPolicySource is null
                && tab.AgentPolicy.HasPolicyOverride != workspacePolicy.HasPolicyOverride
                || tabWorkspacePolicySource != workspacePolicySource
                || screenPolicySource is null
                && !tab.AgentPolicy.HasSamePolicyAs(workspacePolicy)))
        {
            error = "Recovered tab policy does not match its workspace policy lineage.";
            return false;
        }

        error = null;
        var panelKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in tab.Panels)
        {
            if (panel is null
                || !panelKeys.Add(panel.Key)
                || !TryValidate(panel, tab.Columns, tab.Rows, out error))
            {
                error ??= "Runtime panel recovery metadata is invalid.";
                return false;
            }
        }

        if (tab.ActivePanelKey is not null && !panelKeys.Contains(tab.ActivePanelKey))
        {
            error = "The recovered active panel does not exist.";
            return false;
        }

        if (tab.ZoomedPanelKey is not null
            && (!panelKeys.Contains(tab.ZoomedPanelKey)
                || !string.Equals(tab.ZoomedPanelKey, tab.ActivePanelKey, StringComparison.Ordinal)))
        {
            error = "The recovered zoomed panel does not match the active panel.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidate(RuntimeHistorySourceRecoveryPayload source) =>
        IsIdentifier(source.SourceKind)
        && IsIdentifier(source.SourceValue)
        && IsDisplayText(source.DurableTitle, 256);

    private static bool TryValidate(
        RuntimePanelRecoveryPayload panel,
        int columns,
        int rows,
        out string? error)
    {
        var validKindData = panel.Kind switch
        {
            RuntimePanelRecoveryKind.Terminal =>
                IsIdentifier(panel.ConnectionId)
                && IsOptionalText(panel.StartupLocation, 4_096)
                && panel.FileLocation is null
                && (panel.Multiplexer is null
                    || panel.Multiplexer.Mode == TerminalMultiplexingMode.Automatic
                    && TerminalMultiplexerSession.IsValidSessionName(
                        panel.Multiplexer.SessionName)),
            RuntimePanelRecoveryKind.FileViewer =>
                panel.Multiplexer is null
                && IsOptionalIdentifier(panel.FileProviderProfileId)
                && IsOptionalIdentifier(panel.ConnectionId)
                && (panel.FileLocation is null
                    || panel.FileLocation.TryValidate(panel.FileProviderProfileId, out _))
                && IsOptionalText(panel.Filter, 1_024),
            RuntimePanelRecoveryKind.Browser =>
                panel.Multiplexer is null
                && IsOptionalIdentifier(panel.ConnectionId)
                && panel.StartupLocation is { } browserAddress
                && BrowserAddress.TryParse(browserAddress, out _)
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null
                && IsIdentifier(panel.BrowserProfileId)
                && IsBrowserProfilePartition(
                    panel.BrowserProfileKind,
                    panel.BrowserProfileIdentity),
            RuntimePanelRecoveryKind.Unavailable =>
                panel.Multiplexer is null
                && IsDisplayText(panel.KindLabel, 128)
                && panel.ConnectionId is null
                && panel.FileLocation is null,
            RuntimePanelRecoveryKind.Placeholder =>
                panel.Multiplexer is null
                && panel.KindLabel is null
                && panel.ConnectionId is null
                && panel.StartupLocation is null
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null,
            RuntimePanelRecoveryKind.Statistics or RuntimePanelRecoveryKind.ProcessMonitor =>
                panel.Multiplexer is null
                && panel.KindLabel is null
                && IsOptionalIdentifier(panel.ConnectionId)
                && panel.StartupLocation is null
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null,
            RuntimePanelRecoveryKind.DatabaseViewer =>
                panel.Multiplexer is null
                && panel.KindLabel is null
                && IsOptionalIdentifier(panel.ConnectionId)
                && IsOptionalText(panel.StartupLocation, 4_096)
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null,
            RuntimePanelRecoveryKind.Docker =>
                panel.Multiplexer is null
                && panel.KindLabel is null
                && IsIdentifier(panel.ConnectionId)
                && panel.StartupLocation is null
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null,
            RuntimePanelRecoveryKind.Git =>
                panel.Multiplexer is null
                && panel.KindLabel is null
                && IsIdentifier(panel.ConnectionId)
                && IsOptionalText(panel.StartupLocation, 4_096)
                && panel.FileProviderProfileId is null
                && panel.FileLocation is null
                && !panel.ShowHidden
                && panel.Filter is null,
            _ => false,
        };
        if (!IsIdentifier(panel.Key)
            || !IsDisplayText(panel.Title, 256)
            || !validKindData
            || panel.Kind != RuntimePanelRecoveryKind.Browser
                && (panel.BrowserProfileId is not null
                    || panel.BrowserProfileKind is not null
                    || panel.BrowserProfileIdentity is not null)
            || panel.Column < 0
            || panel.Row < 0
            || panel.ColumnSpan <= 0
            || panel.RowSpan <= 0
            || panel.Column + panel.ColumnSpan > columns
            || panel.Row + panel.RowSpan > rows
            || panel.MinimumWidth is < 1 or > 100_000
            || panel.MinimumHeight is < 1 or > 100_000)
        {
            error = "Runtime panel recovery metadata is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);

    private static bool IsOptionalIdentifier(string? value) =>
        value is null || IsIdentifier(value);

    private static bool IsDisplayText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Contains('\0');

    private static bool IsOptionalText(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength && !value.Contains('\0');

    private static bool IsBrowserProfilePartition(
        BrowserProfileKind? kind,
        string? identity)
    {
        if (kind is null || !Enum.IsDefined(kind.Value) || identity is null)
        {
            return false;
        }

        try
        {
            _ = new BrowserProfileKey(kind.Value, identity);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

internal sealed record RuntimeWindowRecoveryPayload(RuntimeWorkspaceRecoveryPayload? Workspace);

/// <summary>
/// A restored workspace, and — since a restart must not sever it — which
/// definition it was opened from.
///
/// <see cref="HistorySource"/> is optional and last so a payload written before
/// it existed still reads. Without it a restored workspace was anonymous: the
/// shell could not tell that the tile you clicked was the workspace already
/// running, so it built a second one and the first workspace's terminals were
/// replaced by fresh shells.
/// </summary>
internal sealed record RuntimeWorkspaceRecoveryPayload(
    string Name,
    string Accent,
    string? ActiveTabKey,
    string[] ConnectionIds,
    RuntimeAgentPolicyRecoveryPayload? AgentPolicy,
    RuntimeTabRecoveryPayload[] Tabs,
    RuntimeHistorySourceRecoveryPayload? HistorySource = null,
    // Despite the historical property name, this stores only a concrete
    // workspace override. Null follows the current application preference.
    TerminalMultiplexingMode? TerminalMultiplexingMode = null,
    // Optional-at-the-wire through the default: legacy snapshots restore as
    // host workspaces, while current isolated snapshots cannot silently do so.
    bool IsIsolated = false,
    RuntimeWorkspaceIsolationMountRecoveryPayload[]? IsolationMounts = null,
    string? IsolationImageReference = null);

internal sealed record RuntimeWorkspaceIsolationMountRecoveryPayload(
    string HostSource,
    string GuestDestination,
    bool IsReadOnly)
{
    public static RuntimeWorkspaceIsolationMountRecoveryPayload Capture(
        WorkspaceIsolationMount mount) =>
        new(mount.HostSource, mount.GuestDestination, mount.IsReadOnly);

    public WorkspaceIsolationMount ToMount() =>
        new(HostSource, GuestDestination, IsReadOnly);
}

internal sealed record RuntimeTabRecoveryPayload(
    string Key,
    string Title,
    string Source,
    string? ActivePanelKey,
    string? ZoomedPanelKey,
    bool UsesAutomaticLayout,
    string DockLayoutJson,
    int Columns,
    int Rows,
    RuntimeHistorySourceRecoveryPayload? HistorySource,
    RuntimeAgentPolicyRecoveryPayload? AgentPolicy,
    RuntimePanelRecoveryPayload[] Panels,
    string? Icon = null,
    bool? HasChosenTitle = null,
    bool? HasChosenIcon = null);

internal sealed record RuntimeAgentPolicyRecoveryPayload(
    string Provider,
    string Model,
    Dictionary<AgentCapability, AgentPermission> Permissions,
    RuntimeAgentPolicySourceRecoveryPayload[] Sources,
    bool HasPolicyOverride,
    AgentModelSelection CompactionModel,
    AgentModelSelection TitleModel,
    string? SystemPrompt = null)
{
    public static RuntimeAgentPolicyRecoveryPayload? Capture(
        RuntimeAgentPolicyProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (provenance.EffectivePolicy is not { } policy)
        {
            return null;
        }

        return new(
            policy.Provider,
            policy.Model,
            policy.Permissions.ToDictionary(
                item => item.Key,
                item => item.Value),
            [.. provenance.Sources.Select(RuntimeAgentPolicySourceRecoveryPayload.Capture)],
            provenance.HasPolicyOverride,
            policy.CompactionModel,
            policy.TitleModel,
            policy.SystemPrompt);
    }

    public bool TryValidate()
    {
        if (Permissions is null
            || Sources is null
            || Sources.Length > 2
            || Sources.Any(source => source is null || !source.TryValidate())
            || Sources
                .GroupBy(source => source.Kind, StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
        {
            return false;
        }

        var policy = new AgentPolicy(
            Provider,
            Model,
            Permissions.ToImmutableDictionary())
        {
            CompactionModel = CompactionModel,
            TitleModel = TitleModel,
            SystemPrompt = SystemPrompt,
        };
        return policy.IsValidForDurableStorage()
            && (!HasPolicyOverride || Sources.Length > 0)
            && Permissions.Keys.ToHashSet().SetEquals(AgentPolicy.Capabilities);
    }

    public RuntimeAgentPolicyProvenance ToProvenance()
    {
        if (!TryValidate())
        {
            throw new InvalidOperationException(
                "Recovered agent-policy provenance is invalid.");
        }

        return new(
            new AgentPolicy(
                Provider,
                Model,
                Permissions.ToImmutableDictionary())
            {
                CompactionModel = CompactionModel,
                TitleModel = TitleModel,
                SystemPrompt = SystemPrompt,
            },
            Sources.Select(source => source.ToSource()),
            HasPolicyOverride);
    }

    public bool HasSamePolicyAs(RuntimeAgentPolicyRecoveryPayload other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Provider, other.Provider, StringComparison.Ordinal)
            && string.Equals(Model, other.Model, StringComparison.Ordinal)
            && CompactionModel == other.CompactionModel
            && TitleModel == other.TitleModel
            && string.Equals(SystemPrompt, other.SystemPrompt, StringComparison.Ordinal)
            && Permissions.Count == other.Permissions.Count
            && Permissions.All(item =>
                other.Permissions.TryGetValue(item.Key, out var permission)
                && item.Value == permission);
    }

}

internal sealed record RuntimeAgentPolicySourceRecoveryPayload(
    string Kind,
    string Value,
    long Revision)
{
    public static RuntimeAgentPolicySourceRecoveryPayload Capture(
        RuntimeAgentPolicyProvenance.Source source) =>
        new(source.Definition.Kind.Value, source.Definition.Value, source.Revision);

    public bool TryValidate() =>
        IsIdentifier(Kind)
        && IsIdentifier(Value)
        && Revision > 0
        && Kind is "screen" or "workspace";

    public RuntimeAgentPolicyProvenance.Source ToSource() =>
        new(
            new DefinitionKey(new DefinitionKind(Kind), Value),
            Revision);

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);
}

internal sealed record RuntimeHistorySourceRecoveryPayload(
    string SourceKind,
    string SourceValue,
    string DurableTitle)
{
    public static RuntimeHistorySourceRecoveryPayload Capture(RuntimeHistorySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(
            source.SourceDefinition.Kind.Value,
            source.SourceDefinition.Value,
            source.DurableTitle);
    }

    public RuntimeHistorySource ToHistorySource() =>
        new(
            new DefinitionKey(new DefinitionKind(SourceKind), SourceValue),
            DurableTitle);
}

internal enum RuntimePanelRecoveryKind
{
    Terminal = 0,
    FileViewer = 1,
    Unavailable = 2,
    Statistics = 3,
    ProcessMonitor = 4,
    Browser = 5,
    Placeholder = 6,
    DatabaseViewer = 7,
    Docker = 8,
    Git = 9,
}

internal sealed record RuntimePanelRecoveryPayload(
    string Key,
    RuntimePanelRecoveryKind Kind,
    string Title,
    string? KindLabel,
    string? ConnectionId,
    string? StartupLocation,
    string? FileProviderProfileId,
    RuntimeFileLocationRecoveryPayload? FileLocation,
    bool ShowHidden,
    string? Filter,
    int Column,
    int Row,
    int ColumnSpan,
    int RowSpan,
    double MinimumWidth,
    double MinimumHeight,
    RuntimeTerminalMultiplexerRecoveryPayload? Multiplexer = null,
    string? BrowserProfileId = null,
    BrowserProfileKind? BrowserProfileKind = null,
    string? BrowserProfileIdentity = null);

internal sealed record RuntimeTerminalMultiplexerRecoveryPayload(
    TerminalMultiplexingMode Mode,
    string SessionName,
    bool IsEstablished)
{
    public TerminalMultiplexerSession ToSession() =>
        new(Mode, SessionName, IsEstablished);
}

internal enum RuntimeFileAddressRecoveryKind
{
    Hierarchical,
    ObjectKey,
    ContainerRoot,
}

internal sealed record RuntimeFileLocationRecoveryPayload(
    string ProviderProfileId,
    string? Authority,
    RuntimeFileAddressRecoveryKind AddressKind,
    string[]? PathSegments,
    string? ObjectKey)
{
    public static RuntimeFileLocationRecoveryPayload Capture(FilePanelLocation location) =>
        location.Address switch
        {
            FilePanelAddress.Hierarchical hierarchical => new(
                location.ProviderProfileId,
                location.Authority,
                RuntimeFileAddressRecoveryKind.Hierarchical,
                [.. hierarchical.Path.Segments.Select(segment => segment.Value)],
                null),
            FilePanelAddress.ObjectKey objectKey => new(
                location.ProviderProfileId,
                location.Authority,
                RuntimeFileAddressRecoveryKind.ObjectKey,
                null,
                objectKey.Key),
            FilePanelAddress.ContainerRoot => new(
                location.ProviderProfileId,
                location.Authority,
                RuntimeFileAddressRecoveryKind.ContainerRoot,
                null,
                null),
            _ => throw new InvalidOperationException("The file location address is not supported."),
        };

    public FilePanelLocation ToLocation()
    {
        FilePanelAddress address = AddressKind switch
        {
            RuntimeFileAddressRecoveryKind.Hierarchical => new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments(
                    (PathSegments ?? []).Select(segment => new FilePanelPathSegment(segment)))),
            RuntimeFileAddressRecoveryKind.ObjectKey => new FilePanelAddress.ObjectKey(ObjectKey!),
            RuntimeFileAddressRecoveryKind.ContainerRoot => new FilePanelAddress.ContainerRoot(),
            _ => throw new InvalidOperationException("The recovered file address is not supported."),
        };
        return new FilePanelLocation(ProviderProfileId, Authority, address);
    }

    public bool TryValidate(string? expectedProfileId, out string? error)
    {
        if (!IsBounded(ProviderProfileId, 128)
            || expectedProfileId is not null && !string.Equals(ProviderProfileId, expectedProfileId
, StringComparison.Ordinal) || Authority is { } authority
                && (!IsBounded(authority, 255)
                    || authority.Any(character => character is '/' or '\\')))
        {
            error = "The recovered file-provider identity is invalid.";
            return false;
        }

        var validAddress = AddressKind switch
        {
            RuntimeFileAddressRecoveryKind.Hierarchical =>
                ObjectKey is null
                && PathSegments is { Length: <= 1_024 }
                && PathSegments.All(segment =>
                    IsBounded(segment, 255)
                    && segment is not "." and not ".."
                    && !segment.Contains('/', StringComparison.Ordinal)),
            RuntimeFileAddressRecoveryKind.ObjectKey =>
                PathSegments is null && IsBounded(ObjectKey, 4_096),
            RuntimeFileAddressRecoveryKind.ContainerRoot =>
                PathSegments is null && ObjectKey is null,
            _ => false,
        };
        error = validAddress ? null : "The recovered file location is invalid.";
        return validAddress;
    }

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= maximumLength
        && !value.Contains('\0');
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RuntimeWindowRecoveryPayload))]
internal sealed partial class RuntimeWorkspaceRecoveryJsonContext : JsonSerializerContext;

using System.Collections.Immutable;

namespace Asura.Core;

public enum AgentCapability
{
    /// <summary>
    /// Bounded terminal screen, cursor, mode, and shell-integration reads.
    /// </summary>
    TerminalRead,

    /// <summary>
    /// Exact text, key, paste, mouse, and interrupt input to a terminal.
    /// </summary>
    RunCommands,

    /// <summary>
    /// File creation, replacement, rename, move, and deletion.
    /// </summary>
    EditFiles,

    ReadFiles,
    Search,

    /// <summary>
    /// Git operations that change a worktree, index, refs, or remotes.
    /// </summary>
    Git,

    WebFetch,

    /// <summary>
    /// Docker engine, container lifecycle, and exec control.
    /// </summary>
    Docker,

    DestructiveTerminalActions,
    BrowserNavigation,
    BrowserData,

    /// <summary>
    /// Process mutations such as signaling or priority changes.
    /// </summary>
    ProcessControl,
    McpTools,
    SecretUse,
    BrowserInteraction,

    /// <summary>
    /// JavaScript evaluation in an exact browser document.
    /// </summary>
    BrowserScripting,

    /// <summary>
    /// Browser console, network, and DevTools diagnostics.
    /// </summary>
    BrowserDiagnostics,

    /// <summary>
    /// Bounded relational database and Redis observations.
    /// </summary>
    DatabaseRead,

    /// <summary>
    /// Relational database and Redis mutations.
    /// </summary>
    DatabaseWrite,

    /// <summary>
    /// Docker observations distinct from lifecycle control.
    /// </summary>
    DockerData,

    /// <summary>
    /// Aggregate local system statistics.
    /// </summary>
    SystemData,

    /// <summary>
    /// Bounded local process observations.
    /// </summary>
    ProcessData,

    /// <summary>
    /// Browser and cross-panel artifact transfers.
    /// </summary>
    ArtifactTransfer,

    /// <summary>
    /// Creating, splitting, and closing tabs and panels in the bound workspace.
    /// </summary>
    WorkspaceLayout,

    /// <summary>
    /// Bounded Git repository observations distinct from index, ref, and remote
    /// mutation authority.
    /// </summary>
    GitData,
}

public enum AgentPermission
{
    Off,
    Ask,
    Auto,
    Yolo,
}

public sealed record AgentModelSelection(string Provider, string Model)
{
    public bool IsStructurallyValid() =>
        AgentPolicy.IsValidProvider(Provider)
        && AgentPolicy.IsValidModel(Model);
}

public sealed record AgentPolicy(
    string Provider,
    string Model,
    ImmutableDictionary<AgentCapability, AgentPermission> Permissions)
{
    public const int MaximumSystemPromptLength = 8_000;
    public const int MaximumProviderLength = 256;
    public const int MaximumModelLength = 256;

    public static ImmutableArray<AgentCapability> Capabilities { get; } =
        [.. Enum.GetValues<AgentCapability>()];

    public static ImmutableDictionary<AgentCapability, AgentPermission> InitialPermissions
    { get; } =
        new Dictionary<AgentCapability, AgentPermission>
        {
            [AgentCapability.TerminalRead] = AgentPermission.Auto,
            [AgentCapability.RunCommands] = AgentPermission.Ask,
            [AgentCapability.EditFiles] = AgentPermission.Ask,
            [AgentCapability.ReadFiles] = AgentPermission.Auto,
            [AgentCapability.Search] = AgentPermission.Auto,
            [AgentCapability.Git] = AgentPermission.Ask,
            [AgentCapability.WebFetch] = AgentPermission.Ask,
            [AgentCapability.Docker] = AgentPermission.Off,
            [AgentCapability.DestructiveTerminalActions] = AgentPermission.Ask,
            [AgentCapability.BrowserNavigation] = AgentPermission.Ask,
            [AgentCapability.BrowserData] = AgentPermission.Ask,
            [AgentCapability.BrowserInteraction] = AgentPermission.Ask,
            [AgentCapability.ProcessControl] = AgentPermission.Off,
            [AgentCapability.McpTools] = AgentPermission.Off,
            [AgentCapability.SecretUse] = AgentPermission.Ask,
            [AgentCapability.BrowserScripting] = AgentPermission.Off,
            [AgentCapability.BrowserDiagnostics] = AgentPermission.Off,
            [AgentCapability.DatabaseRead] = AgentPermission.Off,
            [AgentCapability.DatabaseWrite] = AgentPermission.Off,
            [AgentCapability.DockerData] = AgentPermission.Off,
            [AgentCapability.SystemData] = AgentPermission.Off,
            [AgentCapability.ProcessData] = AgentPermission.Off,
            [AgentCapability.ArtifactTransfer] = AgentPermission.Off,
            [AgentCapability.WorkspaceLayout] = AgentPermission.Ask,
            [AgentCapability.GitData] = AgentPermission.Off,
        }.ToImmutableDictionary();

    public static AgentPolicy Default { get; } = new(
        "Anthropic",
        "claude-opus-4.8",
        InitialPermissions)
    {
        CompactionModel = new AgentModelSelection(
            "Anthropic",
            "claude-opus-4.8"),
        TitleModel = new AgentModelSelection(
            "Anthropic",
            "claude-opus-4.8"),
    };

    /// <summary>
    /// Explicit model route used to summarize conversations before the context
    /// limit is reached.
    /// </summary>
    public required AgentModelSelection CompactionModel { get; init; }

    /// <summary>
    /// Explicit model route used to generate conversation titles.
    /// </summary>
    public required AgentModelSelection TitleModel { get; init; }

    /// <summary>
    /// Optional user-authored instructions appended to the invariant runtime
    /// safety prompt. Null inherits the next broader policy layer.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Selects the primary conversation route. Maintenance routes are independent
    /// configuration and are never changed implicitly.
    /// </summary>
    public AgentPolicy SelectPrimaryModel(string provider, string model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        if (!IsValidProvider(provider))
        {
            throw new ArgumentException(
                "A valid provider identity is required.",
                nameof(provider));
        }

        if (!IsValidModel(model))
        {
            throw new ArgumentException(
                "A valid model identity is required.",
                nameof(model));
        }

        var nextPrimary = new AgentModelSelection(provider.Trim(), model.Trim());
        return this with
        {
            Provider = nextPrimary.Provider,
            Model = nextPrimary.Model,
        };
    }

    public string EffectiveSummary =>
        $"Commands: {Format(GetPermission(AgentCapability.RunCommands))} · " +
        $"Files: {Format(GetPermission(AgentCapability.EditFiles))} · " +
        $"Git: {Format(GetPermission(AgentCapability.Git))} · " +
        $"Docker: {Format(GetPermission(AgentCapability.Docker))}";

    /// <summary>
    /// Returns a fail-closed permission for execution.
    /// </summary>
    public AgentPermission GetPermission(AgentCapability capability)
    {
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        return Permissions is not null
            && Permissions.TryGetValue(capability, out var permission)
            && Enum.IsDefined(permission)
                ? permission
                : AgentPermission.Off;
    }

    public bool IsStructurallyValid()
    {
        if (!IsValidProvider(Provider)
            || !IsValidModel(Model)
            || CompactionModel is null
            || !CompactionModel.IsStructurallyValid()
            || TitleModel is null
            || !TitleModel.IsStructurallyValid()
            || SystemPrompt is not null && !IsValidSystemPrompt(SystemPrompt)
            || Permissions is null
            || Permissions.Keys.Any(capability => !Enum.IsDefined(capability))
            || Permissions.Values.Any(permission => !Enum.IsDefined(permission)))
        {
            return false;
        }

        return Permissions.Keys.ToImmutableHashSet().SetEquals(Capabilities);
    }

    /// <summary>
    /// Durable policies are baseline configuration. YOLO is granted only as a
    /// explicitly selected, scoped run-local overlay that ends with the run.
    /// </summary>
    public bool IsValidForDurableStorage() =>
        IsStructurallyValid()
        && Permissions.Values.All(permission => permission != AgentPermission.Yolo);

    public static bool IsValidProvider(string? value) =>
        IsValidIdentityValue(value, MaximumProviderLength);

    public static bool IsValidModel(string? value) =>
        IsValidIdentityValue(value, MaximumModelLength);

    public static bool IsValidSystemPrompt(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumSystemPromptLength
        && value.All(character =>
            !char.IsControl(character)
            || character is '\r' or '\n' or '\t');

    private static string Format(AgentPermission permission) =>
        permission == AgentPermission.Yolo ? "YOLO" : permission.ToString();

    private static bool IsValidIdentityValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);
}

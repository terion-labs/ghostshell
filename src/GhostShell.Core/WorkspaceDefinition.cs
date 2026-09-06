using System.Text.Json.Serialization;

namespace GhostShell.Core;

public sealed record WorkspaceDefinition : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultIcon = "workspace";
    public const int MaximumIsolationMountCount = 32;
    public const int MaximumIsolationImageReferenceLength = 512;

    /// <summary>
    /// The one workspace that always exists. It is seeded on start when absent
    /// and the catalog refuses to delete it.
    /// </summary>
    public const string DefaultWorkspaceId = "default";

    /// <summary>
    /// What that workspace is called. "Main" rather than "Default", because
    /// the shell names it to the person using it, not to the code that seeds
    /// it — it is where you are when you have not chosen to be anywhere else.
    /// </summary>
    public const string DefaultWorkspaceName = "Main";

    /// <summary>
    /// The name it was seeded with before. A profile still carrying it is
    /// carrying a seed value, not a choice, so renaming it is safe; a
    /// workspace the user has named anything else is left alone.
    /// </summary>
    public const string LegacyDefaultWorkspaceName = "Default";

    [JsonConstructor]
    public WorkspaceDefinition(
        WorkspaceId id,
        int schemaVersion,
        string name,
        string? description,
        string? accent,
        IReadOnlyList<WorkspaceEntry> entries,
        AgentPolicy? agentPolicyOverride = null,
        string? icon = null,
        bool autoSave = false,
        string? color = null,
        bool agentPanelPinned = false,
        TerminalMultiplexingMode? terminalMultiplexingOverride = null,
        WorkspaceBrowserProfileMode? browserProfileOverride = null,
        bool hasExplicitAccent = false,
        bool isIsolated = false,
        IReadOnlyList<WorkspaceIsolationMountDefinition>? isolationMounts = null,
        string? isolationImageReference = null,
        bool runAgentInIsolation = false,
        NetworkPolicy? networkOverride = null)
    {
        Id = id;
        SchemaVersion = schemaVersion;
        Name = name;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Accent = string.IsNullOrWhiteSpace(accent) ? null : accent.Trim();
        Entries = Array.AsReadOnly(entries?.ToArray() ?? throw new ArgumentNullException(nameof(entries)));
        AgentPolicyOverride = agentPolicyOverride;
        Icon = string.IsNullOrWhiteSpace(icon) ? DefaultIcon : icon.Trim();
        AutoSave = autoSave;
        Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        AgentPanelPinned = agentPanelPinned;
        if (terminalMultiplexingOverride is not null
            && !Enum.IsDefined(terminalMultiplexingOverride.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalMultiplexingOverride));
        }

        TerminalMultiplexingOverride = terminalMultiplexingOverride;
        if (browserProfileOverride is not null
            && !Enum.IsDefined(browserProfileOverride.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(browserProfileOverride));
        }

        BrowserProfileOverride = browserProfileOverride;
        HasExplicitAccent = hasExplicitAccent;
        IsIsolated = isIsolated;
        IsolationMounts = Array.AsReadOnly(isolationMounts?.ToArray() ?? []);
        IsolationImageReference = string.IsNullOrWhiteSpace(isolationImageReference)
            ? null
            : isolationImageReference.Trim();
        RunAgentInIsolation = runAgentInIsolation;
        NetworkOverride = networkOverride;
    }

    public static DefinitionKind Kind => DefinitionKind.Workspace;

    public WorkspaceId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    public string? Description { get; }

    /// <summary>
    /// How the accent of the shell is retinted while this workspace is
    /// active. Optional and independent of <see cref="Color"/>: leaving it
    /// unset keeps whatever accent the shell would otherwise use, including
    /// one followed from the host, rather than pinning a colour of ours.
    /// </summary>
    public string? Accent { get; }

    /// <summary>
    /// Distinguishes a user-selected accent from the bronze value seeded by
    /// older releases. This provenance marker makes the one-time migration
    /// safe even when the selected color happens to equal the old seed.
    /// </summary>
    public bool HasExplicitAccent { get; }

    /// <summary>
    /// The colour this workspace is recognised by — its tile in the rail and
    /// its mark on tabs. Null means the presentation picks one, so a
    /// workspace saved before colours existed still looks like something
    /// rather than a hole.
    /// </summary>
    public string? Color { get; }

    /// <summary>
    /// A renderer-neutral semantic icon identifier. Views map this stable value to
    /// the icon set available on the current platform.
    /// </summary>
    public string Icon { get; }

    /// <summary>The stored sequence is the launcher's visible and keyboard traversal order.</summary>
    public IReadOnlyList<WorkspaceEntry> Entries { get; }

    public AgentPolicy? AgentPolicyOverride { get; }

    /// <summary>
    /// When set, tab and panel changes made while working inside the open
    /// workspace are written back to this definition automatically.
    /// </summary>
    public bool AutoSave { get; }

    /// <summary>
    /// Whether the AI agent panel holds a docked slot in this workspace's
    /// layout. Unpinned — the default — it floats over the canvas when
    /// summoned and is gone when dismissed.
    /// </summary>
    public bool AgentPanelPinned { get; }

    /// <summary>
    /// Whether supported workspace processes run inside this workspace's one
    /// persistent platform isolation environment.
    /// </summary>
    public bool IsIsolated { get; }

    /// <summary>
    /// Host paths made visible to the isolated workspace. An empty collection
    /// creates a guest-only environment with no host files mounted.
    /// </summary>
    public IReadOnlyList<WorkspaceIsolationMountDefinition> IsolationMounts { get; }

    /// <summary>
    /// The OCI image used to create this workspace's isolate. Null asks the platform
    /// provider to use its default image while preserving an already-created isolate.
    /// </summary>
    public string? IsolationImageReference { get; }

    /// <summary>
    /// Whether this workspace's AI agent and its tools use the workspace
    /// isolation environment. False keeps the agent on the host even when the
    /// rest of the workspace is isolated.
    /// </summary>
    public bool RunAgentInIsolation { get; }

    /// <summary>
    /// Null inherits application networking. A value replaces the complete application
    /// policy for this workspace.
    /// </summary>
    public NetworkPolicy? NetworkOverride { get; }

    /// <summary>
    /// Null inherits the application preference. A concrete value makes the
    /// workspace behavior stable even when the global preference changes.
    /// </summary>
    public TerminalMultiplexingMode? TerminalMultiplexingOverride { get; }

    /// <summary>
    /// Null inherits the application browser setting. Shared uses the global
    /// profile; Isolated uses this durable workspace id as the profile key.
    /// </summary>
    public WorkspaceBrowserProfileMode? BrowserProfileOverride { get; }

    public WorkspaceDefinition MoveEntry(WorkspaceEntryId entryId, int destinationIndex)
    {
        if (destinationIndex < 0 || destinationIndex >= Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        }

        var sourceIndex = Entries
            .Select((entry, index) => (entry, index))
            .Where(item => item.entry.Id == entryId)
            .Select(item => item.index)
            .SingleOrDefault(-1);
        if (sourceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entryId), entryId, "The entry does not belong to this workspace.");
        }

        var reordered = Entries.ToList();
        var entry = reordered[sourceIndex];
        reordered.RemoveAt(sourceIndex);
        reordered.Insert(destinationIndex, entry);

        return new(
            Id,
            SchemaVersion,
            Name,
            Description,
            Accent,
            reordered,
            AgentPolicyOverride,
            Icon,
            AutoSave,
            Color,
            AgentPanelPinned,
            TerminalMultiplexingOverride,
            BrowserProfileOverride,
            HasExplicitAccent,
            IsIsolated,
            IsolationMounts,
            IsolationImageReference,
            RunAgentInIsolation,
            NetworkOverride);
    }

    public static bool IsValidIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon) || icon.Length > 48 || !IsLowerAsciiLetter(icon[0]))
        {
            return false;
        }

        return icon.All(character =>
            IsLowerAsciiLetter(character)
            || character is >= '0' and <= '9'
            || character is '-' or '.');
    }

    private static bool IsLowerAsciiLetter(char character) => character is >= 'a' and <= 'z';
}

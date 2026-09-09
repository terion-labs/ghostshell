using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// Reusable panel geometry. Imported and designer-produced instances must pass
/// <see cref="LayoutValidator"/> before they are persisted or opened.
/// </summary>
public sealed record LayoutDefinition : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The id prefix of layouts written by workspace autosave. These carry a
    /// live tab's captured geometry; pickers and catalog listings hide them.
    /// </summary>
    public const string AutoSaveIdPrefix = "auto.";

    public static bool IsAutoSaved(LayoutId id) =>
        id.Value.StartsWith(AutoSaveIdPrefix, StringComparison.Ordinal);

    [JsonConstructor]
    public LayoutDefinition(
        LayoutId id,
        int schemaVersion,
        string name,
        LayoutGrid grid,
        IReadOnlyList<LayoutSlotDefinition> slots,
        string? dockLayoutJson = null)
    {
        Id = id;
        SchemaVersion = schemaVersion;
        Name = name;
        Grid = grid ?? throw new ArgumentNullException(nameof(grid));
        Slots = Array.AsReadOnly(slots?.ToArray() ?? throw new ArgumentNullException(nameof(slots)));
        DockLayoutJson = string.IsNullOrWhiteSpace(dockLayoutJson)
            ? null
            : dockLayoutJson;
    }

    public static DefinitionKind Kind => DefinitionKind.Layout;

    public LayoutId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    public LayoutGrid Grid { get; }

    /// <summary>The list order is the default keyboard and accessibility traversal order.</summary>
    public IReadOnlyList<LayoutSlotDefinition> Slots { get; }

    /// <summary>
    /// Dock's serialized recursive layout, including split proportions and
    /// floating-window geometry. Core deliberately treats this as an opaque
    /// payload; the desktop adapter owns its format and rebinds leaf ids to live
    /// panel instances after deserialization.
    /// </summary>
    public string? DockLayoutJson { get; }
}

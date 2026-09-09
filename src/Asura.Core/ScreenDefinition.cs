using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// A saved template. Runtime instances copy this definition so later edits do not mutate live panels.
/// </summary>
public sealed record ScreenDefinition : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public ScreenDefinition(
        ScreenId id,
        int schemaVersion,
        string name,
        string? description,
        LayoutId layoutId,
        IReadOnlyList<ScreenPanelDefinition> panels,
        IReadOnlyList<string>? tags = null,
        AgentPolicy? agentPolicyOverride = null)
    {
        Id = id;
        SchemaVersion = schemaVersion;
        Name = name;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        LayoutId = layoutId;
        Panels = Array.AsReadOnly(panels?.ToArray() ?? throw new ArgumentNullException(nameof(panels)));
        Tags = Array.AsReadOnly(tags?.ToArray() ?? []);
        AgentPolicyOverride = agentPolicyOverride;
    }

    public static DefinitionKind Kind => DefinitionKind.Screen;

    public ScreenId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    public string? Description { get; }

    public LayoutId LayoutId { get; }

    public IReadOnlyList<ScreenPanelDefinition> Panels { get; }

    public IReadOnlyList<string> Tags { get; }

    public AgentPolicy? AgentPolicyOverride { get; }
}

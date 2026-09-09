using System.Text.Json.Serialization;

namespace Asura.Core;

public enum KeymapLayer
{
    Application,
    Terminal,
}

public enum FailedSequenceBehavior
{
    DiscardAndShowHint,
    PassThrough,
}

public sealed record PrefixConfiguration
{
    public PrefixConfiguration(
        KeyStroke stroke,
        TimeSpan timeout,
        bool repeatable,
        FailedSequenceBehavior failedSequenceBehavior)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Prefix timeout must be greater than zero and no longer than ten seconds.");
        }

        Stroke = stroke;
        Timeout = timeout;
        Repeatable = repeatable;
        FailedSequenceBehavior = failedSequenceBehavior;
    }

    public KeyStroke Stroke { get; }

    public TimeSpan Timeout { get; }

    public bool Repeatable { get; }

    public FailedSequenceBehavior FailedSequenceBehavior { get; }
}

public sealed record KeymapProfile : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public KeymapProfile(
        KeymapProfileId id,
        string name,
        KeymapLayer layer,
        IReadOnlyList<CommandBinding> bindings,
        PrefixConfiguration? prefix = null,
        KeymapProfileId? basedOn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bindings);

        Id = id;
        Name = name;
        Layer = layer;
        Bindings = Array.AsReadOnly(bindings.ToArray());
        Prefix = prefix;
        BasedOn = basedOn;
    }

    public static DefinitionKind Kind => DefinitionKind.Keymap;

    public KeymapProfileId Id { get; }

    public int SchemaVersion => CurrentSchemaVersion;

    public string Name { get; }

    public KeymapLayer Layer { get; }

    public IReadOnlyList<CommandBinding> Bindings { get; }

    public PrefixConfiguration? Prefix { get; }

    public KeymapProfileId? BasedOn { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public KeymapProfile CloneAs(KeymapProfileId id, string name) => new(
        id,
        name,
        Layer,
        Bindings,
        Prefix,
        Id);
}

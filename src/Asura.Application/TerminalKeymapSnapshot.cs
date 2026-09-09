using Asura.Core;

namespace Asura.Application;

/// <summary>
/// An immutable terminal-layer keymap captured when a terminal session is launched.
/// Editing or replacing the durable profile cannot change shortcuts in an existing session.
/// </summary>
public sealed record TerminalKeymapSnapshot
{
    public TerminalKeymapSnapshot(
        KeymapProfileId id,
        string name,
        IReadOnlyList<CommandBinding> bindings,
        PrefixConfiguration? prefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bindings);

        Id = id;
        Name = name;
        Bindings = Array.AsReadOnly(bindings
            .Select(CopyBinding)
            .ToArray());
        Prefix = prefix is null
            ? null
            : new PrefixConfiguration(
                prefix.Stroke,
                prefix.Timeout,
                prefix.Repeatable,
                prefix.FailedSequenceBehavior);
    }

    public KeymapProfileId Id { get; }

    public string Name { get; }

    public IReadOnlyList<CommandBinding> Bindings { get; }

    public PrefixConfiguration? Prefix { get; }

    public static TerminalKeymapSnapshot FromProfile(KeymapProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Layer != KeymapLayer.Terminal)
        {
            throw new ArgumentException(
                "A terminal launch requires a terminal-layer keymap.",
                nameof(profile));
        }

        return new TerminalKeymapSnapshot(
            profile.Id,
            profile.Name,
            profile.Bindings,
            profile.Prefix);
    }

    private static CommandBinding CopyBinding(CommandBinding binding) => new(
        binding.CommandId,
        new KeySequence(binding.Sequence.Strokes),
        binding.Contexts,
        binding.Arguments);
}

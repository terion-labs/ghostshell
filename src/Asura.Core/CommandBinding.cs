using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Asura.Core;

public sealed record CommandBinding
{
    [JsonConstructor]
    public CommandBinding(
        CommandId commandId,
        KeySequence sequence,
        CommandContext contexts,
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var argumentBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (arguments is not null)
        {
            foreach (var (name, value) in arguments)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException(
                        "Command argument names and values cannot be empty.",
                        nameof(arguments));
                }

                argumentBuilder.Add(name, value);
            }
        }

        CommandId = commandId;
        Sequence = sequence;
        Contexts = CommandContextRules.Require(contexts, nameof(contexts));
        Arguments = argumentBuilder.ToImmutable();
    }

    public CommandId CommandId { get; }

    public KeySequence Sequence { get; }

    public CommandContext Contexts { get; }

    public IReadOnlyDictionary<string, string> Arguments { get; }
}

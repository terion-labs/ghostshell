using System.Text;

namespace Asura.Core;

/// <summary>
/// Stable owner of an agent conversation catalog. Unlike a live workspace
/// instance ID, this survives closing and reopening a saved workspace.
/// </summary>
public readonly record struct AgentConversationScopeId
{
    public const int MaximumUtf8Bytes = 256;

    public AgentConversationScopeId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) > MaximumUtf8Bytes
            || value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                $"An agent conversation scope must be printable and at most {MaximumUtf8Bytes} UTF-8 bytes.",
                nameof(value));
        }

        Value = string.Concat(value);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

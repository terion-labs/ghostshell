using System.Text.Json.Serialization;

namespace Asura.Application;

/// <summary>
/// The provider-visible identifier for one short-lived browser element lease.
/// It carries no selector or native-handle semantics.
/// </summary>
public readonly record struct BrowserElementReferenceId
{
    public const int MaximumValueBytes = 128;

    [JsonConstructor]
    public BrowserElementReferenceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumValueBytes
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                $"A browser element reference ID must be a URL-safe ASCII "
                + $"identifier of at most {MaximumValueBytes} bytes.",
                nameof(value));
        }

        Value = string.Concat(value);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

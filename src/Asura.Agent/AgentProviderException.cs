namespace Asura.Agent;

/// <summary>
/// A provider-boundary failure whose code and message are safe to show to the
/// local user. Transport payloads, response bodies, credentials, and exception
/// details remain outside this contract.
/// </summary>
public abstract class AgentProviderException : Exception
{
    protected AgentProviderException(
        string stableCode,
        string publicMessage,
        Exception? innerException = null)
        : base(publicMessage, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicMessage);
        if (stableCode.Length > 128
            || stableCode.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'
                    and not '-'))
        {
            throw new ArgumentException(
                "A provider failure code must be a bounded stable identifier.",
                nameof(stableCode));
        }

        if (publicMessage.Length > 512 || publicMessage.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A provider failure message must be bounded printable text.",
                nameof(publicMessage));
        }

        StableCode = stableCode;
        PublicMessage = publicMessage;
    }

    public string StableCode { get; }

    public string PublicMessage { get; }

}

public sealed class AgentProviderFailure
{
    internal AgentProviderFailure(string stableCode, string message)
    {
        StableCode = stableCode;
        Message = message;
    }

    public string StableCode { get; }

    public string Message { get; }
}

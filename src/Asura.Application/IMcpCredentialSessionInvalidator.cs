using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Closes live MCP sessions that resolved one rotated or removed credential.
/// </summary>
public interface IMcpCredentialSessionInvalidator
{
    ValueTask InvalidateAsync(SecretRef reference);
}

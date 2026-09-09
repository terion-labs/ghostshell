using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Application-facing compatibility facade for the shared literal-secret
/// classifier used at durable and governed boundaries.
/// </summary>
public static class AgentLiteralSecretValidator
{
    public static bool ContainsLikelyLiteralSecret(string value) =>
        LiteralSecretValidator.ContainsLikelyLiteralSecret(value);
}

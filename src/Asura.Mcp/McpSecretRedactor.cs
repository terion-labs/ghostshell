using System.Security.Cryptography;
using Asura.Application;

namespace Asura.Mcp;

/// <summary>
/// Keeps exact secret values only as clearable character buffers so an MCP
/// server cannot reflect its environment into provider-visible metadata or
/// tool results.
/// </summary>
internal sealed class McpSecretRedactor : IDisposable
{
    private const string Replacement = "[REDACTED MCP CONTENT]";
    private readonly char[][] _literals;
    private int _disposed;

    public McpSecretRedactor(IEnumerable<char[]> literals)
    {
        ArgumentNullException.ThrowIfNull(literals);
        _literals = [.. literals
            .Select(literal =>
            {
                ArgumentNullException.ThrowIfNull(literal);
                if (literal.Length == 0)
                {
                    throw new ArgumentException(
                        "An MCP redaction literal cannot be empty.",
                        nameof(literals));
                }

                return literal;
            })];
    }

    public string Redact(string value, out bool redacted)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(value);
        if (AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            redacted = true;
            return Replacement;
        }

        foreach (var literal in _literals)
        {
            if (value.AsSpan().IndexOf(literal) < 0)
            {
                continue;
            }

            redacted = true;
            return Replacement;
        }

        redacted = false;
        return value;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var literal in _literals)
        {
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                    literal.AsSpan()));
        }
    }

}

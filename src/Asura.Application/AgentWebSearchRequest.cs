using System.Text;

namespace Asura.Application;

public sealed record AgentWebSearchRequest : AgentWebToolRequest
{
    public const int DefaultResultCount = 10;
    public const int MaximumQueryBytes = 512;
    public const int MaximumResultCount = 10;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentWebSearchRequest(string query, int resultCount = DefaultResultCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var normalized = query.Trim();
        if (normalized.Any(char.IsControl)
            || !TryGetByteCount(normalized, out var byteCount)
            || byteCount > MaximumQueryBytes)
        {
            throw new ArgumentException(
                $"A web search query must be printable and at most {MaximumQueryBytes} UTF-8 bytes.",
                nameof(query));
        }

        if (resultCount is < 1 or > MaximumResultCount)
        {
            throw new ArgumentOutOfRangeException(nameof(resultCount));
        }

        Query = normalized;
        ResultCount = resultCount;
    }

    public string Query { get; }

    public int ResultCount { get; }

    public override string ToolName => BuiltInAgentTools.WebSearch;

    private static bool TryGetByteCount(string value, out int byteCount)
    {
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            byteCount = 0;
            return false;
        }
    }
}

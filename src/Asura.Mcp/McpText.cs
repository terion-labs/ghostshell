using System.Text;

namespace Asura.Mcp;

internal static class McpText
{
    public static string RequireIdentifier(string value, int maxUtf8Bytes, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(value) > maxUtf8Bytes)
        {
            throw new ArgumentException(
                $"The value must contain at most {maxUtf8Bytes} UTF-8 bytes and no control characters.",
                parameterName);
        }

        return value;
    }

    public static bool IsBounded(string? value, int maxUtf8Bytes) =>
        value is null || Encoding.UTF8.GetByteCount(value) <= maxUtf8Bytes;
}

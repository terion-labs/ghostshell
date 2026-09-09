using System.Buffers.Binary;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class TerminalScrollbackAnchorCodec
{
    private const byte Version = 1;
    private const int PayloadLength = 1 + sizeof(long) + sizeof(int);

    public static string Encode(TerminalScrollbackRowAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(payload[1..], anchor.ContentRevision);
        BinaryPrimitives.WriteInt32BigEndian(payload[(1 + sizeof(long))..], anchor.LineIndex);
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(
        string? value,
        out TerminalScrollbackRowAnchor? anchor)
    {
        anchor = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        Span<byte> payload = stackalloc byte[PayloadLength];
        if (!Convert.TryFromBase64String(base64, payload, out var written)
            || written != PayloadLength
            || payload[0] != Version)
        {
            return false;
        }

        var revision = BinaryPrimitives.ReadInt64BigEndian(payload[1..]);
        var lineIndex = BinaryPrimitives.ReadInt32BigEndian(
            payload[(1 + sizeof(long))..]);
        if (revision < 0 || lineIndex < 0)
        {
            return false;
        }

        anchor = new TerminalScrollbackRowAnchor(revision, lineIndex);
        return true;
    }
}

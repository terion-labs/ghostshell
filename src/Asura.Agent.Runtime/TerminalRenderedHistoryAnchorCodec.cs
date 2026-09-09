using System.Buffers.Binary;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class TerminalRenderedHistoryAnchorCodec
{
    private const byte Kind = 2;
    private const int PayloadLength = 1 + sizeof(long) + sizeof(int);

    public static string Encode(TerminalRenderedHistoryRowAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = Kind;
        BinaryPrimitives.WriteInt64BigEndian(payload[1..], anchor.ContentRevision);
        BinaryPrimitives.WriteInt32BigEndian(
            payload[(1 + sizeof(long))..],
            anchor.RowIndex);
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(
        string? value,
        out TerminalRenderedHistoryRowAnchor? anchor)
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
            || payload[0] != Kind)
        {
            return false;
        }

        var revision = BinaryPrimitives.ReadInt64BigEndian(payload[1..]);
        var rowIndex = BinaryPrimitives.ReadInt32BigEndian(
            payload[(1 + sizeof(long))..]);
        if (revision < 0 || rowIndex < 0)
        {
            return false;
        }

        anchor = new TerminalRenderedHistoryRowAnchor(revision, rowIndex);
        return true;
    }
}

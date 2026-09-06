using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GhostShell.Infrastructure;

internal static class WorkspacePacketFrameCodec
{
    internal const int HeaderLength = 20;
    internal const int AuthenticationTagLength = 32;
    internal const int MaximumPayloadLength = ushort.MaxValue;
    internal const byte CurrentVersion = 1;

    private const uint Magic = 0x47534e57; // GSNW

    internal enum MessageKind : byte
    {
        GuestHello = 1,
        HostConfiguration = 2,
        GuestReady = 3,
        IpPacket = 4,
    }

    internal sealed record Frame(MessageKind Kind, byte[] Payload);

    internal static async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> authenticationKey,
        MessageKind kind,
        ulong sequence,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateKey(authenticationKey);
        if (payload.Length > MaximumPayloadLength)
        {
            throw Failure(
                WorkspacePacketChannelFailure.FrameTooLarge,
                $"A packet-channel frame cannot exceed {MaximumPayloadLength} bytes.");
        }

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, Magic);
        header[4] = CurrentVersion;
        header[5] = (byte)kind;
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)payload.Length);
        var tag = Authenticate(authenticationKey.Span, header, payload.Span);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<Frame> ReadAsync(
        Stream stream,
        ReadOnlyMemory<byte> authenticationKey,
        ulong expectedSequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateKey(authenticationKey);

        var header = new byte[HeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != Magic || header[6] != 0 || header[7] != 0)
        {
            throw Failure(WorkspacePacketChannelFailure.InvalidFrame, "The packet-channel frame header is invalid.");
        }

        if (header[4] != CurrentVersion)
        {
            throw Failure(
                WorkspacePacketChannelFailure.UnsupportedVersion,
                $"Packet-channel protocol version {header[4]} is not supported.");
        }

        if (!Enum.IsDefined((MessageKind)header[5]))
        {
            throw Failure(WorkspacePacketChannelFailure.UnexpectedMessage, "The packet-channel message type is unknown.");
        }

        var sequence = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
        if (sequence != expectedSequence)
        {
            throw Failure(
                WorkspacePacketChannelFailure.InvalidSequence,
                $"Expected packet-channel sequence {expectedSequence}, received {sequence}.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
        if (payloadLength > MaximumPayloadLength)
        {
            throw Failure(
                WorkspacePacketChannelFailure.FrameTooLarge,
                $"The declared frame length {payloadLength} exceeds {MaximumPayloadLength} bytes.");
        }

        var payload = new byte[payloadLength];
        var suppliedTag = new byte[AuthenticationTagLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        await ReadExactlyAsync(stream, suppliedTag, cancellationToken).ConfigureAwait(false);
        var expectedTag = Authenticate(authenticationKey.Span, header, payload);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, suppliedTag))
        {
            throw Failure(
                WorkspacePacketChannelFailure.AuthenticationFailed,
                "The packet-channel frame authentication tag is invalid.");
        }

        return new Frame((MessageKind)header[5], payload);
    }

    internal static void ValidateKey(ReadOnlyMemory<byte> authenticationKey)
    {
        if (authenticationKey.Length != AuthenticationTagLength)
        {
            throw new ArgumentException(
                "The packet-channel authentication key must contain exactly 32 bytes.",
                nameof(authenticationKey));
        }
    }

    private static byte[] Authenticate(
        ReadOnlySpan<byte> authenticationKey,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, authenticationKey);
        hash.AppendData(header);
        hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new WorkspacePacketChannelProtocolException(
                WorkspacePacketChannelFailure.UnexpectedEndOfStream,
                "The packet channel ended in the middle of a frame.",
                exception);
        }
    }

    private static WorkspacePacketChannelProtocolException Failure(
        WorkspacePacketChannelFailure failure,
        string message) => new(failure, message);
}

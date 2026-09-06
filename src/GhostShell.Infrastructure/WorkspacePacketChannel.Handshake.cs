using System.Security.Cryptography;
using GhostShell.Application;

namespace GhostShell.Infrastructure;

public sealed partial class WorkspacePacketChannel
{
    private const int NonceLength = 32;
    private const int HelloPayloadLength = 2 + NonceLength;
    private const int ConfigurationPrefixLength = 1 + (2 * NonceLength);
    private const int ReadyPayloadLength = 1 + (2 * NonceLength);

    private static readonly byte[] GuestToHostKeyLabel =
        "GhostSHELL workspace packet channel guest to host v1"u8.ToArray();
    private static readonly byte[] HostToGuestKeyLabel =
        "GhostSHELL workspace packet channel host to guest v1"u8.ToArray();

    public static async ValueTask<WorkspacePacketChannel> AcceptHostAsync(
        Stream transport,
        ReadOnlyMemory<byte> authenticationKey,
        WorkspaceGuestNetworkConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ValidateTransport(transport);
        ArgumentNullException.ThrowIfNull(configuration);
        WorkspacePacketFrameCodec.ValidateKey(authenticationKey);

        var guestToHostHandshakeKey = DeriveKey(
            authenticationKey.Span,
            GuestToHostKeyLabel);
        var hostToGuestHandshakeKey = DeriveKey(
            authenticationKey.Span,
            HostToGuestKeyLabel);
        var hostNonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[]? guestNonce = null;
        byte[]? guestToHostSessionKey = null;
        byte[]? hostToGuestSessionKey = null;
        try
        {
            var hello = await WorkspacePacketFrameCodec.ReadAsync(
                    transport,
                    guestToHostHandshakeKey,
                    0,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireKind(hello, WorkspacePacketFrameCodec.MessageKind.GuestHello);
            guestNonce = ParseHello(hello.Payload);

            var configurationPayload = CreateConfigurationPayload(
                guestNonce,
                hostNonce,
                configuration);
            await WorkspacePacketFrameCodec.WriteAsync(
                    transport,
                    hostToGuestHandshakeKey,
                    WorkspacePacketFrameCodec.MessageKind.HostConfiguration,
                    0,
                    configurationPayload,
                    cancellationToken)
                .ConfigureAwait(false);

            var ready = await WorkspacePacketFrameCodec.ReadAsync(
                    transport,
                    guestToHostHandshakeKey,
                    1,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireKind(ready, WorkspacePacketFrameCodec.MessageKind.GuestReady);
            ValidateReady(ready.Payload, guestNonce, hostNonce);
            guestToHostSessionKey = DeriveSessionKey(
                authenticationKey.Span,
                GuestToHostKeyLabel,
                guestNonce,
                hostNonce);
            hostToGuestSessionKey = DeriveSessionKey(
                authenticationKey.Span,
                HostToGuestKeyLabel,
                guestNonce,
                hostNonce);

            return new WorkspacePacketChannel(
                transport,
                guestToHostSessionKey,
                hostToGuestSessionKey,
                nextReadSequence: 2,
                nextWriteSequence: 1,
                configuration);
        }
        catch
        {
            ZeroIfPresent(guestToHostSessionKey);
            ZeroIfPresent(hostToGuestSessionKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(guestToHostHandshakeKey);
            CryptographicOperations.ZeroMemory(hostToGuestHandshakeKey);
            if (guestNonce is not null)
            {
                CryptographicOperations.ZeroMemory(guestNonce);
            }

            CryptographicOperations.ZeroMemory(hostNonce);
        }
    }

    public static async ValueTask<WorkspacePacketChannel> ConnectGuestAsync(
        Stream transport,
        ReadOnlyMemory<byte> authenticationKey,
        CancellationToken cancellationToken)
    {
        ValidateTransport(transport);
        WorkspacePacketFrameCodec.ValidateKey(authenticationKey);

        var guestToHostHandshakeKey = DeriveKey(
            authenticationKey.Span,
            GuestToHostKeyLabel);
        var hostToGuestHandshakeKey = DeriveKey(
            authenticationKey.Span,
            HostToGuestKeyLabel);
        var guestNonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[]? hostNonce = null;
        byte[]? guestToHostSessionKey = null;
        byte[]? hostToGuestSessionKey = null;
        try
        {
            var hello = new byte[HelloPayloadLength];
            hello[0] = WorkspacePacketFrameCodec.CurrentVersion;
            hello[1] = WorkspacePacketFrameCodec.CurrentVersion;
            guestNonce.CopyTo(hello, 2);
            await WorkspacePacketFrameCodec.WriteAsync(
                    transport,
                    guestToHostHandshakeKey,
                    WorkspacePacketFrameCodec.MessageKind.GuestHello,
                    0,
                    hello,
                    cancellationToken)
                .ConfigureAwait(false);

            var response = await WorkspacePacketFrameCodec.ReadAsync(
                    transport,
                    hostToGuestHandshakeKey,
                    0,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireKind(response, WorkspacePacketFrameCodec.MessageKind.HostConfiguration);
            var (configuration, receivedHostNonce) = ParseConfiguration(
                response.Payload,
                guestNonce);
            hostNonce = receivedHostNonce;

            var ready = CreateReadyPayload(guestNonce, hostNonce);
            await WorkspacePacketFrameCodec.WriteAsync(
                    transport,
                    guestToHostHandshakeKey,
                    WorkspacePacketFrameCodec.MessageKind.GuestReady,
                    1,
                    ready,
                    cancellationToken)
                .ConfigureAwait(false);
            guestToHostSessionKey = DeriveSessionKey(
                authenticationKey.Span,
                GuestToHostKeyLabel,
                guestNonce,
                hostNonce);
            hostToGuestSessionKey = DeriveSessionKey(
                authenticationKey.Span,
                HostToGuestKeyLabel,
                guestNonce,
                hostNonce);

            return new WorkspacePacketChannel(
                transport,
                hostToGuestSessionKey,
                guestToHostSessionKey,
                nextReadSequence: 1,
                nextWriteSequence: 2,
                configuration);
        }
        catch
        {
            ZeroIfPresent(guestToHostSessionKey);
            ZeroIfPresent(hostToGuestSessionKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(guestToHostHandshakeKey);
            CryptographicOperations.ZeroMemory(hostToGuestHandshakeKey);
            CryptographicOperations.ZeroMemory(guestNonce);
            if (hostNonce is not null)
            {
                CryptographicOperations.ZeroMemory(hostNonce);
            }
        }
    }

    private static byte[] ParseHello(byte[] payload)
    {
        if (payload.Length != HelloPayloadLength)
        {
            throw Malformed("The guest hello payload has an invalid length.");
        }

        var minimumVersion = payload[0];
        var maximumVersion = payload[1];
        if (minimumVersion > WorkspacePacketFrameCodec.CurrentVersion
            || maximumVersion < WorkspacePacketFrameCodec.CurrentVersion
            || minimumVersion > maximumVersion)
        {
            throw new WorkspacePacketChannelProtocolException(
                WorkspacePacketChannelFailure.UnsupportedVersion,
                "The host and guest do not share a packet-channel protocol version.");
        }

        return payload.AsSpan(2, NonceLength).ToArray();
    }

    private static byte[] CreateConfigurationPayload(
        ReadOnlySpan<byte> guestNonce,
        ReadOnlySpan<byte> hostNonce,
        WorkspaceGuestNetworkConfiguration configuration)
    {
        var encodedConfiguration = WorkspaceGuestNetworkConfigurationCodec.Encode(configuration);
        var payload = new byte[ConfigurationPrefixLength + encodedConfiguration.Length];
        payload[0] = WorkspacePacketFrameCodec.CurrentVersion;
        guestNonce.CopyTo(payload.AsSpan(1, NonceLength));
        hostNonce.CopyTo(payload.AsSpan(1 + NonceLength, NonceLength));
        encodedConfiguration.CopyTo(payload, ConfigurationPrefixLength);
        return payload;
    }

    private static (WorkspaceGuestNetworkConfiguration Configuration, byte[] HostNonce)
        ParseConfiguration(byte[] payload, ReadOnlySpan<byte> expectedGuestNonce)
    {
        if (payload.Length <= ConfigurationPrefixLength
            || payload[0] != WorkspacePacketFrameCodec.CurrentVersion)
        {
            throw Malformed("The host configuration payload is invalid.");
        }

        var receivedGuestNonce = payload.AsSpan(1, NonceLength);
        if (!CryptographicOperations.FixedTimeEquals(receivedGuestNonce, expectedGuestNonce))
        {
            throw new WorkspacePacketChannelProtocolException(
                WorkspacePacketChannelFailure.AuthenticationFailed,
                "The host configuration does not belong to this handshake.");
        }

        var hostNonce = payload.AsSpan(1 + NonceLength, NonceLength).ToArray();
        var configuration = WorkspaceGuestNetworkConfigurationCodec.Decode(
            payload.AsSpan(ConfigurationPrefixLength));
        return (configuration, hostNonce);
    }

    private static byte[] CreateReadyPayload(
        ReadOnlySpan<byte> guestNonce,
        ReadOnlySpan<byte> hostNonce)
    {
        var payload = new byte[ReadyPayloadLength];
        payload[0] = WorkspacePacketFrameCodec.CurrentVersion;
        guestNonce.CopyTo(payload.AsSpan(1, NonceLength));
        hostNonce.CopyTo(payload.AsSpan(1 + NonceLength, NonceLength));
        return payload;
    }

    private static void ValidateReady(
        byte[] payload,
        ReadOnlySpan<byte> expectedGuestNonce,
        ReadOnlySpan<byte> expectedHostNonce)
    {
        if (payload.Length != ReadyPayloadLength
            || payload[0] != WorkspacePacketFrameCodec.CurrentVersion)
        {
            throw Malformed("The guest-ready payload is invalid.");
        }

        var guestNonce = payload.AsSpan(1, NonceLength);
        var hostNonce = payload.AsSpan(1 + NonceLength, NonceLength);
        if (!CryptographicOperations.FixedTimeEquals(guestNonce, expectedGuestNonce)
            || !CryptographicOperations.FixedTimeEquals(hostNonce, expectedHostNonce))
        {
            throw new WorkspacePacketChannelProtocolException(
                WorkspacePacketChannelFailure.AuthenticationFailed,
                "The guest-ready payload does not belong to this handshake.");
        }
    }

    private static byte[] DeriveKey(
        ReadOnlySpan<byte> authenticationKey,
        ReadOnlySpan<byte> label) => HMACSHA256.HashData(authenticationKey, label);

    private static byte[] DeriveSessionKey(
        ReadOnlySpan<byte> authenticationKey,
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> guestNonce,
        ReadOnlySpan<byte> hostNonce)
    {
        using var hash = IncrementalHash.CreateHMAC(
            HashAlgorithmName.SHA256,
            authenticationKey);
        hash.AppendData(label);
        hash.AppendData(guestNonce);
        hash.AppendData(hostNonce);
        return hash.GetHashAndReset();
    }

    private static void ZeroIfPresent(byte[]? key)
    {
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void ValidateTransport(Stream transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!transport.CanRead || !transport.CanWrite)
        {
            throw new ArgumentException(
                "The packet-channel transport must be readable and writable.",
                nameof(transport));
        }
    }

    private static WorkspacePacketChannelProtocolException Malformed(string message) =>
        new(WorkspacePacketChannelFailure.MalformedPayload, message);
}

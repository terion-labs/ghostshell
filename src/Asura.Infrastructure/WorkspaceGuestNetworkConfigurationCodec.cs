using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Asura.Application;

namespace Asura.Infrastructure;

internal static class WorkspaceGuestNetworkConfigurationCodec
{
    private const byte Ipv4Flag = 1;
    private const byte Ipv6Flag = 2;

    internal static byte[] Encode(WorkspaceGuestNetworkConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var interfaceName = Encoding.ASCII.GetBytes(configuration.InterfaceName);
        var length = 1 + sizeof(ushort) + 1 + interfaceName.Length + 1;
        length += configuration.Ipv4Address is null ? 0 : 4 + 1;
        length += configuration.Ipv6Address is null ? 0 : 16 + 1;
        length += configuration.DnsServers.Sum(server => 1 + server.GetAddressBytes().Length);

        var payload = new byte[length];
        var offset = 0;
        payload[offset++] = (byte)(
            (configuration.Ipv4Address is null ? 0 : Ipv4Flag)
            | (configuration.Ipv6Address is null ? 0 : Ipv6Flag));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset), (ushort)configuration.Mtu);
        offset += sizeof(ushort);
        payload[offset++] = (byte)interfaceName.Length;
        interfaceName.CopyTo(payload, offset);
        offset += interfaceName.Length;

        if (configuration.Ipv4Address is not null)
        {
            offset = WriteAddress(
                payload,
                offset,
                configuration.Ipv4Address,
                configuration.Ipv4PrefixLength!.Value);
        }

        if (configuration.Ipv6Address is not null)
        {
            offset = WriteAddress(
                payload,
                offset,
                configuration.Ipv6Address,
                configuration.Ipv6PrefixLength!.Value);
        }

        payload[offset++] = (byte)configuration.DnsServers.Count;
        foreach (var dnsServer in configuration.DnsServers)
        {
            var address = dnsServer.GetAddressBytes();
            payload[offset++] = dnsServer.AddressFamily == AddressFamily.InterNetwork
                ? (byte)4
                : (byte)6;
            address.CopyTo(payload, offset);
            offset += address.Length;
        }

        return payload;
    }

    internal static WorkspaceGuestNetworkConfiguration Decode(ReadOnlySpan<byte> payload)
    {
        try
        {
            var offset = 0;
            var flags = ReadByte(payload, ref offset);
            if ((flags & ~(Ipv4Flag | Ipv6Flag)) != 0 || flags == 0)
            {
                throw Malformed("The guest network address-family flags are invalid.");
            }

            var mtu = BinaryPrimitives.ReadUInt16BigEndian(Take(payload, ref offset, sizeof(ushort)));
            var interfaceNameLength = ReadByte(payload, ref offset);
            var interfaceName = Encoding.ASCII.GetString(
                Take(payload, ref offset, interfaceNameLength));

            IPAddress? ipv4Address = null;
            int? ipv4PrefixLength = null;
            if ((flags & Ipv4Flag) != 0)
            {
                ipv4Address = new IPAddress(Take(payload, ref offset, 4));
                ipv4PrefixLength = ReadByte(payload, ref offset);
            }

            IPAddress? ipv6Address = null;
            int? ipv6PrefixLength = null;
            if ((flags & Ipv6Flag) != 0)
            {
                ipv6Address = new IPAddress(Take(payload, ref offset, 16));
                ipv6PrefixLength = ReadByte(payload, ref offset);
            }

            var dnsCount = ReadByte(payload, ref offset);
            var dnsServers = new List<IPAddress>(dnsCount);
            for (var index = 0; index < dnsCount; index++)
            {
                var family = ReadByte(payload, ref offset);
                var addressLength = family switch
                {
                    4 => 4,
                    6 => 16,
                    _ => throw Malformed("A DNS server has an unknown address family."),
                };
                dnsServers.Add(new IPAddress(Take(payload, ref offset, addressLength)));
            }

            if (offset != payload.Length)
            {
                throw Malformed("The guest network configuration has trailing bytes.");
            }

            return new WorkspaceGuestNetworkConfiguration(
                interfaceName,
                mtu,
                ipv4Address,
                ipv4PrefixLength,
                ipv6Address,
                ipv6PrefixLength,
                dnsServers);
        }
        catch (WorkspacePacketChannelProtocolException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            throw new WorkspacePacketChannelProtocolException(
                WorkspacePacketChannelFailure.MalformedPayload,
                "The host supplied an invalid guest network configuration.",
                exception);
        }
    }

    private static int WriteAddress(
        byte[] destination,
        int offset,
        IPAddress address,
        int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        bytes.CopyTo(destination, offset);
        offset += bytes.Length;
        destination[offset++] = (byte)prefixLength;
        return offset;
    }

    private static byte ReadByte(ReadOnlySpan<byte> payload, ref int offset) =>
        Take(payload, ref offset, 1)[0];

    private static ReadOnlySpan<byte> Take(
        ReadOnlySpan<byte> payload,
        ref int offset,
        int length)
    {
        if (length < 0 || offset > payload.Length - length)
        {
            throw Malformed("The guest network configuration is truncated.");
        }

        var value = payload.Slice(offset, length);
        offset += length;
        return value;
    }

    private static WorkspacePacketChannelProtocolException Malformed(string message) =>
        new(WorkspacePacketChannelFailure.MalformedPayload, message);
}

namespace Asura.Application;

/// <summary>
/// Private host endpoints for a workspace's SDK-owned NIC. The runtime control socket
/// owns route leases; the packet socket connects the selected host VPN/proxy adapter.
/// Neither socket is mounted into the guest.
/// </summary>
public sealed record WorkspaceHostNetworkAttachment
{
    public WorkspaceHostNetworkAttachment(string controlSocketPath, string packetSocketPath)
    {
        ValidatePath(controlSocketPath, nameof(controlSocketPath));
        ValidatePath(packetSocketPath, nameof(packetSocketPath));
        if (string.Equals(controlSocketPath, packetSocketPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("Control and packet sockets must be distinct.", nameof(packetSocketPath));
        }

        ControlSocketPath = controlSocketPath;
        PacketSocketPath = packetSocketPath;
    }

    public string ControlSocketPath { get; }

    public string PacketSocketPath { get; }

    private static void ValidatePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Path.IsPathFullyQualified(value) || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A host network socket must have an absolute path without NUL characters.", parameterName);
        }
    }
}

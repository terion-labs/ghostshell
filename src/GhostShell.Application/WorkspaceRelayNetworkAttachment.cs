namespace GhostShell.Application;

/// <summary>A private exec launch carrying Ethernet frames, never a guest TCP listener.</summary>
public sealed record WorkspaceRelayNetworkAttachment(WorkspaceProcessLaunch PacketLaunch, string Architecture);

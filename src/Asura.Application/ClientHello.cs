namespace Asura.Application;

public sealed record ClientHello(
    IReadOnlyList<int> SupportedProtocolVersions,
    CapabilitySet Capabilities);

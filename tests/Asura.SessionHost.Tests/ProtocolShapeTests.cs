using Asura.Application;
using Asura.Protocol;

namespace Asura.SessionHost.Tests;

public sealed class ProtocolShapeTests
{
    [Fact]
    public async Task NegotiationSelectsHighestSharedVersionAndCapabilities()
    {
        await using var harness = new SessionHostTestHarness();
        var result = await harness.Client.NegotiateAsync(
            new ClientHello([0, ProtocolVersions.Current], SessionHostTestHarness.AllCapabilities()),
            harness.HumanContext(),
            CancellationToken.None);

        var hello = result.Value();
        Assert.Equal(ProtocolVersions.Current, hello.ProtocolVersion);
        Assert.Equal(HostMode.Desktop, hello.HostMode);
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.InputLease));
    }

    [Fact]
    public async Task UnsupportedProtocolReturnsStableError()
    {
        await using var harness = new SessionHostTestHarness();
        var result = await harness.Client.NegotiateAsync(
            new ClientHello([99], CapabilitySet.Empty),
            harness.HumanContext(),
            CancellationToken.None);

        var error = result.Error();
        Assert.Equal(HostErrorCode.UnsupportedProtocol, error.Code);
        Assert.Equal("unsupported_protocol", error.StableCode);
    }
}

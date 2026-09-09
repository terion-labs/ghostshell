using Asura.Application;
using Asura.Core;

namespace Asura.Browser.Tests;

public sealed class BrowserCapabilityProfileTests
{
    [Fact]
    public void ProductionAndCandidateProfilesAreBoundedAndImmutable()
    {
        Assert.Equal(
        [
            SessionCapabilities.BrowserAgentInputBarrier,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserKey,
            SessionCapabilities.BrowserMouse,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserScroll,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserWait,
        ],
            BrowserCapabilityProfile.Production.Capabilities.Values);
        Assert.Equal(
        [
            SessionCapabilities.BrowserAgentInputBarrier,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserEvaluate,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserKey,
            SessionCapabilities.BrowserMouse,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserScroll,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserWait,
        ],
            BrowserCapabilityProfile.FullAutomationCandidate.Capabilities.Values);
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)BrowserCapabilityProfile
                    .Production
                    .Capabilities
                    .Values)
                .Clear());
    }

    [Fact]
    public async Task FactoryFixesOneExplicitProfileForEveryCreatedSession()
    {
        var factory = new BrowserPanelSessionFactory(
            BrowserCapabilityProfile.FullAutomationCandidate);
        await using var session = await factory.CreateAsync(
            new SessionId("browser-profile"),
            BrowserAddress.Blank,
            CancellationToken.None);

        Assert.Same(
            BrowserCapabilityProfile.FullAutomationCandidate,
            factory.CapabilityProfile);
        Assert.Same(
            BrowserCapabilityProfile.FullAutomationCandidate.Capabilities,
            factory.Capabilities);
        Assert.Same(
            BrowserCapabilityProfile.FullAutomationCandidate.Capabilities,
            session.Capabilities);
    }
}

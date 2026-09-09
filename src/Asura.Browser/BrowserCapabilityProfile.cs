using Asura.Application;

namespace Asura.Browser;

/// <summary>
/// Names the complete browser contract that a factory, session, and renderer
/// agree to expose for their full lifetime.
/// </summary>
public sealed class BrowserCapabilityProfile
{
    /// <summary>
    /// The capability set used by desktop production. Semantic automation is
    /// enabled only because the native CEF adapter is covered by the focused
    /// conformance suite.
    /// </summary>
    public static BrowserCapabilityProfile Production { get; } = new(
    [
        SessionCapabilities.BrowserReadState,
        SessionCapabilities.BrowserNavigate,
        SessionCapabilities.BrowserBack,
        SessionCapabilities.BrowserForward,
        SessionCapabilities.BrowserReload,
        SessionCapabilities.BrowserStop,
        SessionCapabilities.BrowserOriginGuard,
        SessionCapabilities.BrowserAgentInputBarrier,
        SessionCapabilities.BrowserSnapshot,
        SessionCapabilities.BrowserWait,
        SessionCapabilities.BrowserClick,
        SessionCapabilities.BrowserFill,
        SessionCapabilities.BrowserCheck,
        SessionCapabilities.BrowserMouse,
        SessionCapabilities.BrowserKey,
        SessionCapabilities.BrowserScroll,
    ]);

    /// <summary>
    /// The complete implemented browser contract. Tests inject this profile
    /// explicitly. Evaluate is deliberately dormant in production until a
    /// credential-safe scripting boundary can prevent derived secret access;
    /// source scanning and result-name filtering are not authority boundaries.
    /// </summary>
    public static BrowserCapabilityProfile FullAutomationCandidate { get; } = new(
    [
        .. Production.Capabilities.Values,
        SessionCapabilities.BrowserEvaluate,
    ]);

    private BrowserCapabilityProfile(IEnumerable<string> capabilities)
    {
        Capabilities = new CapabilitySet(capabilities);
    }

    public CapabilitySet Capabilities { get; }

    internal bool Supports(string capability) =>
        Capabilities.Contains(capability);
}

using Asura.Application;
using Asura.Core;

namespace Asura.Browser;

public sealed class BrowserPanelSessionFactory : IBrowserPanelSessionFactory
{
    private readonly TimeProvider _timeProvider;

    public BrowserPanelSessionFactory()
        : this(BrowserCapabilityProfile.Production, TimeProvider.System)
    {
    }

    public BrowserPanelSessionFactory(BrowserCapabilityProfile capabilityProfile)
        : this(capabilityProfile, TimeProvider.System)
    {
    }

    internal BrowserPanelSessionFactory(TimeProvider timeProvider)
        : this(BrowserCapabilityProfile.Production, timeProvider)
    {
    }

    internal BrowserPanelSessionFactory(
        BrowserCapabilityProfile capabilityProfile,
        TimeProvider timeProvider)
    {
        CapabilityProfile = capabilityProfile
            ?? throw new ArgumentNullException(nameof(capabilityProfile));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public BrowserCapabilityProfile CapabilityProfile { get; }

    public CapabilitySet Capabilities => CapabilityProfile.Capabilities;

    public ValueTask<IBrowserPanelSession> CreateAsync(
        SessionId sessionId,
        BrowserAddress initialAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialAddress);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IBrowserPanelSession>(
            new BrowserPanelSession(
                sessionId,
                initialAddress,
                _timeProvider,
                CapabilityProfile));
    }
}

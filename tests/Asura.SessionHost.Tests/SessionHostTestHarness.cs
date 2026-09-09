using Asura.Application;
using Asura.Core;
using Asura.SessionHost;

namespace Asura.SessionHost.Tests;

internal sealed class SessionHostTestHarness : IAsyncDisposable
{
    public SessionHostTestHarness(
        HostMode mode = HostMode.Desktop,
        int eventRetention = 256)
    {
        Clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        Factory = new FakeTerminalSessionFactory();
        FileFactory = new FakeFilePanelSessionFactory();
        ISessionLifecyclePolicy policy = mode == HostMode.Desktop
            ? new DesktopLifecyclePolicy()
            : new ServerLifecyclePolicy();
        Client = new InMemorySessionHostClient(
            Factory,
            policy,
            Clock,
            eventRetention,
            FileFactory);
        ClientId = new ClientId("test-client");
        WindowId = new WindowInstanceId("window-1");
        WorkspaceId = new WorkspaceInstanceId("workspace-1");
        TabId = new TabInstanceId("tab-1");
        PanelId = new PanelInstanceId("panel-1");
        SessionId = new SessionId("session-1");
    }

    public ManualTimeProvider Clock { get; }

    public FakeTerminalSessionFactory Factory { get; }

    public FakeFilePanelSessionFactory FileFactory { get; }

    public InMemorySessionHostClient Client { get; }

    public ClientId ClientId { get; }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstanceId WorkspaceId { get; }

    public TabInstanceId TabId { get; }

    public PanelInstanceId PanelId { get; }

    public SessionId SessionId { get; }

    public async ValueTask<SessionSnapshot> OpenAsync(
        OperationContext? context = null,
        SessionId? sessionId = null,
        PanelInstanceId? panelId = null,
        TerminalLaunchRequest? launch = null,
        WorkspaceInstanceId? workspaceId = null,
        TabInstanceId? tabId = null)
    {
        var id = sessionId ?? SessionId;
        var result = await Client.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                id,
                new SessionOwner(
                    HostMode.Desktop,
                    WindowId,
                    workspaceId ?? WorkspaceId,
                    tabId ?? TabId,
                    panelId ?? PanelId),
                "test terminal",
                launch ?? new TerminalLaunchRequest("/tmp")),
            context ?? HumanContext(),
            CancellationToken.None);
        return result.Value();
    }

    public async ValueTask<AttachmentResult> AttachAsync(
        AttachmentKind kind = AttachmentKind.Interactive,
        ClientId? clientId = null,
        SessionId? sessionId = null)
    {
        var result = await Client.AttachAsync(
            new AttachSessionRequest(
                sessionId ?? SessionId,
                clientId ?? ClientId,
                kind,
                new ViewportDescriptor(800, 600, 2),
                AllCapabilities()),
            HumanContext(clientId),
            CancellationToken.None);
        return result.Value();
    }

    public OperationContext HumanContext(
        ClientId? clientId = null,
        long? expectedRevision = null,
        IdempotencyKey? idempotencyKey = null,
        DateTimeOffset? deadline = null)
    {
        var actualClientId = clientId ?? ClientId;
        return new OperationContext(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId(actualClientId.Value),
                ActorKind.Human,
                "Test user",
                actualClientId),
            expectedRevision,
            idempotencyKey,
            CancellationId.New(),
            deadline);
    }

    public static OperationContext AgentContext() =>
        new(
            RequestId.New(),
            new ActorDescriptor(new ActorId("agent-1"), ActorKind.Agent, "Test agent"),
            CancellationId: CancellationId.New());

    public static CapabilitySet AllCapabilities() => new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.AttachInteractive,
        SessionCapabilities.InputLease,
        SessionCapabilities.NativeRenderer,
        SessionCapabilities.TerminalAgentInputBarrier,
        SessionCapabilities.TerminalFocus,
        SessionCapabilities.TerminalReadScreen,
        SessionCapabilities.TerminalResize,
        SessionCapabilities.TerminalWrite,
        SessionCapabilities.TerminalSendKeys,
        SessionCapabilities.TerminalSendChord,
        SessionCapabilities.TerminalEnter,
        SessionCapabilities.TerminalInterrupt,
        SessionCapabilities.TerminalWait,
        SessionCapabilities.TerminalMouse,
        SessionCapabilities.TerminalScrollback,
        SessionCapabilities.TerminalClearScrollback,
        SessionCapabilities.TerminalFind,
        SessionCapabilities.TerminalSelection,
        SessionCapabilities.TerminalPaste,
    ]);

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}

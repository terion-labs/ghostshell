using Asura.Core;

namespace Asura.Application;

/// <summary>
/// The closed set of embedded-browser operations that can be prepared for
/// agent authorization. Provider-defined names and arguments cannot extend it.
/// </summary>
public abstract record AgentBrowserRequest
{
    private AgentBrowserRequest()
    {
    }

    public sealed record ReadState(SessionId SessionId) : AgentBrowserRequest;

    public sealed record Snapshot(
        SessionId SessionId,
        BrowserSnapshotQuery? Query = null) : AgentBrowserRequest;

    public sealed record Wait(BrowserWaitRequest Value) : AgentBrowserRequest;

    public sealed record Click(BrowserElementClickRequest Value) : AgentBrowserRequest;

    public sealed record Fill(BrowserElementFillRequest Value) : AgentBrowserRequest;

    public sealed record Check(BrowserElementCheckRequest Value) : AgentBrowserRequest;

    public sealed record Mouse(BrowserMouseRequest Value) : AgentBrowserRequest;

    public sealed record Key(BrowserKeyRequest Value) : AgentBrowserRequest;

    public sealed record Scroll(BrowserScrollRequest Value) : AgentBrowserRequest;

    public sealed record Evaluate(BrowserEvaluateRequest Value) : AgentBrowserRequest;

    public sealed record Navigate(BrowserNavigateRequest Value) : AgentBrowserRequest;

    public sealed record Back(SessionId SessionId) : AgentBrowserRequest;

    public sealed record Forward(SessionId SessionId) : AgentBrowserRequest;

    public sealed record Reload(SessionId SessionId) : AgentBrowserRequest;

    public sealed record Stop(SessionId SessionId) : AgentBrowserRequest;
}

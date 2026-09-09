using Asura.Application;
using Asura.Core;

namespace Asura.App;

/// <summary>
/// A live attachment between a browser session and the renderer showing it,
/// held by the panel for as long as the panel exists.
///
/// It is deliberately not held by the control that draws the panel. Rearranging
/// panels rebuilds those controls, and an attachment that went with them would
/// make a session's lifetime a function of where a panel sits on screen — which
/// is wrong on its face, and wrong twice over for a backgrounded workspace or a
/// headless run, where nothing sits anywhere.
/// </summary>
internal sealed class BrowserRendererAttachment(
    ISessionHostClient client,
    ClientId clientId,
    SessionId sessionId,
    AttachmentId attachmentId)
{
    private int _released;

    public ISessionHostClient Client { get; } = client;

    public ClientId ClientId { get; } = clientId;

    public SessionId SessionId { get; } = sessionId;

    public AttachmentId AttachmentId { get; } = attachmentId;

    /// <summary>
    /// Whether this attachment is the one a host with these bindings would make,
    /// and so can be adopted instead of made again.
    /// </summary>
    public bool Matches(ISessionHostClient? client, ClientId? clientId, SessionId? sessionId) =>
        Volatile.Read(ref _released) == 0
        && ReferenceEquals(Client, client)
        && ClientId == clientId
        && SessionId == sessionId;

    /// <summary>
    /// Ends the attachment. Called when the panel is gone, and only then.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _ = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        try
        {
            _ = await Client.DetachAsync(
                new DetachSessionRequest(AttachmentId, SessionId),
                OperationContext.ForHuman(ClientId),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace(
                "browser.attachment.release-failed",
                exception);
        }
    }
}

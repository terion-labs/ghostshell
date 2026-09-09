using Asura.Application;
using Asura.Core;

namespace Asura.App;

/// <summary>
/// Identifies the one hosted file session represented by a presentation client.
/// </summary>
public sealed record HostedFilePanelClientOptions
{
    public HostedFilePanelClientOptions(
        SessionId sessionId,
        SessionOwner owner,
        ClientId clientId,
        string title,
        FilePanelLocation initialLocation,
        TimeSpan? operationTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        SessionId = sessionId;
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ClientId = clientId;
        Title = title;
        InitialLocation = initialLocation
            ?? throw new ArgumentNullException(nameof(initialLocation));
        RequiredProfileId = new FileProviderProfileId(initialLocation.ProviderProfileId);
        OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        if (OperationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                "The file operation timeout must be positive.");
        }
    }

    private HostedFilePanelClientOptions(
        SessionId sessionId,
        SessionOwner owner,
        ClientId clientId,
        string title,
        FileProviderProfileId? requiredProfileId,
        TimeSpan? operationTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        SessionId = sessionId;
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ClientId = clientId;
        Title = title;
        RequiredProfileId = requiredProfileId;
        OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        if (OperationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                "The file operation timeout must be positive.");
        }
    }

    public static HostedFilePanelClientOptions Deferred(
        SessionId sessionId,
        SessionOwner owner,
        ClientId clientId,
        string title,
        FileProviderProfileId? requiredProfileId = null,
        TimeSpan? operationTimeout = null) =>
        new(
            sessionId,
            owner,
            clientId,
            title,
            requiredProfileId,
            operationTimeout);

    public SessionId SessionId { get; }

    public SessionOwner Owner { get; }

    public ClientId ClientId { get; }

    public string Title { get; }

    public FilePanelLocation? InitialLocation { get; }

    public FileProviderProfileId? RequiredProfileId { get; }

    public TimeSpan OperationTimeout { get; }
}

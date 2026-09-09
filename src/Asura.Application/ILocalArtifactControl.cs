namespace Asura.Application;

/// <summary>
/// Provides bounded inspection and deletion of disposable, application-owned
/// files. Durable application data is outside this boundary.
/// </summary>
public interface ILocalArtifactControl
{
    ValueTask<LocalArtifactControlResult<LocalArtifactInventory>> InspectAsync(
        CancellationToken cancellationToken);

    ValueTask<LocalArtifactControlResult<LocalArtifactClearReceipt>> ClearAsync(
        LocalArtifactKind kind,
        CancellationToken cancellationToken);
}

using Asura.Core;

namespace Asura.Application;

public readonly record struct FilePanelTransferId
{
    public FilePanelTransferId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A file transfer ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static FilePanelTransferId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public enum FilePanelTransferOperation
{
    Copy,
    Move,
}

public enum FilePanelConflictPolicy
{
    Fail,
    Skip,
    Replace,
    KeepBoth,
}

public enum FilePanelTransferState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    Skipped,
}

public sealed record FilePanelTransferRequest
{
    public FilePanelTransferRequest(
        FilePanelLocation source,
        FilePanelLocation destination,
        FilePanelTransferOperation operation,
        FilePanelConflictPolicy conflictPolicy)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }

        if (!Enum.IsDefined(conflictPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy), conflictPolicy, null);
        }

        Operation = operation;
        ConflictPolicy = conflictPolicy;
    }

    public FilePanelLocation Source { get; }

    public FilePanelLocation Destination { get; }

    public FilePanelTransferOperation Operation { get; }

    public FilePanelConflictPolicy ConflictPolicy { get; }
}

public sealed record FilePanelTransferSnapshot(
    FilePanelTransferId Id,
    FilePanelTransferRequest Request,
    FilePanelLocation EffectiveDestination,
    FilePanelTransferState State,
    string Stage,
    long BytesTransferred,
    long? TotalBytes,
    FilePanelError? Error,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    public bool CancellationRequested { get; init; }

    public bool CanCancel =>
        !CancellationRequested
        && State is (FilePanelTransferState.Queued or FilePanelTransferState.Running);

    public bool CanRetry => State is FilePanelTransferState.Failed or FilePanelTransferState.Cancelled;
}

public interface IFileTransferQueueClient
{
    IReadOnlyList<FilePanelTransferSnapshot> Transfers { get; }

    event EventHandler? TransfersChanged;

    ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<Unit>> CancelAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken);
}

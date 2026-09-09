namespace Asura.Files;

/// <summary>
/// Describes a copy or move within one provider profile. Transfers stream through the provider
/// and remain cancellable without imposing an application-level file-size ceiling.
/// </summary>
public sealed record FileTransferRequest
{
    public FileTransferRequest(
        FileLocation source,
        FileLocation destination,
        FileTransferKind kind,
        int bufferSize,
        FileMutationPrecondition destinationPrecondition)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        ArgumentNullException.ThrowIfNull(destinationPrecondition);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        Source = source;
        Destination = destination;
        Kind = kind;
        BufferSize = bufferSize;
        DestinationPrecondition = destinationPrecondition;
    }

    public FileLocation Source { get; }

    public FileLocation Destination { get; }

    public FileTransferKind Kind { get; }

    public int BufferSize { get; }

    public FileMutationPrecondition DestinationPrecondition { get; }
}

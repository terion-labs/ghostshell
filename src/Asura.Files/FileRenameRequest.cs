namespace Asura.Files;

public sealed record FileRenameRequest
{
    public FileRenameRequest(
        FileLocation source,
        FileLocation destination,
        FileMutationPrecondition destinationPrecondition)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(destinationPrecondition);
        Source = source;
        Destination = destination;
        DestinationPrecondition = destinationPrecondition;
    }

    public FileLocation Source { get; }

    public FileLocation Destination { get; }

    public FileMutationPrecondition DestinationPrecondition { get; }
}

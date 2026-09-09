namespace Asura.Files;

public sealed record FileCreateDirectoryRequest
{
    public FileCreateDirectoryRequest(
        FileLocation location,
        FileMutationPrecondition precondition)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(precondition);
        Location = location;
        Precondition = precondition;
    }

    public FileLocation Location { get; }

    public FileMutationPrecondition Precondition { get; }
}

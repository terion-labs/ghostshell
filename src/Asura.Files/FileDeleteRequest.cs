namespace Asura.Files;

public sealed record FileDeleteRequest
{
    public FileDeleteRequest(
        FileLocation location,
        bool recursive,
        FileMutationPrecondition precondition)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(precondition);
        Location = location;
        Recursive = recursive;
        Precondition = precondition;
    }

    public FileLocation Location { get; }

    public bool Recursive { get; }

    public FileMutationPrecondition Precondition { get; }
}

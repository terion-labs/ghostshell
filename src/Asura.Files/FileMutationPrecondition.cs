namespace Asura.Files;

/// <summary>Expresses the destination state a mutation is allowed to replace.</summary>
public abstract record FileMutationPrecondition
{
    private FileMutationPrecondition()
    {
    }

    public sealed record Any : FileMutationPrecondition;

    public sealed record MustNotExist : FileMutationPrecondition;

    public sealed record MustExist : FileMutationPrecondition;

    public sealed record VersionMatches(FileVersion Version) : FileMutationPrecondition;
}

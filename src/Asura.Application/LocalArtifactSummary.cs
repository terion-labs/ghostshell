namespace Asura.Application;

public sealed record LocalArtifactSummary
{
    public LocalArtifactSummary(
        LocalArtifactKind kind,
        long fileCount,
        long totalBytes)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (fileCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fileCount),
                "The artifact file count cannot be negative.");
        }

        if (totalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalBytes),
                "The artifact byte count cannot be negative.");
        }

        Kind = kind;
        FileCount = fileCount;
        TotalBytes = totalBytes;
    }

    public LocalArtifactKind Kind { get; }

    public long FileCount { get; }

    public long TotalBytes { get; }
}

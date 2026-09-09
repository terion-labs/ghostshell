namespace Asura.Application;

public sealed record LocalArtifactClearReceipt
{
    public LocalArtifactClearReceipt(
        LocalArtifactKind kind,
        long filesRemoved,
        long bytesRemoved)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (filesRemoved < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(filesRemoved),
                "The removed file count cannot be negative.");
        }

        if (bytesRemoved < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesRemoved),
                "The removed byte count cannot be negative.");
        }

        Kind = kind;
        FilesRemoved = filesRemoved;
        BytesRemoved = bytesRemoved;
    }

    public LocalArtifactKind Kind { get; }

    public long FilesRemoved { get; }

    public long BytesRemoved { get; }
}

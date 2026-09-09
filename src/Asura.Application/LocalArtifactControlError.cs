namespace Asura.Application;

/// <summary>
/// A path-free local artifact failure suitable for presentation to the user.
/// Removal counters are populated when a non-cancellable mutation fails after
/// deleting part of its validated plan.
/// </summary>
public sealed record LocalArtifactControlError
{
    public LocalArtifactControlError(
        LocalArtifactControlErrorCode code,
        string message,
        long filesRemoved = 0,
        long bytesRemoved = 0)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
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

        Code = code;
        Message = message;
        FilesRemoved = filesRemoved;
        BytesRemoved = bytesRemoved;
    }

    public LocalArtifactControlErrorCode Code { get; }

    public string Message { get; }

    public long FilesRemoved { get; }

    public long BytesRemoved { get; }
}

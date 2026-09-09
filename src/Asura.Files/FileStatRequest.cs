namespace Asura.Files;

public sealed record FileStatRequest
{
    public FileStatRequest(FileLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        Location = location;
    }

    public FileLocation Location { get; }
}

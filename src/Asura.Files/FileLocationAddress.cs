namespace Asura.Files;

public abstract record FileLocationAddress
{
    private FileLocationAddress()
    {
    }

    public sealed record Hierarchical(FilePath Path) : FileLocationAddress;

    public sealed record Object(FileObjectKey Key) : FileLocationAddress;

    public sealed record ContainerRoot : FileLocationAddress;
}

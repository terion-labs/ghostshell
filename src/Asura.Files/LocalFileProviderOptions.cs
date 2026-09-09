namespace Asura.Files;

public sealed record LocalFileProviderOptions
{
    public LocalFileProviderOptions(
        FileProviderProfileId profileId,
        FileAuthority authority,
        string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ProfileId = profileId;
        Authority = authority;
        RootPath = rootPath;
    }

    public FileProviderProfileId ProfileId { get; }

    public FileAuthority Authority { get; }

    public string RootPath { get; }
}

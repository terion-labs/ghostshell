namespace Asura.Files;

/// <summary>
/// Identifies an entry without borrowing any provider's path-string syntax. A version is an
/// opaque provider token: reads target that version and mutations treat it as a precondition.
/// </summary>
public sealed record FileLocation
{
    public FileLocation(
        FileProviderProfileId providerProfileId,
        FileAuthority? authority,
        FilePath path,
        FileVersion? version = null)
        : this(
            providerProfileId,
            authority,
            new FileLocationAddress.Hierarchical(path),
            version)
    {
    }

    private FileLocation(
        FileProviderProfileId providerProfileId,
        FileAuthority? authority,
        FileLocationAddress address,
        FileVersion? version)
    {
        ArgumentNullException.ThrowIfNull(address);
        ProviderProfileId = providerProfileId;
        Authority = authority;
        Address = address;
        Version = version;
    }

    public FileProviderProfileId ProviderProfileId { get; }

    public FileAuthority? Authority { get; }

    public FileLocationAddress Address { get; }

    public FilePath Path => Address is FileLocationAddress.Hierarchical hierarchical
        ? hierarchical.Path
        : throw new InvalidOperationException("This location does not contain a hierarchical path.");

    public FileObjectKey? ObjectKey => Address is FileLocationAddress.Object value
        ? value.Key
        : null;

    public bool IsContainerRoot => Address is FileLocationAddress.ContainerRoot;

    public FileVersion? Version { get; }

    public FileLocation Child(FilePathSegment segment) =>
        Address is FileLocationAddress.Hierarchical hierarchical
            ? new(ProviderProfileId, Authority, hierarchical.Path.Append(segment))
            : throw new InvalidOperationException("Only a hierarchical location can have a path child.");

    public FileLocation WithVersion(FileVersion? version) =>
        new(ProviderProfileId, Authority, Address, version);

    public static FileLocation ForObjectKey(
        FileProviderProfileId providerProfileId,
        FileAuthority authority,
        FileObjectKey objectKey,
        FileVersion? version = null) =>
        new(
            providerProfileId,
            authority,
            new FileLocationAddress.Object(objectKey),
            version);

    public static FileLocation ForContainerRoot(
        FileProviderProfileId providerProfileId,
        FileAuthority authority,
        FileVersion? version = null) =>
        new(
            providerProfileId,
            authority,
            new FileLocationAddress.ContainerRoot(),
            version);
}

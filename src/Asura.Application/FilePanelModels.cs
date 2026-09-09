using System.Collections.Immutable;

namespace Asura.Application;

public enum FileProviderFamily
{
    Posix,
    Windows,
    S3,
    Sftp,
    Ftp,
    Smb,
    WebDav,
}

[Flags]
public enum FilePanelCapability : ulong
{
    None = 0,
    List = 1UL << 0,
    Stat = 1UL << 1,
    RangedRead = 1UL << 2,
    StreamingWrite = 1UL << 3,
    CreateDirectory = 1UL << 4,
    CreateContainer = 1UL << 5,
    Rename = 1UL << 6,
    Copy = 1UL << 7,
    Move = 1UL << 8,
    Delete = 1UL << 9,
    Search = 1UL << 10,
    Watch = 1UL << 11,
    Checksum = 1UL << 12,
    ResumableTransfer = 1UL << 13,
    Versioning = 1UL << 14,
    Symlinks = 1UL << 15,
    Permissions = 1UL << 16,
    AccessControlLists = 1UL << 17,
    AtomicReplace = 1UL << 18,
    ServerSideCopy = 1UL << 19,
    Pagination = 1UL << 20,
    GovernedCreateDirectory = 1UL << 21,
    GovernedDelete = 1UL << 22,
    GovernedRename = 1UL << 23,
    GovernedCreateFile = 1UL << 24,
    GovernedReplaceFile = 1UL << 25,
    GovernedCopySource = 1UL << 26,
    GovernedCopy = 1UL << 27,
}

public sealed record FileProviderProfileDescriptor
{
    public FileProviderProfileDescriptor(
        string Id,
        string Name,
        FileProviderFamily Family,
        FilePanelLocation Root,
        FilePanelCapability Capabilities,
        int MaximumPageSize,
        long MaximumPreviewBytes,
        FilePanelLocation? StartLocation = null,
        bool RequiresHostTransferForPreview = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentNullException.ThrowIfNull(Root);
        if (!Enum.IsDefined(Family))
        {
            throw new ArgumentOutOfRangeException(nameof(Family), Family, null);
        }

        if (!string.Equals(Root.ProviderProfileId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The profile descriptor and root location must use the same profile ID.",
                nameof(Root));
        }

        if ((Capabilities & ~AllCapabilities) != FilePanelCapability.None)
        {
            throw new ArgumentOutOfRangeException(nameof(Capabilities), Capabilities, null);
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPreviewBytes);
        this.Id = Id;
        this.Name = Name.Trim();
        this.Family = Family;
        this.Root = Root;
        this.Capabilities = Capabilities;
        this.MaximumPageSize = MaximumPageSize;
        this.MaximumPreviewBytes = MaximumPreviewBytes;
        this.RequiresHostTransferForPreview = RequiresHostTransferForPreview
            || Family is FileProviderFamily.S3
                or FileProviderFamily.Sftp
                or FileProviderFamily.Ftp
                or FileProviderFamily.Smb
                or FileProviderFamily.WebDav;
        if (StartLocation is not null && !string.Equals(StartLocation.ProviderProfileId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The profile descriptor and start location must use the same profile ID.",
                nameof(StartLocation));
        }

        this.StartLocation = StartLocation ?? Root;
    }

    public string Id { get; }

    public string Name { get; }

    public FileProviderFamily Family { get; }

    public FilePanelLocation Root { get; }

    /// <summary>
    /// Where a panel opens on this provider, which need not be its root. The
    /// local provider reaches the whole filesystem but opens at the user's
    /// home folder, because that is where their files are.
    /// </summary>
    public FilePanelLocation StartLocation { get; }

    public FilePanelCapability Capabilities { get; }

    public int MaximumPageSize { get; }

    public long MaximumPreviewBytes { get; }

    /// <summary>
    /// Previewing this provider's files materializes content on the host. The
    /// viewer uses this transport fact—not the provider's protocol name—to
    /// apply its size gate and explicit-load toggle.
    /// </summary>
    public bool RequiresHostTransferForPreview { get; }

    public override string ToString() => Name;

    private const FilePanelCapability AllCapabilities =
        FilePanelCapability.List
        | FilePanelCapability.Stat
        | FilePanelCapability.RangedRead
        | FilePanelCapability.StreamingWrite
        | FilePanelCapability.CreateDirectory
        | FilePanelCapability.CreateContainer
        | FilePanelCapability.Rename
        | FilePanelCapability.Copy
        | FilePanelCapability.Move
        | FilePanelCapability.Delete
        | FilePanelCapability.Search
        | FilePanelCapability.Watch
        | FilePanelCapability.Checksum
        | FilePanelCapability.ResumableTransfer
        | FilePanelCapability.Versioning
        | FilePanelCapability.Symlinks
        | FilePanelCapability.Permissions
        | FilePanelCapability.AccessControlLists
        | FilePanelCapability.AtomicReplace
        | FilePanelCapability.ServerSideCopy
        | FilePanelCapability.Pagination
        | FilePanelCapability.GovernedCreateDirectory
        | FilePanelCapability.GovernedDelete
        | FilePanelCapability.GovernedRename
        | FilePanelCapability.GovernedCreateFile
        | FilePanelCapability.GovernedReplaceFile
        | FilePanelCapability.GovernedCopySource
        | FilePanelCapability.GovernedCopy;
}

public enum FilePanelEntryKind
{
    File,
    Directory,
    Link,
    Other,
}

public sealed record FilePanelEntry
{
    public FilePanelEntry(
        FilePanelLocation Location,
        string Name,
        FilePanelEntryKind Kind,
        long? Size,
        DateTimeOffset? LastModifiedAt,
        bool IsHidden)
    {
        ArgumentNullException.ThrowIfNull(Location);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null);
        }

        if (Size is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Size), Size, null);
        }

        this.Location = Location;
        this.Name = Name;
        this.Kind = Kind;
        this.Size = Size;
        this.LastModifiedAt = LastModifiedAt;
        this.IsHidden = IsHidden;
    }

    public FilePanelLocation Location { get; }

    public string Name { get; }

    public FilePanelEntryKind Kind { get; }

    public long? Size { get; }

    public DateTimeOffset? LastModifiedAt { get; }

    public bool IsHidden { get; }
}

public sealed record FilePanelPage
{
    public FilePanelPage(IEnumerable<FilePanelEntry> entries, string? continuationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = [.. entries];
        ContinuationToken = continuationToken;
    }

    public ImmutableArray<FilePanelEntry> Entries { get; }

    public string? ContinuationToken { get; }
}

public enum FilePanelPreviewKind
{
    Text,
    StructuredText,
    Image,
    Hex,

    /// <summary>
    /// A database file. Its bytes are not the preview: the presentation opens
    /// it with a database engine, which needs the whole file by path rather
    /// than the bounded head this preview carries.
    /// </summary>
    Database,

    /// <summary>A PDF document, rendered a page at a time from the whole file.</summary>
    Pdf,

    /// <summary>A web page, rendered by the embedded browser.</summary>
    Html,
}

public sealed record FilePanelPreview
{
    private readonly byte[] _content;

    public FilePanelPreview(
        FilePanelLocation location,
        FilePanelPreviewKind kind,
        string mediaType,
        ReadOnlySpan<byte> content,
        bool isTruncated)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        Location = location;
        Kind = kind;
        MediaType = mediaType;
        _content = content.ToArray();
        IsTruncated = isTruncated;
    }

    public FilePanelLocation Location { get; }

    public FilePanelPreviewKind Kind { get; }

    public string MediaType { get; }

    public ReadOnlyMemory<byte> Content => _content;

    public bool IsTruncated { get; }
}

public enum FilePanelMutationPreconditionKind
{
    Any,
    MustNotExist,
    MustExist,
    VersionMatches,
}

public sealed record FilePanelMutationPrecondition
{
    public FilePanelMutationPrecondition(
        FilePanelMutationPreconditionKind kind,
        string? version = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        if (kind == FilePanelMutationPreconditionKind.VersionMatches)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }
        else if (version is not null)
        {
            throw new ArgumentException(
                "Only a version-matching precondition may include a version.",
                nameof(version));
        }

        Kind = kind;
        Version = version;
    }

    public FilePanelMutationPreconditionKind Kind { get; }

    public string? Version { get; }

    public static FilePanelMutationPrecondition Any { get; } = new(
        FilePanelMutationPreconditionKind.Any);

    public static FilePanelMutationPrecondition MustNotExist { get; } = new(
        FilePanelMutationPreconditionKind.MustNotExist);

    public static FilePanelMutationPrecondition MustExist { get; } = new(
        FilePanelMutationPreconditionKind.MustExist);

    public static FilePanelMutationPrecondition VersionMatches(string version) => new(
        FilePanelMutationPreconditionKind.VersionMatches,
        version);
}

public sealed record FilePanelListRequest
{
    public FilePanelListRequest(
        FilePanelLocation Location,
        int PageSize,
        string? ContinuationToken,
        bool ShowHidden)
    {
        ArgumentNullException.ThrowIfNull(Location);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
        this.Location = Location;
        this.PageSize = PageSize;
        this.ContinuationToken = ContinuationToken;
        this.ShowHidden = ShowHidden;
    }

    public FilePanelLocation Location { get; }

    public int PageSize { get; }

    public string? ContinuationToken { get; }

    public bool ShowHidden { get; }
}

public sealed record FilePanelPreviewRequest
{
    public FilePanelPreviewRequest(FilePanelLocation Location, long MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(Location);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBytes);
        this.Location = Location;
        this.MaximumBytes = MaximumBytes;
    }

    public FilePanelLocation Location { get; }

    public long MaximumBytes { get; }
}

public sealed record FilePanelCreateDirectoryRequest
{
    public FilePanelCreateDirectoryRequest(
        FilePanelLocation Location,
        FilePanelMutationPrecondition Precondition)
    {
        this.Location = Location ?? throw new ArgumentNullException(nameof(Location));
        this.Precondition = Precondition ?? throw new ArgumentNullException(nameof(Precondition));
    }

    public FilePanelLocation Location { get; }

    public FilePanelMutationPrecondition Precondition { get; }
}

public sealed record FilePanelRenameRequest
{
    public FilePanelRenameRequest(
        FilePanelLocation Source,
        FilePanelLocation Destination,
        FilePanelMutationPrecondition DestinationPrecondition)
    {
        this.Source = Source ?? throw new ArgumentNullException(nameof(Source));
        this.Destination = Destination ?? throw new ArgumentNullException(nameof(Destination));
        this.DestinationPrecondition = DestinationPrecondition
            ?? throw new ArgumentNullException(nameof(DestinationPrecondition));
    }

    public FilePanelLocation Source { get; }

    public FilePanelLocation Destination { get; }

    public FilePanelMutationPrecondition DestinationPrecondition { get; }
}

public sealed record FilePanelDeleteRequest
{
    public FilePanelDeleteRequest(
        FilePanelLocation Location,
        bool Recursive,
        FilePanelMutationPrecondition Precondition)
    {
        this.Location = Location ?? throw new ArgumentNullException(nameof(Location));
        this.Recursive = Recursive;
        this.Precondition = Precondition ?? throw new ArgumentNullException(nameof(Precondition));
    }

    public FilePanelLocation Location { get; }

    public bool Recursive { get; }

    public FilePanelMutationPrecondition Precondition { get; }
}

public sealed record FilePanelDeleteReceipt
{
    public FilePanelDeleteReceipt(FilePanelLocation DeletedLocation, bool WasDirectory)
    {
        this.DeletedLocation = DeletedLocation
            ?? throw new ArgumentNullException(nameof(DeletedLocation));
        this.WasDirectory = WasDirectory;
    }

    public FilePanelLocation DeletedLocation { get; }

    public bool WasDirectory { get; }
}

public sealed record FilePanelTextWriteRequest
{
    public FilePanelTextWriteRequest(
        FilePanelLocation location,
        string content,
        FilePanelMutationPrecondition precondition)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Precondition = precondition ?? throw new ArgumentNullException(nameof(precondition));
    }

    public FilePanelLocation Location { get; }

    public string Content { get; }

    public FilePanelMutationPrecondition Precondition { get; }
}

public sealed record FilePanelTextWriteReceipt
{
    public FilePanelTextWriteReceipt(
        FilePanelLocation destination,
        long bytesWritten,
        bool replacedExisting)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesWritten);
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        BytesWritten = bytesWritten;
        ReplacedExisting = replacedExisting;
    }

    public FilePanelLocation Destination { get; }

    public long BytesWritten { get; }

    public bool ReplacedExisting { get; }
}

public sealed record FilePanelCopyRequest
{
    public FilePanelCopyRequest(
        FilePanelLocation source,
        FilePanelLocation destination,
        long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        MaximumBytes = maximumBytes;
    }

    public FilePanelLocation Source { get; }

    public FilePanelLocation Destination { get; }

    public long MaximumBytes { get; }
}

public sealed record FilePanelCopyReceipt
{
    public FilePanelCopyReceipt(
        FilePanelLocation source,
        FilePanelLocation destination,
        long bytesCopied)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesCopied);
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        BytesCopied = bytesCopied;
    }

    public FilePanelLocation Source { get; }

    public FilePanelLocation Destination { get; }

    public long BytesCopied { get; }
}

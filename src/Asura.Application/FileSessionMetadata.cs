using System.Text;

namespace Asura.Application;

/// <summary>
/// Immutable provider scope captured when a File Viewer session opens. The root is authority,
/// not presentation state: governed file tools may only resolve descendants of this location.
/// </summary>
public sealed record FileSessionMetadata
{
    public const int MaximumTrustedRootSegments = 64;
    public const int MaximumTrustedRootSegmentBytes = 255;
    public const int MaximumTrustedRootBytes = 4 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

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

    public FileSessionMetadata(
        FilePanelLocation trustedRoot,
        FilePanelCapability capabilities,
        int maximumListPageSize,
        long maximumPreviewBytes)
    {
        TrustedRoot = trustedRoot ?? throw new ArgumentNullException(nameof(trustedRoot));
        if ((capabilities & ~AllCapabilities) != FilePanelCapability.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                capabilities,
                "File-session capabilities contain an unknown flag.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumListPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPreviewBytes);
        ValidateRootText(trustedRoot);

        Capabilities = capabilities;
        MaximumListPageSize = maximumListPageSize;
        MaximumPreviewBytes = maximumPreviewBytes;
    }

    public FilePanelLocation TrustedRoot { get; }

    public FilePanelCapability Capabilities { get; }

    public int MaximumListPageSize { get; }

    public long MaximumPreviewBytes { get; }

    private static void ValidateRootText(FilePanelLocation root)
    {
        if (root.Authority is { } authority)
        {
            _ = GetStrictUtf8ByteCount(authority, nameof(root));
        }

        switch (root.Address)
        {
            case FilePanelAddress.Hierarchical hierarchical:
                if (hierarchical.Path.Segments.Length > MaximumTrustedRootSegments)
                {
                    throw new ArgumentException(
                        $"A file-session root cannot contain more than "
                        + $"{MaximumTrustedRootSegments} path segments.",
                        nameof(root));
                }

                var totalBytes = 0;
                foreach (var segment in hierarchical.Path.Segments)
                {
                    var byteCount = GetStrictUtf8ByteCount(segment.Value, nameof(root));
                    if (byteCount > MaximumTrustedRootSegmentBytes)
                    {
                        throw new ArgumentException(
                            "A file-session root segment is too large.",
                            nameof(root));
                    }

                    totalBytes = checked(totalBytes + byteCount + 1);
                }

                if (totalBytes > MaximumTrustedRootBytes)
                {
                    throw new ArgumentException(
                        "The file-session root is too large.",
                        nameof(root));
                }

                break;
            case FilePanelAddress.ObjectKey objectKey:
                if (GetStrictUtf8ByteCount(objectKey.Key, nameof(root))
                    > MaximumTrustedRootBytes)
                {
                    throw new ArgumentException(
                        "The file-session object root is too large.",
                        nameof(root));
                }

                break;
            case FilePanelAddress.ContainerRoot:
                break;
            default:
                throw new ArgumentException(
                    "The file-session root address is unsupported.",
                    nameof(root));
        }

        if (root.Version is { } version)
        {
            _ = GetStrictUtf8ByteCount(version, nameof(root));
        }
    }

    private static int GetStrictUtf8ByteCount(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "File-session metadata must contain valid Unicode text.",
                parameterName,
                exception);
        }
    }
}

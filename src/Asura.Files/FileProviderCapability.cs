namespace Asura.Files;

[Flags]
public enum FileProviderCapability : ulong
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
}

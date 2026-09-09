using System.Runtime.CompilerServices;
using SharpCompress.Archives.Zip;
using SharpCompress.Common.Zip;

namespace Asura.Previews;

/// <summary>
/// The pinned SharpCompress package exposes its central-directory iterator only
/// to subclasses, but its internal constructors prevent external inheritance.
/// Keep this one compatibility call here instead of maintaining a library fork.
/// Entries must be consumed within the archive lifetime, without concurrent
/// enumeration or mutation. Never fall back to Entries: it retains skipped rows.
/// </summary>
internal static class ZipDirectoryEnumeration
{
    internal static IEnumerable<ZipArchiveEntry> EnumerateEntriesUncached(this ZipArchive archive) =>
        LoadEntries(archive, archive.Volumes);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "LoadEntries")]
    private static extern IEnumerable<ZipArchiveEntry> LoadEntries(
        ZipArchive archive, IEnumerable<ZipVolume> volumes);
}

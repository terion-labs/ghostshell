using System.Formats.Tar;
using System.IO.Compression;
using Asura.Application.Previews;
using Asura.Previews;

namespace Asura.Previews.Tests;

/// <summary>
/// Listing archives built here rather than checked in, so a fixture is exactly
/// what the test says it is.
/// </summary>
public sealed class ArchiveTableOfContentsTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("asura-archive-preview").FullName;

    private readonly ArchiveTableOfContents _reader = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("bundle.zip")]
    [InlineData("BUNDLE.ZIP")]
    [InlineData("library.jar")]
    [InlineData("source.tar")]
    [InlineData("source.tar.gz")]
    [InlineData("source.tgz")]
    public void Archives_are_claimed(string name) => Assert.True(_reader.Claims(name));

    [Theory]
    [InlineData("notes.md")]
    [InlineData("photo.jpg")]
    public void Other_files_are_not(string name) => Assert.False(_reader.Claims(name));

    [Fact]
    public async Task A_zip_is_listed_from_its_index()
    {
        var path = WriteZip("bundle.zip");

        var entries = await _reader.ReadAsync(
            Asura.Application.FilePreviewContent.FromLocalFile(path),
            Path.GetFileName(path),
            100,
            CancellationToken.None);

        Assert.NotNull(entries);
        Assert.Equal(
            ["docs/", "docs/guide.md", "src/main.c"],
            entries!.Select(entry => entry.Path).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var guide = entries.Single(entry => entry.Path.EndsWith("guide.md", StringComparison.Ordinal));
        Assert.False(guide.IsDirectory);
        Assert.Equal(11, guide.Size);
        Assert.True(entries.Single(entry => string.Equals(entry.Path, "docs/", StringComparison.Ordinal)).IsDirectory);
    }

    [Fact]
    public async Task A_tar_is_listed_without_extracting_anything()
    {
        var path = WriteTar("source.tar", compressed: false);

        var entries = await _reader.ReadAsync(
            Asura.Application.FilePreviewContent.FromLocalFile(path),
            Path.GetFileName(path),
            100,
            CancellationToken.None);

        Assert.NotNull(entries);
        var file = Assert.Single(entries!, entry => string.Equals(entry.Path, "readme.txt", StringComparison.Ordinal));
        Assert.Equal(5, file.Size);
        // Nothing is unpacked: the only file beside the archive is the one the
        // fixture wrote to build it.
        Assert.Equal(
            ["readme.txt", Path.GetFileName(path)],
            Directory.GetFiles(_root)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_gzipped_tar_is_listed_too()
    {
        var path = WriteTar("source.tar.gz", compressed: true);

        var entries = await _reader.ReadAsync(
            Asura.Application.FilePreviewContent.FromLocalFile(path),
            Path.GetFileName(path),
            100,
            CancellationToken.None);

        Assert.NotNull(entries);
        Assert.Contains(entries!, entry => string.Equals(entry.Path, "readme.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_high_amplification_tar_stops_at_the_expanded_byte_budget()
    {
        var path = Path.Combine(_root, "amplified.tar.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        using (var writer = new TarWriter(gzip, leaveOpen: false))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "large-zero-file.bin")
            {
                DataStream = new MemoryStream(new byte[2 * 1024 * 1024], writable: false),
            };
            writer.WriteEntry(entry);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "after-budget.txt")
            {
                DataStream = new MemoryStream("after"u8.ToArray(), writable: false),
            });
        }

        var entries = await _reader.ReadAsync(
            Asura.Application.FilePreviewContent.FromLocalFile(path),
            Path.GetFileName(path),
            100,
            CancellationToken.None);

        Assert.Null(entries);
    }

    [Fact]
    public async Task A_listing_stops_at_the_limit()
    {
        var path = WriteZip("many.zip", entryCount: 40);

        var entries = await _reader.ReadAsync(
            Asura.Application.FilePreviewContent.FromLocalFile(path),
            Path.GetFileName(path),
            10,
            CancellationToken.None);

        Assert.Equal(10, entries!.Count);
    }

    [Fact]
    public async Task A_file_that_is_not_the_archive_its_name_claims_lists_nothing()
    {
        var path = Path.Combine(_root, "broken.zip");
        await File.WriteAllTextAsync(path, "this is not a zip");

        Assert.Null(await _reader.ReadAsync(
            Asura.Application.FilePreviewContent.FromLocalFile(path),
            Path.GetFileName(path),
            100,
            CancellationToken.None));
    }

    private string WriteZip(string name, int entryCount = 0)
    {
        var path = Path.Combine(_root, name);
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        if (entryCount == 0)
        {
            archive.CreateEntry("docs/");
            Write(archive, "docs/guide.md", "# The guide");
            Write(archive, "src/main.c", "int main(){}");
            return path;
        }

        for (var index = 0; index < entryCount; index++)
        {
            Write(archive, $"file-{index}.txt", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return path;

        static void Write(ZipArchive archive, string entryName, string content)
        {
            using var stream = archive.CreateEntry(entryName).Open();
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }
    }

    private string WriteTar(string name, bool compressed)
    {
        var source = Path.Combine(_root, "readme.txt");
        File.WriteAllText(source, "hello");
        var path = Path.Combine(_root, name);
        using var file = File.Create(path);
        using Stream sink = compressed
            ? new GZipStream(file, CompressionLevel.Fastest)
            : file;
        using var writer = new TarWriter(sink, leaveOpen: true);
        writer.WriteEntry(source, "readme.txt");
        return path;
    }
}

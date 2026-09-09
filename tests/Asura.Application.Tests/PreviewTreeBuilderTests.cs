using Asura.Application.Previews;

namespace Asura.Application.Tests;

/// <summary>
/// Archives record flat paths, not folders. These cover turning the one into
/// the other, including the folder that exists only because a file mentions it.
/// </summary>
public sealed class PreviewTreeBuilderTests
{
    [Fact]
    public void A_path_creates_the_folders_it_names()
    {
        var nodes = PreviewTreeBuilder.FromPaths(
            [new ArchiveEntryDescriptor("docs/api/index.html", false, 120, 60)]);

        var docs = Assert.Single(nodes);
        Assert.Equal("docs", docs.Name);
        Assert.True(docs.IsContainer);
        var api = Assert.Single(docs.Children);
        Assert.Equal("api", api.Name);
        var file = Assert.Single(api.Children);
        Assert.Equal("index.html", file.Name);
        Assert.False(file.IsContainer);
        Assert.Equal("120 B", file.Detail);
    }

    [Fact]
    public void Folders_come_before_files_and_each_group_is_sorted()
    {
        var nodes = PreviewTreeBuilder.FromPaths(
        [
            new ArchiveEntryDescriptor("readme.md", false, 10, 10),
            new ArchiveEntryDescriptor("src/main.c", false, 20, 10),
            new ArchiveEntryDescriptor("assets/logo.png", false, 30, 20),
        ]);

        Assert.Equal(["assets", "src", "readme.md"], nodes.Select(node => node.Name), StringComparer.Ordinal);
    }

    [Fact]
    public void An_explicit_folder_entry_is_a_folder_not_a_file()
    {
        var nodes = PreviewTreeBuilder.FromPaths(
            [new ArchiveEntryDescriptor("empty/", true, null, null)]);

        var folder = Assert.Single(nodes);
        Assert.True(folder.IsContainer);
        Assert.Empty(folder.Children);
        Assert.Equal("0 items", folder.Detail);
    }

    [Fact]
    public void Windows_separators_describe_the_same_folders()
    {
        var nodes = PreviewTreeBuilder.FromPaths(
            [new ArchiveEntryDescriptor(@"docs\guide.md", false, 1, 1)]);

        Assert.Equal("docs", Assert.Single(nodes).Name);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1_572_864, "1.5 MB")]
    public void Sizes_read_the_way_a_person_reads_them(long bytes, string expected) =>
        Assert.Equal(expected, PreviewTreeBuilder.FormatSize(bytes));
}

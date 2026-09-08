using System.Text;
using GhostShell.App.ViewModels;
using GhostShell.App.Views.Components;
using GhostShell.Testing;

namespace GhostShell.App.Tests;

public sealed class DatabaseWorkspaceFileExportTests : IDisposable
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"GhostShell.DatabaseExportTests.{Guid.NewGuid():N}");

    public DatabaseWorkspaceFileExportTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Local_export_replaces_the_destination_only_after_a_complete_write()
    {
        var target = Path.Combine(_directory, "page.csv");
        await File.WriteAllTextAsync(target, "original", Encoding.UTF8);

        await DatabaseWorkspaceView.WriteLocalStorageFileAtomicallyAsync(
            target,
            async destination =>
            {
                await destination.WriteAsync("replacement"u8.ToArray());
            });

        Assert.Equal("replacement", await File.ReadAllTextAsync(target, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Failed_local_export_preserves_the_previous_destination()
    {
        var target = Path.Combine(_directory, "page.json");
        await File.WriteAllTextAsync(target, "original", Encoding.UTF8);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseWorkspaceView.WriteLocalStorageFileAtomicallyAsync(
                target,
                async destination =>
                {
                    await destination.WriteAsync("partial"u8.ToArray());
                    throw new InvalidDataException("serialization failed");
                }));

        Assert.Equal("serialization failed", error.Message);
        Assert.Equal("original", await File.ReadAllTextAsync(target, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Canceled_streamed_export_removes_temporary_and_keeps_original()
    {
        var target = Path.Combine(_directory, "page.csv");
        await File.WriteAllTextAsync(target, "original", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseWorkspaceView.WriteLocalStorageFileAtomicallyAsync(target, destination =>
            {
                using var writer = DatabaseGridExport.CreateFileWriter(destination, cancellation.Token);
                writer.Write("partial");
                writer.Flush();
                cancellation.Cancel();
                writer.Write("must not commit");
                return Task.CompletedTask;
            }));
        Assert.Equal("original", await File.ReadAllTextAsync(target, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Page_export_accepts_only_the_supported_data_formats()
    {
        Assert.Equal(
            DatabaseGridExportFormat.Csv,
            DatabaseWorkspaceView.ResolvePageExportFormat("page.csv"));
        Assert.Equal(
            DatabaseGridExportFormat.Json,
            DatabaseWorkspaceView.ResolvePageExportFormat("PAGE.JSON"));
        Assert.Equal(
            DatabaseGridExportFormat.Csv,
            DatabaseWorkspaceView.ResolvePageExportFormat("page"));
    }

    [Fact]
    public void Page_export_rejects_sql_insert_files()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            DatabaseWorkspaceView.ResolvePageExportFormat("page.sql"));

        Assert.Contains("Use .csv or .json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_export_picker_offers_only_csv_and_json()
    {
        Assert.Collection(
            DatabaseWorkspaceView.PageExportFileTypes,
            csv =>
            {
                Assert.Equal("CSV data", csv.Name);
                Assert.Equal(["*.csv"], csv.Patterns);
            },
            json =>
            {
                Assert.Equal("JSON data", json.Name);
                Assert.Equal(["*.json"], json.Patterns);
            });
    }

    [Fact]
    public void Database_row_copy_controls_offer_json_csv_and_insert()
    {
        var copyRowAs = ApplicationViews
            .FindUniqueNamedElement("CopyRowAsMenuItem")
            .Element;
        var contextMenuFormats = copyRowAs.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "MenuItem", StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Header") ?? string.Empty)
            .ToArray();
        var inspector = ApplicationViews
            .FindUniqueNamedElement("InspectorColumn")
            .Element;
        // The copy buttons carry their label as a CopyLabel TextBlock so a
        // success check can fade in over it after copying.
        var inspectorFormats = inspector.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal))
            .Where(element => (string?)element.Attribute("Click") is
                "OnCopyRowJsonClick" or "OnCopyRowCsvClick" or "OnCopyRowInsertClick")
            .Select(element => element.Descendants()
                .Where(child => string.Equals(child.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && ((string?)child.Attribute("Classes") ?? string.Empty)
                        .Contains("CopyLabel", StringComparison.Ordinal))
                .Select(child => (string?)child.Attribute("Text") ?? string.Empty)
                .FirstOrDefault() ?? string.Empty)
            .ToArray();

        Assert.Equal(["JSON", "CSV", "INSERT"], contextMenuFormats);
        Assert.Equal(["JSON", "CSV", "INSERT"], inspectorFormats);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

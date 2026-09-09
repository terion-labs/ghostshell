using System.Text;
using Asura.Application;

namespace Asura.Files.Tests;

public sealed class DatabaseResultContentStoreTests
{
    [Fact]
    public async Task ExportLeaseCanOpenLaterCellsAfterQueryReplacement()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "database-results");
        using var result = new DatabaseResultContentStore(path);
        var content = await result.StoreAsync(DatabaseValueKind.Text,
            (destination, token) => destination.WriteAsync("complete"u8.ToArray(), token).AsTask(), CancellationToken.None);
        using var export = result.Retain();
        result.Dispose();
        using (var first = content.OpenRead())
        using (var reader = new StreamReader(first))
        {
            Assert.Equal("complete", await reader.ReadToEndAsync());
        }
        using (var later = content.OpenRead())
        {
            Assert.Equal(8, later.Length);
        }
        export.Dispose();
        Assert.Throws<ObjectDisposedException>(() => content.OpenRead());
        Assert.DoesNotContain(Directory.GetFiles(path), file => file.EndsWith(".db", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewClearCannotDeleteLiveResultAndOpenReadRetainsDisposedResult()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "database-results");
        using var result = new DatabaseResultContentStore(path);
        using var previews = new PreviewContentCache(directory: Path.Combine(directory.Path, "file-previews"));
        var bytes = Encoding.UTF8.GetBytes(new string('x', 300_000) + "complete tail");
        var content = await result.StoreAsync(
            DatabaseValueKind.Text,
            (destination, token) => destination.WriteAsync(bytes, token).AsTask(),
            CancellationToken.None);
        Assert.Equal(bytes.Length, content.Length);
        Assert.DoesNotContain(Directory.GetFiles(path), file =>
            file.EndsWith(".db", StringComparison.Ordinal)
            && File.ReadAllBytes(file).AsSpan().IndexOf("complete tail"u8) >= 0);
        await previews.ClearAsync(CancellationToken.None);
        await using var read = content.OpenRead();
        result.Dispose();
        Assert.Throws<ObjectDisposedException>(() => content.OpenRead());
        using var output = new MemoryStream();
        await read.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
        Assert.Contains(Directory.GetFiles(path), file => file.EndsWith(".db", StringComparison.Ordinal));
        await read.DisposeAsync();
        Assert.DoesNotContain(Directory.GetFiles(path), file => file.EndsWith(".db", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CanceledWriteIsNotCommittedAndResultDisposalRemovesItsContainer()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "database-results");
        using var result = new DatabaseResultContentStore(path);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.StoreAsync(
            DatabaseValueKind.Binary,
            async (destination, token) =>
            {
                await destination.WriteAsync(new byte[8192], token);
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            },
            cancellation.Token));
        result.Dispose();
        Assert.DoesNotContain(Directory.GetFiles(path), file => file.EndsWith(".db", StringComparison.Ordinal));
    }
}

using Asura.App;

namespace Asura.App.Tests;

public sealed class LocalDiagnosticsBundleDestinationTests
{
    [Fact]
    public async Task CompletePublishesOnlyTheFinishedBundleAndReplacesAnExistingTarget()
    {
        var directory = CreateTemporaryDirectory();
        var targetPath = Path.Combine(directory, "asura-diagnostics.zip");
        await File.WriteAllBytesAsync(targetPath, [9, 9, 9]);

        try
        {
            await using var destination = new LocalDiagnosticsBundleDestination(targetPath);
            await destination.Content.WriteAsync(new byte[] { 1, 2, 3, 4 });

            Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(targetPath));

            await destination.CompleteAsync(CancellationToken.None);

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(targetPath));
            Assert.Equal(targetPath, destination.Artifact.Locator);
            Assert.Equal("asura-diagnostics.zip", destination.Artifact.DisplayName);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeWithoutCompleteRemovesTheTemporaryFileAndPreservesTheTarget()
    {
        var directory = CreateTemporaryDirectory();
        var targetPath = Path.Combine(directory, "existing.zip");
        await File.WriteAllBytesAsync(targetPath, [7, 8, 9]);

        try
        {
            await using (var destination = new LocalDiagnosticsBundleDestination(targetPath))
            {
                await destination.Content.WriteAsync(new byte[] { 1, 2, 3 });
            }

            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(targetPath));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Asura.App.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

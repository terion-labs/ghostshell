using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class RecentSessionHistoryExportControllerTests
{
    [Fact]
    public async Task Export_publishes_same_directory_temporary_output_atomically()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var selectedPath = Path.Combine(directory, "history");
            var exporter = new RecordingExporter("{\"safe\":true}");
            var controller = new RecentSessionHistoryExportController(
                exporter,
                new FixedPathPicker(selectedPath));

            var result = await controller.ExportAsync(
                [Record("session-1")],
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            var expectedPath = $"{selectedPath}.json";
            Assert.Equal(expectedPath, result.Value!.Path);
            Assert.Equal("{\"safe\":true}", await File.ReadAllTextAsync(expectedPath));
            Assert.Equal(1, result.Value.Export.RecordCount);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            Assert.Equal(1, exporter.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancelled_picker_never_invokes_exporter_or_creates_a_file()
    {
        var exporter = new RecordingExporter("{}");
        var controller = new RecentSessionHistoryExportController(
            exporter,
            new FixedPathPicker(null));

        var result = await controller.ExportAsync([], CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RecentSessionHistoryExportErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(0, exporter.Calls);
    }

    [Fact]
    public async Task Export_failure_preserves_existing_destination_and_removes_temporary_file()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "history.json");
            await File.WriteAllTextAsync(path, "existing");
            var exporter = new RecordingExporter("partial")
            {
                Failure = new RecentSessionHistoryExportError(
                    RecentSessionHistoryExportErrorCode.InvalidHistory,
                    "Invalid history."),
            };
            var controller = new RecentSessionHistoryExportController(
                exporter,
                new FixedPathPicker(path));

            var result = await controller.ExportAsync(
                [Record("session-1")],
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(RecentSessionHistoryExportErrorCode.InvalidHistory, result.Error!.Code);
            Assert.Equal("existing", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cleanup_failure_is_reported_with_the_residual_metadata_file_name()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "history.json");
            var exporter = new RecordingExporter("partial")
            {
                Failure = new RecentSessionHistoryExportError(
                    RecentSessionHistoryExportErrorCode.InvalidHistory,
                    "Invalid history."),
            };
            var fileSystem = new DeleteFailingFileSystem();
            var controller = new RecentSessionHistoryExportController(
                exporter,
                new FixedPathPicker(path),
                fileSystem);

            var result = await controller.ExportAsync(
                [Record("session-cleanup-failure")],
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(
                RecentSessionHistoryExportErrorCode.CleanupFailure,
                result.Error!.Code);
            var temporaryFile = Assert.Single(Directory.EnumerateFiles(directory, "*.tmp"));
            Assert.Contains(
                Path.GetFileName(temporaryFile),
                result.Error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_after_serialization_prevents_atomic_publish()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var path = Path.Combine(directory, "history.json");
            var exporter = new RecordingExporter("complete")
            {
                BeforeSuccess = cancellation.Cancel,
            };
            var controller = new RecentSessionHistoryExportController(
                exporter,
                new FixedPathPicker(path));

            var result = await controller.ExportAsync(
                [Record("session-cancel-before-publish")],
                cancellation.Token);

            Assert.False(result.IsSuccess);
            Assert.Equal(RecentSessionHistoryExportErrorCode.Cancelled, result.Error!.Code);
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"asura-history-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static RecentSessionRecord Record(string id)
    {
        var started = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        return new RecentSessionRecord(
            new SessionId(id),
            new DefinitionKey(ConnectionProfile.Kind, "local"),
            PanelKind.Terminal,
            "Local shell",
            started,
            started.AddMinutes(2),
            RecentSessionOutcome.GracefullyClosed);
    }

    private sealed class FixedPathPicker(string? path) : IRecentSessionHistoryPathPicker
    {
        public ValueTask<string?> PickExportPathAsync(
            string suggestedFileName,
            int recordCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(RecentSessionHistoryExportController.SuggestedExportFileName, suggestedFileName);
            Assert.True(recordCount >= 0);
            return ValueTask.FromResult(path);
        }
    }

    private sealed class RecordingExporter(string payload) : IRecentSessionHistoryExporter
    {
        public int Calls { get; private set; }

        public RecentSessionHistoryExportError? Failure { get; init; }

        public Action? BeforeSuccess { get; init; }

        public async ValueTask<RecentSessionHistoryExportResult<RecentSessionHistoryExportReceipt>>
            ExportAsync(
                IReadOnlyList<RecentSessionRecord> recentSessions,
                Stream destination,
                CancellationToken cancellationToken)
        {
            Calls++;
            var bytes = Encoding.UTF8.GetBytes(payload);
            await destination.WriteAsync(bytes, cancellationToken);
            if (Failure is not null)
            {
                return RecentSessionHistoryExportResult<RecentSessionHistoryExportReceipt>
                    .Failure(Failure);
            }

            BeforeSuccess?.Invoke();
            return RecentSessionHistoryExportResult<RecentSessionHistoryExportReceipt>.Success(
                new RecentSessionHistoryExportReceipt(
                    recentSessions.Count,
                    DateTimeOffset.UnixEpoch,
                    bytes.Length,
                    new string('0', 64)));
        }
    }

    private sealed class DeleteFailingFileSystem : IRecentSessionHistoryExportFileSystem
    {
        private readonly LocalRecentSessionHistoryExportFileSystem _inner = new();

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public Stream CreateTemporaryFile(string path) => _inner.CreateTemporaryFile(path);

        public void Publish(string temporaryPath, string destinationPath) =>
            _inner.Publish(temporaryPath, destinationPath);

        public void Delete(string path) =>
            throw new IOException("Simulated cleanup failure.");
    }
}

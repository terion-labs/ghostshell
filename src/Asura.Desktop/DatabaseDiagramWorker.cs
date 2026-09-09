using System.Diagnostics;
using System.Text;
using Asura.Application;
using Asura.ConnectionBackend;
using Asura.Databases;
using Asura.Infrastructure;
using SkiaSharp;

namespace Asura.Desktop;

internal sealed class DatabaseDiagramWorker(SelfReentryLaunch? selfReentry = null) : IDatabaseDiagramWorkerFactory
{
    internal const string Marker = "--asura-database-diagram-worker";
    private static readonly SemaphoreSlim Admission = new(2, 2);

    public Task<IDatabaseDiagramSession> OpenAsync(DatabaseSchemaGraph graph, CancellationToken cancellationToken,
        DatabaseDiagramPurpose purpose = DatabaseDiagramPurpose.Display)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return OpenCoreAsync(session => session.InitializeAsync(graph, purpose, cancellationToken), cancellationToken);
    }

    public Task<IDatabaseDiagramSession> OpenAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken,
        DatabaseDiagramPurpose purpose = DatabaseDiagramPurpose.Display)
    {
        return OpenCoreAsync(session => session.InitializeAsync(connection, purpose, cancellationToken), cancellationToken);
    }

    private async Task<IDatabaseDiagramSession> OpenCoreAsync(Func<WorkerSession, Task> initialize, CancellationToken cancellationToken)
    {
        var launch = selfReentry ?? SelfReentryLaunch.Detect();
        var start = new ProcessStartInfo(launch.Executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var prefix in launch.PrefixArguments)
        {
            start.ArgumentList.Add(prefix);
        }

        start.ArgumentList.Add(Marker);
        await Admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        WorkerSession session;
        try
        {
            var process = Process.Start(start) ?? throw new IOException("The database schema worker could not start.");
            session = new WorkerSession(process);
        }
        catch
        {
            Admission.Release();
            throw;
        }
        try
        {
            await initialize(session).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<int> RunAsync()
    {
        // This mode has only inherited anonymous pipes: no listening endpoint,
        // profile/vault access, user-supplied executable, or network bypass mode.
        await using var input = Console.OpenStandardInput();
        await using var output = Console.OpenStandardOutput();
        try
        {
            var request = await DatabaseDiagramProtocol.ReadRequestAsync(input, CancellationToken.None).ConfigureAwait(false);
            var detachedGraph = request.Operation is "open-graph" or "open-graph-source";
            if (!detachedGraph && (request.Operation is not "open" and not "open-source"
                || request.DriverId is null || request.ConnectionString is null))
            {
                return 64;
            }

            if (detachedGraph && (request.DriverId is not null || request.ConnectionString is not null
                || request.SqliteSnapshotBytes is not null))
            {
                return 64;
            }

            if (request.SqliteSnapshotBytes is not null && !string.Equals(request.DriverId, "sqlite", StringComparison.Ordinal))
            {
                return 64;
            }
            var source = detachedGraph
                ? DatabaseMermaidErDiagram.CreateSource(await DatabaseMetadataProtocol.ReadAsync(input,
                    DatabaseOperationJsonContext.Default.DatabaseSchemaGraph, CancellationToken.None).ConfigureAwait(false))
                : await ReadConnectionSourceAsync(request, input, output).ConfigureAwait(false);
            using var diagram = request.Operation is "open-source" or "open-graph-source"
                ? null
                : new DatabaseDiagramPicture(source);
            await DatabaseDiagramProtocol.WriteFrameAsync(output, "ready"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            while (true)
            {
                request = await DatabaseDiagramProtocol.ReadRequestAsync(input, CancellationToken.None).ConfigureAwait(false);
                if (string.Equals(request.Operation, "viewport", StringComparison.Ordinal) && request.Viewport is { } viewport)
                {
                    var picture = diagram?.ForTheme(viewport.DarkTheme)
                        ?? throw new InvalidDataException("This worker exports schema source only.");
                    viewport.Validate();
                    using var bitmap = new SKBitmap(viewport.Width, viewport.Height);
                    using var canvas = new SKCanvas(bitmap);
                    canvas.Clear(SKColors.Transparent);
                    var bounds = picture.CullRect;
                    var fit = Math.Min(viewport.Width / Math.Max(1, bounds.Width), viewport.Height / Math.Max(1, bounds.Height));
                    var scale = (float)(fit * viewport.Zoom);
                    canvas.Translate((float)(viewport.Width / 2.0 + viewport.PanX), (float)(viewport.Height / 2.0 + viewport.PanY));
                    canvas.Scale(scale);
                    canvas.Translate(-bounds.MidX, -bounds.MidY);
                    canvas.DrawPicture(picture);
                    using var image = SKImage.FromBitmap(bitmap);
                    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                    if (png.Size > DatabaseDiagramProtocol.MaximumImageBytes)
                    {
                        throw new InvalidDataException("The diagram viewport image is too large.");
                    }

                    await DatabaseDiagramProtocol.WriteFrameAsync(output, "viewport"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                    await DatabaseDiagramProtocol.WriteFrameAsync(output, png.ToArray(), CancellationToken.None).ConfigureAwait(false);
                }
                else if (string.Equals(request.Operation, "export", StringComparison.Ordinal))
                {
                    await DatabaseDiagramProtocol.WriteFrameAsync(output, "export"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                    if (request.Format == DatabaseDiagramExport.MermaidMarkdown)
                    {
                        await WriteTextChunksAsync(output, "```mermaid\n", CancellationToken.None).ConfigureAwait(false);
                        await WriteTextChunksAsync(output, source, CancellationToken.None).ConfigureAwait(false);
                        await WriteTextChunksAsync(output, "\n```\n", CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (request.Format == DatabaseDiagramExport.Svg && diagram is not null)
                    {
                        await WriteTextChunksAsync(output, diagram.Svg, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        return 64;
                    }

                    await DatabaseDiagramProtocol.WriteFrameAsync(output, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    return 64;
                }
            }
        }
        catch (EndOfStreamException)
        {
            return 0;
        }
        catch
        {
            // Never echo provider exceptions: they can contain connection data.
            return 70;
        }
    }

    private static async Task<string> ReadConnectionSourceAsync(DatabaseDiagramRequest request, Stream input, Stream output)
    {
        using var previewImage = request.SqliteSnapshotBytes is { } length
            ? await DatabaseWorkerSqliteSnapshot.ReceiveAsync(length, input, output, CancellationToken.None).ConfigureAwait(false)
            : null;
        if (previewImage is not null) { request = request with { ConnectionString = previewImage.ConnectionString }; }
        await using var client = new DatabasePanelClient();
        var graph = await client.GetDatabaseSchemaGraphAsync(request.DriverId!, request.ConnectionString!, null, CancellationToken.None)
            .ConfigureAwait(false);
        return DatabaseMermaidErDiagram.CreateSource(graph);
    }

    private static async Task WriteTextChunksAsync(Stream output, string value, CancellationToken token)
    {
        var encoder = Encoding.UTF8.GetEncoder();
        var bytes = new byte[DatabaseDiagramProtocol.MaximumExportChunkBytes];
        var offset = 0;
        while (offset < value.Length)
        {
            encoder.Convert(value.AsSpan(offset), bytes, flush: true, out var chars, out var count, out _);
            offset += chars;
            await DatabaseDiagramProtocol.WriteFrameAsync(output, bytes.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }

    private sealed class WorkerSession(Process process) : IDatabaseDiagramSession
    {
        private readonly SemaphoreSlim _commands = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        private readonly DiagramMemoryWatchdog _watchdog = new(
            () => { process.Refresh(); return process.WorkingSet64; },
            () => { if (!process.HasExited) { process.Kill(entireProcessTree: false); } });
        private int _disposed;
        private bool _operationFailed;

        public Task InitializeAsync(DatabaseSchemaGraph graph, DatabaseDiagramPurpose purpose, CancellationToken token) =>
            RunAsync(async cancellation =>
            {
                await SendAsync(new DatabaseDiagramRequest(
                    purpose == DatabaseDiagramPurpose.SourceExport ? "open-graph-source" : "open-graph"), cancellation).ConfigureAwait(false);
                await DatabaseMetadataProtocol.WriteAsync(process.StandardInput.BaseStream, graph,
                    DatabaseOperationJsonContext.Default.DatabaseSchemaGraph, cancellation).ConfigureAwait(false);
                await ExpectControlAsync("ready"u8.ToArray(), cancellation).ConfigureAwait(false);
                return true;
            }, token);

        public async Task InitializeAsync(DatabaseWorkerConnection connection, DatabaseDiagramPurpose purpose, CancellationToken token)
        {
            await using var previewImage = DatabaseWorkerSqliteSnapshot.Borrow(connection);
            await RunAsync(async cancellation =>
            {
                await SendAsync(new DatabaseDiagramRequest(
                    purpose == DatabaseDiagramPurpose.SourceExport ? "open-source" : "open",
                    connection.DriverId, previewImage is null ? connection.ConnectionString : "Data Source=:memory:",
                    SqliteSnapshotBytes: previewImage?.Length), cancellation).ConfigureAwait(false);
                if (previewImage is not null)
                {
                    await DatabaseWorkerSqliteSnapshot.SendAsync(previewImage, process.StandardOutput.BaseStream,
                        process.StandardInput.BaseStream, cancellation).ConfigureAwait(false);
                }
                var response = await DatabaseDiagramProtocol.ReadFrameAsync(process.StandardOutput.BaseStream,
                    DatabaseDiagramProtocol.MaximumRequestBytes, cancellation).ConfigureAwait(false);
                if (!response.AsSpan().SequenceEqual("ready"u8))
                {
                    throw new IOException("The database schema worker could not initialize.");
                }
                return true;
            }, token).ConfigureAwait(false);
        }

        public Task<byte[]> RenderViewportAsync(DatabaseDiagramViewport viewport, CancellationToken cancellationToken)
        {
            viewport.Validate();
            return RunAsync(async token =>
            {
                await SendAsync(new DatabaseDiagramRequest("viewport", Viewport: viewport), token).ConfigureAwait(false);
                await ExpectControlAsync("viewport"u8.ToArray(), token).ConfigureAwait(false);
                return await DatabaseDiagramProtocol.ReadFrameAsync(process.StandardOutput.BaseStream,
                    DatabaseDiagramProtocol.MaximumImageBytes, token).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task ExportAsync(Stream destination, DatabaseDiagramExport format, CancellationToken cancellationToken) =>
            RunAsync(async token =>
            {
                await SendAsync(new DatabaseDiagramRequest("export", Format: format), token).ConfigureAwait(false);
                await ExpectControlAsync("export"u8.ToArray(), token).ConfigureAwait(false);
                while (true)
                {
                    var chunk = await DatabaseDiagramProtocol.ReadFrameAsync(process.StandardOutput.BaseStream,
                        DatabaseDiagramProtocol.MaximumExportChunkBytes, token).ConfigureAwait(false);
                    if (chunk.Length == 0)
                    {
                        return true;
                    }

                    await destination.WriteAsync(chunk, token).ConfigureAwait(false);
                }
            }, cancellationToken);

        private Task SendAsync(DatabaseDiagramRequest request, CancellationToken token) =>
            DatabaseDiagramProtocol.WriteRequestAsync(process.StandardInput.BaseStream, request, token);

        private async Task ExpectControlAsync(byte[] expected, CancellationToken token)
        {
            var control = await DatabaseDiagramProtocol.ReadFrameAsync(process.StandardOutput.BaseStream,
                DatabaseDiagramProtocol.MaximumRequestBytes, token).ConfigureAwait(false);
            if (!control.AsSpan().SequenceEqual(expected)) { throw new InvalidDataException("The diagram response phase is invalid."); }
        }

        private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            await _commands.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                return await operation(cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _operationFailed = true;
                KillOwnedWorker();
                throw new IOException(_watchdog.Exceeded
                    ? "The complete diagram exceeded its 2 GiB rendering budget. Export the full schema source without rendering, or retry after reducing other memory use."
                    : "The database schema worker stopped. Reopen the diagram to retry.");
            }
            catch
            {
                _operationFailed = true;
                KillOwnedWorker();
                throw;
            }
            finally
            {
                _commands.Release();
            }
        }

        private void KillOwnedWorker()
        {
            DatabaseOperationWorker.StopOwnedProcess(process);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await DatabaseOperationWorker.RunCleanupAsync(_operationFailed,
                    () => _lifetime.CancelAsync(),
                    () => { KillOwnedWorker(); return Task.CompletedTask; },
                    async () =>
                    {
                        using var drainDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await _commands.WaitAsync(drainDeadline.Token).ConfigureAwait(false);
                        _commands.Release();
                    },
                    async () => await _watchdog.DisposeAsync().ConfigureAwait(false),
                    async () => await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false),
                    async () => { try { await _stderrDrain.ConfigureAwait(false); } catch (IOException) { } }).ConfigureAwait(false);
            }
            finally
            {
                process.Dispose();
                _lifetime.Dispose();
                Admission.Release();
            }
        }
    }

}

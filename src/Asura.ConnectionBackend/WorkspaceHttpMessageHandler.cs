using System.Diagnostics;
using System.Net;

namespace Asura.ConnectionBackend;

/// <summary>
/// HttpClient's existing transport seam, backed by an owned guest exchange.
/// Provider parsing, credential resolution and origin policy remain in their
/// existing host modules; TCP/TLS/HTTP execution follows the guest gateway.
/// </summary>
internal sealed class WorkspaceHttpMessageHandler(
    Func<CancellationToken, Task<DatabaseWorkspaceOperationLaunch>> launch) : HttpMessageHandler
{
    private readonly CancellationTokenSource _lifetime = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var owned = await launch(startup.Token).ConfigureAwait(false);
        Exchange? exchange = null;
        try
        {
            exchange = new Exchange(owned, _lifetime.Token);
            var headers = request.Headers.Select(header => new WorkspaceHttpHeader(header.Key, [.. header.Value])).ToList();
            if (request.Content is { } content)
            {
                headers.AddRange(content.Headers.Select(header => new WorkspaceHttpHeader(header.Key, [.. header.Value], Content: true)));
            }
            WorkspaceHttpProtocol.Validate([.. headers]);
            await BackendJsonFrames.WriteAsync(exchange.Input, new WorkspaceHttpRequest(request.Method.Method,
                request.RequestUri?.AbsoluteUri ?? throw new InvalidDataException("The HTTP request has no absolute origin."), [.. headers], request.Content is not null),
                WorkspaceHttpJsonContext.Default.WorkspaceHttpRequest, startup.Token).ConfigureAwait(false);
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStreamAsync(startup.Token).ConfigureAwait(false);
                await WorkspaceHttpProtocol.WriteBodyAsync(body, exchange.Input, WorkspaceHttpProtocol.RequestBytes, startup.Token).ConfigureAwait(false);
            }
            else { await DatabaseOperationProtocol.WriteFrameAsync(exchange.Input, ReadOnlyMemory<byte>.Empty, startup.Token).ConfigureAwait(false); }
            await DatabaseOperationProtocol.ExpectAsync(exchange.Output, "ready"u8.ToArray(), startup.Token).ConfigureAwait(false);
            await DatabaseOperationProtocol.WriteFrameAsync(exchange.Input, "execute"u8.ToArray(), startup.Token).ConfigureAwait(false);
            var metadata = await BackendJsonFrames.ReadAsync(exchange.Output, WorkspaceHttpJsonContext.Default.WorkspaceHttpResponse, startup.Token).ConfigureAwait(false);
            if (metadata.Status is < 100 or > 599) { throw new InvalidDataException("Invalid backend HTTP status."); }
            WorkspaceHttpProtocol.Validate(metadata.Headers);
            var response = new HttpResponseMessage((HttpStatusCode)metadata.Status)
            {
                RequestMessage = request,
                Content = new StreamContent(new ResponseStream(exchange)),
            };
            try
            {
                foreach (var header in metadata.Headers)
                {
                    var target = header.Content ? (System.Net.Http.Headers.HttpHeaders)response.Content.Headers : response.Headers;
                    if (!target.TryAddWithoutValidation(header.Name, header.Values)) { throw new InvalidDataException("Invalid backend HTTP response header placement."); }
                }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        catch (Exception exception)
        {
            await DatabaseOperationWorker.RunCleanupAsync(operationFailed: true,
                exchange is not null ? () => exchange.DisposeAsync().AsTask() : owned.CleanupAsync).ConfigureAwait(false);
            if (exception is OperationCanceledException) { throw; }
            throw new HttpRequestException("The workspace HTTP exchange failed. It was not replayed.", exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lifetime.Cancel(); }
        base.Dispose(disposing);
    }

    private sealed class Exchange : IAsyncDisposable
    {
        private readonly DatabaseWorkspaceOperationLaunch _owned;
        private readonly Process _process;
        private readonly Task _errors;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationToken _token;
        private readonly CancellationTokenRegistration _stop;
        private readonly object _gate = new();
        private Task? _dispose;

        internal Exchange(DatabaseWorkspaceOperationLaunch owned, CancellationToken owner)
        {
            _owned = owned;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner, owned.Lifetime);
            _token = _lifetime.Token;
            try
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var start = owned.StartInfo;
                start.UseShellExecute = false; start.RedirectStandardInput = true; start.RedirectStandardOutput = true;
                start.RedirectStandardError = true; start.CreateNoWindow = true;
                _process = Process.Start(start) ?? throw new IOException("The HTTP backend could not start.");
                _errors = _process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
                _stop = _lifetime.Token.Register(() => Abort());
            }
            catch { _lifetime.Dispose(); throw; }
        }

        internal Stream Input => _process.StandardInput.BaseStream;
        internal Stream Output => _process.StandardOutput.BaseStream;
        internal CancellationToken Lifetime => _token;

        internal void Abort()
        {
            DatabaseOperationWorker.StopOwnedProcess(_process);
            _ = Task.Run(async () =>
            {
                try { await DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { Asura.Application.SecretSafeDiagnosticProjection.WriteTrace("workspace.http.cleanup.failed", exception); }
            });
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate) { return new(_dispose ??= CloseAsync()); }
        }

        private async Task CloseAsync()
        {
            await _stop.DisposeAsync().ConfigureAwait(false);
            try
            {
                DatabaseOperationWorker.StopOwnedProcess(_process);
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                await _errors.ConfigureAwait(false);
            }
            finally
            {
                _process.Dispose(); _lifetime.Dispose();
                await _owned.CleanupAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class ResponseStream(Exchange exchange) : Stream
    {
        private byte[] _chunk = [];
        private int _offset;
        private long _total;
        private bool _complete;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Backend HTTP bodies require asynchronous reads.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0 || _complete) { return 0; }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, exchange.Lifetime);
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (_offset == _chunk.Length)
                {
                    _chunk = await DatabaseOperationProtocol.ReadFrameAsync(exchange.Output, WorkspaceHttpProtocol.ChunkBytes, cancellation.Token).ConfigureAwait(false);
                    _offset = 0;
                    _total += _chunk.Length;
                    if (_total > WorkspaceHttpProtocol.ResponseBytes) { throw new InvalidDataException("The backend HTTP response exceeds its limit."); }
                    if (_chunk.Length == 0)
                    {
                        _complete = true;
                        await exchange.DisposeAsync().ConfigureAwait(false);
                        return 0;
                    }
                }
                var count = Math.Min(buffer.Length, _chunk.Length - _offset);
                _chunk.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
                return count;
            }
            catch (Exception) when (cancellation.IsCancellationRequested)
            {
                exchange.Abort();
                throw new OperationCanceledException(cancellation.Token);
            }
            catch { exchange.Abort(); throw; }
        }
        protected override void Dispose(bool disposing) { if (disposing && !_complete) { exchange.Abort(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync()
        {
            _complete = true;
            try { await exchange.DisposeAsync().ConfigureAwait(false); }
            finally { await base.DisposeAsync().ConfigureAwait(false); }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

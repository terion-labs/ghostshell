using System.Net;
using System.Text;
using GhostShell.Application;

namespace GhostShell.Browser.Tests;

public sealed class PeerBoundHttpFetcherTests
{
    [Fact]
    public async Task FetchConnectsOnlyToResolvedPublicPeer()
    {
        IPAddress? connected = null;
        var fetcher = new PeerBoundHttpFetcher(
            static (_, _) => ValueTask.FromResult(
                new[] { IPAddress.Parse("93.184.216.34") }),
            (address, port, _) =>
            {
                connected = address;
                Assert.Equal(80, port);
                return ValueTask.FromResult<Stream>(Response(
                    "200 OK",
                    "application/json",
                    "{\"ok\":true}"));
            });

        var result = await fetcher.FetchAsync(
            new AgentHttpFetchRequest("http://api.example.test/v1"),
            CancellationToken.None);

        var succeeded = Assert.IsType<AgentWebToolExecutionResult.Succeeded>(result);
        var fetched = Assert.IsType<AgentHttpFetchResult>(succeeded.Result);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), connected);
        Assert.Equal(200, fetched.StatusCode);
        Assert.Equal("{\"ok\":true}", fetched.Content);
    }

    [Fact]
    public async Task MixedPublicAndPrivateDnsAnswersFailBeforeConnect()
    {
        var connectCount = 0;
        var fetcher = new PeerBoundHttpFetcher(
            static (_, _) => ValueTask.FromResult(
                new[]
                {
                    IPAddress.Parse("93.184.216.34"),
                    IPAddress.Parse("10.0.0.1"),
                }),
            (_, _, _) =>
            {
                connectCount++;
                return ValueTask.FromResult<Stream>(Response(
                    "200 OK",
                    "text/plain",
                    "unsafe"));
            });

        var result = await fetcher.FetchAsync(
            new AgentHttpFetchRequest("http://mixed.example.test/"),
            CancellationToken.None);

        var failed = Assert.IsType<AgentWebToolExecutionResult.Failed>(result);
        Assert.Equal(AgentWebToolErrorCode.DestinationDenied, failed.Code);
        Assert.Equal(0, connectCount);
    }

    [Fact]
    public async Task RedirectRevalidatesAndBindsTheNextHost()
    {
        var resolvedHosts = new List<string>();
        var responses = new Queue<Stream>(
        [
            Response(
                "302 Found",
                "text/plain",
                string.Empty,
                "Location: http://other.example.test/final\r\n"),
            Response("200 OK", "text/plain", "done"),
        ]);
        var fetcher = new PeerBoundHttpFetcher(
            (host, _) =>
            {
                resolvedHosts.Add(host);
                return ValueTask.FromResult(
                    new[] { IPAddress.Parse("93.184.216.34") });
            },
            (_, _, _) => ValueTask.FromResult(responses.Dequeue()));

        var result = await fetcher.FetchAsync(
            new AgentHttpFetchRequest("http://first.example.test/start"),
            CancellationToken.None);

        var succeeded = Assert.IsType<AgentWebToolExecutionResult.Succeeded>(result);
        var fetched = Assert.IsType<AgentHttpFetchResult>(succeeded.Result);
        Assert.Equal(
            ["first.example.test", "other.example.test"],
            resolvedHosts);
        Assert.Equal("http://other.example.test/final", fetched.FinalUrl);
        Assert.Equal("done", fetched.Content);
    }

    [Fact]
    public async Task BinaryMediaTypeFailsWithoutReturningBody()
    {
        var fetcher = new PeerBoundHttpFetcher(
            static (_, _) => ValueTask.FromResult(
                new[] { IPAddress.Parse("93.184.216.34") }),
            static (_, _, _) => ValueTask.FromResult<Stream>(Response(
                "200 OK",
                "application/octet-stream",
                "binary")));

        var result = await fetcher.FetchAsync(
            new AgentHttpFetchRequest("http://downloads.example.test/file"),
            CancellationToken.None);

        var failed = Assert.IsType<AgentWebToolExecutionResult.Failed>(result);
        Assert.Equal(AgentWebToolErrorCode.UnsupportedContentType, failed.Code);
    }

    [Fact]
    public async Task OversizedDecompressedTextFailsInsteadOfTruncating()
    {
        var fetcher = new PeerBoundHttpFetcher(
            static (_, _) => ValueTask.FromResult(
                new[] { IPAddress.Parse("93.184.216.34") }),
            static (_, _, _) => ValueTask.FromResult<Stream>(Response(
                "200 OK",
                "text/plain",
                new string('x', AgentHttpFetchResult.MaximumContentBytes + 1))));

        var result = await fetcher.FetchAsync(
            new AgentHttpFetchRequest("http://large.example.test/data"),
            CancellationToken.None);

        var failed = Assert.IsType<AgentWebToolExecutionResult.Failed>(result);
        Assert.Equal(AgentWebToolErrorCode.BodyTooLarge, failed.Code);
    }

    [Fact]
    public async Task CallerCancellationHasStableResult()
    {
        var fetcher = new PeerBoundHttpFetcher(
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(
                    new[] { IPAddress.Parse("93.184.216.34") });
            },
            static (_, _, _) => ValueTask.FromResult<Stream>(Response(
                "200 OK",
                "text/plain",
                "unused")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await fetcher.FetchAsync(
            new AgentHttpFetchRequest("http://cancelled.example.test/"),
            cancellation.Token);

        var failed = Assert.IsType<AgentWebToolExecutionResult.Failed>(result);
        Assert.Equal(AgentWebToolErrorCode.Cancelled, failed.Code);
    }

    [Fact]
    public async Task RoutedDnsFailureDoesNotAttemptAConnection()
    {
        var connectCount = 0;
        var fetcher = new PeerBoundHttpFetcher(
            static (_, _) => throw new IOException("The routed DNS port is blocked."),
            (_, _, _) =>
            {
                connectCount++;
                return ValueTask.FromResult<Stream>(Response(
                    "200 OK",
                    "text/plain",
                    "unsafe"));
            });

        var result = await fetcher.FetchAsync(
            new AgentHttpFetchRequest("https://blocked-dns.example.test/"),
            CancellationToken.None);

        var failed = Assert.IsType<AgentWebToolExecutionResult.Failed>(result);
        Assert.Equal(AgentWebToolErrorCode.DnsFailed, failed.Code);
        Assert.Equal(0, connectCount);
    }

    private static Stream Response(
        string status,
        string mediaType,
        string body,
        string additionalHeaders = "")
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {mediaType}\r\nContent-Length: {bodyBytes.Length}\r\n{additionalHeaders}Connection: close\r\n\r\n");
        return new ScriptedHttpStream([.. headers, .. bodyBytes]);
    }

    private sealed class ScriptedHttpStream(byte[] response) : Stream
    {
        private readonly MemoryStream _response = new(response, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            _response.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _response.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

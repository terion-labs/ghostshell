using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Asura.Files.Tests;

public sealed class WebDavMutationNoReplayTests
{
    private static readonly FileProviderProfileId ProfileId = new("webdav-no-replay");
    private static readonly FileAuthority Authority = new("webdav-no-replay.test");

    [Fact]
    public async Task CreateDirectoryDispatchesMkColOnlyOnceWhenServerDropsResponse()
    {
        await using var endpoint = new ResponseDroppingWebDavEndpoint("MKCOL");
        using var client = CreateClient();
        var provider = CreateProvider(client, endpoint.BaseUri);
        var location = Root.Child(new FilePathSegment("directory"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await provider.CreateDirectoryAsync(
            new FileCreateDirectoryRequest(
                location,
                new FileMutationPrecondition.MustNotExist()),
            timeout.Token);

        Assert.False(result.IsSuccess);
        Assert.False(
            timeout.IsCancellationRequested,
            "The response-less WebDAV MKCOL did not complete within the bounded test timeout.");
        var request = Assert.Single(endpoint.MutationRequests);
        Assert.Equal("MKCOL", request.Method);
        Assert.Equal("/root/directory/", request.Target);
        Assert.Equal(0, request.ContentLength);
    }

    [Fact]
    public async Task DeleteDispatchesDeleteOnlyOnceWhenServerDropsResponse()
    {
        await using var endpoint = new ResponseDroppingWebDavEndpoint("DELETE");
        using var client = CreateClient();
        var provider = CreateProvider(client, endpoint.BaseUri);
        var location = Root.Child(new FilePathSegment("file.txt"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await provider.DeleteAsync(
            new FileDeleteRequest(
                location,
                recursive: false,
                new FileMutationPrecondition.MustExist()),
            timeout.Token);

        Assert.False(result.IsSuccess);
        Assert.False(
            timeout.IsCancellationRequested,
            "The response-less WebDAV DELETE did not complete within the bounded test timeout.");
        var request = Assert.Single(endpoint.MutationRequests);
        Assert.Equal("DELETE", request.Method);
        Assert.Equal("/root/file.txt", request.Target);
        Assert.Equal(0, request.ContentLength);
    }

    [Fact]
    public async Task WriteDispatchesPutOnlyOnceWhenServerDropsResponse()
    {
        await using var endpoint = new ResponseDroppingWebDavEndpoint("PUT");
        using var client = CreateClient();
        var provider = CreateProvider(client, endpoint.BaseUri);
        var location = Root.Child(new FilePathSegment("file.txt"));
        await using var source = new MemoryStream([1, 2, 3], writable: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await provider.WriteAsync(
            new FileWriteRequest(
                location,
                contentLength: 3,
                bufferSize: 1024,
                new FileMutationPrecondition.MustNotExist()),
            source,
            progress: null,
            timeout.Token);

        Assert.False(result.IsSuccess);
        Assert.False(timeout.IsCancellationRequested);
        var request = Assert.Single(endpoint.MutationRequests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("/root/file.txt", request.Target);
        Assert.Equal(3, request.ContentLength);
    }

    [Fact]
    public async Task CopyDispatchesCopyOnlyOnceWhenServerDropsResponse()
    {
        await using var endpoint = new ResponseDroppingWebDavEndpoint("COPY");
        using var client = CreateClient();
        var provider = CreateProvider(client, endpoint.BaseUri);
        var source = Root.Child(new FilePathSegment("file.txt"));
        var destination = Root.Child(new FilePathSegment("copy.txt"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await provider.TransferAsync(
            new FileTransferRequest(
                source,
                destination,
                FileTransferKind.Copy,
                bufferSize: 1024,
                new FileMutationPrecondition.MustNotExist()),
            progress: null,
            timeout.Token);

        Assert.False(result.IsSuccess);
        Assert.False(timeout.IsCancellationRequested);
        var request = Assert.Single(endpoint.MutationRequests);
        Assert.Equal("COPY", request.Method);
        Assert.Equal("/root/file.txt", request.Target);
        Assert.Equal(0, request.ContentLength);
    }

    private static FileLocation Root =>
        new(ProfileId, Authority, FilePath.Root);

    private static HttpClient CreateClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            UseProxy = false,
        });

    private static WebDavFileProvider CreateProvider(HttpClient client, Uri baseUri) =>
        new(
            client,
            new WebDavFileProviderOptions(
                ProfileId,
                Authority,
                baseUri));

    private sealed class ResponseDroppingWebDavEndpoint : IAsyncDisposable
    {
        private const int MaximumRequestBytes = 64 * 1024;
        private static readonly byte[] FilePropertyResponse = CreateResponse(
            "207 Multi-Status",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/root/file.txt</d:href>
                <d:propstat>
                  <d:prop>
                    <d:resourcetype />
                    <d:getcontentlength>1</d:getcontentlength>
                    <d:getlastmodified>Thu, 24 Jul 2026 12:00:00 GMT</d:getlastmodified>
                    <d:getetag>"file-etag"</d:getetag>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """);
        private static readonly byte[] MethodNotAllowedResponse = CreateResponse(
            "405 Method Not Allowed",
            string.Empty,
            closeConnection: true);

        private readonly string _mutationMethod;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _acceptLoop;
        private readonly ConcurrentQueue<CapturedRequest> _mutationRequests = new();

        public ResponseDroppingWebDavEndpoint(string mutationMethod)
        {
            _mutationMethod = mutationMethod;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var address = (IPEndPoint)_listener.LocalEndpoint;
            BaseUri = new Uri($"http://127.0.0.1:{address.Port}/root/");
            _acceptLoop = AcceptLoopAsync();
        }

        public Uri BaseUri { get; }

        public CapturedRequest[] MutationRequests => [.. _mutationRequests];

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                _shutdown.Dispose();
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                using (client)
                {
                    await ServeConnectionAsync(client.GetStream(), _shutdown.Token);
                }
            }
        }

        private async Task ServeConnectionAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await ReadRequestAsync(stream, cancellationToken);
                if (request is null)
                {
                    return;
                }

                if (string.Equals(request.Method, _mutationMethod, StringComparison.Ordinal))
                {
                    _mutationRequests.Enqueue(request);
                    // Closing the connection after fully consuming the mutation emulates the
                    // ambiguous transport failure that must not trigger an automatic replay.
                    return;
                }

                var response = string.Equals(request.Method, "PROPFIND"
, StringComparison.Ordinal) ? FilePropertyResponse
                    : MethodNotAllowedResponse;
                await stream.WriteAsync(response, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                if (response == MethodNotAllowedResponse)
                {
                    return;
                }
            }
        }

        private static async Task<CapturedRequest?> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[MaximumRequestBytes];
            var received = 0;
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var count = await stream.ReadAsync(
                    buffer.AsMemory(received),
                    cancellationToken);
                if (count == 0)
                {
                    return null;
                }

                received += count;
                headerEnd = FindHeaderEnd(buffer.AsSpan(0, received));
                if (received == buffer.Length && headerEnd < 0)
                {
                    throw new InvalidDataException(
                        "The loopback WebDAV request headers exceeded the test bound.");
                }
            }

            var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length < 2)
            {
                throw new InvalidDataException(
                    "The loopback WebDAV endpoint received an invalid request line.");
            }

            int? contentLength = null;
            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(
                        line["Content-Length:".Length..].Trim(),
                        CultureInfo.InvariantCulture);
                }
            }

            var bodyStart = headerEnd + 4;
            var requestLength = checked(bodyStart + contentLength.GetValueOrDefault());
            if (requestLength > buffer.Length)
            {
                throw new InvalidDataException(
                    "The loopback WebDAV request body exceeded the test bound.");
            }

            while (received < requestLength)
            {
                var count = await stream.ReadAsync(
                    buffer.AsMemory(received, requestLength - received),
                    cancellationToken);
                if (count == 0)
                {
                    throw new EndOfStreamException(
                        "The WebDAV client disconnected before sending its declared request body.");
                }

                received += count;
            }

            return new CapturedRequest(
                requestLine[0],
                requestLine[1],
                contentLength);
        }

        private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index <= bytes.Length - 4; index++)
            {
                if (bytes[index] == '\r'
                    && bytes[index + 1] == '\n'
                    && bytes[index + 2] == '\r'
                    && bytes[index + 3] == '\n')
                {
                    return index;
                }
            }

            return -1;
        }

        private static byte[] CreateResponse(
            string status,
            string body,
            bool closeConnection = false)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\n"
                + "Content-Type: application/xml\r\n"
                + $"Content-Length: {bodyBytes.Length}\r\n"
                + (closeConnection ? "Connection: close\r\n" : string.Empty)
                + "\r\n");
            return [.. headers, .. bodyBytes];
        }
    }

    private sealed record CapturedRequest(
        string Method,
        string Target,
        int? ContentLength);
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Asura.Application;
using Asura.Core;

namespace Asura.Files.Tests;

public sealed class S3SdkNoReplayTests
{
    [Theory]
    [InlineData(FailureMode.ServiceUnavailable)]
    [InlineData(FailureMode.ResponseDropped)]
    public async Task ProductionStoreDispatchesOneKeyDeleteOnlyOnce(FailureMode failureMode)
    {
        await using var endpoint = new LoopbackS3Endpoint(failureMode);
        using var client = CreateClient(endpoint);
        var store = new AwsS3ObjectStore(client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var failure = await Record.ExceptionAsync(
            async () => await store.DeleteAsync(
                "asura-test-bucket",
                "mutation-target",
                ifMatch: null,
                timeout.Token));

        Assert.NotNull(failure);
        Assert.False(
            timeout.IsCancellationRequested,
            "The S3 one-key delete did not complete within the bounded test timeout.");
        Assert.Equal(1, endpoint.RequestCount);
        Assert.Equal("POST", endpoint.Request.Method);
        Assert.Contains("?delete", endpoint.Request.Target, StringComparison.Ordinal);
        Assert.Contains("<Key>mutation-target</Key>", endpoint.Request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneKeyDeleteTurnsEmbeddedVersionedErrorIntoStoreFailure()
    {
        await using var endpoint = new LoopbackS3Endpoint(FailureMode.EmbeddedPreconditionError);
        using var client = CreateClient(endpoint);
        var store = new AwsS3ObjectStore(client);

        var failure = await Assert.ThrowsAsync<S3StoreException>(
            async () => await store.DeleteAsync(
                "asura-test-bucket",
                "mutation-target",
                "\"etag-1\"",
                CancellationToken.None));

        Assert.Equal(1, endpoint.RequestCount);
        Assert.Contains("<ETag>\"etag-1\"</ETag>", endpoint.Request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<VersionId>", endpoint.Request.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.PreconditionFailed, failure.StatusCode);
        Assert.Equal("PreconditionFailed", failure.ServiceCode);
        Assert.DoesNotContain("version-from-service", failure.Message, StringComparison.Ordinal);
    }

    public enum FailureMode
    {
        ServiceUnavailable,
        ResponseDropped,
        EmbeddedPreconditionError,
    }

    private static AmazonS3Client CreateClient(LoopbackS3Endpoint endpoint)
    {
        var configuration = FileProviderAdapterFactory.CreateS3ClientConfiguration(
            new FileProviderConfiguration.S3(
                "asura-test-bucket",
                serviceUri: endpoint.ServiceUri,
                forcePathStyle: true,
                allowInsecureTransport: true));
        return new AmazonS3Client(new AnonymousAWSCredentials(), configuration);
    }

    private sealed class LoopbackS3Endpoint : IAsyncDisposable
    {
        private const int MaximumRequestBytes = 64 * 1024;
        private static readonly byte[] ContinueResponse = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();
        private static readonly byte[] ServiceUnavailableResponse = CreateResponse(
            "503 Service Unavailable",
            """
            <Error><Code>SlowDown</Code><Message>retry later</Message><RequestId>test</RequestId></Error>
            """);
        private static readonly byte[] EmbeddedPreconditionErrorResponse = CreateResponse(
            "200 OK",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <DeleteResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Error><Key>mutation-target</Key><VersionId>version-from-service</VersionId><Code>PreconditionFailed</Code><Message>stale</Message></Error></DeleteResult>
            """);

        private readonly FailureMode _failureMode;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _acceptLoop;
        private CapturedRequest? _request;
        private int _requestCount;

        public LoopbackS3Endpoint(FailureMode failureMode)
        {
            _failureMode = failureMode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var address = (IPEndPoint)_listener.LocalEndpoint;
            ServiceUri = new Uri($"http://127.0.0.1:{address.Port}/");
            _acceptLoop = AcceptLoopAsync();
        }

        public Uri ServiceUri { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public CapturedRequest Request =>
            Volatile.Read(ref _request)
            ?? throw new InvalidOperationException("The loopback endpoint did not receive a request.");

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
                    var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, _shutdown.Token);
                    if (request is null)
                    {
                        continue;
                    }

                    Volatile.Write(ref _request, request);
                    Interlocked.Increment(ref _requestCount);
                    var response = _failureMode switch
                    {
                        FailureMode.ServiceUnavailable => ServiceUnavailableResponse,
                        FailureMode.EmbeddedPreconditionError =>
                            EmbeddedPreconditionErrorResponse,
                        _ => null,
                    };
                    if (response is not null)
                    {
                        await stream.WriteAsync(response, _shutdown.Token);
                        await stream.FlushAsync(_shutdown.Token);
                    }
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
                    throw new InvalidDataException("The loopback request headers exceeded the test bound.");
                }
            }

            var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length < 2)
            {
                throw new InvalidDataException("The loopback endpoint received an invalid request line.");
            }

            var contentLength = 0;
            var expectsContinue = false;
            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(
                        line["Content-Length:".Length..].Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                }

                if (line.Equals("Expect: 100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    expectsContinue = true;
                }
            }

            if (expectsContinue)
            {
                await stream.WriteAsync(ContinueResponse, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            var bodyStart = headerEnd + 4;
            var requestLength = checked(bodyStart + contentLength);
            if (requestLength > buffer.Length)
            {
                throw new InvalidDataException("The loopback request body exceeded the test bound.");
            }

            while (received < requestLength)
            {
                var count = await stream.ReadAsync(
                    buffer.AsMemory(received, requestLength - received),
                    cancellationToken);
                if (count == 0)
                {
                    throw new EndOfStreamException(
                        "The loopback client disconnected before sending its declared request body.");
                }

                received += count;
            }

            return new CapturedRequest(
                requestLine[0],
                requestLine[1],
                Encoding.UTF8.GetString(buffer, bodyStart, contentLength));
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

        private static byte[] CreateResponse(string status, string body)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\n"
                + "Content-Type: application/xml\r\n"
                + $"Content-Length: {bodyBytes.Length}\r\n"
                + "Connection: close\r\n"
                + "x-amz-request-id: asura-test\r\n"
                + "\r\n");
            return [.. headers, .. bodyBytes];
        }
    }

    private sealed record CapturedRequest(string Method, string Target, string Body);
}

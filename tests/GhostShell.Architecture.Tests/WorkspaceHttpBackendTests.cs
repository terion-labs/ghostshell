using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using GhostShell.ConnectionBackend;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceHttpBackendTests
{
    [Fact]
    public async Task RealChildPreservesMethodAuthorizationAndBinaryBodies()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var body = new byte[128 * 1024];
        Random.Shared.NextBytes(body);
        var serving = Task.Run(async () =>
        {
            using var socket = await origin.AcceptTcpClientAsync(timeout.Token);
            var stream = socket.GetStream();
            var headers = await ReadHeadersAsync(stream, timeout.Token);
            Assert.StartsWith("POST /resource?q=1 HTTP/1.1", headers, StringComparison.Ordinal);
            Assert.Contains("Authorization: Bearer fixture-secret\r\n", headers, StringComparison.Ordinal);
            Assert.Contains("X-Fixture: exact-value\r\n", headers, StringComparison.Ordinal);
            var received = new byte[body.Length];
            await stream.ReadExactlyAsync(received, timeout.Token);
            Assert.Equal(body, received);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 201 Created\r\nContent-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nX-Fixture: reply\r\nConnection: close\r\n\r\n"), timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        }, timeout.Token);
        await using var backend = new OwnedBackend();
        using var client = new HttpClient(backend.Handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, Address(origin, "/resource?q=1"))
        {
            Content = new ByteArrayContent(body),
        };
        request.Headers.Authorization = new("Bearer", "fixture-secret");
        request.Headers.Add("X-Fixture", "exact-value");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("reply", Assert.Single(response.Headers.GetValues("X-Fixture")));
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync(timeout.Token));
        await serving;
        await backend.Cleaned.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, backend.Launches);
        Assert.Equal(1, backend.Cleanups);
    }

    [Fact]
    public async Task RealChildDoesNotFollowRedirectOrSendAuthorizationToAnotherOrigin()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var redirect = new TcpListener(IPAddress.Loopback, 0);
        origin.Start(); redirect.Start();
        var serving = Task.Run(async () =>
        {
            using var socket = await origin.AcceptTcpClientAsync(timeout.Token);
            var stream = socket.GetStream();
            _ = await ReadHeadersAsync(stream, timeout.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {Address(redirect, "/secret")}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
        }, timeout.Token);
        await using var backend = new OwnedBackend();
        using var client = new HttpClient(backend.Handler);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-secret");
        using var response = await client.GetAsync(Address(origin, "/"), timeout.Token);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(Address(redirect, "/secret"), response.Headers.Location);
        Assert.False(redirect.Pending());
        await serving;
        await backend.Cleaned.Task.WaitAsync(timeout.Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StreamingCancellationAndRouteRevocationCloseChildWithoutReplay(bool revokeRoute, bool buffered)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var route = new CancellationTokenSource();
        using var read = new CancellationTokenSource();
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(async () =>
        {
            using var socket = await origin.AcceptTcpClientAsync(timeout.Token);
            var stream = socket.GetStream();
            _ = await ReadHeadersAsync(stream, timeout.Token);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\ndata: first\n\n"u8.ToArray(), timeout.Token);
            connected.TrySetResult();
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);
        await using var backend = new OwnedBackend(route.Token);
        using var client = new HttpClient(backend.Handler);
        try
        {
            using var response = await client.GetAsync(Address(origin, "/events"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await connected.Task.WaitAsync(timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[buffered ? 1 : "data: first\n\n"u8.Length];
            await stream.ReadExactlyAsync(buffer, timeout.Token);
            Assert.Equal(buffered ? "d" : "data: first\n\n", Encoding.UTF8.GetString(buffer));
            var pending = buffered ? null : stream.ReadAsync(new byte[1], read.Token).AsTask();
            if (revokeRoute) { await route.CancelAsync(); }
            else { await read.CancelAsync(); }
            pending ??= stream.ReadAsync(new byte[1], read.Token).AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await backend.Cleaned.Task.WaitAsync(timeout.Token);
            Assert.Equal(1, backend.Launches);
            Assert.Equal(1, backend.Cleanups);
            Assert.False(origin.Pending());
        }
        finally { release.TrySetResult(); await serving; }
    }

    [Fact]
    public async Task ChildCannotContactOriginBeforeExecuteAndRejectsUnknownFields()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await BackendJsonFrames.WriteAsync(input, new WorkspaceHttpRequest("POST", Address(origin, "/").AbsoluteUri, [], false),
            WorkspaceHttpJsonContext.Default.WorkspaceHttpRequest, CancellationToken.None);
        await DatabaseOperationProtocol.WriteFrameAsync(input, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        input.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => WorkspaceHttpProtocol.RunChildAsync(input, output, CancellationToken.None));
        output.Position = 0;
        await DatabaseOperationProtocol.ExpectAsync(output, "ready"u8.ToArray(), CancellationToken.None);
        Assert.False(origin.Pending());

        using var unknown = new MemoryStream();
        await DatabaseOperationProtocol.WriteFrameAsync(unknown,
            "{\"Method\":\"GET\",\"Address\":\"http://127.0.0.1/\",\"Headers\":[],\"HasContent\":false,\"CredentialFile\":\"/private\"}"u8.ToArray(), CancellationToken.None);
        unknown.Position = 0;
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => WorkspaceHttpProtocol.RunChildAsync(unknown, Stream.Null, CancellationToken.None));
        Assert.False(origin.Pending());
    }

    [Fact]
    public async Task BoundsAndHeaderInjectionAreRejectedBeforeDispatch()
    {
        Assert.Throws<InvalidDataException>(() => WorkspaceHttpProtocol.Validate([new("Authorization", ["secret\r\nInjected: yes"])]));
        Assert.Throws<InvalidDataException>(() => WorkspaceHttpProtocol.Validate([new("Invalid Header", ["value"])]));
        Assert.Throws<InvalidDataException>(() => WorkspaceHttpProtocol.Validate([new("X-Large", [new string('x', WorkspaceHttpProtocol.ChunkBytes)])]));
        using var body = new MemoryStream(new byte[20]);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceHttpProtocol.WriteBodyAsync(body, Stream.Null, 19, CancellationToken.None));
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, WorkspaceHttpProtocol.ChunkBytes + 1);
        using var oversized = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseOperationProtocol.ReadFrameAsync(oversized, WorkspaceHttpProtocol.ChunkBytes, CancellationToken.None));
    }

    [Fact]
    public async Task FailedLaunchStillReleasesOwnedScratchAndDoesNotRetry()
    {
        var cleanup = 0;
        var launches = 0;
        using var client = new HttpClient(new WorkspaceHttpMessageHandler(_ =>
        {
            launches++;
            return Task.FromResult(new DatabaseWorkspaceOperationLaunch(new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing")),
                () => { cleanup++; return Task.CompletedTask; }));
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://127.0.0.1/")));
        Assert.Equal(1, launches);
        Assert.Equal(1, cleanup);
    }

    private static Uri Address(TcpListener listener, string path) => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{path}");

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var single = new byte[1];
        while (bytes.Length < 64 * 1024)
        {
            await stream.ReadExactlyAsync(single, token);
            bytes.WriteByte(single[0]);
            var span = bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length));
            if (span.EndsWith("\r\n\r\n"u8)) { return Encoding.ASCII.GetString(span); }
        }
        throw new InvalidDataException("Fixture headers exceeded their limit.");
    }

    private sealed class OwnedBackend(CancellationToken route = default) : IAsyncDisposable
    {
        private readonly List<string> _ids = [];
        private WorkspaceHttpMessageHandler? _handler;
        internal TaskCompletionSource Cleaned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Launches { get; private set; }
        internal int Cleanups { get; private set; }
        internal WorkspaceHttpMessageHandler Handler => _handler ??= new(LaunchAsync);

        private Task<DatabaseWorkspaceOperationLaunch> LaunchAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var id = Guid.NewGuid().ToString("N");
            DatabaseWorkspaceScratch.Prepare(id);
            _ids.Add(id); Launches++;
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) { root = root.Parent; }
            Assert.NotNull(root);
            var backend = typeof(WorkspaceHttpBackendTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => string.Equals(attribute.Key, "DatabaseBackendPath", StringComparison.Ordinal)).Value;
            var start = new ProcessStartInfo(Path.Combine(root.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            start.ArgumentList.Add(backend!); start.ArgumentList.Add("http"); start.ArgumentList.Add(id);
            return Task.FromResult(new DatabaseWorkspaceOperationLaunch(start, async () =>
            {
                await DatabaseWorkspaceScratch.CleanupAsync(id, CancellationToken.None);
                Cleanups++;
                Cleaned.TrySetResult();
            }, route));
        }

        public async ValueTask DisposeAsync()
        {
            _handler?.Dispose();
            foreach (var id in _ids) { await DatabaseWorkspaceScratch.CleanupAsync(id, CancellationToken.None); }
        }
    }
}

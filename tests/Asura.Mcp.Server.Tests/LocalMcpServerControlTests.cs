using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Asura.Mcp.Server.Tests;

public sealed class LocalMcpServerControlTests
{
    [Fact]
    public async Task SavedChoiceStartsAfterReopeningAndDisablingReleasesPort()
    {
        await using var profile = new Profile();
        await profile.Control.InitializeAsync(CancellationToken.None);
        Assert.False(profile.Control.State.Enabled);
        Assert.False(File.Exists(Path.Combine(profile.Directory, "mcp-token")));
        var port = UnusedPort();
        await profile.Control.ConfigureAsync(true, port);
        Assert.True(profile.Control.State.IsRunning);
        Assert.Null(profile.Control.State.Error);
        var token = await profile.Control.ReadTokenAsync();
        await profile.Control.DisposeAsync();

        await using var reopenedServer = new WorkspaceMcpServer();
        await using var reopened = new LocalMcpServerControl(reopenedServer, profile.Directory);
        await reopened.InitializeAsync(CancellationToken.None);
        Assert.True(reopened.State.IsRunning);
        Assert.Equal(port, reopened.State.Port);
        Assert.Equal(token, await reopened.ReadTokenAsync());
        await reopened.ConfigureAsync(false, port);
        Assert.False(reopened.State.Enabled);
        Assert.False(reopened.State.IsRunning);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        await using var disabledServer = new WorkspaceMcpServer();
        await using var disabled = new LocalMcpServerControl(disabledServer, profile.Directory);
        await disabled.InitializeAsync(CancellationToken.None);
        Assert.False(disabled.State.Enabled);
        Assert.Null(disabled.State.Error);
    }

    [Fact]
    public async Task ReplacingTokenRejectsOldCredentialWithoutRestartingApp()
    {
        await using var profile = new Profile();
        var port = UnusedPort();
        await profile.Control.ConfigureAsync(true, port);
        var oldToken = await profile.Control.ReadTokenAsync();
        await profile.Control.RotateTokenAsync();
        var newToken = await profile.Control.ReadTokenAsync();
        Assert.NotEqual(oldToken, newToken, StringComparer.Ordinal);
        Assert.True(profile.Control.State.IsRunning);
        using var client = new HttpClient();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldToken);
        using var rejected = await client.GetAsync(endpoint);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        using var accepted = await client.GetAsync(endpoint);
        Assert.NotEqual(HttpStatusCode.Unauthorized, accepted.StatusCode);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(profile.Directory, "mcp-token")));
        }
    }

    [Fact]
    public async Task OccupiedPortReportsFailureAndSavedChoiceCanBeRetried()
    {
        await using var profile = new Profile();
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await profile.Control.ConfigureAsync(true, port);
        Assert.True(profile.Control.State.Enabled);
        Assert.False(profile.Control.State.IsRunning);
        Assert.NotNull(profile.Control.State.Error);
        occupied.Stop();
        await profile.Control.ConfigureAsync(true, port);
        Assert.True(profile.Control.State.IsRunning);
        Assert.Null(profile.Control.State.Error);
        var replacementPort = UnusedPort();
        await profile.Control.ConfigureAsync(true, replacementPort);
        Assert.True(profile.Control.State.IsRunning);
        Assert.Equal(replacementPort, profile.Control.State.Port);
        using var previousPort = new TcpListener(IPAddress.Loopback, port);
        previousPort.Start();
    }

    [Fact]
    public async Task CorruptPreferencesLeaveServerOffAndCanBeReplacedInSettings()
    {
        await using var profile = new Profile();
        System.IO.Directory.CreateDirectory(profile.Directory);
        await File.WriteAllTextAsync(Path.Combine(profile.Directory, "mcp-server.json"), "broken");
        await profile.Control.InitializeAsync(CancellationToken.None);
        Assert.False(profile.Control.State.IsRunning);
        Assert.NotNull(profile.Control.State.Error);
        await profile.Control.ConfigureAsync(false, 18765);
        Assert.Null(profile.Control.State.Error);
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(65536)]
    public async Task InvalidPortDoesNotStopRunningServer(int port)
    {
        await using var profile = new Profile();
        await profile.Control.ConfigureAsync(true, UnusedPort());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => profile.Control.ConfigureAsync(true, port));
        Assert.True(profile.Control.State.IsRunning);
    }

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Profile : IAsyncDisposable
    {
        private readonly WorkspaceMcpServer _server = new();

        public Profile() => Control = new(_server, Directory);

        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "asura-mcp-settings-" + Guid.NewGuid().ToString("N"));

        public LocalMcpServerControl Control { get; }

        public async ValueTask DisposeAsync()
        {
            await Control.DisposeAsync();
            await _server.DisposeAsync();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}

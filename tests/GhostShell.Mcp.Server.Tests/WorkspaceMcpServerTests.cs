using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace GhostShell.Mcp.Server.Tests;

public sealed class WorkspaceMcpServerTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Discover = """
        {"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{
        "io.modelcontextprotocol/protocolVersion":"2026-07-28",
        "io.modelcontextprotocol/clientInfo":{"name":"test","version":"1"},
        "io.modelcontextprotocol/clientCapabilities":{}}}}
        """;

    [Fact]
    public async Task ModernToolsCanBeListedAndCalledWithoutInitialize()
    {
        var port = UnusedPort();
        await using var server = new WorkspaceMcpServer();
        await server.StartAsync(port, Token, CancellationToken.None);
        using var client = CreateClient(port);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        client.DefaultRequestHeaders.Remove("Mcp-Method");
        client.DefaultRequestHeaders.Add("Mcp-Method", "tools/list");
        using var listed = await client.PostAsync(new Uri("mcp", UriKind.Relative), Json(Discover.Replace(
            "server/discover", "tools/list", StringComparison.Ordinal)));
        var listing = await listed.Content.ReadAsStringAsync();
        Assert.True(listed.IsSuccessStatusCode, listing);
        Assert.Contains("ghostshell.workspaces", listing, StringComparison.Ordinal);
        client.DefaultRequestHeaders.Remove("Mcp-Method");
        client.DefaultRequestHeaders.Add("Mcp-Method", "tools/call");
        client.DefaultRequestHeaders.Add("Mcp-Name", "ghostshell.workspaces");
        using var called = await client.PostAsync(new Uri("mcp", UriKind.Relative), Json("""
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{
            "name":"ghostshell.workspaces","arguments":{},"_meta":{
            "io.modelcontextprotocol/protocolVersion":"2026-07-28",
            "io.modelcontextprotocol/clientInfo":{"name":"test","version":"1"},
            "io.modelcontextprotocol/clientCapabilities":{}}}}
            """));
        var receipt = await called.Content.ReadAsStringAsync();
        Assert.True(called.IsSuccessStatusCode, receipt);
        Assert.Contains("[]", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiresBearerTokenBeforeProtocolProcessing()
    {
        var port = UnusedPort();
        await using var server = new WorkspaceMcpServer();
        await server.StartAsync(port, Token, CancellationToken.None);
        using var client = CreateClient(port);
        using var absent = await client.PostAsync(new Uri("mcp", UriKind.Relative), Json(Discover));
        Assert.Equal(HttpStatusCode.Unauthorized, absent.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        using var wrong = await client.PostAsync(new Uri("mcp", UriKind.Relative), Json(Discover));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var valid = await client.PostAsync(new Uri("mcp", UriKind.Relative), Json(Discover));
        var body = await valid.Content.ReadAsStringAsync();
        Assert.True(valid.IsSuccessStatusCode, body);
        Assert.Contains("2026-07-28", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, body, StringComparison.Ordinal);
        Assert.False(valid.Headers.Contains("Mcp-Session-Id"));
    }

    [Theory]
    [InlineData("Origin", "https://attacker.example")]
    [InlineData("Host", "attacker.example")]
    public async Task RejectsBrowserOriginAndDnsRebinding(string header, string value)
    {
        var port = UnusedPort();
        await using var server = new WorkspaceMcpServer();
        await server.StartAsync(port, Token, CancellationToken.None);
        using var client = CreateClient(port);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        client.DefaultRequestHeaders.Add(header, value);
        using var response = await client.PostAsync(new Uri("mcp", UriKind.Relative), Json(Discover));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LegacyInitializeStillRequiresAuthentication()
    {
        var port = UnusedPort();
        await using var server = new WorkspaceMcpServer();
        await server.StartAsync(port, Token, CancellationToken.None);
        using var client = CreateClient(port);
        client.DefaultRequestHeaders.Remove("MCP-Protocol-Version");
        client.DefaultRequestHeaders.Remove("Mcp-Method");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await client.PostAsync(new Uri("mcp", UriKind.Relative), Json("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
            "protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
            """));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("2025-11-25", body, StringComparison.Ordinal);
        var session = Assert.Single(response.Headers.GetValues("Mcp-Session-Id"));
        client.DefaultRequestHeaders.Add("Mcp-Session-Id", session);
        client.DefaultRequestHeaders.Authorization = null;
        using var unauthenticated = await client.PostAsync(new Uri("mcp", UriKind.Relative), Json(Discover));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    private static HttpClient CreateClient(int port)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2026-07-28");
        client.DefaultRequestHeaders.Add("Mcp-Method", "server/discover");
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

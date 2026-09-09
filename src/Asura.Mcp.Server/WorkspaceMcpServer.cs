using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Asura.Agent.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace Asura.Mcp.Server;

/// <summary>
/// Optional loopback MCP transport. Authentication is checked for every HTTP request,
/// including discovery and resumed legacy sessions. The SDK owns protocol negotiation.
/// </summary>
public sealed partial class WorkspaceMcpServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WeakReference<GovernedAgentRuntime>> _workspaces = new(StringComparer.Ordinal);
    private WebApplication? _application;

    public void Register(GovernedAgentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        // The desktop attaches its trusted layout port after constructing the runtime.
        _workspaces[Guid.NewGuid().ToString("N")] = new WeakReference<GovernedAgentRuntime>(runtime);
    }

    public async Task StartAsync(int port, string token, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length < 32 || token.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The MCP bearer token must contain at least 32 non-whitespace characters.", nameof(token));
        }

        if (_application is not null)
        {
            throw new InvalidOperationException("The MCP server is already started.");
        }

        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var authority = $"127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        // Never inherit ASPNETCORE_URLS or application logging providers that might retain credentials.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 1024 * 1024;
            options.Limits.MaxConcurrentConnections = 32;
        });
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "Asura", Version = "1.0.0" };
        })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!string.Equals(context.Request.Host.Value, authority, StringComparison.Ordinal)
                || (context.Request.Headers.TryGetValue("Origin", out var origin)
                    && !string.Equals(origin.ToString(), $"http://{authority}", StringComparison.Ordinal)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || !CryptographicOperations.FixedTimeEquals(tokenHash,
                    SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]))))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"Asura\"";
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        app.MapMcp("/mcp");
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _application = app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => StopAsync();

    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _application, null) is { } app)
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Dictionary<string, GovernedAgentRuntime> LiveWorkspaces()
    {
        var live = new Dictionary<string, GovernedAgentRuntime>(StringComparer.Ordinal);
        foreach (var entry in _workspaces)
        {
            if (!entry.Value.TryGetTarget(out var runtime))
            {
                _workspaces.TryRemove(entry.Key, out _);
            }
            else if (runtime.ExternalToolTarget is { } target)
            {
                live[target.WorkspaceId.Value] = runtime;
            }
        }

        return live;
    }
}

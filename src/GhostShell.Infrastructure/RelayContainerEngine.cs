using System.Text.Json;
using GhostShell.Application;

namespace GhostShell.Infrastructure;

/// <summary>Captures a local engine endpoint once. Later context changes cannot move a live relay.</summary>
internal sealed record RelayContainerEngine(string Executable, IReadOnlyList<string> Prefix, string Architecture, string Kind = "docker")
{
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);

    internal WorkspaceProcessLaunch Launch(IReadOnlyList<string> arguments) =>
        new(Executable, [.. Prefix, .. arguments], EmptyEnvironment, null);

    internal static async Task<RelayContainerEngine> DiscoverAsync(IConnectionExecutableLocator locator,
        IWorkspaceIsolationCommandRunner runner, CancellationToken token)
    {
        // Docker Desktop and OrbStack expose the same local API. Podman's macOS
        // client normally reaches its local machine over SSH instead of a Unix socket.
        foreach (var name in new[] { "docker", "podman" })
        {
            if (locator.Find(name) is not { } executable) { continue; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                IReadOnlyList<string> prefix;
                if (name is "docker")
                {
                    var endpoint = Environment.GetEnvironmentVariable("DOCKER_HOST");
                    if (string.IsNullOrWhiteSpace(endpoint))
                    {
                        var context = await RunAsync(executable, ["context", "inspect", "--format", "{{.Endpoints.docker.Host}}"], runner, timeout.Token).ConfigureAwait(false);
                        endpoint = context.Trim();
                    }
                    ValidateEndpoint(endpoint, allowLocalSsh: false);
                    prefix = ["--host", endpoint];
                }
                else
                {
                    var connections = await RunAsync(executable, ["system", "connection", "list", "--format", "json"], runner, timeout.Token).ConfigureAwait(false);
                    using var json = JsonDocument.Parse(connections);
                    var selected = json.RootElement.EnumerateArray().FirstOrDefault(item => item.GetProperty("Default").GetBoolean());
                    if (selected.ValueKind == JsonValueKind.Undefined) { continue; }
                    var endpoint = selected.GetProperty("URI").GetString()!;
                    ValidateEndpoint(endpoint, allowLocalSsh: true);
                    var identity = selected.TryGetProperty("Identity", out var value) ? value.GetString() : null;
                    prefix = string.IsNullOrEmpty(identity) ? ["--url", endpoint] : ["--url", endpoint, "--identity", identity];
                }
                var format = name is "docker" ? "{{.Architecture}}" : "{{.Host.Arch}}";
                var arch = (await RunAsync(executable, [.. prefix, "info", "--format", format], runner, timeout.Token).ConfigureAwait(false)).Trim();
                return new(executable, prefix, NormalizeArchitecture(arch), name);
            }
            catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException
                || exception is OperationCanceledException && !token.IsCancellationRequested)
            {
                SecretSafeDiagnosticProjection.WriteTrace("workspace.relay.engine.unavailable", exception);
            }
        }
        throw new IOException("Start a local Docker Desktop, OrbStack, or Podman engine to use routed connections on this system. Remote engines are not used.");
    }

    internal static string NormalizeArchitecture(string architecture) => architecture switch
    {
        "aarch64" or "arm64" => "arm64",
        "x86_64" or "amd64" or "x64" => "x64",
        _ => throw new IOException("The relay engine must run Linux ARM64 or x64."),
    };

    internal static void ValidateEndpoint(string endpoint, bool allowLocalSsh)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !(uri.Scheme is "unix" && uri.Host.Length == 0 && uri.AbsolutePath.Length > 1
                || allowLocalSsh && uri.Scheme is "ssh" && uri.IsLoopback && !uri.UserInfo.Contains(':', StringComparison.Ordinal)))
        {
            throw new IOException("Relay isolates require a local engine socket or a local Podman machine, not a remote engine.");
        }
    }

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> args,
        IWorkspaceIsolationCommandRunner runner, CancellationToken token)
    {
        var result = await runner.RunAsync(new(executable, args, EmptyEnvironment, null), ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
        if (result.ExitCode != 0) { throw new IOException("The local container engine command failed."); }
        return result.StandardOutput;
    }
}

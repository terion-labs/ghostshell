using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.ConnectionBackend;
using GhostShell.Core;
using GhostShell.Desktop;
using GhostShell.Files;
using GhostShell.Infrastructure;
using Oracle.ManagedDataAccess.Client;

namespace GhostShell.Architecture.Tests;

public sealed partial class WorkspaceDatabaseBackendNativeTests
{
    [WorkspaceBackendFact]
    public async Task Service_guest_Oracle_keeps_private_DNS_redirects_and_TLS_hostname_validation()
    {
        var assets = Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_SDK_RUNTIME_ROOT")!;
        var archive = Environment.GetEnvironmentVariable("GHOSTSHELL_WORKSPACE_BACKEND_ARCHIVE")!;
        var directory = Directory.CreateTempSubdirectory("ghostshell-service-oracle-");
        var executable = Path.Combine(assets, "workspace-runtime");
        var provider = WorkspaceSdkIsolationProvider.CreateServiceProvider(executable, Path.Combine(directory.FullName, "state"));
        var gateway = Path.Combine(Path.GetDirectoryName(assets)!, "ghostshell-workspace-gateway-darwin-arm64");
        var processes = new WorkspaceGatewayProcessRunner();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await using var destination = new DatabaseServiceOracleFixture();
        await using var origin = new DatabaseServiceOracleFixture(
            redirect: "(ADDRESS=(PROTOCOL=TCP)(HOST=oracle-destination.synthetic.invalid)(PORT=1522))");
        await using var tls = new DatabaseServiceOracleFixture(tlsHost: "oracle-tls.synthetic.invalid");
        await using var socks = new DatabaseServiceSocksFixture((host, port) => (host, port) switch
        {
            ("oracle-origin.synthetic.invalid", 1521) => origin.Port,
            ("oracle-destination.synthetic.invalid", 1522) => destination.Port,
            ("oracle-tls.synthetic.invalid", 1521) => tls.Port,
            ("oracle-wrong.synthetic.invalid", 1521) => tls.Port,
            _ => throw new InvalidOperationException("Unexpected Oracle fixture endpoint."),
        });
        WorkspaceIsolationBinding? binding = null;
        IWorkspacePacketGatewaySession? session = null;
        try
        {
            binding = Prepared(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(
                new WorkspaceId($"oracle-{Guid.NewGuid():N}")), deadline.Token));
            await provider.ConfigureServiceNetworkingAsync(binding, deadline.Token);
            var runtime = new HostWorkspacePacketGatewayRuntime(new BundledWorkspacePacketGatewayBackend([],
                new WorkspaceHostNetworkRouteLauncher(executable, processes), processes, gateway));
            var opened = await runtime.OpenAsync(new WorkspacePacketGatewayOpenRequest(new WorkspaceInstanceId("service-oracle"),
                binding, serviceProxy: new WorkspacePacketGatewayServiceProxy(socks.Start(), socks.Credentials)), null, deadline.Token);
            session = Assert.IsType<NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success>(opened).Value;
            var commands = new GuestCommands(provider, binding);
            await using var backend = new WorkspaceDatabaseBackend(commands,
                Path.Combine(Path.GetDirectoryName(archive)!, "backend-assets.json"), Path.Combine(directory.FullName, "cache"));
            using var worker = new DatabaseOperationWorker(() => new DatabaseResultContentStore(Path.Combine(directory.FullName, "results")),
                workspaceLaunch: backend.PlanAsync);

            await ExpectOracleListenerFailureAsync(worker, "oracle-origin.synthetic.invalid", "TCP", deadline.Token);
            Assert.True((await origin.Observed.Reader.ReadAsync(deadline.Token)).Connect);
            Assert.True((await destination.Observed.Reader.ReadAsync(deadline.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), deadline.Token)).Connect);
            Assert.Contains(("oracle-origin.synthetic.invalid", 1521), socks.Calls);
            Assert.Contains(("oracle-destination.synthetic.invalid", 1522), socks.Calls);
            output.WriteLine("Stock ODP.NET followed the synthetic TNS redirect; both private names resolved at the selected SOCKS authority.");

            // Trust only this throwaway VM's fixture certificate. No host trust
            // store, Keychain, saved profile, or user credential is touched.
            var certificate = Convert.ToBase64String(tls.Certificate!.Export(X509ContentType.Cert));
            Assert.Equal("fixture-written", await RunShellAsync(commands, """
                set -eu
                printf '%s' "$1" | base64 -d > /tmp/ghostshell-oracle-fixture.der
                openssl x509 -inform DER -in /tmp/ghostshell-oracle-fixture.der -out /tmp/ghostshell-oracle-fixture.crt
                printf fixture-written
                """, [certificate], deadline.Token));
            var trustRequest = new WorkspaceSdkExecRequest(["/bin/sh", "-c", """
                set -eu
                install -m 644 /tmp/ghostshell-oracle-fixture.crt /usr/local/share/ca-certificates/ghostshell-oracle-fixture.crt
                update-ca-certificates >/dev/null
                """], new Dictionary<string, string>(StringComparer.Ordinal), "/", 0, 0);
            var trustResult = await processes.RunAsync(new WorkspaceGatewayProcessRequest(executable,
                ["exec", "--socket", binding.Network!.HostAttachment!.ControlSocketPath, "--request",
                    Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(trustRequest, WorkspaceSdkJsonContext.Default.WorkspaceSdkExecRequest))],
                ReadOnlyMemory<byte>.Empty), TimeSpan.FromSeconds(30), deadline.Token);
            Assert.True(trustResult.ExitCode == 0, trustResult.Diagnostic);
            foreach (var host in new[] { "oracle-tls.synthetic.invalid", "oracle-wrong.synthetic.invalid" })
            {
                await ExpectOracleListenerFailureAsync(worker, host, "TCPS", deadline.Token);
                var observed = await tls.Observed.Reader.ReadAsync(deadline.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                output.WriteLine($"Oracle TCPS host={host}; SNI={observed.Sni ?? "<none>"}; CONNECT after TLS={observed.Connect}.");
                Assert.Equal(host, observed.Sni);
                Assert.Equal(string.Equals(host, "oracle-tls.synthetic.invalid", StringComparison.Ordinal), observed.Connect);
                // ODP.NET can make multiple listener attempts before reporting
                // the fixture's EOF. Drain and verify every completed attempt
                // before switching identities; a leftover positive observation
                // must never be mistaken for the negative connection's result.
                while (tls.Observed.Reader.TryRead(out var additional))
                {
                    output.WriteLine($"Additional Oracle attempt SNI={additional.Sni ?? "<none>"}; CONNECT after TLS={additional.Connect}.");
                    Assert.Equal(host, additional.Sni);
                    Assert.Equal(string.Equals(host, "oracle-tls.synthetic.invalid", StringComparison.Ordinal), additional.Connect);
                }
            }
        }
        finally
        {
            if (session is not null) { await session.DisposeAsync(); }
            if (binding is not null) { _ = Prepared(await provider.StopAsync(binding, CancellationToken.None)); }
            directory.Delete(recursive: true);
        }
    }

    private static async Task ExpectOracleListenerFailureAsync(DatabaseOperationWorker worker, string host, string protocol,
        CancellationToken token)
    {
        var options = new OracleConnectionStringBuilder
        {
            DataSource = $"(DESCRIPTION=(CONNECT_TIMEOUT=3)(TRANSPORT_CONNECT_TIMEOUT=3)(RETRY_COUNT=0)(ADDRESS=(PROTOCOL={protocol})(HOST={host})(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=fixture))(SECURITY=(SSL_SERVER_DN_MATCH=YES)))",
            UserID = "fixture",
            Password = "fixture",
            Pooling = false,
            ConnectionTimeout = 3,
        };
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        // This server deliberately closes before Oracle authentication. Socket
        // and TLS observations, not a fake successful query, are the evidence.
        await Assert.ThrowsAsync<DatabaseProviderOperationException>(() => worker.QueryAsync(
            new("oracle", options.ConnectionString), "SELECT 1 FROM DUAL", 1, false, bounded.Token));
    }
}

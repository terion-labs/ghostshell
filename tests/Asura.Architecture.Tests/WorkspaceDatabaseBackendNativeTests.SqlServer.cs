using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Asura.Application;
using Asura.ConnectionBackend;
using Asura.Core;
using Asura.Desktop;
using Asura.Files;
using Asura.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TDS;
using Microsoft.SqlServer.TDS.EndPoint;
using Microsoft.SqlServer.TDS.EnvChange;
using Microsoft.SqlServer.TDS.Servers;

namespace Asura.Architecture.Tests;

[Collection(SqlClientRouteCollection.Name)]
public sealed partial class WorkspaceDatabaseBackendNativeTests
{
    [WorkspaceBackendFact]
    public Task Service_guest_SQL_Server_redirects_retries_and_failover_keep_remote_resolution() => VerifySqlServiceAsync(false);

    [ContainerBackendFact]
    public Task Container_relay_SQL_Server_redirects_retries_TLS_and_failover_keep_remote_resolution() => VerifySqlServiceAsync(true);

    private async Task VerifySqlServiceAsync(bool containerRelay)
    {
        var assets = Environment.GetEnvironmentVariable("ASURA_TEST_SDK_RUNTIME_ROOT") ?? string.Empty;
        var archive = Environment.GetEnvironmentVariable("ASURA_WORKSPACE_BACKEND_ARCHIVE")!;
        var directory = Directory.CreateTempSubdirectory("asura-service-tds-");
        var executable = Path.Combine(assets, "workspace-runtime");
        IWorkspaceConnectionServiceProvider provider = containerRelay
            ? new ContainerRelayIsolationProvider(async token =>
            {
                var engine = await RelayContainerEngine.DiscoverAsync(new ContainerTestLocator(), new WorkspaceIsolationCommandRunner(), token);
                return Environment.GetEnvironmentVariable("ASURA_TEST_RELAY_ARCHITECTURE") is { } architecture ? engine with { Architecture = architecture } : engine;
            }, (_, _) => Task.FromResult(archive))
            : WorkspaceSdkIsolationProvider.CreateServiceProvider(executable, Path.Combine(directory.FullName, "state"));
        var gateway = containerRelay ? Environment.GetEnvironmentVariable("ASURA_TEST_RELAY_GATEWAY")!
            : Path.Combine(Path.GetDirectoryName(assets)!, "asura-workspace-gateway-darwin-arm64");
        var processes = new ContainerDiagnosticRunner(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        using var authority = new CancellationTokenSource();
        using var canceledLogin = new CancellationTokenSource();
        using var destination = new SqlClientLoopbackServer(new GenericTDSServer());
        using var origin = new SqlClientLoopbackServer(new RoutingTDSServer(new RoutingTDSServerArguments
        { RoutingTCPHost = "destination.synthetic.invalid", RoutingTCPPort = 15433 }));
        using var retryBehavior = new TransientFaultTDSServer(new TransientFaultTDSServerArguments
        { IsEnabledTransientError = true, Number = 40613, Message = "Synthetic service retry" });
        using var retry = new SqlClientLoopbackServer(retryBehavior);
        using var partner = new SqlClientLoopbackServer(new ServicePartnerServer());
        using var cancellationServer = new SqlClientLoopbackServer(new CancelingServiceServer(canceledLogin));
        await using var tls = new DatabaseServiceTlsFixture();
        await using var socks = new DatabaseServiceSocksFixture((host, port) => (host, port) switch
        {
            ("origin.synthetic.invalid", 1433) => origin.Port,
            ("destination.synthetic.invalid", 15433) => destination.Port,
            ("retry.synthetic.invalid", 1433) => retry.Port,
            ("primary.synthetic.invalid", 1433) => 0,
            ("partner.synthetic.invalid", 1433) => partner.Port,
            ("tls.synthetic.invalid", 1433) => tls.Port,
            ("cancel.synthetic.invalid", 1433) => cancellationServer.Port,
            _ => throw new InvalidOperationException("Unexpected service SQL endpoint."),
        });
        WorkspaceIsolationBinding? binding = null;
        IWorkspacePacketGatewaySession? session = null;
        try
        {
            binding = Prepared(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(
                new WorkspaceId($"service-tds-{Guid.NewGuid():N}")), deadline.Token));
            var runtime = new HostWorkspacePacketGatewayRuntime(new BundledWorkspacePacketGatewayBackend([],
                new WorkspaceHostNetworkRouteLauncher(executable, processes, gateway), processes, gateway));
            var opened = await runtime.OpenAsync(new WorkspacePacketGatewayOpenRequest(new WorkspaceInstanceId("service-tds"),
                binding, serviceProxy: new WorkspacePacketGatewayServiceProxy(socks.Start(), socks.Credentials)), null, deadline.Token);
            Assert.True(opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success,
                opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure failure ? failure.Error.Message : "No gateway returned.");
            session = ((NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success)opened).Value;
            await provider.ConfigureServiceNetworkingAsync(binding, deadline.Token);
            await using var backend = new WorkspaceDatabaseBackend(new GuestCommands(provider, binding),
                Path.Combine(Path.GetDirectoryName(archive)!, "backend-assets.json"), Path.Combine(directory.FullName, "cache"),
                binding.Network?.RelayAttachment?.Architecture ?? "arm64");
            using var worker = new DatabaseOperationWorker(() => new DatabaseResultContentStore(Path.Combine(directory.FullName, "results")),
                workspaceLaunch: async token => (await backend.PlanAsync(token)) with { Lifetime = authority.Token },
                importHostConnectionFiles: true);

            var options = SqlClientLoopbackServer.Options("origin.synthetic.invalid");
            options.ApplicationIntent = ApplicationIntent.ReadOnly;
            options.MultiSubnetFailover = true;
            await VerifyServiceScalarAsync(worker, options, deadline.Token);
            Assert.Contains(("origin.synthetic.invalid", 1433), socks.Calls);
            Assert.Contains(("destination.synthetic.invalid", 15433), socks.Calls);
            output.WriteLine("SQL Server login redirect reached original and redirected DNS names through the service SOCKS authority.");

            options = SqlClientLoopbackServer.Options("retry.synthetic.invalid");
            options.ConnectRetryCount = 1;
            await VerifyServiceScalarAsync(worker, options, deadline.Token);
            Assert.Equal(2, socks.Calls.Count(call => call.Host is "retry.synthetic.invalid"));
            output.WriteLine("Transient login retry stayed on the same service authority.");

            await VerifyServiceTlsAsync(worker, tls, directory.FullName, deadline.Token);
            output.WriteLine("Imported exact TLS pin accepted, wrong pin rejected; both handshakes retained the original logical SNI name.");

            options = SqlClientLoopbackServer.Options("primary.synthetic.invalid");
            options.FailoverPartner = "partner.synthetic.invalid,1433";
            options.InitialCatalog = "fixture";
            await VerifyServiceScalarAsync(worker, options, deadline.Token);
            Assert.Contains(("primary.synthetic.invalid", 1433), socks.Calls);
            Assert.Contains(("partner.synthetic.invalid", 1433), socks.Calls);
            output.WriteLine("Failover partner selection remained in the service VM and remote resolver.");

            using (var canceledWorker = new DatabaseOperationWorker(
                () => new DatabaseResultContentStore(Path.Combine(directory.FullName, "cancel-results")),
                workspaceLaunch: async token => (await backend.PlanAsync(token)) with { Lifetime = canceledLogin.Token }))
            {
                var canceledOptions = SqlClientLoopbackServer.Options("cancel.synthetic.invalid");
                var error = await Record.ExceptionAsync(() => canceledWorker.QueryAsync(new("sqlserver", canceledOptions.ConnectionString),
                    "SELECT 1", 10, false, deadline.Token));
                Assert.True(error is OperationCanceledException or DatabaseMutationOutcomeUnknownException,
                    $"Route revocation must cancel the operation, not reopen it: {error?.GetType().Name}");
                Assert.True(canceledLogin.IsCancellationRequested);
                Assert.Single(socks.Calls, call => call.Host is "cancel.synthetic.invalid");
            }
            output.WriteLine("Revocation during login canceled the child and did not authorize a retry endpoint.");

            await authority.CancelAsync();
            var count = socks.Calls.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.QueryAsync(new("sqlserver", options.ConnectionString),
                "SELECT 1", 10, false, deadline.Token));
            Assert.Equal(count, socks.Calls.Count);
        }
        finally
        {
            if (session is not null) { await session.DisposeAsync(); }
            if (binding is not null) { _ = Prepared(await provider.StopAsync(binding, CancellationToken.None)); }
            directory.Delete(recursive: true);
        }
    }

    private sealed class ContainerTestLocator : IConnectionExecutableLocator
    {
        public string? Find(string executable) => Environment.GetEnvironmentVariable("ASURA_TEST_RELAY_ENGINE") is { } selected
            && !string.Equals(selected, executable, StringComparison.Ordinal) ? null : new PathConnectionExecutableLocator().Find(executable);
    }

    private sealed class ContainerBackendFactAttribute : FactAttribute
    {
        public ContainerBackendFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ASURA_TEST_RELAY_GATEWAY") is null
                || Environment.GetEnvironmentVariable("ASURA_WORKSPACE_BACKEND_ARCHIVE") is null)
            {
                Skip = "Requires explicit disposable-container backend integration assets.";
            }
        }
    }

    private sealed class ContainerDiagnosticRunner(Xunit.Abstractions.ITestOutputHelper output) : IWorkspaceGatewayProcessRunner
    {
        private readonly WorkspaceGatewayProcessRunner _runner = new();
        public async ValueTask<WorkspaceGatewayProcessStart> StartAsync(WorkspaceGatewayProcessRequest request, TimeSpan timeout, CancellationToken token)
        {
            var started = await _runner.StartAsync(request, timeout, token);
            started.Process.Exited += (_, _) => output.WriteLine("Relay test process exit: " + started.Process.Diagnostic);
            return started;
        }
        public ValueTask<WorkspaceGatewayCommandResult> RunAsync(WorkspaceGatewayProcessRequest request, TimeSpan timeout, CancellationToken token) =>
            _runner.RunAsync(request, timeout, token);
    }

    private static async Task VerifyServiceTlsAsync(DatabaseOperationWorker worker, DatabaseServiceTlsFixture server,
        string directory, CancellationToken token)
    {
        using var otherKey = RSA.Create(2048);
        using var wrongCertificate = new CertificateRequest("CN=wrong.synthetic.invalid", otherKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        foreach (var correct in new[] { true, false })
        {
            var pin = Path.Combine(directory, correct ? "server.cer" : "wrong.cer");
            await File.WriteAllBytesAsync(pin, (correct ? server.Certificate : wrongCertificate).Export(X509ContentType.Cert), token);
            var options = SqlClientLoopbackServer.Options("tls.synthetic.invalid");
            options.Encrypt = SqlConnectionEncryptOption.Strict;
            options.ServerCertificate = pin;
            options.ConnectRetryCount = 0;
            options.Pooling = false;
            // The controlled endpoint deliberately stops after TLS and one
            // TDS byte. A provider error is expected, never a query result.
            await Assert.ThrowsAsync<DatabaseProviderOperationException>(() => worker.QueryAsync(
                new("sqlserver", options.ConnectionString), "SELECT 1", 10, false, token));
            var handshake = await server.Handshakes.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal("tls.synthetic.invalid", handshake.Name);
            Assert.Equal(correct, handshake.ReceivedTds);
        }
    }

    private static async Task VerifyServiceScalarAsync(DatabaseOperationWorker worker, SqlConnectionStringBuilder options,
        CancellationToken token)
    {
        using var result = await worker.QueryAsync(new("sqlserver", options.ConnectionString), "SELECT 1", 10, false, token);
        Assert.Equal(1L, Convert.ToInt64(Assert.Single(Assert.Single(result.ValueRows)).RawValue,
            System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class ServicePartnerServer : GenericTDSServer
    {
        public override TDSMessageCollection OnLogin7Request(ITDSServerSession session, TDSMessage request)
        {
            var messages = base.OnLogin7Request(session, request);
            var login = messages[^1];
            login.Insert(login.Count - 1, new TDSEnvChangeToken(TDSEnvChangeTokenType.RealTimeLogShipping, "primary.synthetic.invalid"));
            return messages;
        }
    }

    private sealed class CancelingServiceServer(CancellationTokenSource lifetime) : GenericTDSServer
    {
        public override TDSMessageCollection OnLogin7Request(ITDSServerSession session, TDSMessage request)
        {
            lifetime.Cancel();
            return base.OnLogin7Request(session, request);
        }
    }
}

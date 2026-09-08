using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GhostShell.Application;
using GhostShell.ConnectionBackend;
using GhostShell.Core;
using GhostShell.Desktop;
using GhostShell.Files;
using GhostShell.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TDS;
using Microsoft.SqlServer.TDS.EndPoint;
using Microsoft.SqlServer.TDS.EnvChange;
using Microsoft.SqlServer.TDS.Servers;

namespace GhostShell.Architecture.Tests;

[Collection(SqlClientRouteCollection.Name)]
public sealed partial class WorkspaceDatabaseBackendNativeTests
{
    [WorkspaceBackendFact]
    public async Task Service_guest_SQL_Server_redirects_retries_and_failover_keep_remote_resolution()
    {
        var assets = Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_SDK_RUNTIME_ROOT")!;
        var archive = Environment.GetEnvironmentVariable("GHOSTSHELL_WORKSPACE_BACKEND_ARCHIVE")!;
        var directory = Directory.CreateTempSubdirectory("ghostshell-service-tds-");
        var executable = Path.Combine(assets, "workspace-runtime");
        var provider = WorkspaceSdkIsolationProvider.CreateServiceProvider(executable, Path.Combine(directory.FullName, "state"));
        var gateway = Path.Combine(Path.GetDirectoryName(assets)!, "ghostshell-workspace-gateway-darwin-arm64");
        var processes = new WorkspaceGatewayProcessRunner();
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
                new WorkspaceId($"tds-{Guid.NewGuid():N}")), deadline.Token));
            await provider.ConfigureServiceNetworkingAsync(binding, deadline.Token);
            var runtime = new HostWorkspacePacketGatewayRuntime(new BundledWorkspacePacketGatewayBackend([],
                new WorkspaceHostNetworkRouteLauncher(executable, processes), processes, gateway));
            var opened = await runtime.OpenAsync(new WorkspacePacketGatewayOpenRequest(new WorkspaceInstanceId("service-tds"),
                binding, serviceProxy: new WorkspacePacketGatewayServiceProxy(socks.Start(), socks.Credentials)), null, deadline.Token);
            Assert.True(opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success,
                opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure failure ? failure.Error.Message : "No gateway returned.");
            session = ((NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success)opened).Value;
            await using var backend = new WorkspaceDatabaseBackend(new GuestCommands(provider, binding),
                Path.Combine(Path.GetDirectoryName(archive)!, "backend-assets.json"), Path.Combine(directory.FullName, "cache"));
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

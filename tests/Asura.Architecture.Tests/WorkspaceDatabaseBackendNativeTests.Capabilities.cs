using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.ConnectionBackend;
using Asura.Core;
using Asura.Desktop;
using Asura.Files;
using Asura.Infrastructure;

namespace Asura.Architecture.Tests;

public sealed partial class WorkspaceDatabaseBackendNativeTests
{
    private async Task VerifyGuestFileProviderAsync(WorkspaceDatabaseBackend backend, GuestCommands commands, CancellationToken token)
    {
        using var vault = new InMemorySecretVault();
        var profile = new FileProviderProfile(new("guest-files"), 1, "Guest fixture files", new FileProviderConfiguration.Local("/home/asura"));
        var factory = new WorkspaceFileProviderFactory(vault, new NoFixtureSshTrust(), null,
            cancellation => backend.PlanAsync("files", cancellation), localInWorkspace: true);
        using var owned = await factory.CreateAsync(profile, new Dictionary<ConnectionId, ConnectionProfile>(), token);
        var provider = owned.Registration.Provider;
        Assert.False(provider is ILocalFilePathSource);
        var location = owned.Registration.Root.Child(new("guest-backend-content.bin"));
        var content = new byte[192 * 1024 + 7];
        Random.Shared.NextBytes(content);
        using var source = new MemoryStream(content, writable: false);
        var write = await provider.WriteAsync(new(location, content.Length, 128 * 1024, new FileMutationPrecondition.MustNotExist()), source, null, token);
        Assert.True(write.IsSuccess, write.Error?.Message);
        Assert.Equal(content.Length, write.Value?.BytesWritten);
        var stat = await provider.StatAsync(new(location), token);
        Assert.True(stat.IsSuccess, stat.Error?.Message);
        Assert.Equal(content.Length, stat.Value?.Size);
        using var target = new MemoryStream();
        var read = await provider.ReadAsync(new(location, 0, content.Length, 128 * 1024), target, null, token);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(content, target.ToArray());
        var page = await provider.ListAsync(new(owned.Registration.Root, 100), token);
        Assert.True(page.IsSuccess, page.Error?.Message);
        Assert.Contains(page.Value!.Items, item => item.Location.Path.Name?.Value == "guest-backend-content.bin");
        var copy = owned.Registration.Root.Child(new("guest-backend-copy.bin"));
        var transfer = await provider.TransferAsync(new(location, copy, FileTransferKind.Copy, 65536, new FileMutationPrecondition.MustNotExist()), null, token);
        Assert.True(transfer.IsSuccess, transfer.Error?.Message);
        var removed = await provider.DeleteAsync(new(copy, false, new FileMutationPrecondition.MustExist()), token);
        Assert.True(removed.IsSuccess, removed.Error?.Message);
        Assert.Equal("guest-only", await RunShellAsync(commands,
            "test -s /home/asura/guest-backend-content.bin && test ! -e /home/asura/guest-backend-copy.bin && printf guest-only", [], token));
        Assert.False(File.Exists("/home/asura/guest-backend-content.bin"));
        output.WriteLine("Actual files backend streamed 192 KiB through private pipes; stat/list/copy/delete operated only on the guest filesystem.");
    }

    private async Task ProvisionGuestNetworkFixturesAsync(string assets, WorkspaceIsolationBinding binding, CancellationToken token)
    {
        var executable = Path.Combine(assets, "workspace-runtime");
        var gateway = Path.Combine(Path.GetDirectoryName(assets)!, "asura-workspace-gateway-darwin-arm64");
        var processes = new WorkspaceGatewayProcessRunner();
        var runtime = new HostWorkspacePacketGatewayRuntime(new BundledWorkspacePacketGatewayBackend([],
            new WorkspaceHostNetworkRouteLauncher(executable, processes), processes, gateway));
        var opened = await runtime.OpenAsync(new WorkspacePacketGatewayOpenRequest(new WorkspaceInstanceId("backend-fixture-provision"), binding), null, token);
        Assert.True(opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success,
            opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure failure ? failure.Error.Message : "No fixture gateway returned.");
        await using (var route = ((NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success)opened).Value)
        {
            // This is the test's fresh, unmounted VM. Distro Redis/Python are test
            // fixtures only: they are never added to product archives or user VMs.
            var request = new WorkspaceSdkExecRequest(["/bin/sh", "-c", """
                set -eu
                export DEBIAN_FRONTEND=noninteractive
                apt-get update
                apt-get install -y --no-install-recommends redis-server python3 openssh-server
                systemctl stop redis-server || true
                systemctl stop ssh || true
                mkdir -p /run/sshd
                ssh-keygen -A
                systemd-run --unit=asura-test-sshd --collect /usr/sbin/sshd -D -e -f /dev/null -h /etc/ssh/ssh_host_ed25519_key -p 12222 -o ListenAddress=127.0.0.1 -o PasswordAuthentication=no -o UsePAM=yes -o 'Subsystem=sftp internal-sftp'
                systemd-run --unit=asura-test-redis --collect /usr/bin/redis-server --bind 127.0.0.1 --port 16379 --save '' --appendonly no
                systemd-run --unit=asura-test-http --collect /usr/bin/python3 -m http.server 18081 --bind 127.0.0.1 --directory /home/asura
                for attempt in $(seq 1 100); do
                    if redis-cli -h 127.0.0.1 -p 16379 ping && curl --noproxy '*' --fail --silent http://127.0.0.1:18081/guest-backend-content.bin >/dev/null; then
                        printf 'fixtures-ready\n'
                        exit 0
                    fi
                    sleep 0.1
                done
                exit 1
                """], new Dictionary<string, string>(StringComparer.Ordinal), "/", 0, 0);
            var result = await processes.RunAsync(new WorkspaceGatewayProcessRequest(executable,
                ["exec", "--socket", binding.Network!.HostAttachment!.ControlSocketPath, "--request",
                    Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request, WorkspaceSdkJsonContext.Default.WorkspaceSdkExecRequest))],
                ReadOnlyMemory<byte>.Empty), TimeSpan.FromMinutes(10), token);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("fixtures-ready", result.Diagnostic, StringComparison.Ordinal);
        }
        output.WriteLine("Distro Redis and Python HTTP fixtures installed only in the throwaway VM; provisioning gateway closed before client tests.");
    }

    private async Task VerifyGuestSftpAsync(WorkspaceDatabaseBackend backend, GuestCommands commands, CancellationToken token)
    {
        using var key = RSA.Create(2048);
        var keyBytes = Encoding.UTF8.GetBytes(key.ExportRSAPrivateKeyPem());
        using var vault = new InMemorySecretVault();
        var reference = new SecretRef("native-sftp-fixture-key");
        var connection = new ConnectionProfile(new("native-sftp-fixture"), 1, "Guest SFTP fixture",
            new ConnectionEndpoint.Ssh("127.0.0.1", 12222, "asura"),
            new ConnectionAuthentication.PrivateKey(reference), ConnectionStartup.Default, ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);
        try
        {
            using var material = SecretMaterial.CopyFrom(keyBytes);
            _ = await vault.CreateAsync(new(reference, "Fixture key", SecretKind.PrivateKey,
                new(SecretScopeKind.Connection, connection.Id.Value), new(SecretUseKind.ConnectionAuthentication, connection.Id.Value)), material, token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
        var hostKey = (await RunShellAsync(commands, "cut -d ' ' -f 1,2 /etc/ssh/ssh_host_ed25519_key.pub", [], token)).Split(' ');
        var trust = new PinnedFixtureSshTrust(connection.Id, new(hostKey[0], hostKey[1]));
        var profile = new FileProviderProfile(new("native-sftp-files"), 1, "Guest SFTP", new FileProviderConfiguration.Sftp(connection.Id, "/home/asura"));
        using (var credentials = new FileWorkspaceHostCredentials(profile, connection, vault, trust, null))
        {
            var identities = await credentials.HandleAsync(new(FileWorkspaceMessageKind.Identities), token);
            var publicKey = identities.Identities![0].PublicKey;
            Assert.Equal("authorized", await RunShellAsync(commands,
                "umask 077; mkdir -p /home/asura/.ssh; printf 'ssh-rsa %s\\n' \"$1\" > /home/asura/.ssh/authorized_keys; printf authorized",
                [Convert.ToBase64String(publicKey)], token));
        }
        var factory = new WorkspaceFileProviderFactory(vault, trust, null, cancellation => backend.PlanAsync("files", cancellation));
        using var owned = await factory.CreateAsync(profile, new Dictionary<ConnectionId, ConnectionProfile> { [connection.Id] = connection }, token);
        var provider = owned.Registration.Provider;
        var location = owned.Registration.Root.Child(new("sftp-callback-proof.bin"));
        var bytes = new byte[96 * 1024 + 3];
        Random.Shared.NextBytes(bytes);
        using var source = new MemoryStream(bytes, writable: false);
        var write = await provider.WriteAsync(new(location, bytes.Length, 65536, new FileMutationPrecondition.MustNotExist()), source, null, token);
        Assert.True(write.IsSuccess, write.Error?.Message);
        using var destination = new MemoryStream();
        var read = await provider.ReadAsync(new(location, 0, bytes.Length, 65536), destination, null, token);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(bytes, destination.ToArray());
        var listing = await provider.ListAsync(new(owned.Registration.Root, 100), token);
        Assert.True(listing.IsSuccess, listing.Error?.Message);
        Assert.Contains(listing.Value!.Items, item => item.Location.Path.Name?.Value == "sftp-callback-proof.bin");
        var deleted = await provider.DeleteAsync(new(location, false, new FileMutationPrecondition.MustExist()), token);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.True(trust.Verifications > 0);
        output.WriteLine("Actual guest SFTP child authenticated through host-only ephemeral key signing and pinned host-key callbacks, then streamed/listed/deleted 96 KiB against the throwaway guest SSH server.");
    }

    private sealed class PinnedFixtureSshTrust(ConnectionId connection, SshHostKeyCandidate expected) : ISshHostKeyTrustStore
    {
        internal int Verifications { get; private set; }

        public SshHostKeyVerification Verify(ConnectionId connectionId, SshHostKeyPolicy policy, SshHostKeyCandidate presented)
        {
            Assert.Equal(connection, connectionId);
            Assert.Equal(SshHostKeyPolicy.Strict, policy);
            Assert.Equal(expected, presented);
            Verifications++;
            return SshHostKeyVerification.Trusted;
        }
    }

    private async Task VerifyGuestRedisAndHttpAsync(WorkspaceDatabaseBackend backend, GuestCommands commands, CancellationToken token)
    {
        using var handler = new WorkspaceHttpMessageHandler(cancellation => backend.PlanAsync("http", cancellation));
        using var http = new HttpClient(handler);
        using var response = await http.GetAsync(new Uri("http://127.0.0.1:18081/guest-backend-content.bin"), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(192 * 1024 + 7, (await response.Content.ReadAsByteArrayAsync(token)).Length);
        var factory = new RedisWorkspaceSessionFactory((_, cancellation) => backend.PlanAsync("redis", cancellation));
        await using var redis = await factory.OpenAsync("127.0.0.1:16379,abortConnect=true,connectTimeout=10000,syncTimeout=10000", null, token);
        Assert.Equal(RedisTopologyKind.Standalone, redis.Facts.Topology);
        await redis.SelectDatabaseAsync(1, token);
        Assert.Equal(1, redis.Facts.SelectedDatabase);
        var key = new RedisKeyReference("fixture-binary-key", [0, 1, 2, 255, 128, 10]);
        var value = new string('x', 128 * 1024);
        await redis.SetStringAsync(key, value, TimeSpan.FromMinutes(1), token);
        var read = await redis.ReadKeyAsync(key, 5000, token);
        Assert.Equal(value[..5000], Assert.Single(read.Entries).Value);
        Assert.Equal(value.Length, read.Length);
        Assert.True(read.Truncated);
        Assert.Equal(key.Bytes, read.Summary.Key.Bytes);
        var scan = await redis.ScanKeysAsync("*", null, 100, token);
        Assert.Contains(scan.Keys, candidate => candidate.Key.Bytes.SequenceEqual(key.Bytes));
        var subscription = new RedisSubscription(RedisSubscriptionKind.Channel, "guest-fixture-channel");
        var received = new TaskCompletionSource<RedisPubSubMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.MessageReceived += (_, message) => received.TrySetResult(message);
        await redis.SubscribeAsync(subscription, token);
        Assert.True(await redis.PublishAsync(subscription.Name, "guest-pubsub", false, token) >= 1);
        Assert.Equal("guest-pubsub", (await received.Task.WaitAsync(TimeSpan.FromSeconds(10), token)).Payload);
        await redis.UnsubscribeAsync(subscription, token);
        Assert.True(await redis.DeleteKeyAsync(key, token));
        Assert.Equal("still-blocked", await RunShellAsync(commands, """
            if timeout 3 /bin/bash -c 'exec 3<>/dev/tcp/1.1.1.1/443'; then exit 1; fi
            printf still-blocked
            """, [], token));
        output.WriteLine("Actual guest HTTP child streamed the guest-only file; actual Redis child verified a binary key with a 128 KiB value, facts, database selection, scan/delete and pub/sub while external networking stayed blocked.");
    }

    private sealed class NoFixtureSshTrust : ISshHostKeyTrustStore
    {
        public SshHostKeyVerification Verify(ConnectionId connectionId, SshHostKeyPolicy policy, SshHostKeyCandidate presented) =>
            throw new InvalidOperationException("Guest-local fixture must not ask for host SSH trust.");
    }
}

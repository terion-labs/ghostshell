using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class WorkspaceNetworkPasswordRecoveryTests
{
    private static readonly NetworkConnectionId ConnectionId = new("password-recovery");
    private static readonly SecretRef PasswordRef = new("stored-password");
    private static readonly SecretScope Scope = new(SecretScopeKind.NetworkConnection, ConnectionId.Value);
    private static readonly SecretUsePurpose Purpose = new(SecretUseKind.UserManagement, ConnectionId.Value);

    [Theory]
    [InlineData(NetworkConnectionKind.Proxy, false)]
    [InlineData(NetworkConnectionKind.AnyConnect, false)]
    [InlineData(NetworkConnectionKind.OpenVpn, false)]
    [InlineData(NetworkConnectionKind.Proxy, true)]
    [InlineData(NetworkConnectionKind.AnyConnect, true)]
    [InlineData(NetworkConnectionKind.OpenVpn, true)]
    public async Task Rejected_password_retries_once_then_offers_to_update_the_same_vault_entry(
        NetworkConnectionKind kind, bool isolated)
    {
        using var vault = await CreateVaultAsync();
        var route = new Route(kind);
        var prompt = new Prompt(route)
        {
            Save = true,
            BeforeSave = async () => Assert.Equal("old-password", await ReadPasswordAsync(vault)),
        };
        var profile = Profile(kind);
        var runtime = new WorkspaceNetworkRuntime([route], passwordPrompt: prompt, packetGatewayRuntime: route, secretVault: vault);

        await using var session = await runtime.OpenAsync(Request(profile, isolated), null, default);

        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
        Assert.Equal(2, route.Attempts);
        Assert.Single(prompt.Requests);
        Assert.True(prompt.Requests[0].StoredPasswordRejected);
        Assert.Equal(1, prompt.SaveOffers);
        Assert.Equal("replacement", await ReadPasswordAsync(vault));
        Assert.Equal(Profile(kind), profile);
        Assert.Equal(Profile(kind, stored: false), route.LastProfile);
        Assert.Equal("replacement", route.ObservedPassword);
        Assert.True(route.LastMaterial!.IsDisposed);
        Assert.True(prompt.Material!.IsDisposed);
    }

    [Fact]
    public async Task Declining_save_keeps_the_connection_but_preserves_the_old_password()
    {
        using var vault = await CreateVaultAsync();
        var route = new Route(NetworkConnectionKind.Proxy);
        var prompt = new Prompt(route);
        var runtime = new WorkspaceNetworkRuntime([route], passwordPrompt: prompt, secretVault: vault);
        await using var session = await runtime.OpenAsync(Request(Profile(route.Kind)), null, default);

        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
        Assert.Equal(1, prompt.SaveOffers);
        Assert.Equal("old-password", await ReadPasswordAsync(vault));
        Assert.True(prompt.Material!.IsDisposed);
    }

    [Theory]
    [InlineData(NetworkConnectionErrorCode.ConnectionFailed)]
    [InlineData(NetworkConnectionErrorCode.RouteUnavailable)]
    [InlineData(NetworkConnectionErrorCode.AuthenticationRequired)]
    [InlineData(NetworkConnectionErrorCode.Cancelled)]
    public async Task Non_rejection_errors_never_prompt_for_password_replacement(NetworkConnectionErrorCode error)
    {
        using var vault = await CreateVaultAsync();
        var route = new Route(NetworkConnectionKind.Proxy) { InitialError = error };
        var prompt = new Prompt(route) { Save = true };
        var runtime = new WorkspaceNetworkRuntime([route], passwordPrompt: prompt, secretVault: vault);
        await using var session = await runtime.OpenAsync(Request(Profile(route.Kind)), null, default);

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(1, route.Attempts);
        Assert.Empty(prompt.Requests);
        Assert.Equal(0, prompt.SaveOffers);
        Assert.Equal("old-password", await ReadPasswordAsync(vault));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_replacement_or_cancel_does_not_loop_or_write_the_vault(bool cancel)
    {
        using var vault = await CreateVaultAsync();
        var route = new Route(NetworkConnectionKind.Proxy) { RejectReplacement = true };
        var prompt = new Prompt(route) { Save = true, Cancel = cancel };
        var runtime = new WorkspaceNetworkRuntime([route], passwordPrompt: prompt, secretVault: vault);
        await using var session = await runtime.OpenAsync(Request(Profile(route.Kind)), null, default);

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(cancel ? 1 : 2, route.Attempts);
        Assert.Single(prompt.Requests);
        Assert.Equal(0, prompt.SaveOffers);
        Assert.Equal("old-password", await ReadPasswordAsync(vault));
    }

    [Fact]
    public async Task Missing_stored_password_does_not_trigger_a_replacement_loop()
    {
        var route = new Route(NetworkConnectionKind.Proxy);
        var prompt = new Prompt(route);
        var runtime = new WorkspaceNetworkRuntime([route], passwordPrompt: prompt);
        await using var session = await runtime.OpenAsync(Request(Profile(route.Kind, stored: false)), null, default);

        Assert.Equal(1, route.Attempts);
        Assert.False(Assert.Single(prompt.Requests).StoredPasswordRejected);
        Assert.Equal(0, prompt.SaveOffers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_edited_or_removed_during_prompt_is_not_overwritten(bool remove)
    {
        using var vault = await CreateVaultAsync();
        var route = new Route(NetworkConnectionKind.Proxy);
        var prompt = new Prompt(route)
        {
            Save = true,
            BeforeSave = async () =>
            {
                if (remove)
                {
                    _ = await vault.DeleteAsync(new DeleteSecretRequest(PasswordRef, Scope, Purpose), default);
                }
                else
                {
                    using var edited = SecretMaterial.CopyFrom("edited-password"u8);
                    _ = await vault.ReplaceAsync(new ReplaceSecretRequest(PasswordRef, Scope, Purpose), edited, default);
                }
            },
        };
        var runtime = new WorkspaceNetworkRuntime([route], passwordPrompt: prompt, secretVault: vault);
        await using var session = await runtime.OpenAsync(Request(Profile(route.Kind)), null, default);

        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
        Assert.Equal(1, prompt.SaveFailures);
        if (!remove)
        {
            Assert.Equal("edited-password", await ReadPasswordAsync(vault));
        }
    }

    [Fact]
    public async Task Successful_authentication_with_an_invalid_route_does_not_offer_to_save()
    {
        using var vault = await CreateVaultAsync();
        var route = new Route(NetworkConnectionKind.Proxy) { InvalidRoute = true };
        var prompt = new Prompt(route) { Save = true };
        var runtime = new WorkspaceNetworkRuntime([route], passwordPrompt: prompt, secretVault: vault);
        await using var session = await runtime.OpenAsync(Request(Profile(route.Kind)), null, default);

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(0, prompt.SaveOffers);
        Assert.True(route.Session!.Disposed);
        Assert.Equal("old-password", await ReadPasswordAsync(vault));
    }

    [Fact]
    public async Task Vault_failure_after_authentication_preserves_route_ownership_and_notifies()
    {
        using var vault = await CreateVaultAsync();
        var route = new Route(NetworkConnectionKind.Proxy);
        var prompt = new Prompt(route)
        {
            Save = true,
            BeforeSave = () => { vault.Dispose(); return Task.CompletedTask; },
        };
        var runtime = new WorkspaceNetworkRuntime([route], passwordPrompt: prompt, secretVault: vault);
        await using var session = await runtime.OpenAsync(Request(Profile(route.Kind)), null, default);

        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
        Assert.False(route.Session!.Disposed);
        Assert.Equal(1, prompt.SaveFailures);
    }

    [Fact]
    public async Task Cancellation_during_save_confirmation_disposes_the_route_and_replacement()
    {
        using var vault = await CreateVaultAsync();
        using var cancellation = new CancellationTokenSource();
        var route = new Route(NetworkConnectionKind.Proxy);
        var prompt = new Prompt(route)
        {
            Save = true,
            BeforeSave = () =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        var runtime = new WorkspaceNetworkRuntime([route], passwordPrompt: prompt, secretVault: vault);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var session = await runtime.OpenAsync(Request(Profile(route.Kind)), null, cancellation.Token);
        });

        Assert.True(route.Session!.Disposed);
        Assert.True(prompt.Material!.IsDisposed);
        Assert.Equal("old-password", await ReadPasswordAsync(vault));
    }

    private static NetworkConnectionProfile Profile(NetworkConnectionKind kind, bool stored = true)
    {
        SecretRef? password = stored ? PasswordRef : null;
        NetworkConnectionConfiguration configuration = kind switch
        {
            NetworkConnectionKind.AnyConnect => new NetworkConnectionConfiguration.AnyConnect(
                new Uri("https://vpn.example"), "alice", password, "staff", new SecretRef("certificate")),
            NetworkConnectionKind.OpenVpn => new NetworkConnectionConfiguration.OpenVpn(new SecretRef("config"), "alice", password),
            _ => new NetworkConnectionConfiguration.Proxy(NetworkProxyProtocol.Http, "proxy.example", 8080, "alice", password),
        };
        return new NetworkConnectionProfile(ConnectionId, NetworkConnectionProfile.CurrentSchemaVersion, "Test connection", configuration);
    }

    private static WorkspaceNetworkOpenRequest Request(NetworkConnectionProfile profile, bool isolated = false) => new(
        new WorkspaceInstanceId("password-workspace"),
        new WorkspaceNetworkPolicyUpdate(new NetworkPolicy([ConnectionId], ConnectionId, true,
            isolated || profile.ConnectionKind == NetworkConnectionKind.Proxy), [profile]),
        isolated ? WorkspaceNetworkPlacement.Isolated(new WorkspaceIsolationBinding(
            new WorkspaceId("password-workspace"), new WorkspaceIsolationProviderId("test"),
            WorkspaceIsolationCapability.DedicatedNetworkNamespace, "test-isolate", [], Guid.NewGuid())) : WorkspaceNetworkPlacement.Host);

    private static async Task<InMemorySecretVault> CreateVaultAsync()
    {
        var vault = new InMemorySecretVault();
        using var password = SecretMaterial.CopyFrom("old-password"u8);
        Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(await vault.CreateAsync(
            new CreateSecretRequest(PasswordRef, "Stored password", SecretKind.Password, Scope, Purpose), password, default));
        return vault;
    }

    private static async Task<string> ReadPasswordAsync(InMemorySecretVault vault)
    {
        var result = await vault.ResolveAsync(new ResolveSecretRequest(PasswordRef, Scope,
            new SecretUsePurpose(SecretUseKind.NetworkConnectionAuthentication, ConnectionId.Value)), default);
        using var material = Assert.IsType<SecretVaultResult<SecretMaterial>.Success>(result).Value;
        return Read(material);
    }

    private static string Read(SecretMaterial material)
    {
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class Prompt(Route route) : INetworkPasswordPrompt
    {
        public bool Save { get; init; }
        public bool Cancel { get; init; }
        public Func<Task>? BeforeSave { get; init; }
        public List<NetworkPasswordPromptRequest> Requests { get; } = [];
        public int SaveOffers { get; private set; }
        public int SaveFailures { get; private set; }
        public SecretMaterial? Material { get; private set; }

        public ValueTask<NetworkConnectionResult<SecretMaterial>> RequestPasswordAsync(NetworkPasswordPromptRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Cancel)
            {
                return ValueTask.FromResult(NetworkConnectionResult<SecretMaterial>.Fail(
                    new NetworkConnectionError(NetworkConnectionErrorCode.Cancelled, "cancelled", "Cancelled.", true)));
            }

            Material = SecretMaterial.CopyFrom("replacement"u8);
            return ValueTask.FromResult(NetworkConnectionResult<SecretMaterial>.Succeed(Material));
        }

        public async ValueTask<bool> ConfirmPasswordUpdateAsync(NetworkPasswordPromptRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal(2, route.Attempts);
            Assert.False(route.RejectReplacement);
            SaveOffers++;
            if (BeforeSave is not null)
            {
                await BeforeSave();
            }

            return Save;
        }

        public ValueTask NotifyPasswordUpdateFailedAsync(NetworkPasswordPromptRequest request, CancellationToken cancellationToken)
        {
            SaveFailures++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Route(NetworkConnectionKind kind) : INetworkConnectionProvider, IWorkspacePacketGatewayRuntime
    {
        public NetworkConnectionKind Kind => kind;
        public NetworkConnectionErrorCode InitialError { get; init; } = NetworkConnectionErrorCode.AuthenticationRejected;
        public bool RejectReplacement { get; init; }
        public bool InvalidRoute { get; init; }
        public HostSession? Session { get; private set; }
        public int Attempts { get; private set; }
        public NetworkConnectionProfile? LastProfile { get; private set; }
        public SecretMaterial? LastMaterial { get; private set; }
        public string? ObservedPassword { get; private set; }

        private bool Start(NetworkConnectionProfile profile, SecretMaterial? password)
        {
            Attempts++;
            LastProfile = profile;
            LastMaterial = password;
            ObservedPassword = password is null ? null : Read(password);
            return Attempts > 1 && !RejectReplacement;
        }

        private NetworkConnectionError Error => new(InitialError, "test_start_failed", "Authentication or route failure.", false);

        public ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
            NetworkConnectionStartRequest request, IProgress<NetworkConnectionProgress>? progress, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Start(request.Connection, request.TransientPassword)
                ? NetworkConnectionResult<INetworkConnectionSession>.Succeed(Session = new HostSession(InvalidRoute))
                : NetworkConnectionResult<INetworkConnectionSession>.Fail(Error));

        public ValueTask<NetworkConnectionResult<IWorkspacePacketGatewaySession>> OpenAsync(
            WorkspacePacketGatewayOpenRequest request, IProgress<NetworkConnectionProgress>? progress, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Start(request.Connection!, request.TransientPassword)
                ? NetworkConnectionResult<IWorkspacePacketGatewaySession>.Succeed(new GatewaySession())
                : NetworkConnectionResult<IWorkspacePacketGatewaySession>.Fail(Error));
    }

    private sealed class HostSession(bool invalidRoute) : INetworkConnectionSession
    {
        public bool Disposed { get; private set; }
        public NetworkConnectionSnapshot Snapshot { get; } = new(ConnectionId, NetworkConnectionState.Connected);
        public WorkspaceNetworkEgress Egress => invalidRoute ? WorkspaceNetworkEgress.Blocked
            : WorkspaceNetworkEgress.ViaProxy(new Uri("socks5://127.0.0.1:43123"));
        public event EventHandler<NetworkConnectionSnapshot>? Changed { add { } remove { } }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatewaySession : IWorkspacePacketGatewaySession
    {
        public WorkspacePacketGatewaySnapshot Snapshot { get; } = new(WorkspacePacketGatewayState.Ready,
            new WorkspacePacketRouteCapabilities(WorkspaceIpAddressFamilies.Ipv4, WorkspaceIpProtocolCapabilities.All, 1500));
        public event EventHandler<WorkspacePacketGatewaySnapshot>? Changed { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

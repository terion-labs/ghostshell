using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class ConnectionCredentialBrokerTests
{
    private static readonly TimeSpan TestConnectTimeout = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void Private_helper_classifier_requires_the_credential_marker_first()
    {
        Assert.True(ConnectionCredentialProcessHost.IsPrivateHelperInvocation(
            [ConnectionCredentialSessionInvocation.Marker, "metadata"],
            hasAskpassPipe: false));
        Assert.False(ConnectionCredentialProcessHost.IsPrivateHelperInvocation(
            ["--type=renderer", ConnectionCredentialSessionInvocation.Marker],
            hasAskpassPipe: false));
    }

    [Fact]
    public void Private_helper_classifier_keeps_normal_cef_processes_in_cef_dispatch()
    {
        Assert.False(ConnectionCredentialProcessHost.IsPrivateHelperInvocation(
            ["--type=gpu-process", "--lang=en-US"],
            hasAskpassPipe: false));
        Assert.True(ConnectionCredentialProcessHost.IsPrivateHelperInvocation(
            ["Password:"],
            hasAskpassPipe: true));
    }

    [Fact]
    public async Task Password_ticket_is_one_use_connection_bound_and_contains_no_credential_value()
    {
        const string credential = "password-canary-1942";
        var reference = new SecretRef("password-ref");
        using var vault = new BrokerSecretVault();
        vault.Add(reference, "ssh-password", credential);
        await using var broker = CreateBroker(vault);
        var request = Request(
            "ssh-password",
            ConnectionKind.Ssh,
            ConnectionAuthenticationMode.Password,
            [new ConnectionSecretRequirement(ConnectionSecretRole.Password, reference)]);

        var launch = Success(await broker.PrepareLaunchAsync(request, CancellationToken.None));
        var invocation = ConnectionCredentialSessionInvocation.Parse(launch.Arguments);
        Assert.NotNull(invocation);
        Assert.Equal(request.Launch.ConnectionMetadata, launch.ConnectionMetadata);
        Assert.Equal(request.Launch.Keymap, launch.Keymap);

        Assert.DoesNotContain(credential, launch.Executable!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            launch.Arguments,
            argument => argument.Contains(credential, StringComparison.Ordinal));
        Assert.DoesNotContain(
            launch.Environment.Values,
            value => value.Contains(credential, StringComparison.Ordinal));
        Assert.DoesNotContain(credential, launch.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(credential, request.ToString(), StringComparison.Ordinal);

        var wrongToken = invocation.Access with { Token = new string('0', invocation.Access.Token.Length) };
        var denied = await ConnectionCredentialBrokerClient.ClaimAsync(
            wrongToken,
            TestConnectTimeout,
            CancellationToken.None);
        Assert.Equal(
            ConnectionCredentialClaimStatus.Denied,
            Assert.IsType<ConnectionCredentialClaimResult.Failure>(denied).Status);

        var wrongConnection = invocation.Access with { ConnectionId = new ConnectionId("other-connection") };
        denied = await ConnectionCredentialBrokerClient.ClaimAsync(
            wrongConnection,
            TestConnectTimeout,
            CancellationToken.None);
        Assert.Equal(
            ConnectionCredentialClaimStatus.Denied,
            Assert.IsType<ConnectionCredentialClaimResult.Failure>(denied).Status);

        var claimed = await ConnectionCredentialBrokerClient.ClaimAsync(
            invocation.Access,
            TestConnectTimeout,
            CancellationToken.None);
        using (var claim = Assert.IsType<ConnectionCredentialClaimResult.Success>(claimed).Claim)
        {
            var material = claim.Take(ConnectionSecretRole.Password);
            Assert.NotNull(material);
            using (material)
            {
                Assert.Equal(credential, Read(material));
                Assert.Empty(claim.Entries);
            }
        }

        var replay = await ConnectionCredentialBrokerClient.ClaimAsync(
            invocation.Access,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        Assert.Equal(
            ConnectionCredentialClaimStatus.Unavailable,
            Assert.IsType<ConnectionCredentialClaimResult.Failure>(replay).Status);
        Assert.Equal(
            SecretUseKind.ConnectionAuthentication,
            Assert.Single(vault.ResolveRequests).Purpose.Kind);
    }

    [Fact]
    public async Task Private_key_passphrase_and_environment_remain_distinct_binary_claims()
    {
        var keyReference = new SecretRef("key-ref");
        var passphraseReference = new SecretRef("passphrase-ref");
        var environmentReference = new SecretRef("environment-ref");
        using var vault = new BrokerSecretVault();
        vault.Add(keyReference, "ssh-key", "private-key-canary");
        vault.Add(passphraseReference, "ssh-key", "passphrase-canary");
        vault.Add(environmentReference, "ssh-key", "environment-canary");
        await using var broker = CreateBroker(vault);
        var request = Request(
            "ssh-key",
            ConnectionKind.Ssh,
            ConnectionAuthenticationMode.PrivateKeyWithPassphrase,
            [
                new ConnectionSecretRequirement(ConnectionSecretRole.PrivateKey, keyReference),
                new ConnectionSecretRequirement(
                    ConnectionSecretRole.PrivateKeyPassphrase,
                    passphraseReference),
                new ConnectionSecretRequirement(
                    ConnectionSecretRole.EnvironmentVariable,
                    environmentReference,
                    "REMOTE_TOKEN"),
            ]);

        var launch = Success(await broker.PrepareLaunchAsync(request, CancellationToken.None));
        var invocation = ConnectionCredentialSessionInvocation.Parse(launch.Arguments);
        Assert.NotNull(invocation);
        var result = await ConnectionCredentialBrokerClient.ClaimAsync(
            invocation.Access,
            TestConnectTimeout,
            CancellationToken.None);

        using var claim = Assert.IsType<ConnectionCredentialClaimResult.Success>(result).Claim;
        var key = claim.Take(ConnectionSecretRole.PrivateKey);
        var passphrase = claim.Take(ConnectionSecretRole.PrivateKeyPassphrase);
        Assert.NotNull(key);
        Assert.NotNull(passphrase);
        using var keyLifetime = key;
        using var passphraseLifetime = passphrase;
        var environment = Assert.Single(claim.TakeEnvironment());
        using (environment.Material)
        {
            Assert.Equal("private-key-canary", Read(key));
            Assert.Equal("passphrase-canary", Read(passphrase));
            Assert.Equal("REMOTE_TOKEN", environment.EnvironmentVariableName);
            Assert.Equal("environment-canary", Read(environment.Material));
        }

        Assert.Collection(
            vault.ResolveRequests,
            item => Assert.Equal(SecretUseKind.ConnectionAuthentication, item.Purpose.Kind),
            item => Assert.Equal(SecretUseKind.ConnectionAuthentication, item.Purpose.Kind),
            item => Assert.Equal(SecretUseKind.ConnectionEnvironment, item.Purpose.Kind));
    }

    [Fact]
    public async Task Expired_ticket_fails_closed_before_vault_resolution()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var reference = new SecretRef("password-ref");
        using var vault = new BrokerSecretVault();
        vault.Add(reference, "ssh-expired", "never-resolve-this");
        await using var broker = CreateBroker(vault, clock, TimeSpan.FromSeconds(30));
        var launch = Success(await broker.PrepareLaunchAsync(
            Request(
                "ssh-expired",
                ConnectionKind.Ssh,
                ConnectionAuthenticationMode.Password,
                [new ConnectionSecretRequirement(ConnectionSecretRole.Password, reference)]),
            CancellationToken.None));
        var invocation = ConnectionCredentialSessionInvocation.Parse(launch.Arguments);
        Assert.NotNull(invocation);
        clock.Advance(TimeSpan.FromMinutes(1));

        var result = await ConnectionCredentialBrokerClient.ClaimAsync(
            invocation.Access,
            TestConnectTimeout,
            CancellationToken.None);

        Assert.Equal(
            ConnectionCredentialClaimStatus.Expired,
            Assert.IsType<ConnectionCredentialClaimResult.Failure>(result).Status);
        Assert.Empty(vault.ResolveRequests);
    }

    [Fact]
    public async Task Broker_disposal_cancels_an_in_flight_vault_resolution()
    {
        var reference = new SecretRef("password-ref");
        using var vault = new BlockingBrokerSecretVault(reference, "ssh-cancelled");
        var broker = CreateBroker(vault);
        var launch = Success(await broker.PrepareLaunchAsync(
            Request(
                "ssh-cancelled",
                ConnectionKind.Ssh,
                ConnectionAuthenticationMode.Password,
                [new ConnectionSecretRequirement(ConnectionSecretRole.Password, reference)]),
            CancellationToken.None));
        var invocation = ConnectionCredentialSessionInvocation.Parse(launch.Arguments);
        Assert.NotNull(invocation);
        var claimTask = ConnectionCredentialBrokerClient.ClaimAsync(
            invocation.Access,
            TimeSpan.FromSeconds(2),
            CancellationToken.None).AsTask();
        await vault.ResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await broker.DisposeAsync();
        var result = await claimTask;

        Assert.True(vault.ResolutionCancelled);
        Assert.IsType<ConnectionCredentialClaimResult.Failure>(result);
    }

    [Fact]
    public async Task Local_plan_executes_secret_environment_only_inside_the_broker_child_boundary()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string credential = "environment-canary-8127";
        var reference = new SecretRef("local-env-ref");
        using var vault = new BrokerSecretVault();
        vault.Add(reference, "local-broker", credential);
        await using var broker = CreateBroker(vault);
        var locator = new RecordingExecutableLocator();
        locator.Add("/bin/sh", "/bin/sh");
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(
                ConnectionHostPlatform.MacOs,
                "/bin/sh",
                "/Users/test"),
            broker);
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            new ConnectionStartup(
                environment:
                [
                    new ConnectionEnvironmentVariable(
                        "BROKER_TEST_SECRET",
                        new ConnectionEnvironmentValue.Secret(reference)),
                ]),
            id: "local-broker");

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));
        var invocation = ConnectionCredentialSessionInvocation.Parse(plan.Launch.Arguments);
        Assert.NotNull(invocation);
        invocation = invocation with
        {
            Executable = "/bin/sh",
            Arguments = ["-c", "test -n \"$BROKER_TEST_SECRET\""],
        };

        Assert.True(plan.IsSecretBrokerPrepared);
        Assert.False(plan.RequiresSecretBroker);
        Assert.Empty(vault.ResolveRequests);
        Assert.Equal(
            SecretUseKind.ConnectionEnvironment,
            Assert.Single(vault.MetadataRequests).Purpose.Kind);
        Assert.DoesNotContain(credential, string.Join('\n', plan.Launch.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain(credential, string.Join('\n', plan.Launch.Environment.Values), StringComparison.Ordinal);
        Assert.Equal(0, await ConnectionCredentialProcessHost.RunSessionAsync(
            invocation,
            CancellationToken.None));
        Assert.Equal(
            SecretUseKind.ConnectionEnvironment,
            Assert.Single(vault.ResolveRequests).Purpose.Kind);
    }

    [Fact]
    public async Task Private_key_file_is_owner_only_and_deleted_when_its_lifetime_ends()
    {
        using var material = SecretMaterial.CopyFrom("private-key-file-canary"u8);
        var key = await EphemeralPrivateKeyFile.CreateAsync(material, CancellationToken.None);
        var path = key.Path;

        Assert.True(File.Exists(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
        }
        else
        {
            var owner = WindowsIdentity.GetCurrent().User;
            Assert.NotNull(owner);
            var fileSecurity = new FileInfo(path).GetAccessControl();
            Assert.True(fileSecurity.AreAccessRulesProtected);
            Assert.Equal(owner, fileSecurity.GetOwner(typeof(SecurityIdentifier)));
            var fileRule = Assert.Single(fileSecurity
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>());
            Assert.Equal(owner, fileRule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, fileRule.AccessControlType);

            var directorySecurity = new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl();
            Assert.True(directorySecurity.AreAccessRulesProtected);
            Assert.Equal(owner, directorySecurity.GetOwner(typeof(SecurityIdentifier)));
            var directoryRule = Assert.Single(directorySecurity
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>());
            Assert.Equal(owner, directoryRule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, directoryRule.AccessControlType);
        }

        await key.DisposeAsync();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Ssh_password_and_remote_environment_prepare_an_executable_helper_plan()
    {
        const string passwordValue = "ssh-password-value-canary";
        const string environmentValue = "ssh-environment-value-canary";
        var password = new SecretRef("ssh-password-ref");
        var environment = new SecretRef("ssh-environment-ref");
        using var vault = new BrokerSecretVault();
        vault.Add(password, "ssh-brokered", passwordValue);
        vault.Add(environment, "ssh-brokered", environmentValue);
        await using var broker = CreateBroker(vault);
        var locator = new RecordingExecutableLocator();
        locator.Add("ssh", "/usr/bin/ssh");
        var adapter = new SshConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            new SshKnownHostStore(Path.Combine(
                Path.GetTempPath(),
                $"asura-broker-known-hosts-{Guid.NewGuid():N}")),
            broker);
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "operator"),
            new ConnectionAuthentication.Password(password),
            new ConnectionStartup(
                environment:
                [
                    new ConnectionEnvironmentVariable(
                        "REMOTE_TOKEN",
                        new ConnectionEnvironmentValue.Secret(environment)),
                ]),
            id: "ssh-brokered");

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));
        var invocation = ConnectionCredentialSessionInvocation.Parse(plan.Launch.Arguments);
        Assert.NotNull(invocation);

        Assert.True(plan.IsSecretBrokerPrepared);
        Assert.False(plan.RequiresSecretBroker);
        Assert.Empty(vault.ResolveRequests);
        Assert.Equal("/usr/bin/ssh", invocation.Executable);
        Assert.Contains("SendEnv=REMOTE_TOKEN", invocation.Arguments, StringComparer.Ordinal);
        Assert.Contains("PreferredAuthentications=password", invocation.Arguments, StringComparer.Ordinal);
        Assert.Contains("PubkeyAuthentication=no", invocation.Arguments, StringComparer.Ordinal);
        Assert.Contains("PasswordAuthentication=yes", invocation.Arguments, StringComparer.Ordinal);
        Assert.Contains("KbdInteractiveAuthentication=no", invocation.Arguments, StringComparer.Ordinal);
        Assert.Contains("NumberOfPasswordPrompts=1", invocation.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(passwordValue, plan.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(environmentValue, plan.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(passwordValue, string.Join('\n', plan.Launch.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain(environmentValue, string.Join('\n', plan.Launch.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain(passwordValue, string.Join('\n', plan.Launch.Environment.Values), StringComparison.Ordinal);
        Assert.DoesNotContain(environmentValue, string.Join('\n', plan.Launch.Environment.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docker_and_wsl_forward_secret_environment_by_name_not_value()
    {
        const string dockerValue = "docker-environment-canary";
        const string wslValue = "wsl-environment-canary";
        var dockerReference = new SecretRef("docker-env-ref");
        var wslReference = new SecretRef("wsl-env-ref");
        using var vault = new BrokerSecretVault();
        vault.Add(dockerReference, "docker-brokered", dockerValue);
        vault.Add(wslReference, "wsl-brokered", wslValue);
        await using var broker = CreateBroker(vault);

        var dockerLocator = new RecordingExecutableLocator();
        dockerLocator.Add("docker", "/usr/bin/docker");
        var docker = new DockerConnectionRuntimeAdapter(
            vault,
            dockerLocator,
            new RecordingCommandRunner(),
            broker);
        var dockerPlan = ConnectionRuntimeTestSupport.Success(await docker.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(
                new ConnectionEndpoint.Docker("app"),
                startup: new ConnectionStartup(
                    environment:
                    [
                        new ConnectionEnvironmentVariable(
                            "DOCKER_TOKEN",
                            new ConnectionEnvironmentValue.Secret(dockerReference)),
                    ]),
                id: "docker-brokered"),
            null,
            CancellationToken.None));
        var dockerInvocation = ConnectionCredentialSessionInvocation.Parse(dockerPlan.Launch.Arguments);
        Assert.NotNull(dockerInvocation);
        Assert.True(dockerPlan.IsSecretBrokerPrepared);
        Assert.Equal("/usr/bin/docker", dockerInvocation.Executable);
        Assert.Contains("DOCKER_TOKEN", dockerInvocation.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(dockerValue, string.Join('\n', dockerPlan.Launch.Arguments), StringComparison.Ordinal);

        var wslLocator = new RecordingExecutableLocator();
        wslLocator.Add("wsl.exe", "C:\\Windows\\System32\\wsl.exe");
        var wsl = new WslConnectionRuntimeAdapter(
            vault,
            wslLocator,
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(
                ConnectionHostPlatform.Windows,
                "cmd.exe",
                "C:\\Users\\test"),
            broker);
        var wslPlan = ConnectionRuntimeTestSupport.Success(await wsl.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(
                new ConnectionEndpoint.Wsl("Ubuntu"),
                startup: new ConnectionStartup(
                    environment:
                    [
                        new ConnectionEnvironmentVariable(
                            "WSL_TOKEN",
                            new ConnectionEnvironmentValue.Secret(wslReference)),
                    ]),
                id: "wsl-brokered"),
            null,
            CancellationToken.None));
        var wslInvocation = ConnectionCredentialSessionInvocation.Parse(wslPlan.Launch.Arguments);
        Assert.NotNull(wslInvocation);
        Assert.True(wslPlan.IsSecretBrokerPrepared);
        Assert.Equal("C:\\Windows\\System32\\wsl.exe", wslInvocation.Executable);
        Assert.DoesNotContain(wslValue, string.Join('\n', wslPlan.Launch.Arguments), StringComparison.Ordinal);

        var child = ConnectionCredentialProcessHost.BuildStartInfo(
            wslInvocation,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["WSL_TOKEN"] = wslValue },
            null,
            null);
        Assert.Contains("WSL_TOKEN", child.Environment["WSLENV"], StringComparison.Ordinal);
    }

    [Fact]
    public void Private_key_child_uses_an_identity_path_without_credential_material_in_arguments()
    {
        const string credential = "private-key-value-canary";
        var invocation = new ConnectionCredentialSessionInvocation(
            new ConnectionCredentialBrokerAccess(
                "pipe",
                "ticket",
                "token",
                new ConnectionId("ssh-key")),
            ConnectionKind.Ssh,
            ConnectionAuthenticationMode.PrivateKey,
            TestConnectTimeout,
            "/usr/bin/ssh",
            ["-o", "IdentitiesOnly=yes", "--", "host.example"]);

        var startInfo = ConnectionCredentialProcessHost.BuildStartInfo(
            invocation,
            new Dictionary<string, string>(StringComparer.Ordinal),
            "/private/key/path",
            null);

        Assert.Equal("/usr/bin/ssh", startInfo.FileName);
        Assert.Contains("IdentityAgent=none", startInfo.ArgumentList, StringComparer.Ordinal);
        Assert.Contains("/private/key/path", startInfo.ArgumentList, StringComparer.Ordinal);
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            argument => argument.Contains(credential, StringComparison.Ordinal));
        Assert.DoesNotContain(
            startInfo.Environment.Values,
            value => value?.Contains(credential, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Askpass_value_is_available_once_and_never_enters_its_access_metadata()
    {
        const string credential = "askpass-value-canary";
        var server = ConnectionCredentialAskpassServer.Create(
            SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(credential)),
            ConnectionCredentialAskpassRole.Password);
        await using (server)
        {
            Assert.DoesNotContain(credential, server.Access.ToString(), StringComparison.Ordinal);
            using var claimed = await ConnectionCredentialAskpassServer.ClaimAsync(
                server.Access,
                TestConnectTimeout,
                CancellationToken.None);
            Assert.NotNull(claimed);
            Assert.Equal(credential, Read(claimed));

            var replay = await ConnectionCredentialAskpassServer.ClaimAsync(
                server.Access,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            Assert.Null(replay);
        }
    }

    [Fact]
    public async Task Askpass_ticket_rejects_a_wrong_role_without_disclosing_material()
    {
        var server = ConnectionCredentialAskpassServer.Create(
            SecretMaterial.CopyFrom("role-bound-canary"u8),
            ConnectionCredentialAskpassRole.Password);
        await using (server)
        {
            var wrongRole = server.Access with
            {
                Role = ConnectionCredentialAskpassRole.PrivateKeyPassphrase,
            };

            var claimed = await ConnectionCredentialAskpassServer.ClaimAsync(
                wrongRole,
                TestConnectTimeout,
                CancellationToken.None);

            Assert.Null(claimed);
        }
    }

    [Theory]
    [InlineData(0, "Введите пароль оператора: ")]
    [InlineData(1, "Entrez le secret de la clé privée : ")]
    public void Askpass_accepts_bounded_localized_prompts_for_the_preselected_role(
        int roleValue,
        string localizedPrompt)
    {
        var role = (ConnectionCredentialAskpassRole)roleValue;
        Assert.True(ConnectionCredentialProcessHost.PromptMatches(role, localizedPrompt));
        Assert.False(ConnectionCredentialProcessHost.PromptMatches(role, string.Empty));
        Assert.False(ConnectionCredentialProcessHost.PromptMatches(role, "line one\nline two"));
    }

    [Fact]
    public void Self_reentry_distinguishes_apphost_and_dotnet_host_without_reading_user_arguments()
    {
        var applicationDirectory = OperatingSystem.IsWindows()
            ? "C:\\application"
            : "/application";
        var appHostPath = Path.Combine(
            applicationDirectory,
            $"Asura{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}");
        var appHost = SelfReentryLaunch.Detect(
            appHostPath,
            string.Empty,
            _ => false);

        Assert.Equal(appHostPath, appHost.Executable);
        Assert.Empty(appHost.PrefixArguments);
        Assert.Equal(appHost.Executable, appHost.AskpassExecutable);

        var managedAssembly = Path.Combine(applicationDirectory, "Asura.dll");
        var siblingAppHost = appHostPath;
        var existing = new HashSet<string>(StringComparer.Ordinal)
        {
            managedAssembly,
            siblingAppHost,
        };
        var dotnetHost = OperatingSystem.IsWindows() ? "C:\\sdk\\dotnet.exe" : "/sdk/dotnet";
        var frameworkDependent = SelfReentryLaunch.Detect(
            dotnetHost,
            managedAssembly,
            existing.Contains);

        Assert.Equal(dotnetHost, frameworkDependent.Executable);
        Assert.Equal([managedAssembly], frameworkDependent.PrefixArguments);
        Assert.Equal(siblingAppHost, frameworkDependent.AskpassExecutable);
        Assert.Equal(
            Path.GetDirectoryName(dotnetHost),
            frameworkDependent.AskpassEnvironment["DOTNET_ROOT"]);
        Assert.DoesNotContain(
            frameworkDependent.AskpassEnvironment.Keys,
            name => name.StartsWith("DOTNET_ROOT_", StringComparison.Ordinal));
        Assert.DoesNotContain("user", frameworkDependent.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Framework_dependent_helper_prefix_precedes_the_marker_and_askpass_uses_the_apphost()
    {
        var reference = new SecretRef("framework-password-ref");
        using var vault = new BrokerSecretVault();
        vault.Add(reference, "framework-ssh", "framework-password-canary");
        var reentry = new SelfReentryLaunch(
            "/sdk/dotnet",
            ["/application/Asura.dll"],
            "/application/Asura",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DOTNET_ROOT"] = "/sdk",
            });
        await using var broker = new ConnectionCredentialBroker(
            vault,
            TimeProvider.System,
            new ConnectionCredentialBrokerOptions
            {
                SelfReentry = reentry,
                TicketLifetime = TimeSpan.FromSeconds(10),
                ConnectTimeout = TestConnectTimeout,
            });
        var launch = Success(await broker.PrepareLaunchAsync(
            Request(
                "framework-ssh",
                ConnectionKind.Ssh,
                ConnectionAuthenticationMode.Password,
                [new ConnectionSecretRequirement(ConnectionSecretRole.Password, reference)]),
            CancellationToken.None));

        Assert.Equal("/sdk/dotnet", launch.Executable);
        Assert.Equal("/application/Asura.dll", launch.Arguments[0]);
        Assert.Equal(ConnectionCredentialSessionInvocation.Marker, launch.Arguments[1]);
        var invocation = ConnectionCredentialSessionInvocation.ParsePreparedLaunch(launch, reentry);
        Assert.NotNull(invocation);

        var askpass = new ConnectionCredentialAskpassAccess(
            "askpass-pipe",
            "askpass-token",
            ConnectionCredentialAskpassRole.Password);
        var startInfo = ConnectionCredentialProcessHost.BuildStartInfo(
            invocation,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            askpass,
            reentry);
        Assert.Equal(reentry.AskpassExecutable, startInfo.Environment["SSH_ASKPASS"]);
        Assert.Equal("/sdk", startInfo.Environment["DOTNET_ROOT"]);
        Assert.DoesNotContain(
            reentry.PrefixArguments,
            argument => string.Equals(
                argument,
                startInfo.Environment["SSH_ASKPASS"],
                StringComparison.Ordinal));
    }

    private static ConnectionCredentialBroker CreateBroker(
        ISecretVault vault,
        TimeProvider? timeProvider = null,
        TimeSpan? lifetime = null) =>
        new(
            vault,
            timeProvider ?? TimeProvider.System,
            new ConnectionCredentialBrokerOptions
            {
                SelfReentry = new SelfReentryLaunch(
                    "/asura-test-helper",
                    [],
                    "/asura-test-helper"),
                TicketLifetime = lifetime ?? TimeSpan.FromSeconds(10),
                ConnectTimeout = TestConnectTimeout,
            });

    private static ConnectionCredentialBrokerRequest Request(
        string connectionId,
        ConnectionKind kind,
        ConnectionAuthenticationMode authentication,
        IReadOnlyList<ConnectionSecretRequirement> requirements) =>
        new(
            new ConnectionId(connectionId),
            kind,
            authentication,
            new TerminalLaunchRequest(
                null,
                "/usr/bin/ssh",
                ["host.example"],
                keymap: TerminalKeymapSnapshot.FromProfile(BuiltInKeymaps.LinuxTerminal),
                connectionId: new ConnectionId(connectionId),
                connectionMetadata: new TerminalConnectionMetadata(
                    "SSH: operator@host.example:22",
                    "/srv/start")),
            requirements);

    private static TerminalLaunchRequest Success(
        ConnectionRuntimeResult<TerminalLaunchRequest> result) =>
        Assert.IsType<ConnectionRuntimeResult<TerminalLaunchRequest>.Success>(result).Value;

    private static string Read(SecretMaterial material)
    {
        var bytes = new byte[material.Length];
        try
        {
            material.CopyTo(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}

internal class BrokerSecretVault : ISecretVault
{
    private readonly Dictionary<SecretRef, (SecretScope Scope, byte[] Value)> _entries = [];

    public SecretVaultAvailability Availability { get; } = new(
        SecretVaultAvailabilityState.Available,
        SecretVaultPersistenceKind.MemoryOnly,
        SecretVaultCapabilities.All,
        "test",
        "test",
        "Test vault.");

    public List<ResolveSecretRequest> ResolveRequests { get; } = [];

    public List<GetSecretMetadataRequest> MetadataRequests { get; } = [];

    public void Add(SecretRef reference, string connectionId, string value) =>
        _entries.Add(
            reference,
            (new SecretScope(SecretScopeKind.Connection, connectionId), Encoding.UTF8.GetBytes(value)));

    public virtual ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request,
        CancellationToken cancellationToken)
    {
        ResolveRequests.Add(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.Cancelled)));
        }

        if (!_entries.TryGetValue(request.Reference, out var entry) || entry.Scope != request.Scope)
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.AccessDenied)));
        }

        return ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Succeed(
            SecretMaterial.CopyFrom(entry.Value)));
    }

    public void Dispose()
    {
        foreach (var (_, value) in _entries.Values)
        {
            CryptographicOperations.ZeroMemory(value);
        }

        _entries.Clear();
    }

    public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
        CreateSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
        ReplaceSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
        RelabelSecretRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
        DeleteSecretRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
        GetSecretMetadataRequest request,
        CancellationToken cancellationToken)
    {
        MetadataRequests.Add(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.Cancelled)));
        }

        if (!_entries.TryGetValue(request.Reference, out var entry) || entry.Scope != request.Scope)
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.AccessDenied)));
        }

        var now = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Succeed(
            new SecretMetadata(
                request.Reference,
                "Test credential",
                SecretKind.Other,
                entry.Scope,
                SecretVaultPersistenceKind.MemoryOnly,
                now,
                now)));
    }

    public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
        ListSecretMetadataRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class BlockingBrokerSecretVault(
    SecretRef expectedReference,
    string expectedConnectionId) : BrokerSecretVault
{
    public TaskCompletionSource ResolveStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public bool ResolutionCancelled { get; private set; }

    public override async ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request,
        CancellationToken cancellationToken)
    {
        Assert.Equal(expectedReference, request.Reference);
        Assert.Equal(expectedConnectionId, request.Scope.OwnerId);
        ResolveStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        catch (OperationCanceledException)
        {
            ResolutionCancelled = true;
            return SecretVaultResult<SecretMaterial>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.Cancelled));
        }
    }
}

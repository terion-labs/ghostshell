using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class AppleContainerWorkspaceIsolationProviderTests
{
    private const string CurrentVersionJson =
        """[{"appName":"container","version":"1.0.0","buildType":"release","commit":"abc"}]""";
    private const string GuestHome = "/home/alice";
    private static readonly string HostHome =
        Path.TrimEndingDirectorySeparator(Path.GetTempPath());
    private static readonly WorkspaceId WorkspaceId = new("workspace-alpha");
    private static readonly string PacketGatewayStateRoot = HostPath("packet-gateways");
    private static readonly string PacketGatewayHelperDirectory = HostPath("packet-helper");
    private static readonly IReadOnlyList<WorkspaceIsolationMount> HomeMounts =
    [
        new(HostHome, GuestHome, isReadOnly: false),
    ];

    [Fact]
    public async Task Prepare_reuses_and_starts_the_deterministically_named_persistent_container()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        var binding = Success(await provider.PrepareAsync(
            Request(),
            CancellationToken.None));

        var expectedName = AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId);
        Assert.Equal(expectedName, binding.ResourceName);
        Assert.Equal(
            AppleContainerWorkspaceIsolationProvider.NetworkResourceName(WorkspaceId),
            binding.Network?.ResourceName);
        Assert.Equal("192.168.128.1", binding.Network?.Ipv4Gateway);
        Assert.Equal("192.168.128.0/24", binding.Network?.Ipv4Subnet);
        Assert.Equal(
            Path.Combine(PacketGatewayStateRoot, expectedName, "guest.sock"),
            binding.Network?.PacketSocketPath);
        Assert.Equal(
            AppleContainerWorkspaceIsolationProvider.GuestPacketGatewaySocket,
            binding.Network?.GuestSocketPath);
        Assert.Equal(
            AppleContainerWorkspaceIsolationProvider.GuestPacketGatewayExecutable,
            binding.Network?.GuestHelperPath);
        Assert.Equal(HomeMounts, binding.Mounts);
        Assert.Equal(
            AppleContainerWorkspaceIsolationProvider.ProviderDescriptor.Id,
            binding.Provider);
        Assert.Equal(
            ["system", "version", "--format", "json"],
            runner.Commands[0].Arguments);
        Assert.Equal(
            ["system", "start", "--enable-kernel-install", "--timeout", "180"],
            runner.Commands[1].Arguments);
        Assert.Null(runner.Commands[1].Timeout);
        Assert.Equal(["inspect", expectedName], runner.Commands[2].Arguments);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.Commands[2].Timeout);
        Assert.Equal(["start", expectedName], runner.Commands[3].Arguments);
        Assert.Equal(TimeSpan.FromMinutes(2), runner.Commands[3].Timeout);
        Assert.Equal(["exec", expectedName, "/bin/true"], runner.Commands[4].Arguments);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.Commands[4].Timeout);
        AssertGuestProvisioningCommand(runner.Commands[5], expectedName);
        AssertGuestValidationCommand(runner.Commands[6], expectedName);
        Assert.DoesNotContain(
            runner.Commands.SelectMany(command => command.Arguments),
            argument => string.Equals(argument, "delete", StringComparison.Ordinal)
                        || string.Equals(argument, "--rm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepare_completes_apple_container_system_setup_before_inspecting_workspace()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        _ = Success(await provider.PrepareAsync(Request(), CancellationToken.None));

        Assert.Equal(
            ["system", "start", "--enable-kernel-install", "--timeout", "180"],
            runner.Commands[1].Arguments);
        Assert.Null(runner.Commands[1].Timeout);
        Assert.Equal(
            ["inspect", AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId)],
            runner.Commands[2].Arguments);
    }

    [Fact]
    public async Task Prepare_creates_and_verifies_the_owned_host_only_network_when_absent()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        runner.NetworkResults.Enqueue(AppleContainerCommandResult.Exited(1));
        runner.NetworkResults.Enqueue(AppleContainerCommandResult.Exited(0));
        runner.NetworkResults.Enqueue(AppleContainerCommandResult.Exited(0, NetworkInspectJson()));
        var provider = Provider(runner);

        var binding = Success(await provider.PrepareAsync(Request(), CancellationToken.None));

        var networkName = AppleContainerWorkspaceIsolationProvider.NetworkResourceName(WorkspaceId);
        Assert.Equal(
            ["network", "inspect", networkName],
            runner.NetworkCommands[0].Arguments);
        Assert.Equal(
            [
                "network",
                "create",
                "--internal",
                "--label",
                $"io.ghostshell.workspace={binding.ResourceName}",
                "--label",
                "io.ghostshell.isolation-schema=3",
                networkName,
            ],
            runner.NetworkCommands[1].Arguments);
        Assert.Equal(
            ["network", "inspect", networkName],
            runner.NetworkCommands[2].Arguments);
        Assert.Equal(networkName, binding.Network?.ResourceName);
    }

    [Fact]
    public async Task Prepare_uses_the_existing_nat_network_unless_host_only_is_explicitly_enabled()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson(schemaVersion: "2")),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = new AppleContainerWorkspaceIsolationProvider(
            runner.RunAsync,
            AppleContainerWorkspaceIsolationProvider.DefaultImageReference,
            "/bin/sh",
            ["-l"],
            "/usr/bin/ssh",
            "container-test");

        var binding = Success(await provider.PrepareAsync(Request(), CancellationToken.None));

        Assert.Null(binding.Network);
        Assert.Empty(runner.NetworkCommands);
        Assert.DoesNotContain("--network", runner.Commands[4].Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("--no-dns", runner.Commands[4].Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("--publish-socket", runner.Commands[4].Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(
            AppleContainerWorkspaceIsolationProvider.GuestPacketGatewayDirectory,
            runner.Commands[4].Arguments,
            StringComparer.Ordinal);
        Assert.Contains(
            "io.ghostshell.isolation-schema=2",
            runner.Commands[4].Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Prepare_refuses_a_foreign_network_with_the_deterministic_name()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0));
        runner.NetworkResults.Enqueue(AppleContainerCommandResult.Exited(
            0,
            NetworkInspectJson(workspaceLabel: "another-owner")));
        var provider = Provider(runner);

        var failure = Failure(await provider.PrepareAsync(Request(), CancellationToken.None));

        Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, failure.Code);
        Assert.Single(runner.NetworkCommands);
        Assert.DoesNotContain(
            runner.NetworkCommands,
            command => command.Arguments.Contains("delete", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Prepare_reports_runtime_kernel_and_workspace_steps()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0))
        {
            OutputByCommandIndex = new Dictionary<int, IReadOnlyList<string>>
            {
                [1] =
                [
                    "Launching container-apiserver\n",
                    "Installing kernel\rDownloading kernel 7%",
                    "Downloading kernel 84%",
                    "Verifying kernel archive",
                    "Unpacking kernel",
                ],
            },
        };
        var progress = new RecordingProgress<WorkspaceIsolationProgress>();
        var provider = Provider(runner);

        _ = Success(await provider.PrepareAsync(
            Request(),
            progress,
            CancellationToken.None));

        Assert.Contains(
            progress.Values,
            item => item.Status == "Checking the Apple container runtime…");
        Assert.Contains(
            progress.Values,
            item => item.Status == "Downloading the Apple container Linux kernel… 84%");
        Assert.Contains(
            progress.Values,
            item => item.Status == "Verifying the Apple container Linux kernel…");
        Assert.Contains(
            progress.Values,
            item => item.Status == "Unpacking the Apple container Linux kernel…");
        Assert.Equal("Checking the workspace isolate…", progress.Values[^1].Status);
    }

    [Fact]
    public async Task Prepare_creates_each_configured_mount_without_forwarding_the_ssh_agent()
    {
        IReadOnlyList<WorkspaceIsolationMount> mounts =
        [
            HomeMounts[0],
            new(Path.GetPathRoot(HostHome)!, "/workspace", isReadOnly: true),
        ];
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson(mounts)),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0))
        {
            OutputByCommandIndex = new Dictionary<int, IReadOnlyList<string>>
            {
                [3] = ["[1/2] Fetching image 62% (41 of 56 blobs)"],
            },
        };
        var provider = Provider(runner);
        var progress = new RecordingProgress<WorkspaceIsolationProgress>();

        var binding = Success(await provider.PrepareAsync(
            new WorkspaceIsolationPrepareRequest(WorkspaceId, mounts),
            progress,
            CancellationToken.None));

        Assert.Equal(
            [
                "image",
                "pull",
                "--progress",
                "plain",
                AppleContainerWorkspaceIsolationProvider.DefaultImageReference,
            ],
            runner.Commands[3].Arguments);
        Assert.Null(runner.Commands[3].Timeout);
        Assert.Contains(
            progress.Values,
            item => item.Status == "Downloading the selected workspace image…");
        Assert.Contains(
            progress.Values,
            item => item.Status == "Downloading the workspace image… 62%");
        Assert.Contains(
            progress.Values,
            item => item.Status == "Creating the persistent workspace isolate…");
        var create = runner.Commands[4].Arguments;
        Assert.Equal("create", create[0]);
        Assert.Equal(TimeSpan.FromMinutes(5), runner.Commands[4].Timeout);
        Assert.Contains("--ssh", create, StringComparer.Ordinal);
        Assert.DoesNotContain("--init", create, StringComparer.Ordinal);
        Assert.Contains(
            create.Zip(create.Skip(1)),
            pair => pair.First == "--entrypoint"
                    && pair.Second
                    == AppleContainerWorkspaceIsolationProvider.GuestPacketGatewayExecutable);
        Assert.Contains("--no-dns", create, StringComparer.Ordinal);
        Assert.Contains(
            create.Zip(create.Skip(1)),
            pair => pair.First == "--publish-socket"
                    && pair.Second == Path.Combine(
                        PacketGatewayStateRoot,
                        AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId),
                        "guest.sock")
                        + $":{AppleContainerWorkspaceIsolationProvider.GuestPacketGatewaySocket}");
        Assert.Contains(
            $"type=bind,source={PacketGatewayHelperDirectory},"
            + $"target={AppleContainerWorkspaceIsolationProvider.GuestPacketGatewayDirectory},readonly",
            create,
            StringComparer.Ordinal);
        Assert.Equal(
            [
                "init",
                "--gateway",
                "192.168.128.1",
                "--",
                "/sbin/init",
            ],
            create.TakeLast(5),
            StringComparer.Ordinal);
        Assert.Contains(
            create.Zip(create.Skip(1)),
            pair => pair.First == "--cap-add" && pair.Second == "ALL");
        Assert.Contains(
            create.Zip(create.Skip(1)),
            pair => pair.First == "--masked-path" && pair.Second == "NONE");
        Assert.Contains(
            create.Zip(create.Skip(1)),
            pair => pair.First == "--read-only-path" && pair.Second == "NONE");
        Assert.Contains(
            $"type=bind,source={HostHome},target={GuestHome}",
            create,
            StringComparer.Ordinal);
        Assert.Contains(
            $"type=bind,source={Path.GetPathRoot(HostHome)},target=/workspace,readonly",
            create,
            StringComparer.Ordinal);
        Assert.Contains(
            AppleContainerWorkspaceIsolationProvider.DefaultImageReference,
            create,
            StringComparer.Ordinal);
        Assert.DoesNotContain("/bin/sleep", create, StringComparer.Ordinal);
        Assert.DoesNotContain("infinity", create, StringComparer.Ordinal);
        Assert.DoesNotContain("--rm", create, StringComparer.Ordinal);
        Assert.Equal(binding.ResourceName, create[2]);
        Assert.Equal(3, create.Count(argument => argument == "--label"));
        Assert.Contains(
            $"io.ghostshell.base-image={AppleContainerWorkspaceIsolationProvider.DefaultImageReference}",
            create,
            StringComparer.Ordinal);
        Assert.Contains(
            create.Zip(create.Skip(1)),
            pair => pair.First == "--cpus" && pair.Second == "1");
        Assert.Contains(
            create.Zip(create.Skip(1)),
            pair => pair.First == "--memory" && pair.Second == "1G");
        Assert.Contains(
            create.Zip(create.Skip(1)),
            pair => pair.First == "--network"
                    && pair.Second
                    == AppleContainerWorkspaceIsolationProvider.NetworkResourceName(WorkspaceId));
    }

    [Fact]
    public async Task Prepare_removes_only_the_owned_stale_packet_socket_before_container_creation()
    {
        var testDirectory = Directory.CreateTempSubdirectory("ghostshell-packet-transport-");
        try
        {
            var stateRoot = Path.Combine(testDirectory.FullName, "state");
            var helperDirectory = Directory.CreateDirectory(
                Path.Combine(testDirectory.FullName, "helper"));
            var helperPath = Path.Combine(helperDirectory.FullName, "workspace-gateway");
            await File.WriteAllTextAsync(helperPath, "test-helper");
            var resourceName = AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId);
            var socketDirectory = Directory.CreateDirectory(
                Path.Combine(stateRoot, resourceName));
            var staleSocketPath = Path.Combine(socketDirectory.FullName, "guest.sock");
            await File.WriteAllTextAsync(staleSocketPath, "stale");
            var runner = new RecordingRunner(
                AppleContainerCommandResult.Exited(0, CurrentVersionJson),
                AppleContainerCommandResult.Exited(0),
                AppleContainerCommandResult.Exited(1),
                AppleContainerCommandResult.Exited(0),
                AppleContainerCommandResult.Exited(0),
                AppleContainerCommandResult.Exited(
                    0,
                    InspectJson(
                        packetGatewayStateRoot: stateRoot,
                        packetGatewayHelperDirectory: helperDirectory.FullName)),
                AppleContainerCommandResult.Exited(0),
                AppleContainerCommandResult.Exited(0),
                AppleContainerCommandResult.Exited(0),
                AppleContainerCommandResult.Exited(0));
            var provider = new AppleContainerWorkspaceIsolationProvider(
                runner.RunAsync,
                AppleContainerWorkspaceIsolationProvider.DefaultImageReference,
                "/bin/sh",
                ["-l"],
                "/usr/bin/ssh",
                "container-test",
                useHostOnlyNetwork: true,
                packetGatewayStateRoot: stateRoot,
                guestPacketGatewayHelperDirectory: helperDirectory.FullName,
                requirePacketGatewayPayload: true);

            var binding = Success(await provider.PrepareAsync(
                Request(),
                CancellationToken.None));

            Assert.Equal(staleSocketPath, binding.Network?.PacketSocketPath);
            Assert.False(File.Exists(staleSocketPath));
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                Assert.Equal(
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(socketDirectory.FullName));
            }
        }
        finally
        {
            testDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Prepare_builds_the_default_bootable_image_when_it_is_not_cached()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(
                0,
                InspectJson(
                    imageReference:
                        AppleContainerWorkspaceIsolationProvider.DefaultRuntimeImageReference,
                    baseImageReference:
                        AppleContainerWorkspaceIsolationProvider.DefaultImageReference)),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = new AppleContainerWorkspaceIsolationProvider(
            runner.RunAsync,
            AppleContainerWorkspaceIsolationProvider.DefaultImageReference,
            "/bin/sh",
            ["-l"],
            "/usr/bin/ssh",
            "container-test",
            buildDefaultImage: true,
            useHostOnlyNetwork: true,
            packetGatewayStateRoot: PacketGatewayStateRoot,
            guestPacketGatewayHelperDirectory: PacketGatewayHelperDirectory);
        var progress = new RecordingProgress<WorkspaceIsolationProgress>();

        _ = Success(await provider.PrepareAsync(
            Request(),
            progress,
            CancellationToken.None));

        Assert.Equal(
            [
                "image",
                "inspect",
                AppleContainerWorkspaceIsolationProvider.DefaultRuntimeImageReference,
            ],
            runner.Commands[3].Arguments);
        Assert.Equal("build", runner.Commands[4].Arguments[0]);
        Assert.Contains("--pull", runner.Commands[4].Arguments, StringComparer.Ordinal);
        Assert.Contains("--progress", runner.Commands[4].Arguments, StringComparer.Ordinal);
        Assert.Contains("plain", runner.Commands[4].Arguments, StringComparer.Ordinal);
        Assert.Contains("--tag", runner.Commands[4].Arguments, StringComparer.Ordinal);
        Assert.Contains(
            AppleContainerWorkspaceIsolationProvider.DefaultRuntimeImageReference,
            runner.Commands[4].Arguments,
            StringComparer.Ordinal);
        Assert.Null(runner.Commands[4].Timeout);
        Assert.Contains(
            progress.Values,
            item => item.Status ==
                $"Checking the prepared {AppleContainerWorkspaceIsolationProvider.DefaultImageReference} workspace image…");
        Assert.Contains(
            progress.Values,
            item => item.Status ==
                "Preparing a bootable workspace image from ubuntu:24.04…");
    }

    [Fact]
    public async Task Prepare_does_not_download_optional_ssh_tools_for_a_local_workspace()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        _ = Success(await provider.PrepareAsync(
            Request(),
            CancellationToken.None));

        Assert.DoesNotContain(
            runner.Commands.SelectMany(command => command.Arguments),
            argument => string.Equals(argument, "apk", StringComparison.Ordinal)
                        || string.Equals(argument, "openssh-client", StringComparison.Ordinal));
        Assert.Equal(7, runner.Commands.Count);
    }

    [Fact]
    public async Task Prepare_matches_an_unpinned_default_by_its_saved_base_image()
    {
        const string resolvedImage =
            "docker.io/library/ubuntu@sha256:runtime-resolution";
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(
                0,
                InspectJson(
                    imageReference: resolvedImage,
                    baseImageReference:
                        AppleContainerWorkspaceIsolationProvider.DefaultImageReference)),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        var binding = Success(await provider.PrepareAsync(
            Request(),
            CancellationToken.None));

        Assert.Equal(
            AppleContainerWorkspaceIsolationProvider.DefaultImageReference,
            binding.RuntimeImageReference);
        Assert.DoesNotContain(
            runner.Commands,
            command => command.Arguments.Contains("pull", StringComparer.Ordinal));
        Assert.DoesNotContain(
            runner.Commands,
            command => command.Arguments.Contains("stop", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Prepare_creates_a_new_isolate_from_the_workspace_image()
    {
        const string image = "registry.example.test/team/dev:2026.09";
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(
                0,
                InspectJson(imageReference: image, baseImageReference: image)),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        var binding = Success(await provider.PrepareAsync(
            new WorkspaceIsolationPrepareRequest(WorkspaceId, HomeMounts, image),
            CancellationToken.None));

        Assert.Equal(image, binding.ImageReference);
        Assert.Equal(
            ["image", "pull", "--progress", "plain", image],
            runner.Commands[3].Arguments);
        Assert.Contains(
            $"io.ghostshell.base-image={image}",
            runner.Commands[4].Arguments,
            StringComparer.Ordinal);
        Assert.Contains(image, runner.Commands[4].Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Prepare_rebuilds_an_inactive_isolate_when_its_image_changes()
    {
        const string image = "registry.example.test/team/dev:2026.10";
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        var binding = Success(await provider.PrepareAsync(
            new WorkspaceIsolationPrepareRequest(WorkspaceId, HomeMounts, image),
            CancellationToken.None));

        Assert.Equal(image, binding.ImageReference);
        Assert.Equal(
            ["stop", "--time", "5", binding.ResourceName],
            runner.Commands[3].Arguments);
        Assert.Equal(
            ["image", "pull", "--progress", "plain", image],
            runner.Commands[4].Arguments);
        Assert.Equal(["delete", binding.ResourceName], runner.Commands[5].Arguments);
        Assert.Equal("create", runner.Commands[6].Arguments[0]);
        Assert.Contains(image, runner.Commands[6].Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Prepare_rejects_an_outdated_apple_container_runtime()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(
                0,
                """[{"appName":"container","version":"0.12.3"}]"""));
        var provider = Provider(runner);

        var failure = Failure(await provider.PrepareAsync(
            Request(),
            CancellationToken.None));

        Assert.Equal(WorkspaceIsolationErrorCode.RuntimeVersionTooOld, failure.Code);
        Assert.Equal(WorkspaceIsolationRecoveryAction.UpdateRuntime, failure.RecoveryAction);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task Prepare_maps_a_missing_cli_to_an_install_runtime_error()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.StartFailed(AppleContainerCommandStartFailure.NotFound));
        var provider = Provider(runner);

        var failure = Failure(await provider.PrepareAsync(
            Request(),
            CancellationToken.None));

        Assert.Equal(WorkspaceIsolationErrorCode.RuntimeMissing, failure.Code);
        Assert.Equal(WorkspaceIsolationRecoveryAction.InstallRuntime, failure.RecoveryAction);
    }

    [Fact]
    public async Task Prepare_explains_when_the_selected_image_has_no_init_system()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(
                1,
                standardError: "failed to find target executable /sbin/init"),
            AppleContainerCommandResult.Exited(1));
        var provider = Provider(runner);

        var failure = Failure(await provider.PrepareAsync(
            Request(),
            CancellationToken.None));

        Assert.Equal(WorkspaceIsolationErrorCode.ImageNotBootable, failure.Code);
        Assert.Contains("/sbin/init", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_rejects_a_missing_host_mount_before_invoking_the_runtime()
    {
        var runner = new RecordingRunner();
        var provider = Provider(runner);
        IReadOnlyList<WorkspaceIsolationMount> mounts =
        [
            new(
                Path.Combine(
                    Path.GetTempPath(),
                    "ghostshell-missing-mount-166b7dcc29f34336b84b33e734e5faef"),
                "/workspace",
                isReadOnly: false),
        ];

        var failure = Failure(await provider.PrepareAsync(
            new WorkspaceIsolationPrepareRequest(WorkspaceId, mounts),
            CancellationToken.None));

        Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, failure.Code);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Prepare_rejects_an_existing_file_mount_source_before_runtime_mutation()
    {
        var hostFile = Path.GetTempFileName();
        try
        {
            var runner = new RecordingRunner();
            var provider = Provider(runner);

            var failure = Failure(await provider.PrepareAsync(
                new WorkspaceIsolationPrepareRequest(
                    WorkspaceId,
                    [new WorkspaceIsolationMount(hostFile, "/workspace/config", true)]),
                CancellationToken.None));

            Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, failure.Code);
            Assert.Empty(runner.Commands);
        }
        finally
        {
            File.Delete(hostFile);
        }
    }

    [Fact]
    public async Task Prepare_allows_a_guest_only_persistent_environment()
    {
        var emptyMounts = Array.Empty<WorkspaceIsolationMount>();
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson(emptyMounts)),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        var binding = Success(await provider.PrepareAsync(
            new WorkspaceIsolationPrepareRequest(WorkspaceId),
            CancellationToken.None));
        var launch = Success(provider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(ConnectionKind.Local, "/bin/zsh")));

        Assert.Empty(binding.Mounts);
        Assert.Equal(1, runner.Commands[4].Arguments.Count(argument => argument == "--mount"));
        Assert.Contains(
            $"type=bind,source={PacketGatewayHelperDirectory},"
            + $"target={AppleContainerWorkspaceIsolationProvider.GuestPacketGatewayDirectory},readonly",
            runner.Commands[4].Arguments,
            StringComparer.Ordinal);
        Assert.Contains(
            launch.Arguments.Zip(launch.Arguments.Skip(1)),
            pair => pair.First == "--workdir" && pair.Second == "/home/ghostshell");
    }

    [Theory]
    [InlineData(false, WorkspaceIsolationErrorCode.Cancelled)]
    [InlineData(true, WorkspaceIsolationErrorCode.Timeout)]
    public async Task Prepare_preserves_a_post_create_inspect_interruption(
        bool timedOut,
        WorkspaceIsolationErrorCode expectedError)
    {
        var inspectInterruption = timedOut
            ? AppleContainerCommandResult.TimedOut
            : AppleContainerCommandResult.Cancelled;
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            inspectInterruption,
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0, "[]"));
        var provider = Provider(runner);

        var result = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await provider.PrepareAsync(Request(), CancellationToken.None));
        var cleanup = Assert.IsType<WorkspaceIsolationBinding>(result.CleanupValue);

        Assert.Equal(expectedError, result.Error.Code);
        Assert.Equal(
            ["inspect", AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId)],
            runner.Commands[5].Arguments);
        Assert.Equal(6, runner.Commands.Count);

        _ = Success(await provider.StopAsync(cleanup, CancellationToken.None));

        Assert.Equal(
            ["list", "--all", "--format", "json"],
            runner.Commands[9].Arguments);
        Assert.Equal(10, runner.Commands.Count);
    }

    [Theory]
    [InlineData(false, WorkspaceIsolationErrorCode.Cancelled)]
    [InlineData(true, WorkspaceIsolationErrorCode.Timeout)]
    public async Task Prepare_retains_cleanup_when_create_itself_has_an_ambiguous_outcome(
        bool timedOut,
        WorkspaceIsolationErrorCode expectedError)
    {
        var ambiguousCreate = timedOut
            ? AppleContainerCommandResult.TimedOut
            : AppleContainerCommandResult.Cancelled;
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            ambiguousCreate,
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0, "[]"));
        var provider = Provider(runner);

        var result = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await provider.PrepareAsync(Request(), CancellationToken.None));
        var cleanup = Assert.IsType<WorkspaceIsolationBinding>(result.CleanupValue);

        Assert.Equal(expectedError, result.Error.Code);
        _ = Success(await provider.StopAsync(cleanup, CancellationToken.None));
        Assert.Equal(
            ["list", "--all", "--format", "json"],
            runner.Commands[9].Arguments);
    }

    [Fact]
    public async Task Prepare_retains_cleanup_when_a_failed_create_may_have_reached_the_daemon()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0, "[]"));
        var provider = Provider(runner);

        var result = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await provider.PrepareAsync(Request(), CancellationToken.None));
        var cleanup = Assert.IsType<WorkspaceIsolationBinding>(result.CleanupValue);

        Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, result.Error.Code);
        _ = Success(await provider.StopAsync(cleanup, CancellationToken.None));
        Assert.Equal(
            ["list", "--all", "--format", "json"],
            runner.Commands[9].Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_retains_cleanup_when_a_mutating_command_runner_throws(
        bool duringCreate)
    {
        var initialInspect = duringCreate
            ? AppleContainerCommandResult.Exited(1)
            : AppleContainerCommandResult.Exited(0, InspectJson());
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            initialInspect,
            duringCreate
                ? AppleContainerCommandResult.Exited(0)
                : AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            duringCreate
                ? AppleContainerCommandResult.Exited(1)
                : AppleContainerCommandResult.Exited(0, "[]"),
            AppleContainerCommandResult.Exited(0, "[]"))
        {
            ThrowOnCommandIndex = duringCreate ? 4 : 3,
        };
        var provider = Provider(runner);

        var result = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await provider.PrepareAsync(Request(), CancellationToken.None));
        var cleanup = Assert.IsType<WorkspaceIsolationBinding>(result.CleanupValue);

        Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, result.Error.Code);
        var mutationCommandIndex = duringCreate ? 4 : 3;
        Assert.Equal(
            duringCreate ? "create" : "start",
            runner.Commands[mutationCommandIndex].Arguments[0]);
        _ = Success(await provider.StopAsync(cleanup, CancellationToken.None));
        var listCommandIndex = duringCreate ? 9 : 8;
        Assert.Equal(
            ["list", "--all", "--format", "json"],
            runner.Commands[listCommandIndex].Arguments);
    }

    [Fact]
    public async Task Prepare_preserves_the_root_filesystem_while_reconfiguring_mounts()
    {
        IReadOnlyList<WorkspaceIsolationMount> otherMounts =
        [
            new(HostPath("other"), "/workspace", isReadOnly: true),
        ];
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson(otherMounts)),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(
                0,
                InspectJson(
                    imageReference: SnapshotImageReference())),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);
        var progress = new RecordingProgress<WorkspaceIsolationProgress>();

        var prepare = await provider.PrepareAsync(
            Request(),
            progress,
            CancellationToken.None);
        if (prepare is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure failure)
        {
            Assert.Fail(
                $"{failure.Error.StableCode}; commands: "
                + string.Join(" | ", runner.Commands.Select(command => command.Arguments[0])));
        }

        var binding = Success(prepare);

        Assert.Equal(HomeMounts, binding.Mounts);
        Assert.Equal(["exec", binding.ResourceName, "/bin/true"], runner.Commands[3].Arguments);
        Assert.Equal(["start", binding.ResourceName], runner.Commands[4].Arguments);
        Assert.Contains("tar --numeric-owner", runner.Commands[6].Arguments[4], StringComparison.Ordinal);
        Assert.Null(runner.Commands[6].Timeout);
        Assert.Equal("copy", runner.Commands[7].Arguments[0]);
        Assert.Null(runner.Commands[7].Timeout);
        Assert.Equal("/bin/rm", runner.Commands[8].Arguments[2]);
        Assert.Equal("stop", runner.Commands[9].Arguments[0]);
        Assert.Equal("build", runner.Commands[10].Arguments[0]);
        Assert.Null(runner.Commands[10].Timeout);
        Assert.Equal("delete", runner.Commands[11].Arguments[0]);
        Assert.Equal("create", runner.Commands[12].Arguments[0]);
        Assert.Contains(
            SnapshotImageReference(),
            runner.Commands[12].Arguments,
            StringComparer.Ordinal);
        Assert.Contains(
            progress.Values,
            item => item.Status == "Saving installed packages and guest files…");
        Assert.Contains(
            progress.Values,
            item => item.Status == "Building the preserved workspace image…");
    }

    [Fact]
    public async Task Prepare_preserves_the_root_filesystem_while_adding_ssh_agent_forwarding()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(
                0,
                InspectJson(forwardsSshAgent: false)),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(
                0,
                InspectJson(imageReference: SnapshotImageReference())),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        _ = Success(await provider.PrepareAsync(Request(), CancellationToken.None));

        Assert.Contains("tar --numeric-owner", runner.Commands[6].Arguments[4], StringComparison.Ordinal);
        Assert.Equal("copy", runner.Commands[7].Arguments[0]);
        Assert.Equal("build", runner.Commands[10].Arguments[0]);
        Assert.Equal("create", runner.Commands[12].Arguments[0]);
        Assert.Contains("--ssh", runner.Commands[12].Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Prepare_rejects_an_existing_container_with_an_unexpected_ssh_socket_mount()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(
                0,
                InspectJson(includeUnexpectedSshSocketMount: true)));
        var provider = Provider(runner);

        var failure = Failure(await provider.PrepareAsync(Request(), CancellationToken.None));

        Assert.Equal(
            WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired,
            failure.Code);
        Assert.Equal(3, runner.Commands.Count);
    }

    [Fact]
    public async Task Prepare_returns_an_owned_cleanup_lease_when_the_liveness_probe_fails()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        var result = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await provider.PrepareAsync(Request(), CancellationToken.None));
        var cleanup = Assert.IsType<WorkspaceIsolationBinding>(result.CleanupValue);

        Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, result.Error.Code);
        Assert.Equal(5, runner.Commands.Count);
        _ = Success(await provider.StopAsync(cleanup, CancellationToken.None));
        Assert.Equal(
            ["stop", "--time", "5", AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId)],
            runner.Commands[5].Arguments);
    }

    [Fact]
    public async Task Prepare_returns_an_owned_cleanup_lease_when_guest_validation_fails()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        var result = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await provider.PrepareAsync(Request(), CancellationToken.None));
        var cleanup = Assert.IsType<WorkspaceIsolationBinding>(result.CleanupValue);

        Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, result.Error.Code);
        Assert.Equal(6, runner.Commands.Count);
        _ = Success(await provider.StopAsync(cleanup, CancellationToken.None));
        Assert.Equal(
            ["stop", "--time", "5", AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId)],
            runner.Commands[6].Arguments);
    }

    [Fact]
    public async Task Cleanup_only_lease_cannot_make_a_failed_guest_runtime_reusable()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        var first = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await provider.PrepareAsync(Request(), CancellationToken.None));
        var cleanup = Assert.IsType<WorkspaceIsolationBinding>(first.CleanupValue);
        var second = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await provider.PrepareAsync(Request(), CancellationToken.None));

        Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, first.Error.Code);
        Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, second.Error.Code);
        Assert.Null(second.CleanupValue);
        AssertGuestProvisioningCommand(runner.Commands[8], cleanup.ResourceName);

        _ = Success(await provider.StopAsync(cleanup, CancellationToken.None));
        Assert.Equal(
            ["stop", "--time", "5", cleanup.ResourceName],
            runner.Commands[9].Arguments);
    }

    [Fact]
    public async Task Prepare_revalidates_the_persistent_spec_before_issuing_another_lease()
    {
        IReadOnlyList<WorkspaceIsolationMount> replacementMounts =
        [
            new(HostPath("replacement"), "/workspace", isReadOnly: true),
        ];
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson(replacementMounts)));
        var provider = Provider(runner);
        var request = Request();
        _ = Success(await provider.PrepareAsync(request, CancellationToken.None));

        var failure = Failure(await provider.PrepareAsync(request, CancellationToken.None));

        Assert.Equal(
            WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired,
            failure.Code);
        Assert.Equal(
            ["inspect", AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId)],
            runner.Commands[7].Arguments);
        Assert.Equal(8, runner.Commands.Count);
    }

    [Fact]
    public async Task Prepare_accepts_new_mounts_after_the_stopped_environment_was_reset_externally()
    {
        IReadOnlyList<WorkspaceIsolationMount> replacementMounts =
        [
            new(Path.GetPathRoot(HostHome)!, "/replacement", isReadOnly: true),
        ];
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson(replacementMounts)),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);
        var first = Success(await provider.PrepareAsync(Request(), CancellationToken.None));
        _ = Success(await provider.StopAsync(first, CancellationToken.None));

        var replacement = Success(await provider.PrepareAsync(
            new WorkspaceIsolationPrepareRequest(WorkspaceId, replacementMounts),
            CancellationToken.None));

        Assert.Equal(replacementMounts, replacement.Mounts);
        Assert.Equal("create", runner.Commands[12].Arguments[0]);
        Assert.Contains(
            $"type=bind,source={Path.GetPathRoot(HostHome)},target=/replacement,readonly",
            runner.Commands[12].Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Recreate_removes_the_owned_container_and_private_snapshot()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        _ = Success(await provider.RecreateAsync(
            Request(),
            progress: null,
            CancellationToken.None));

        var resourceName = AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId);
        Assert.Equal(["inspect", resourceName], runner.Commands[0].Arguments);
        Assert.Equal(["delete", "--force", resourceName], runner.Commands[1].Arguments);
        Assert.Equal(
            ["image", "delete", $"{resourceName}-state:latest"],
            runner.Commands[2].Arguments);
        Assert.Equal(
            [
                "network",
                "inspect",
                AppleContainerWorkspaceIsolationProvider.NetworkResourceName(WorkspaceId),
            ],
            runner.NetworkCommands[0].Arguments);
        Assert.Equal(
            [
                "network",
                "delete",
                AppleContainerWorkspaceIsolationProvider.NetworkResourceName(WorkspaceId),
            ],
            runner.NetworkCommands[1].Arguments);
    }

    [Fact]
    public async Task Recreate_refuses_to_delete_a_container_without_our_ownership_label()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(
                0,
                InspectJson(workspaceLabel: "another-owner")));
        var provider = Provider(runner);

        var error = Failure(await provider.RecreateAsync(
            Request(),
            progress: null,
            CancellationToken.None));

        Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, error.Code);
        Assert.Single(runner.Commands);
        Assert.Equal("inspect", runner.Commands[0].Arguments[0]);
    }

    [Fact]
    public async Task Recreate_does_not_treat_an_unconfirmed_inspect_failure_as_absence()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1));
        var provider = Provider(runner);

        var error = Failure(await provider.RecreateAsync(
            Request(),
            progress: null,
            CancellationToken.None));

        Assert.Equal(WorkspaceIsolationErrorCode.PrepareFailed, error.Code);
        Assert.Equal(2, runner.Commands.Count);
        Assert.Equal("inspect", runner.Commands[0].Arguments[0]);
        Assert.Equal("list", runner.Commands[1].Arguments[0]);
    }

    [Fact]
    public async Task Recreate_removes_the_private_snapshot_after_confirming_container_absence()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0, "[]"),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);

        _ = Success(await provider.RecreateAsync(
            Request(),
            progress: null,
            CancellationToken.None));

        var resourceName = AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId);
        Assert.Equal(["inspect", resourceName], runner.Commands[0].Arguments);
        Assert.Equal(
            ["list", "--all", "--format", "json"],
            runner.Commands[1].Arguments);
        Assert.Equal(
            ["image", "delete", $"{resourceName}-state:latest"],
            runner.Commands[2].Arguments);
    }

    [Fact]
    public void Local_launch_maps_the_host_shell_and_keeps_each_environment_value_structured()
    {
        var provider = Provider(new RecordingRunner());
        var binding = Binding();
        var request = new WorkspaceIsolationProcessRequest(
            ConnectionKind.Local,
            "/bin/zsh",
            ["-l"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DANGEROUS"] = "hello; touch /tmp/not-executed",
                ["ALPHA"] = "value with spaces",
            },
            Path.Combine(HostHome, "projects", "ghost shell"),
            WorkspaceProcessMode.Interactive | WorkspaceProcessMode.AllocateTerminal);

        var launch = Success(provider.CreateExecLaunch(binding, request));

        Assert.Equal("container-test", launch.Executable);
        Assert.Null(launch.HostWorkingDirectory);
        Assert.Empty(launch.Environment);
        Assert.Equal(
            [
                "exec",
                "--interactive",
                "--tty",
                "--user",
                "1000",
                "--env",
                "ALPHA=value with spaces",
                "--env",
                "DANGEROUS=hello; touch /tmp/not-executed",
                "--env",
                "HOME=/home/ghostshell",
                "--env",
                "USER=ghostshell",
                "--env",
                "LOGNAME=ghostshell",
                "--workdir",
                "/home/alice/projects/ghost shell",
                binding.ResourceName,
                "/bin/sh",
                "-l",
            ],
            launch.Arguments);
    }

    [Theory]
    [InlineData("bash", "exec bash -l")]
    [InlineData("zsh", "exec zsh -l")]
    [InlineData("fish", "exec fish -l")]
    [InlineData("nu", "exec nu -l")]
    [InlineData("elvish", "exec elvish")]
    public void Interactive_local_terminal_checks_each_supported_guest_shell(
        string shell,
        string invocation)
    {
        var provider = new AppleContainerWorkspaceIsolationProvider(
            containerExecutable: "container-test");

        var launch = Success(provider.CreateExecLaunch(
            Binding(),
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                mode: WorkspaceProcessMode.Interactive
                    | WorkspaceProcessMode.AllocateTerminal)));

        Assert.Equal("/bin/sh", launch.Arguments[^3]);
        Assert.Equal("-c", launch.Arguments[^2]);
        Assert.Contains(
            $"command -v {shell}",
            launch.Arguments[^1],
            StringComparison.Ordinal);
        Assert.Contains(invocation, launch.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains("exec /bin/sh -l", launch.Arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_local_command_keeps_argv_inside_container_exec()
    {
        var provider = Provider(new RecordingRunner());
        var binding = Binding();
        var request = new WorkspaceIsolationProcessRequest(
            ConnectionKind.Local,
            "git",
            ["status", "--short"]);

        var launch = Success(provider.CreateExecLaunch(binding, request));

        Assert.Equal(
            [
                "exec",
                "--user",
                "1000",
                "--env",
                "HOME=/home/ghostshell",
                "--env",
                "USER=ghostshell",
                "--env",
                "LOGNAME=ghostshell",
                "--workdir",
                "/home/ghostshell",
                binding.ResourceName,
                "git",
                "status",
                "--short",
            ],
            launch.Arguments);
    }

    [Fact]
    public void Interactive_local_command_keeps_argv_and_standard_input_open()
    {
        var provider = Provider(new RecordingRunner());
        var binding = Binding();
        var request = new WorkspaceIsolationProcessRequest(
            ConnectionKind.Local,
            "/bin/sh",
            ["-c", "read value; printf 'relay:%s' \"$value\""],
            mode: WorkspaceProcessMode.Interactive);

        var launch = Success(provider.CreateExecLaunch(binding, request));

        Assert.Equal(
            [
                "exec",
                "--interactive",
                "--user",
                "1000",
                "--env",
                "HOME=/home/ghostshell",
                "--env",
                "USER=ghostshell",
                "--env",
                "LOGNAME=ghostshell",
                "--workdir",
                "/home/ghostshell",
                binding.ResourceName,
                "/bin/sh",
                "-c",
                "read value; printf 'relay:%s' \"$value\"",
            ],
            launch.Arguments);
    }

    [Fact]
    public void Local_launch_maps_through_the_most_specific_host_mount()
    {
        var hostRoot = HostPath("mapping");
        var nestedRoot = Path.Combine(hostRoot, "project");
        IReadOnlyList<WorkspaceIsolationMount> mounts =
        [
            new(hostRoot, "/host", isReadOnly: true),
            new(nestedRoot, "/workspace", isReadOnly: false),
        ];
        var request = new WorkspaceIsolationProcessRequest(
            ConnectionKind.Local,
            "/bin/zsh",
            hostWorkingDirectory: Path.Combine(nestedRoot, "src"));

        var launch = Success(Provider(new RecordingRunner())
            .CreateExecLaunch(Binding(mounts), request));

        Assert.Contains(
            launch.Arguments.Zip(launch.Arguments.Skip(1)),
            pair => pair.First == "--workdir" && pair.Second == "/workspace/src");
    }

    [Fact]
    public void Local_launch_rejects_a_working_directory_outside_configured_mounts()
    {
        var provider = Provider(new RecordingRunner());
        foreach (var directory in new[]
                 {
                     Path.Combine(HostHome + "-other", "project"),
                     Path.Combine(
                         Path.GetPathRoot(HostHome)!,
                         "ghostshell-isolation-outside"),
                 })
        {
            var request = new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/zsh",
                hostWorkingDirectory: directory);

            var failure = Failure(provider.CreateExecLaunch(Binding(), request));

            Assert.Equal(
                WorkspaceIsolationErrorCode.WorkingDirectoryNotMounted,
                failure.Code);
            Assert.Equal(
                WorkspaceIsolationRecoveryAction.ChooseMountedDirectory,
                failure.RecoveryAction);
        }
    }

    [Fact]
    public void Explicitly_unverified_ssh_launch_maps_the_darwin_executable_to_the_guest_client()
    {
        var provider = Provider(new RecordingRunner());
        var request = new WorkspaceIsolationProcessRequest(
            ConnectionKind.Ssh,
            "/usr/bin/ssh",
            [
                "-p",
                "2222",
                "-o",
                "StrictHostKeyChecking=no",
                "-o",
                "UserKnownHostsFile=/dev/null",
                "dev@example.test",
            ],
            mode: WorkspaceProcessMode.Interactive | WorkspaceProcessMode.AllocateTerminal);

        var launch = Success(provider.CreateExecLaunch(Binding(), request));

        Assert.Equal("exec", launch.Arguments[0]);
        Assert.Contains("--interactive", launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("--tty", launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("/bin/sh", launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("/usr/bin/ssh", launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("StrictHostKeyChecking=no", launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("UserKnownHostsFile=/dev/null", launch.Arguments, StringComparer.Ordinal);
        Assert.Contains("dev@example.test", launch.Arguments, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("accept-new")]
    public void Verified_ssh_launch_copies_the_approved_host_key_into_the_guest(
        string strictHostKeyChecking)
    {
        var hostKnownHosts = Path.GetTempFileName();
        try
        {
            const string approvedKey = "ghostshell-test ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITest\n";
            File.WriteAllText(hostKnownHosts, approvedKey);
            var provider = Provider(new RecordingRunner());
            var request = new WorkspaceIsolationProcessRequest(
                ConnectionKind.Ssh,
                "/usr/bin/ssh",
                [
                    "-o",
                    $"StrictHostKeyChecking={strictHostKeyChecking}",
                    "-o",
                    $"UserKnownHostsFile=\"{hostKnownHosts}\"",
                    "--",
                    "host.example",
                ]);

            var launch = Success(provider.CreateExecLaunch(Binding(), request));

            var guestKnownHosts = Assert.Single(
                launch.Arguments,
                argument => argument.StartsWith(
                    "UserKnownHostsFile=/home/ghostshell/.ssh/ghostshell-known-hosts/",
                    StringComparison.Ordinal));
            Assert.Contains(
                guestKnownHosts["UserKnownHostsFile=".Length..],
                launch.Arguments,
                StringComparer.Ordinal);
            Assert.Contains(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(approvedKey)),
                launch.Arguments,
                StringComparer.Ordinal);
        }
        finally
        {
            File.Delete(hostKnownHosts);
        }
    }

    [Fact]
    public void Insecure_ssh_launch_keeps_the_guest_null_known_hosts_device()
    {
        var provider = Provider(new RecordingRunner());
        var request = new WorkspaceIsolationProcessRequest(
            ConnectionKind.Ssh,
            "/usr/bin/ssh",
            ["-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null"]);

        var launch = Success(provider.CreateExecLaunch(Binding(), request));

        Assert.Contains("UserKnownHostsFile=/dev/null", launch.Arguments, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(ConnectionKind.Docker)]
    [InlineData(ConnectionKind.Wsl)]
    public void Nested_runtime_launches_are_rejected_until_the_guest_has_a_real_backend(
        ConnectionKind kind)
    {
        var provider = Provider(new RecordingRunner());
        var request = new WorkspaceIsolationProcessRequest(kind, "host-runtime");

        var failure = Failure(provider.CreateExecLaunch(Binding(), request));

        Assert.Equal(WorkspaceIsolationErrorCode.UnsupportedConnectionKind, failure.Code);
    }

    [Fact]
    public void A_host_credential_helper_is_not_mistaken_for_a_guest_executable()
    {
        var provider = Provider(new RecordingRunner());
        var request = new WorkspaceIsolationProcessRequest(
            ConnectionKind.Ssh,
            "/Applications/GhostShell.app/Contents/MacOS/GhostShell.Desktop",
            usesHostCredentialBroker: true);

        var failure = Failure(provider.CreateExecLaunch(Binding(), request));

        Assert.Equal(WorkspaceIsolationErrorCode.HostCredentialBrokerUnavailable, failure.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_is_idempotent_when_the_persistent_container_is_already_stopped(
        bool stringStatus)
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(
                0,
                InspectJson(state: "stopped", stringStatus: stringStatus)));
        var provider = Provider(runner);
        var binding = Success(await provider.PrepareAsync(
            Request(),
            CancellationToken.None));

        var stopped = Success(await provider.StopAsync(binding, CancellationToken.None));

        Assert.Equal(binding, stopped);
        Assert.Equal(
            ["inspect", binding.ResourceName],
            runner.Commands[9].Arguments);
    }

    [Fact]
    public async Task Stop_keeps_the_last_lease_when_inspect_reports_a_running_container()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0, InspectJson(state: "running")),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);
        var binding = Success(await provider.PrepareAsync(Request(), CancellationToken.None));

        var failure = Failure(await provider.StopAsync(binding, CancellationToken.None));
        var retry = Success(await provider.StopAsync(binding, CancellationToken.None));

        Assert.Equal(WorkspaceIsolationErrorCode.StopFailed, failure.Code);
        Assert.Equal(binding, retry);
        Assert.Equal(
            ["stop", "--time", "5", binding.ResourceName],
            runner.Commands[10].Arguments);
    }

    [Fact]
    public async Task Stop_failure_keeps_the_last_lease_available_for_retry()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(1),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);
        var binding = Success(await provider.PrepareAsync(Request(), CancellationToken.None));

        var failure = Failure(await provider.StopAsync(binding, CancellationToken.None));
        var retry = Success(await provider.StopAsync(binding, CancellationToken.None));

        Assert.Equal(WorkspaceIsolationErrorCode.StopFailed, failure.Code);
        Assert.Equal(binding, retry);
        Assert.Equal(
            ["stop", "--time", "5", binding.ResourceName],
            runner.Commands[9].Arguments);
    }

    [Fact]
    public async Task Shared_workspace_is_stopped_only_after_its_last_distinct_lease_is_released()
    {
        var runner = new RecordingRunner(
            AppleContainerCommandResult.Exited(0, CurrentVersionJson),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0, InspectJson()),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0),
            AppleContainerCommandResult.Exited(0));
        var provider = Provider(runner);
        var request = Request();
        var first = Success(await provider.PrepareAsync(request, CancellationToken.None));
        var second = Success(await provider.PrepareAsync(request, CancellationToken.None));

        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.Equal(first.ResourceName, second.ResourceName);
        Assert.Equal(11, runner.Commands.Count);
        Assert.Equal(
            ["inspect", first.ResourceName],
            runner.Commands[7].Arguments);
        Assert.Equal(
            ["exec", first.ResourceName, "/bin/true"],
            runner.Commands[8].Arguments);
        AssertGuestProvisioningCommand(runner.Commands[9], first.ResourceName);
        AssertGuestValidationCommand(runner.Commands[10], first.ResourceName);

        _ = Success(await provider.StopAsync(first, CancellationToken.None));
        _ = Success(await provider.StopAsync(first, CancellationToken.None));
        Assert.Equal(11, runner.Commands.Count);

        _ = Success(await provider.StopAsync(second, CancellationToken.None));
        Assert.Equal(12, runner.Commands.Count);
        Assert.Equal(
            ["stop", "--time", "5", first.ResourceName],
            runner.Commands[11].Arguments);
    }

    private static AppleContainerWorkspaceIsolationProvider Provider(RecordingRunner runner) =>
        new(
            runner.RunAsync,
            AppleContainerWorkspaceIsolationProvider.DefaultImageReference,
            "/bin/sh",
            ["-l"],
            "/usr/bin/ssh",
            "container-test",
            useHostOnlyNetwork: true,
            packetGatewayStateRoot: PacketGatewayStateRoot,
            guestPacketGatewayHelperDirectory: PacketGatewayHelperDirectory);

    private static WorkspaceIsolationBinding Binding(
        IReadOnlyList<WorkspaceIsolationMount>? mounts = null) =>
        new(
            WorkspaceId,
            AppleContainerWorkspaceIsolationProvider.ProviderDescriptor.Id,
            AppleContainerWorkspaceIsolationProvider.ProviderDescriptor.Capabilities,
            AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId),
            mounts ?? HomeMounts,
            Guid.Parse("12d2ce38-5abf-456a-b43b-0afb72fc087f"));

    private static WorkspaceIsolationPrepareRequest Request() =>
        new(WorkspaceId, HomeMounts);

    private static string HostPath(string suffix) =>
        Path.Combine(Path.GetTempPath(), "ghostshell-isolation-provider", suffix);

    private static string InspectJson(
        IReadOnlyList<WorkspaceIsolationMount>? mounts = null,
        string state = "stopped",
        bool includeUnexpectedSshSocketMount = false,
        bool stringStatus = false,
        string? imageReference = null,
        string? baseImageReference = null,
        bool forwardsSshAgent = true,
        string? workspaceLabel = null,
        string schemaVersion = "3",
        string? packetGatewayStateRoot = null,
        string? packetGatewayHelperDirectory = null)
    {
        var name = AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId);
        packetGatewayStateRoot ??= PacketGatewayStateRoot;
        packetGatewayHelperDirectory ??= PacketGatewayHelperDirectory;
        var inspectedMounts = (mounts ?? HomeMounts)
            .Select(mount => (object)new
            {
                source = mount.HostSource,
                destination = mount.GuestDestination,
                options = MountOptions(mount.IsReadOnly),
            })
            .ToList();
        if (string.Equals(schemaVersion, "3", StringComparison.Ordinal))
        {
            inspectedMounts.Add(new
            {
                source = packetGatewayHelperDirectory,
                destination = AppleContainerWorkspaceIsolationProvider
                    .GuestPacketGatewayDirectory,
                options = MountOptions(isReadOnly: true),
            });
        }
        if (includeUnexpectedSshSocketMount)
        {
            inspectedMounts.Add(new
            {
                source = HostHome,
                destination = "/run/host-services/ssh-auth.sock",
                options = MountOptions(isReadOnly: false),
            });
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["io.ghostshell.workspace"] = workspaceLabel ?? name,
            ["io.ghostshell.isolation-schema"] = schemaVersion,
        };
        if (baseImageReference is not null)
        {
            labels["io.ghostshell.base-image"] = baseImageReference;
        }

        return JsonSerializer.Serialize(new[]
        {
            new
            {
                configuration = new
                {
                    id = name,
                    labels,
                    image = new
                    {
                        reference = imageReference
                            ?? AppleContainerWorkspaceIsolationProvider.DefaultImageReference,
                    },
                    resources = new
                    {
                        cpus = 1,
                        memoryInBytes = 1024UL * 1024UL * 1024UL,
                    },
                    ssh = forwardsSshAgent,
                    networks = new[]
                    {
                        new
                        {
                            network = AppleContainerWorkspaceIsolationProvider
                                .NetworkResourceName(WorkspaceId),
                        },
                    },
                    useInit = false,
                    initProcess = new
                    {
                        executable = string.Equals(schemaVersion, "3", StringComparison.Ordinal)
                            ? AppleContainerWorkspaceIsolationProvider
                                .GuestPacketGatewayExecutable
                            : "/sbin/init",
                        arguments = string.Equals(schemaVersion, "3", StringComparison.Ordinal)
                            ? new[]
                            {
                                "init",
                                "--gateway",
                                "192.168.128.1",
                                "--",
                                "/sbin/init",
                            }
                            : [],
                    },
                    dns = new
                    {
                        nameservers = Array.Empty<string>(),
                    },
                    publishedSockets = string.Equals(schemaVersion, "3", StringComparison.Ordinal)
                        ? new[]
                        {
                            new
                            {
                                hostPath = Path.Combine(
                                    packetGatewayStateRoot,
                                    name,
                                    "guest.sock"),
                                containerPath = AppleContainerWorkspaceIsolationProvider
                                    .GuestPacketGatewaySocket,
                            },
                        }
                        : [],
                    mounts = inspectedMounts,
                },
                status = stringStatus ? (object)state : new { state },
            },
        });
    }

    private static IReadOnlyList<string> MountOptions(bool isReadOnly) =>
        isReadOnly ? ["ro"] : [];

    private static string NetworkInspectJson(
        string mode = "hostOnly",
        string? workspaceLabel = null,
        string gateway = "192.168.128.1",
        string subnet = "192.168.128.0/24")
    {
        var containerName = AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId);
        var networkName = AppleContainerWorkspaceIsolationProvider.NetworkResourceName(WorkspaceId);
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                configuration = new
                {
                    labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["io.ghostshell.workspace"] = workspaceLabel ?? containerName,
                        ["io.ghostshell.isolation-schema"] = "3",
                    },
                    mode,
                    name = networkName,
                    options = new { },
                    plugin = "container-network-vmnet",
                },
                id = networkName,
                status = new
                {
                    ipv4Gateway = gateway,
                    ipv4Subnet = subnet,
                },
            },
        });
    }

    private static string SnapshotImageReference() =>
        $"{AppleContainerWorkspaceIsolationProvider.ResourceName(WorkspaceId)}-state:latest";

    private static void AssertGuestProvisioningCommand(
        AppleContainerCommand command,
        string resourceName)
    {
        Assert.Equal(
            ["exec", resourceName, "/bin/sh", "-c"],
            command.Arguments.Take(4),
            StringComparer.Ordinal);
        Assert.Contains("cat /proc/1/comm", command.Arguments[4], StringComparison.Ordinal);
        Assert.Equal("ghostshell-provision", command.Arguments[5]);
        Assert.Equal("ghostshell", command.Arguments[6]);
        Assert.Equal("1000", command.Arguments[7]);
        Assert.Equal("1000", command.Arguments[8]);
        Assert.Equal("/home/ghostshell", command.Arguments[9]);
        Assert.Contains("groupadd --system docker", command.Arguments[4], StringComparison.Ordinal);
        Assert.Contains("usermod -aG docker", command.Arguments[4], StringComparison.Ordinal);
    }

    private static void AssertGuestValidationCommand(
        AppleContainerCommand command,
        string resourceName)
    {
        Assert.Equal("exec", command.Arguments[0]);
        Assert.Contains(
            command.Arguments.Zip(command.Arguments.Skip(1)),
            pair => pair.First == "--user" && pair.Second == "1000");
        Assert.DoesNotContain("--gid", command.Arguments, StringComparer.Ordinal);
        Assert.Contains("HOME=/home/ghostshell", command.Arguments, StringComparer.Ordinal);
        Assert.Contains("/home/ghostshell", command.Arguments, StringComparer.Ordinal);
        Assert.Contains(resourceName, command.Arguments, StringComparer.Ordinal);
        Assert.Contains(
            command.Arguments,
            argument => argument.Contains("sudo -n true", StringComparison.Ordinal));
    }

    private static T Success<T>(WorkspaceIsolationResult<T> result)
    {
        if (result is WorkspaceIsolationResult<T>.Failure failure)
        {
            Assert.Fail(
                $"Workspace isolation failed: {failure.Error.StableCode}: {failure.Error.Message}");
        }

        return Assert.IsType<WorkspaceIsolationResult<T>.Success>(result).Value;
    }

    private static WorkspaceIsolationError Failure<T>(WorkspaceIsolationResult<T> result) =>
        Assert.IsType<WorkspaceIsolationResult<T>.Failure>(result).Error;

    private sealed class RecordingRunner
    {
        private readonly Queue<AppleContainerCommandResult> _results;

        public RecordingRunner(params AppleContainerCommandResult[] results) =>
            _results = new Queue<AppleContainerCommandResult>(results);

        public List<AppleContainerCommand> Commands { get; } = [];

        public List<AppleContainerCommand> NetworkCommands { get; } = [];

        public Queue<AppleContainerCommandResult> NetworkResults { get; } = [];

        public int? ThrowOnCommandIndex { get; init; }

        public IReadOnlyDictionary<int, IReadOnlyList<string>> OutputByCommandIndex { get; init; } =
            new Dictionary<int, IReadOnlyList<string>>();

        public ValueTask<AppleContainerCommandResult> RunAsync(
            AppleContainerCommand command,
            CancellationToken cancellationToken)
        {
            if (command.Arguments.FirstOrDefault() == "network")
            {
                NetworkCommands.Add(command);
                if (NetworkResults.TryDequeue(out var networkResult))
                {
                    return ValueTask.FromResult(networkResult);
                }

                var result = command.Arguments.ElementAtOrDefault(1) switch
                {
                    "inspect" => AppleContainerCommandResult.Exited(0, NetworkInspectJson()),
                    "list" => AppleContainerCommandResult.Exited(0, "[]"),
                    _ => AppleContainerCommandResult.Exited(0),
                };
                return ValueTask.FromResult(result);
            }

            Commands.Add(command);
            if (Commands.Count - 1 == ThrowOnCommandIndex)
            {
                throw new IOException("The test command runner failed after process start.");
            }

            if (OutputByCommandIndex.TryGetValue(
                    Commands.Count - 1,
                    out var outputChunks))
            {
                foreach (var output in outputChunks)
                {
                    command.OutputProgress?.Report(output);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(AppleContainerCommandResult.Cancelled);
            }

            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No command result was configured for this test.");
            }

            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}

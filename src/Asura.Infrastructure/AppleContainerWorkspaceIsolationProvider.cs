using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Bootstrap adapter for Apple's installed <c>container</c> CLI. The long-term macOS
/// provider should ship an app-owned Swift helper built on Apple Containerization so the
/// application, rather than a separately installed CLI, owns lifecycle and networking.
/// </summary>
public sealed partial class AppleContainerWorkspaceIsolationProvider : IWorkspaceIsolationProvider
{
    public static WorkspaceIsolationProviderDescriptor ProviderDescriptor { get; } = new(
        new WorkspaceIsolationProviderId("apple-container"),
        "Apple container",
        WorkspaceIsolationCapability.PersistentRootFileSystem
        | WorkspaceIsolationCapability.DedicatedKernel
        | WorkspaceIsolationCapability.DedicatedNetworkNamespace
        | WorkspaceIsolationCapability.HostBindMounts
        | WorkspaceIsolationCapability.StructuredProcessExecution);

    public const string DefaultContainerExecutablePath = "/usr/local/bin/container";

    public const string DefaultImageReference = WorkspaceIsolationImages.Default;

    internal const string DefaultImageBaseReference = WorkspaceIsolationImages.Default;

    internal const string DefaultRuntimeImageReference =
        "local/asura-workspace-ubuntu:24.04";

    internal const string LegacyAlpineImageReference =
        "docker.io/library/alpine@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce";

    internal const string PacketGatewayLinuxArtifactName =
        "asura-workspace-gateway-linux-arm64";

    internal const string GuestPacketGatewayDirectory = "/opt/asura/bin";

    internal const string GuestPacketGatewayExecutable =
        GuestPacketGatewayDirectory + "/workspace-gateway";

    internal const string GuestPacketGatewaySocket = "/var/lib/asura/network.sock";

    private const int MaximumCapturedCharacters = 64 * 1024;
    private const int MaximumDiagnosticCharacters = 512;
    private const int WorkspaceCpuCount = 1;
    private const ulong WorkspaceMemoryBytes = 1024UL * 1024UL * 1024UL;
    private const string WorkspaceMemoryArgument = "1G";
    private const string WorkspaceUserName = "asura";
    private const string DefaultGuestWorkingDirectory = "/home/asura";
    private const string DefaultImageContainerfileResourceName =
        "Asura.Infrastructure.WorkspaceImages.Ubuntu2404.Containerfile";
    internal const string InteractiveShellBootstrapScript =
        "if command -v bash >/dev/null 2>&1; then exec bash -l; "
        + "elif command -v zsh >/dev/null 2>&1; then exec zsh -l; "
        + "elif command -v fish >/dev/null 2>&1; then exec fish -l; "
        + "elif command -v nu >/dev/null 2>&1; then exec nu -l; "
        + "elif command -v elvish >/dev/null 2>&1; then exec elvish; "
        + "else exec /bin/sh -l; fi";
    private const string SshBootstrapArgumentZero = "asura-ssh";
    private const string GuestKnownHostsDirectory =
        "/home/asura/.ssh/asura-known-hosts";
    private const string GuestSnapshotArchivePath =
        "/tmp/asura-workspace-rootfs.tar";
    private const string GuestSnapshotScript =
        "set -u; archive=$1; "
        + "rm -f -- \"$archive\" || exit $?; sync || exit $?; status=0; "
        + "tar --numeric-owner --xattrs --acls --one-file-system "
        + "--warning=no-file-changed --exclude=./tmp/asura-workspace-rootfs.tar "
        + "--exclude=./var/host-services/ssh-auth.sock "
        + "-cpf \"$archive\" -C / . || status=$?; "
        + "if [ \"$status\" -le 1 ] && [ -s \"$archive\" ]; then exit 0; fi; exit \"$status\"";
    internal const string SshBootstrapScript =
        "ssh=$1; known_hosts=$2; known_hosts_data=$3; shift 3; "
        + "if [ -n \"$known_hosts\" ]; then umask 077; mkdir -p \"${known_hosts%/*}\" || exit $?; "
        + "if [ -n \"$known_hosts_data\" ]; then printf %s \"$known_hosts_data\" | base64 -d > \"$known_hosts\" || exit $?; "
        + "elif [ ! -e \"$known_hosts\" ]; then : > \"$known_hosts\" || exit $?; fi; fi; "
        + "if [ ! -x \"$ssh\" ]; then export DEBIAN_FRONTEND=noninteractive; "
        + "if command -v apt-get >/dev/null 2>&1; then sudo -n apt-get update && sudo -n apt-get install -y --no-install-recommends openssh-client && sudo -n rm -rf /var/lib/apt/lists/*; "
        + "elif command -v apk >/dev/null 2>&1; then sudo -n apk add --no-cache openssh-client; "
        + "else echo 'The selected isolate image cannot install OpenSSH.' >&2; exit 127; fi || exit $?; fi; exec \"$ssh\" \"$@\"";
    internal const string GuestProvisioningScript =
        "set -eu; user_name=$1; uid=$2; gid=$3; user_home=$4; "
        + "pid_one=$(cat /proc/1/comm); test \"$pid_one\" != .cz-init; test \"$pid_one\" != sleep; "
        + "test -x /sbin/init; "
        + "if command -v bash >/dev/null 2>&1; then user_shell=$(command -v bash); "
        + "elif command -v zsh >/dev/null 2>&1; then user_shell=$(command -v zsh); "
        + "elif command -v fish >/dev/null 2>&1; then user_shell=$(command -v fish); "
        + "elif command -v nu >/dev/null 2>&1; then user_shell=$(command -v nu); "
        + "elif command -v elvish >/dev/null 2>&1; then user_shell=$(command -v elvish); "
        + "else user_shell=/bin/sh; fi; "
        + "account=$(awk -F: -v wanted=\"$uid\" '$3 == wanted { print $1; exit }' /etc/passwd); "
        + "if [ -z \"$account\" ]; then "
        + "if grep -q \"^${user_name}:\" /etc/passwd; then exit 65; fi; account=$user_name; "
        + "if ! awk -F: -v wanted=\"$gid\" '$3 == wanted { found=1 } END { exit !found }' /etc/group; then printf '%s:x:%s:\\n' \"$account\" \"$gid\" >> /etc/group; fi; "
        + "printf '%s:x:%s:%s::%s:%s\\n' \"$account\" \"$uid\" \"$gid\" \"$user_home\" \"$user_shell\" >> /etc/passwd; "
        + "if [ -e /etc/shadow ]; then printf '%s:!:19000:0:99999:7:::\\n' \"$account\" >> /etc/shadow; fi; fi; "
        + "mkdir -p \"$user_home\"; "
        + "if [ ! -e \"$user_home/.asura-initialized\" ]; then "
        + "if [ -d /etc/skel ]; then cp -a /etc/skel/. \"$user_home/\"; fi; touch \"$user_home/.asura-initialized\"; fi; "
        + "chown -R \"$uid:$gid\" \"$user_home\"; "
        + "if ! awk -F: '$1 == \"docker\" { found=1 } END { exit !found }' /etc/group; then "
        + "if command -v groupadd >/dev/null 2>&1; then groupadd --system docker; fi; fi; "
        + "if awk -F: '$1 == \"docker\" { found=1 } END { exit !found }' /etc/group; then "
        + "if command -v usermod >/dev/null 2>&1; then usermod -aG docker \"$account\"; "
        + "elif command -v gpasswd >/dev/null 2>&1; then gpasswd -a \"$account\" docker >/dev/null; fi; fi; "
        + "if command -v sudo >/dev/null 2>&1; then mkdir -p /etc/sudoers.d; "
        + "printf '%s ALL=(ALL) NOPASSWD:ALL\\n' \"$account\" > /etc/sudoers.d/asura-workspace; "
        + "chmod 440 /etc/sudoers.d/asura-workspace; fi";
    private const string OwnershipLabel = "io.asura.workspace";
    private const string SchemaLabel = "io.asura.isolation-schema";
    private const string BaseImageLabel = "io.asura.base-image";
    private const string NatSchemaVersion = "2";
    private const string HostOnlyNetworkSchemaVersion = "3";
    private const string SnapshotContainerfile = """
        FROM scratch
        ADD rootfs.tar /
        ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
        ENV container=apple-container
        STOPSIGNAL SIGRTMIN+3
        CMD ["/sbin/init"]
        """;
    // Apple Container 1.0.0's tagged command reference includes every CLI surface used here:
    // create/mount, start/stop, inspect, and structured exec environment/workdir flags.
    private static readonly Version MinimumRuntimeVersion = new(1, 0, 0);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly AppleContainerCommandRunner _commandRunner;
    private readonly string _containerExecutable;
    private readonly string _imageReference;
    private readonly string _guestShellExecutable;
    private readonly IReadOnlyList<string> _guestShellArguments;
    private readonly string _guestSshExecutable;
    private readonly uint _guestUserId;
    private readonly uint _guestGroupId;
    private readonly bool _buildDefaultImage;
    private readonly bool _useHostOnlyNetwork;
    private readonly string _packetGatewayStateRoot;
    private readonly string _guestPacketGatewayHelperDirectory;
    private readonly bool _requirePacketGatewayPayload;
    private readonly ConcurrentDictionary<string, ResourceLeaseState> _resources =
        new(StringComparer.Ordinal);

    public AppleContainerWorkspaceIsolationProvider(
        string imageReference = DefaultImageReference,
        string guestShellExecutable = "/bin/sh",
        string guestSshExecutable = "/usr/bin/ssh",
        string containerExecutable = DefaultContainerExecutablePath,
        bool useHostOnlyNetwork = false)
        : this(
            RunCommandAsync,
            imageReference,
            guestShellExecutable,
            ["-c", InteractiveShellBootstrapScript],
            guestSshExecutable,
            containerExecutable,
            CurrentUserId(),
            CurrentGroupId(),
            buildDefaultImage: true,
            useHostOnlyNetwork,
            packetGatewayStateRoot: null,
            guestPacketGatewayHelperDirectory: null,
            requirePacketGatewayPayload: true)
    {
    }

    internal AppleContainerWorkspaceIsolationProvider(
        AppleContainerCommandRunner commandRunner,
        string imageReference,
        string guestShellExecutable,
        IReadOnlyList<string> guestShellArguments,
        string guestSshExecutable,
        string containerExecutable,
        uint guestUserId = 1000,
        uint guestGroupId = 1000,
        bool buildDefaultImage = false,
        bool useHostOnlyNetwork = false,
        string? packetGatewayStateRoot = null,
        string? guestPacketGatewayHelperDirectory = null,
        bool requirePacketGatewayPayload = false)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _imageReference = ValidateText(imageReference, nameof(imageReference));
        _guestShellExecutable = ValidateText(guestShellExecutable, nameof(guestShellExecutable));
        _guestSshExecutable = ValidateText(guestSshExecutable, nameof(guestSshExecutable));
        _containerExecutable = ValidateText(containerExecutable, nameof(containerExecutable));
        _guestUserId = guestUserId;
        _guestGroupId = guestGroupId;
        _buildDefaultImage = buildDefaultImage;
        _useHostOnlyNetwork = useHostOnlyNetwork;
        _packetGatewayStateRoot = ValidateAbsolutePath(
            packetGatewayStateRoot ?? DefaultPacketGatewayStateRoot(),
            nameof(packetGatewayStateRoot));
        _guestPacketGatewayHelperDirectory = ValidateAbsolutePath(
            guestPacketGatewayHelperDirectory ?? DefaultGuestPacketGatewayHelperDirectory(),
            nameof(guestPacketGatewayHelperDirectory));
        _requirePacketGatewayPayload = requirePacketGatewayPayload;
        ArgumentNullException.ThrowIfNull(guestShellArguments);
        _guestShellArguments = Array.AsReadOnly(guestShellArguments
            .Select(argument => ValidateArgument(argument, nameof(guestShellArguments)))
            .ToArray());
    }

    public WorkspaceIsolationProviderDescriptor Descriptor => ProviderDescriptor;

    public WorkspaceIsolationCapability Capabilities =>
        ProviderDescriptor.Capabilities;

    string IWorkspaceIsolationProvider.DefaultImageReference => _imageReference;

    public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
        WorkspaceIsolationPrepareRequest request,
        CancellationToken cancellationToken) =>
        PrepareAsync(request, progress: null, cancellationToken);

    public async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
        WorkspaceIsolationPrepareRequest request,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.Cancelled);
        }

        if (request.Mounts.Any(mount => !CanEncodeMount(mount)))
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.PrepareFailed);
        }

        // Apple's CLI bind-mount parser currently accepts directories only.
        // Keep that provider constraint here so a future native helper can add
        // regular-file mounts without narrowing the durable workspace model.
        if (request.Mounts.Any(mount => !Directory.Exists(mount.HostSource)))
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.PrepareFailed);
        }

        var resourceName = ResourceName(request.WorkspaceId);
        var resource = _resources.GetOrAdd(
            resourceName,
            static _ => new ResourceLeaseState());
        try
        {
            await resource.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.Cancelled);
        }

        try
        {
            if (resource.ActiveLeases.Count > 0
                && resource.Mounts is not null
                && (!MountsEqual(resource.Mounts, request.Mounts)
                    || !string.Equals(
                        resource.ImageReference,
                        request.ImageReference,
                        StringComparison.Ordinal)))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired);
            }

            if (resource.ActiveLeases.Count > 0)
            {
                if (_useHostOnlyNetwork && resource.Network is null)
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired);
                }

                var activeInspect = await RunAsync(
                        ["inspect", resourceName],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!activeInspect.IsSuccess)
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        MapLifecycleFailure(
                            activeInspect,
                            WorkspaceIsolationErrorCode.PrepareFailed));
                }

                if (!IsExpectedContainer(activeInspect.StandardOutput, request, resourceName))
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired);
                }

                if (TryReadExpectedContainerConfiguration(
                        activeInspect.StandardOutput,
                        resourceName,
                        out _,
                        out var activeImage,
                        out var activeBaseImage,
                        out _))
                {
                    resource.RuntimeImageReference = activeBaseImage ?? activeImage;
                }

                var activeLiveness = await RunAsync(
                        ["exec", resourceName, "/bin/true"],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!activeLiveness.IsSuccess)
                {
                    var restart = await RunAsync(
                            ["start", resourceName],
                            LifecycleTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    activeLiveness = await RunAsync(
                            ["exec", resourceName, "/bin/true"],
                            ProbeTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!activeLiveness.IsSuccess)
                    {
                        var failure = restart.IsSuccess ? activeLiveness : restart;
                        return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                            MapLifecycleFailure(
                                failure,
                                WorkspaceIsolationErrorCode.PrepareFailed));
                    }
                }

                if (!await EnsureGuestRuntimeAsync(resourceName, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        cancellationToken.IsCancellationRequested
                            ? WorkspaceIsolationErrorCode.Cancelled
                            : WorkspaceIsolationErrorCode.PrepareFailed);
                }

                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(
                    AcquireBinding(resource, request, resourceName));
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Checking the Apple container runtime…"));
            var versionResult = await RunAsync(
                ["system", "version", "--format", "json"],
                ProbeTimeout,
                cancellationToken)
                .ConfigureAwait(false);
            if (TryMapCommandFailure(versionResult, out var versionFailure))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(versionFailure);
            }

            if (!TryReadClientVersion(versionResult.StandardOutput, out var runtimeVersion))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    WorkspaceIsolationErrorCode.RuntimeUnavailable);
            }

            if (runtimeVersion < MinimumRuntimeVersion)
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    WorkspaceIsolationErrorCode.RuntimeVersionTooOld);
            }

            // `system start` is also Apple's idempotent readiness operation. Running it on
            // every cold provider preparation repairs a prior interrupted first-run kernel
            // install, while an already-ready runtime returns immediately.
            progress?.Report(new WorkspaceIsolationProgress(
                "Starting Apple container services and checking its Linux kernel…"));
            var runtimeProgress = progress is null
                ? null
                : new AppleContainerRuntimeProgress(progress);
            var startSystem = await RunAsync(
                ["system", "start", "--enable-kernel-install", "--timeout", "180"],
                timeout: null,
                cancellationToken,
                runtimeProgress)
                .ConfigureAwait(false);
            if (TryMapCommandFailure(startSystem, out var startSystemFailure))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    startSystemFailure);
            }

            if (_useHostOnlyNetwork)
            {
                progress?.Report(new WorkspaceIsolationProgress(
                    "Preparing the workspace host-only network…"));
                var networkResult = await EnsureNetworkAsync(resourceName, cancellationToken)
                    .ConfigureAwait(false);
                if (networkResult is WorkspaceIsolationResult<WorkspaceIsolationNetworkBinding>.Failure networkFailure)
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        networkFailure.Error.Code);
                }

                resource.Network =
                    ((WorkspaceIsolationResult<WorkspaceIsolationNetworkBinding>.Success)networkResult).Value;
            }

            var inspect = await RunAsync(
                ["inspect", resourceName],
                ProbeTimeout,
                cancellationToken)
                .ConfigureAwait(false);
            if (!inspect.IsSuccess)
            {
                var image = await PrepareWorkspaceImageAsync(
                        request,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (TryMapCommandFailure(image, out var imageFailure))
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(imageFailure);
                }

                progress?.Report(new WorkspaceIsolationProgress(
                    "Creating the persistent workspace isolate…"));
                if (_useHostOnlyNetwork
                    && !TryPreparePacketTransport(resource.Network!))
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        WorkspaceIsolationErrorCode.PrepareFailed);
                }

                var create = await RunAsync(
                    CreateArguments(request, resourceName),
                    CreateTimeout,
                    cancellationToken)
                    .ConfigureAwait(false);
                inspect = await RunAsync(
                        ["inspect", resourceName],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!inspect.IsSuccess)
                {
                    if (_useHostOnlyNetwork)
                    {
                        _ = TryRemovePacketSocket(resource.Network!.PacketSocketPath!);
                    }

                    var failure = create.IsSuccess
                        || inspect.Outcome is AppleContainerCommandOutcome.Cancelled
                            or AppleContainerCommandOutcome.TimedOut
                        ? inspect
                        : create;
                    if (create.Outcome != AppleContainerCommandOutcome.StartFailed)
                    {
                        resource.Mounts = request.Mounts;
                        return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                            MapLifecycleFailure(
                                failure,
                                WorkspaceIsolationErrorCode.PrepareFailed),
                            AcquireBinding(resource, request, resourceName));
                    }

                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        MapLifecycleFailure(failure, WorkspaceIsolationErrorCode.PrepareFailed));
                }
            }

            if (!TryReadExpectedContainerConfiguration(
                    inspect.StandardOutput,
                    resourceName,
                    out var configuredMounts,
                    out var configuredImage,
                    out var configuredBaseImage,
                    out var forwardsSshAgent))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired);
            }

            resource.RuntimeImageReference = configuredBaseImage ?? configuredImage;

            if (!ImageConfigurationMatches(
                    request,
                    resourceName,
                    configuredImage,
                    configuredBaseImage))
            {
                var recreated = await RecreateContainerForImageAsync(
                        request,
                        resourceName,
                        resource.Network,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!recreated.IsSuccess)
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        MapLifecycleFailure(
                            recreated,
                            WorkspaceIsolationErrorCode.PrepareFailed));
                }

                configuredMounts = request.Mounts;
                resource.RuntimeImageReference = ImageReferenceFor(request);
            }
            else if (!MountsEqual(configuredMounts, request.Mounts) || !forwardsSshAgent)
            {
                var reconfigured = await ReconfigureContainerAsync(
                        request,
                        resourceName,
                        configuredMounts,
                        configuredBaseImage ?? InferBaseImage(configuredImage, resourceName),
                        resource.Network,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!reconfigured.IsSuccess)
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        MapLifecycleFailure(
                            reconfigured,
                            WorkspaceIsolationErrorCode.PrepareFailed));
                }

                inspect = await RunAsync(
                        ["inspect", resourceName],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!inspect.IsSuccess
                    || !IsExpectedContainer(inspect.StandardOutput, request, resourceName))
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        MapLifecycleFailure(
                            inspect,
                            WorkspaceIsolationErrorCode.PrepareFailed));
                }
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Starting the persistent workspace isolate…"));
            var start = await RunAsync(
                ["start", resourceName],
                LifecycleTimeout,
                cancellationToken)
                .ConfigureAwait(false);
            var live = await RunAsync(
                ["exec", resourceName, "/bin/true"],
                ProbeTimeout,
                cancellationToken)
                .ConfigureAwait(false);
            if (!live.IsSuccess)
            {
                var failed = start.IsSuccess ? live : start;
                var error = MapLifecycleFailure(
                    failed,
                    WorkspaceIsolationErrorCode.PrepareFailed);
                resource.Mounts = request.Mounts;
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    error,
                    AcquireBinding(resource, request, resourceName));
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Checking the workspace isolate…"));
            if (!await EnsureGuestRuntimeAsync(resourceName, cancellationToken).ConfigureAwait(false))
            {
                var error = cancellationToken.IsCancellationRequested
                    ? WorkspaceIsolationErrorCode.Cancelled
                    : WorkspaceIsolationErrorCode.PrepareFailed;
                resource.Mounts = request.Mounts;
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    error,
                    AcquireBinding(resource, request, resourceName));
            }

            resource.Mounts = request.Mounts;
            resource.ImageReference = request.ImageReference;
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(
                AcquireBinding(resource, request, resourceName));
        }
        finally
        {
            resource.Gate.Release();
        }
    }

    public async ValueTask<WorkspaceIsolationResult<Unit>> RecreateAsync(
        WorkspaceIsolationPrepareRequest request,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resourceName = ResourceName(request.WorkspaceId);
        var resource = _resources.GetOrAdd(
            resourceName,
            static _ => new ResourceLeaseState());
        try
        {
            await resource.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WorkspaceIsolationResult<Unit>.Fail(
                WorkspaceIsolationErrorCode.Cancelled);
        }

        try
        {
            if (resource.ActiveLeases.Count > 0)
            {
                return WorkspaceIsolationResult<Unit>.Fail(
                    WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired);
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Removing the existing workspace environment…"));
            var inspect = await RunAsync(
                    ["inspect", resourceName],
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (inspect.IsSuccess)
            {
                if (!IsOwnedContainer(inspect.StandardOutput, resourceName))
                {
                    return WorkspaceIsolationResult<Unit>.Fail(
                        WorkspaceIsolationErrorCode.PrepareFailed);
                }

                var delete = await RunAsync(
                        ["delete", "--force", resourceName],
                        LifecycleTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!delete.IsSuccess)
                {
                    return WorkspaceIsolationResult<Unit>.Fail(
                        MapLifecycleFailure(
                            delete,
                            WorkspaceIsolationErrorCode.PrepareFailed));
                }
            }
            else
            {
                var list = await RunAsync(
                        ["list", "--all", "--format", "json"],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!list.IsSuccess
                    || !ConfirmsContainerAbsent(list.StandardOutput, resourceName))
                {
                    return WorkspaceIsolationResult<Unit>.Fail(
                        MapLifecycleFailure(
                            inspect,
                            WorkspaceIsolationErrorCode.PrepareFailed));
                }
            }

            // A stopped workspace is persisted as this private snapshot image.
            // It may not exist for a never-stopped environment, so deletion is
            // intentionally best effort after the owned container is gone.
            _ = await RunAsync(
                    ["image", "delete", $"{resourceName}-state:latest"],
                    LifecycleTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (_useHostOnlyNetwork)
            {
                var removeNetwork = await RemoveOwnedNetworkAsync(resourceName, cancellationToken)
                    .ConfigureAwait(false);
                if (removeNetwork is WorkspaceIsolationResult<Unit>.Failure)
                {
                    return removeNetwork;
                }

                var packetSocketPath = resource.Network?.PacketSocketPath
                    ?? PacketSocketPath(resourceName);
                if (!TryRemovePacketSocket(packetSocketPath))
                {
                    return WorkspaceIsolationResult<Unit>.Fail(
                        WorkspaceIsolationErrorCode.PrepareFailed);
                }
            }

            resource.Mounts = null;
            resource.ImageReference = null;
            resource.RuntimeImageReference = null;
            resource.Network = null;
            return cancellationToken.IsCancellationRequested
                ? WorkspaceIsolationResult<Unit>.Fail(
                    WorkspaceIsolationErrorCode.Cancelled)
                : WorkspaceIsolationResult<Unit>.Succeed(Unit.Value);
        }
        finally
        {
            resource.Gate.Release();
        }
    }

    public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
        WorkspaceIsolationBinding binding,
        WorkspaceIsolationProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);
        if (binding.Provider != Descriptor.Id)
        {
            throw new ArgumentException(
                "The workspace isolation binding belongs to another provider.",
                nameof(binding));
        }

        if (!string.Equals(
                binding.ResourceName,
                ResourceName(binding.WorkspaceId),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The workspace isolation binding has an invalid resource name.",
                nameof(binding));
        }

        if (request.UsesHostCredentialBroker)
        {
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                WorkspaceIsolationErrorCode.HostCredentialBrokerUnavailable);
        }

        if (request.ConnectionKind is ConnectionKind.Docker or ConnectionKind.Wsl)
        {
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                WorkspaceIsolationErrorCode.UnsupportedConnectionKind);
        }

        if (!TryMapWorkingDirectory(
                binding.Mounts,
                request.HostWorkingDirectory,
                out var guestWorkingDirectory))
        {
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                WorkspaceIsolationErrorCode.WorkingDirectoryNotMounted);
        }

        string guestExecutable;
        IReadOnlyList<string> guestArguments;
        switch (request.ConnectionKind)
        {
            case ConnectionKind.Local:
                var interactiveShell =
                    (request.Mode & WorkspaceProcessMode.AllocateTerminal) != 0;
                guestExecutable = interactiveShell
                    ? _guestShellExecutable
                    : request.HostExecutable;
                guestArguments = interactiveShell
                    ? _guestShellArguments
                    : request.Arguments;
                guestWorkingDirectory ??= DefaultGuestWorkingDirectory;
                break;
            case ConnectionKind.Ssh when IsSshExecutable(request.HostExecutable):
                if (!TryPrepareSshTrust(
                        request.Arguments,
                        out var sshArguments,
                        out var guestKnownHostsPath,
                        out var knownHostsData))
                {
                    return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                        WorkspaceIsolationErrorCode.SshHostKeyTrustUnavailable);
                }

                guestExecutable = _guestShellExecutable;
                guestArguments = Array.AsReadOnly(new[]
                {
                    "-c",
                    SshBootstrapScript,
                    SshBootstrapArgumentZero,
                    _guestSshExecutable,
                    guestKnownHostsPath,
                    knownHostsData,
                }.Concat(sshArguments).ToArray());
                break;
            case ConnectionKind.Ssh:
                return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                    WorkspaceIsolationErrorCode.ExecutableMappingUnavailable);
            default:
                return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(
                    WorkspaceIsolationErrorCode.UnsupportedConnectionKind);
        }

        var arguments = new List<string>(
            8 + (request.Environment.Count * 2) + guestArguments.Count)
        {
            "exec",
        };
        if ((request.Mode & WorkspaceProcessMode.Interactive) != 0)
        {
            arguments.Add("--interactive");
        }

        if ((request.Mode & WorkspaceProcessMode.AllocateTerminal) != 0)
        {
            arguments.Add("--tty");
        }

        // Apple Container resolves supplementary groups only through --user. Supplying
        // --uid and --gid directly drops groups such as docker from the guest process.
        arguments.Add("--user");
        arguments.Add(_guestUserId.ToString(CultureInfo.InvariantCulture));

        foreach (var (name, value) in request.Environment.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            if (IsGuestIdentityEnvironment(name))
            {
                continue;
            }

            arguments.Add("--env");
            arguments.Add($"{name}={value}");
        }

        arguments.Add("--env");
        arguments.Add($"HOME={DefaultGuestWorkingDirectory}");
        arguments.Add("--env");
        arguments.Add($"USER={WorkspaceUserName}");
        arguments.Add("--env");
        arguments.Add($"LOGNAME={WorkspaceUserName}");

        if (guestWorkingDirectory is not null)
        {
            arguments.Add("--workdir");
            arguments.Add(guestWorkingDirectory);
        }

        arguments.Add(binding.ResourceName);
        arguments.Add(guestExecutable);
        arguments.AddRange(guestArguments);
        return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
            new WorkspaceProcessLaunch(
                _containerExecutable,
                arguments,
                EmptyEnvironment,
                hostWorkingDirectory: null));
    }

    public async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Provider != Descriptor.Id)
        {
            throw new ArgumentException(
                "The workspace isolation binding belongs to another provider.",
                nameof(binding));
        }

        if (!string.Equals(
                binding.ResourceName,
                ResourceName(binding.WorkspaceId),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The workspace isolation binding has an invalid resource name.",
                nameof(binding));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.Cancelled);
        }

        if (!_resources.TryGetValue(binding.ResourceName, out var resource))
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
        }

        try
        {
            await resource.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                WorkspaceIsolationErrorCode.Cancelled);
        }

        try
        {
            if (!resource.ActiveLeases.Contains(binding.LeaseId))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
            }

            if (resource.ActiveLeases.Count > 1)
            {
                resource.ActiveLeases.Remove(binding.LeaseId);
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
            }

            var stop = await StopContainerAsync(binding, cancellationToken).ConfigureAwait(false);
            if (stop is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success)
            {
                resource.ActiveLeases.Remove(binding.LeaseId);
            }

            return stop;
        }
        finally
        {
            resource.Gate.Release();
        }
    }

    private async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopContainerAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
                ["stop", "--time", "5", binding.ResourceName],
                LifecycleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
        }

        var liveness = await RunAsync(
                ["exec", binding.ResourceName, "/bin/true"],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (liveness.IsSuccess)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                MapLifecycleFailure(result, WorkspaceIsolationErrorCode.StopFailed));
        }

        var inspect = await RunAsync(
                ["inspect", binding.ResourceName],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (inspect.IsSuccess)
        {
            return IsStoppedContainer(inspect.StandardOutput, binding.ResourceName)
                ? WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding)
                : WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                    MapLifecycleFailure(result, WorkspaceIsolationErrorCode.StopFailed));
        }

        var list = await RunAsync(
                ["list", "--all", "--format", "json"],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return list.IsSuccess
               && ConfirmsContainerAbsent(list.StandardOutput, binding.ResourceName)
            ? WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding)
            : WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                MapLifecycleFailure(result, WorkspaceIsolationErrorCode.StopFailed));
    }

    private WorkspaceIsolationBinding AcquireBinding(
        ResourceLeaseState resource,
        WorkspaceIsolationPrepareRequest request,
        string resourceName)
    {
        var leaseId = Guid.NewGuid();
        resource.ActiveLeases.Add(leaseId);
        resource.Mounts = request.Mounts;
        resource.ImageReference = request.ImageReference;
        return new WorkspaceIsolationBinding(
            request.WorkspaceId,
            Descriptor.Id,
            Capabilities,
            resourceName,
            request.Mounts,
            leaseId,
            request.ImageReference,
            resource.RuntimeImageReference ?? ImageReferenceFor(request),
            resource.Network);
    }

    internal static string ResourceName(WorkspaceId workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException("A workspace identifier is required.", nameof(workspaceId));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceId.Value));
        return $"asura-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    internal static string NetworkResourceName(WorkspaceId workspaceId) =>
        $"{ResourceName(workspaceId)}-network";

    internal string PacketSocketPath(WorkspaceId workspaceId) =>
        PacketSocketPath(ResourceName(workspaceId));

    private string IsolationSchemaVersion =>
        _useHostOnlyNetwork ? HostOnlyNetworkSchemaVersion : NatSchemaVersion;

    private static string DefaultPacketGatewayStateRoot() =>
        Path.Combine(
            "/tmp",
            $"asura-{CurrentUserId().ToString(CultureInfo.InvariantCulture)}",
            "network-gateways");

    private static string DefaultGuestPacketGatewayHelperDirectory() =>
        Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            "linux-arm64",
            "guest");

    private string PacketSocketPath(string resourceName) =>
        Path.Combine(_packetGatewayStateRoot, resourceName, "guest.sock");

    private bool TryPreparePacketTransport(
        WorkspaceIsolationNetworkBinding network)
    {
        if (!_useHostOnlyNetwork)
        {
            return true;
        }

        if (!_requirePacketGatewayPayload)
        {
            return true;
        }

        try
        {
            var helperDirectory = new DirectoryInfo(_guestPacketGatewayHelperDirectory);
            var helper = new FileInfo(Path.Combine(
                helperDirectory.FullName,
                Path.GetFileName(GuestPacketGatewayExecutable)));
            if (_requirePacketGatewayPayload
                && (!helperDirectory.Exists
                    || helperDirectory.LinkTarget is not null
                    || !helper.Exists
                    || helper.LinkTarget is not null))
            {
                return false;
            }

            var stateRoot = Directory.CreateDirectory(_packetGatewayStateRoot);
            if (stateRoot.LinkTarget is not null)
            {
                return false;
            }

            var socketDirectory = Directory.CreateDirectory(
                Path.GetDirectoryName(network.PacketSocketPath!)!);
            if (socketDirectory.LinkTarget is not null)
            {
                return false;
            }

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    stateRoot.FullName,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    socketDirectory.FullName,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }

            return TryRemovePacketSocket(network.PacketSocketPath!);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryRemovePacketSocket(string socketPath)
    {
        try
        {
            if (Directory.Exists(socketPath))
            {
                return false;
            }

            var socket = new FileInfo(socketPath);
            if (socket.LinkTarget is not null)
            {
                return false;
            }

            if (socket.Exists)
            {
                socket.Delete();
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    private static bool CanEncodeMount(WorkspaceIsolationMount mount) =>
        CanEncodeMountPath(mount.HostSource)
        && CanEncodeMountPath(mount.GuestDestination);

    private static bool CanEncodeMountPath(string path) =>
        !path.Contains(',', StringComparison.Ordinal)
        && !path.Contains('=', StringComparison.Ordinal);

    private static bool IsSshExecutable(string executable) =>
        string.Equals(Path.GetFileName(executable), "ssh", StringComparison.Ordinal);

    private static bool IsGuestIdentityEnvironment(string name) =>
        name is "HOME" or "USER" or "LOGNAME" or "SHELL";

    internal static bool TryPrepareSshTrust(
        IReadOnlyList<string> arguments,
        out IReadOnlyList<string> guestArguments,
        out string guestKnownHostsPath,
        out string knownHostsData)
    {
        guestArguments = arguments;
        guestKnownHostsPath = string.Empty;
        knownHostsData = string.Empty;
        if (HasOpenSshOption(arguments, "StrictHostKeyChecking", "no")
            && HasOpenSshOption(arguments, "UserKnownHostsFile", "/dev/null"))
        {
            return true;
        }

        var strict = OpenSshOption(arguments, "StrictHostKeyChecking");
        var hostKnownHostsPath = OpenSshOption(arguments, "UserKnownHostsFile");
        if (strict is not ("yes" or "accept-new")
            || string.IsNullOrWhiteSpace(hostKnownHostsPath)
            || !Path.IsPathFullyQualified(hostKnownHostsPath))
        {
            return false;
        }

        byte[] contents;
        try
        {
            if (!File.Exists(hostKnownHostsPath))
            {
                if (!string.Equals(strict, "accept-new", StringComparison.Ordinal))
                {
                    return false;
                }

                contents = [];
            }
            else
            {
                var info = new FileInfo(hostKnownHostsPath);
                if (info.Length is <= 0 or > 128 * 1024)
                {
                    return false;
                }

                contents = File.ReadAllBytes(hostKnownHostsPath);
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return false;
        }

        var identity = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(hostKnownHostsPath)));
        guestKnownHostsPath = $"{GuestKnownHostsDirectory}/{identity}";
        knownHostsData = contents.Length == 0 ? string.Empty : Convert.ToBase64String(contents);
        var rewritten = arguments.ToArray();
        for (var index = 0; index + 1 < rewritten.Length; index++)
        {
            if (!string.Equals(rewritten[index], "-o", StringComparison.Ordinal))
            {
                continue;
            }

            var option = rewritten[index + 1];
            var separator = option.IndexOf('=');
            if (separator > 0
                && string.Equals(
                    option[..separator],
                    "UserKnownHostsFile",
                    StringComparison.OrdinalIgnoreCase))
            {
                rewritten[index + 1] = $"UserKnownHostsFile={guestKnownHostsPath}";
                guestArguments = Array.AsReadOnly(rewritten);
                return true;
            }
        }

        return false;
    }

    private static string? OpenSshOption(
        IReadOnlyList<string> arguments,
        string expectedName)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "-o", StringComparison.Ordinal))
            {
                continue;
            }

            var option = arguments[index + 1];
            var separator = option.IndexOf('=');
            if (separator > 0
                && string.Equals(
                    option[..separator],
                    expectedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return option[(separator + 1)..].Trim('"');
            }
        }

        return null;
    }

    private static bool HasOpenSshOption(
        IReadOnlyList<string> arguments,
        string expectedName,
        string expectedValue)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "-o", StringComparison.Ordinal))
            {
                continue;
            }

            var option = arguments[index + 1];
            var separator = option.IndexOf('=');
            if (separator > 0
                && string.Equals(
                    option[..separator],
                    expectedName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    option[(separator + 1)..].Trim('"'),
                    expectedValue,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryMapWorkingDirectory(
        IReadOnlyList<WorkspaceIsolationMount> mounts,
        string? hostWorkingDirectory,
        out string? guestWorkingDirectory)
    {
        guestWorkingDirectory = null;
        if (hostWorkingDirectory is null)
        {
            return true;
        }

        try
        {
            if (!Path.IsPathFullyQualified(hostWorkingDirectory))
            {
                return false;
            }

            var workingDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(hostWorkingDirectory));
            WorkspaceIsolationMount? matchedMount = null;
            string? matchedRelativePath = null;
            foreach (var mount in mounts.OrderByDescending(
                         candidate => candidate.HostSource.Length))
            {
                var relative = Path.GetRelativePath(mount.HostSource, workingDirectory);
                if (string.Equals(relative, "..", StringComparison.Ordinal)
                    || Path.IsPathRooted(relative)
                    || relative.StartsWith(
                        $"..{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                matchedMount = mount;
                matchedRelativePath = relative;
                break;
            }

            if (matchedMount is null || matchedRelativePath is null)
            {
                return false;
            }

            guestWorkingDirectory = string.Equals(matchedRelativePath, ".", StringComparison.Ordinal)
                ? matchedMount.GuestDestination
                : $"{matchedMount.GuestDestination}/{ToGuestPath(matchedRelativePath)}";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    private static string ToGuestPath(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/');

    private IReadOnlyList<string> CreateArguments(
        WorkspaceIsolationPrepareRequest request,
        string resourceName,
        string? imageReference = null,
        string? baseImageReference = null)
    {
        var arguments = new List<string>(22 + (request.Mounts.Count * 2))
        {
            "create",
            "--name",
            resourceName,
            "--label",
            $"{OwnershipLabel}={resourceName}",
            "--label",
            $"{SchemaLabel}={IsolationSchemaVersion}",
            "--label",
            $"{BaseImageLabel}={baseImageReference ?? ImageReferenceFor(request)}",
            "--cpus",
            WorkspaceCpuCount.ToString(CultureInfo.InvariantCulture),
            "--memory",
            WorkspaceMemoryArgument,
            "--cap-add",
            "ALL",
            "--masked-path",
            "NONE",
            "--read-only-path",
            "NONE",
            "--env",
            "container=apple-container",
            "--ssh",
        };
        if (_useHostOnlyNetwork)
        {
            if (!_resources.TryGetValue(resourceName, out var resource)
                || resource.Network is null)
            {
                throw new InvalidOperationException(
                    "The workspace host-only network must be prepared before its container.");
            }

            arguments.Add("--network");
            arguments.Add(NetworkResourceName(request.WorkspaceId));
            arguments.Add("--no-dns");
            arguments.Add("--publish-socket");
            arguments.Add(
                $"{resource.Network.PacketSocketPath}:{resource.Network.GuestSocketPath}");
            arguments.Add("--mount");
            arguments.Add(
                $"type=bind,source={_guestPacketGatewayHelperDirectory},"
                + $"target={GuestPacketGatewayDirectory},readonly");
        }

        foreach (var mount in request.Mounts.OrderBy(
                     candidate => candidate.GuestDestination,
                     StringComparer.Ordinal))
        {
            arguments.Add("--mount");
            arguments.Add(MountArgument(mount));
        }

        arguments.Add("--entrypoint");
        arguments.Add(_useHostOnlyNetwork
            ? GuestPacketGatewayExecutable
            : "/sbin/init");
        arguments.Add(imageReference ?? RuntimeImageReferenceFor(request));
        if (_useHostOnlyNetwork)
        {
            var network = _resources[resourceName].Network!;
            arguments.Add("init");
            arguments.Add("--gateway");
            arguments.Add(network.Ipv4Gateway);
            arguments.Add("--");
            arguments.Add("/sbin/init");
        }

        return arguments;
    }

    private async ValueTask<AppleContainerCommandResult> ReconfigureContainerAsync(
        WorkspaceIsolationPrepareRequest request,
        string resourceName,
        IReadOnlyList<WorkspaceIsolationMount> configuredMounts,
        string configuredBaseImage,
        WorkspaceIsolationNetworkBinding? network,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new WorkspaceIsolationProgress(
            "Starting the workspace isolate to save its files…"));
        var live = await RunAsync(
                ["exec", resourceName, "/bin/true"],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!live.IsSuccess)
        {
            var start = await RunAsync(
                    ["start", resourceName],
                    LifecycleTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            live = await RunAsync(
                    ["exec", resourceName, "/bin/true"],
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!live.IsSuccess)
            {
                return start.IsSuccess ? live : start;
            }
        }

        var temporaryDirectory = Directory.CreateTempSubdirectory(
            "asura-isolation-reconfigure-");
        try
        {
            var archivePath = Path.Combine(temporaryDirectory.FullName, "rootfs.tar");
            var containerfilePath = Path.Combine(temporaryDirectory.FullName, "Containerfile");

            progress?.Report(new WorkspaceIsolationProgress(
                "Saving installed packages and guest files…"));
            var archive = await RunAsync(
                    [
                        "exec",
                        resourceName,
                        "/bin/sh",
                        "-c",
                        GuestSnapshotScript,
                        "asura-snapshot",
                        GuestSnapshotArchivePath,
                    ],
                    timeout: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!archive.IsSuccess)
            {
                progress?.Report(new WorkspaceIsolationProgress(
                    "Could not save workspace files: "
                    + BoundedDiagnostic(archive.StandardError)));
                return archive;
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Copying the saved workspace files…"));
            var copy = await RunAsync(
                    ["copy", $"{resourceName}:{GuestSnapshotArchivePath}", archivePath],
                    timeout: null,
                    cancellationToken)
                .ConfigureAwait(false);
            _ = await RunAsync(
                    ["exec", resourceName, "/bin/rm", "--", GuestSnapshotArchivePath],
                    ProbeTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!copy.IsSuccess)
            {
                progress?.Report(new WorkspaceIsolationProgress(
                    "Could not copy saved workspace files: "
                    + BoundedDiagnostic(copy.StandardError)));
                return copy;
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Stopping the workspace isolate before applying its configuration…"));
            var stop = await RunAsync(
                    ["stop", "--time", "5", resourceName],
                    LifecycleTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!stop.IsSuccess)
            {
                var stoppedInspect = await RunAsync(
                        ["inspect", resourceName],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!stoppedInspect.IsSuccess
                    || !IsStoppedContainer(stoppedInspect.StandardOutput, resourceName))
                {
                    return stop;
                }
            }

            await File.WriteAllTextAsync(
                    containerfilePath,
                    SnapshotContainerfile,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);

            var snapshotImage = SnapshotImageReference(resourceName);
            progress?.Report(new WorkspaceIsolationProgress(
                "Building the preserved workspace image…"));
            var build = await RunAsync(
                    [
                        "build",
                        "--progress",
                        "plain",
                        "--tag",
                        snapshotImage,
                        temporaryDirectory.FullName,
                    ],
                    timeout: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!build.IsSuccess)
            {
                return build;
            }

            progress?.Report(new WorkspaceIsolationProgress(
                "Replacing the workspace isolate with the updated configuration…"));
            var delete = await RunAsync(
                    ["delete", resourceName],
                    LifecycleTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!delete.IsSuccess)
            {
                return delete;
            }

            if (_useHostOnlyNetwork
                && (network is null || !TryPreparePacketTransport(network)))
            {
                return AppleContainerCommandResult.ExecutionFailed;
            }

            var create = await RunAsync(
                    CreateArguments(
                        request,
                        resourceName,
                        snapshotImage,
                        configuredBaseImage),
                    CreateTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (create.IsSuccess)
            {
                return create;
            }

            // The snapshot image contains the complete writable filesystem. Restore the
            // previous mount configuration if applying the new configuration fails.
            if (_useHostOnlyNetwork
                && (network is null || !TryPreparePacketTransport(network)))
            {
                return create;
            }

            _ = await RunAsync(
                    CreateArguments(
                        new WorkspaceIsolationPrepareRequest(
                            request.WorkspaceId,
                            configuredMounts,
                            configuredBaseImage),
                        resourceName,
                        snapshotImage,
                        configuredBaseImage),
                    CreateTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return create;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AppleContainerCommandResult.Cancelled;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return AppleContainerCommandResult.ExecutionFailed;
        }
        finally
        {
            try
            {
                temporaryDirectory.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException)
            {
                // The isolate has already been rebuilt or left intact. A temporary export
                // cleanup failure must not replace that lifecycle result.
            }
        }
    }

    private static string SnapshotImageReference(string resourceName) =>
        $"{resourceName}-state:latest";

    private string ImageReferenceFor(WorkspaceIsolationPrepareRequest request) =>
        request.ImageReference ?? _imageReference;

    private string RuntimeImageReferenceFor(WorkspaceIsolationPrepareRequest request)
    {
        var configuredImage = ImageReferenceFor(request);
        return _buildDefaultImage
               && string.Equals(
                   configuredImage,
                   DefaultImageReference,
                   StringComparison.Ordinal)
            ? DefaultRuntimeImageReference
            : configuredImage;
    }

    private async ValueTask<AppleContainerCommandResult> PrepareWorkspaceImageAsync(
        WorkspaceIsolationPrepareRequest request,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var imageReference = ImageReferenceFor(request);
        if (!_buildDefaultImage
            || !string.Equals(imageReference, DefaultImageReference, StringComparison.Ordinal))
        {
            progress?.Report(new WorkspaceIsolationProgress(
                "Downloading the selected workspace image…"));
            var imageProgress = progress is null
                ? null
                : new AppleContainerImageProgress(progress);
            return await RunAsync(
                    ["image", "pull", "--progress", "plain", imageReference],
                    timeout: null,
                    cancellationToken,
                    imageProgress)
                .ConfigureAwait(false);
        }

        progress?.Report(new WorkspaceIsolationProgress(
            $"Checking the prepared {DefaultImageReference} workspace image…"));
        var inspect = await RunAsync(
                ["image", "inspect", DefaultRuntimeImageReference],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (inspect.IsSuccess || inspect.Outcome != AppleContainerCommandOutcome.Exited)
        {
            return inspect;
        }

        progress?.Report(new WorkspaceIsolationProgress(
            $"Preparing a bootable workspace image from {DefaultImageBaseReference}…"));
        DirectoryInfo? buildDirectory = null;
        try
        {
            buildDirectory = Directory.CreateTempSubdirectory(
                "asura-ubuntu-workspace-image-");
            var containerfilePath = Path.Combine(buildDirectory.FullName, "Containerfile");
            await using var source = typeof(AppleContainerWorkspaceIsolationProvider)
                .Assembly
                .GetManifestResourceStream(DefaultImageContainerfileResourceName);
            if (source is null)
            {
                return AppleContainerCommandResult.ExecutionFailed;
            }

            await using (var destination = new FileStream(
                             containerfilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            return await RunAsync(
                    [
                        "build",
                        "--pull",
                        "--progress",
                        "plain",
                        "--tag",
                        DefaultRuntimeImageReference,
                        buildDirectory.FullName,
                    ],
                    timeout: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AppleContainerCommandResult.Cancelled;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return AppleContainerCommandResult.ExecutionFailed;
        }
        finally
        {
            if (buildDirectory is not null)
            {
                try
                {
                    buildDirectory.Delete(recursive: true);
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException)
                {
                    // The image build result owns the operation outcome. Temporary-file
                    // cleanup cannot make a completed build appear to have failed.
                }
            }
        }
    }

    private static string InferBaseImage(string imageReference, string resourceName) =>
        string.Equals(
            imageReference,
            SnapshotImageReference(resourceName),
            StringComparison.Ordinal)
            ? LegacyAlpineImageReference
            : imageReference;

    private async ValueTask<AppleContainerCommandResult> RecreateContainerForImageAsync(
        WorkspaceIsolationPrepareRequest request,
        string resourceName,
        WorkspaceIsolationNetworkBinding? network,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new WorkspaceIsolationProgress(
            "Stopping the workspace isolate before changing its runtime image…"));
        var stop = await RunAsync(
                ["stop", "--time", "5", resourceName],
                LifecycleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!stop.IsSuccess)
        {
            var stoppedInspect = await RunAsync(
                    ["inspect", resourceName],
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!stoppedInspect.IsSuccess
                || !IsStoppedContainer(stoppedInspect.StandardOutput, resourceName))
            {
                return stop;
            }
        }

        var image = await PrepareWorkspaceImageAsync(
                request,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (!image.IsSuccess)
        {
            return image;
        }

        progress?.Report(new WorkspaceIsolationProgress(
            "Rebuilding the workspace isolate with the selected image…"));
        var delete = await RunAsync(
                ["delete", resourceName],
                LifecycleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!delete.IsSuccess)
        {
            return delete;
        }

        if (_useHostOnlyNetwork
            && (network is null || !TryPreparePacketTransport(network)))
        {
            return AppleContainerCommandResult.ExecutionFailed;
        }

        return await RunAsync(
                CreateArguments(request, resourceName),
                CreateTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string MountArgument(WorkspaceIsolationMount mount) =>
        $"type=bind,source={mount.HostSource},target={mount.GuestDestination}"
        + (mount.IsReadOnly ? ",readonly" : string.Empty);

    private async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationNetworkBinding>>
        EnsureNetworkAsync(
            string containerResourceName,
            CancellationToken cancellationToken)
    {
        var networkName = $"{containerResourceName}-network";
        var inspect = await RunAsync(
                ["network", "inspect", networkName],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (inspect.IsSuccess)
        {
            return TryReadOwnedNetwork(inspect.StandardOutput, containerResourceName, out var network)
                ? WorkspaceIsolationResult<WorkspaceIsolationNetworkBinding>.Succeed(network)
                : WorkspaceIsolationResult<WorkspaceIsolationNetworkBinding>.Fail(
                    WorkspaceIsolationErrorCode.PrepareFailed);
        }

        if (inspect.Outcome is not AppleContainerCommandOutcome.Exited)
        {
            return WorkspaceIsolationResult<WorkspaceIsolationNetworkBinding>.Fail(
                MapLifecycleFailure(inspect, WorkspaceIsolationErrorCode.PrepareFailed));
        }

        var create = await RunAsync(
                [
                    "network",
                    "create",
                    "--internal",
                    "--label",
                    $"{OwnershipLabel}={containerResourceName}",
                    "--label",
                    $"{SchemaLabel}={HostOnlyNetworkSchemaVersion}",
                    networkName,
                ],
                LifecycleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var createdInspect = await RunAsync(
                ["network", "inspect", networkName],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (createdInspect.IsSuccess
            && TryReadOwnedNetwork(
                createdInspect.StandardOutput,
                containerResourceName,
                out var createdNetwork))
        {
            return WorkspaceIsolationResult<WorkspaceIsolationNetworkBinding>.Succeed(
                createdNetwork);
        }

        var failure = create.IsSuccess ? createdInspect : create;
        return WorkspaceIsolationResult<WorkspaceIsolationNetworkBinding>.Fail(
            MapLifecycleFailure(failure, WorkspaceIsolationErrorCode.PrepareFailed));
    }

    private async ValueTask<WorkspaceIsolationResult<Unit>> RemoveOwnedNetworkAsync(
        string containerResourceName,
        CancellationToken cancellationToken)
    {
        var networkName = $"{containerResourceName}-network";
        var inspect = await RunAsync(
                ["network", "inspect", networkName],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!inspect.IsSuccess)
        {
            if (inspect.Outcome is not AppleContainerCommandOutcome.Exited)
            {
                return WorkspaceIsolationResult<Unit>.Fail(
                    MapLifecycleFailure(inspect, WorkspaceIsolationErrorCode.PrepareFailed));
            }

            var list = await RunAsync(
                    ["network", "list", "--format", "json"],
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            return list.IsSuccess && ConfirmsNetworkAbsent(list.StandardOutput, networkName)
                ? WorkspaceIsolationResult<Unit>.Succeed(Unit.Value)
                : WorkspaceIsolationResult<Unit>.Fail(
                    MapLifecycleFailure(inspect, WorkspaceIsolationErrorCode.PrepareFailed));
        }

        if (!TryReadOwnedNetwork(inspect.StandardOutput, containerResourceName, out _))
        {
            return WorkspaceIsolationResult<Unit>.Fail(
                WorkspaceIsolationErrorCode.PrepareFailed);
        }

        var delete = await RunAsync(
                ["network", "delete", networkName],
                LifecycleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return delete.IsSuccess
            ? WorkspaceIsolationResult<Unit>.Succeed(Unit.Value)
            : WorkspaceIsolationResult<Unit>.Fail(
                MapLifecycleFailure(delete, WorkspaceIsolationErrorCode.PrepareFailed));
    }

    private async ValueTask<AppleContainerCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        IProgress<string>? outputProgress = null)
    {
        try
        {
            return await _commandRunner(
                new AppleContainerCommand(
                    _containerExecutable,
                    arguments,
                    timeout,
                    outputProgress),
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AppleContainerCommandResult.Cancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return AppleContainerCommandResult.ExecutionFailed;
        }
    }

    private async ValueTask<bool> EnsureGuestRuntimeAsync(
        string resourceName,
        CancellationToken cancellationToken)
    {
        var provision = await RunAsync(
                [
                    "exec",
                    resourceName,
                    _guestShellExecutable,
                    "-c",
                    GuestProvisioningScript,
                    "asura-provision",
                    WorkspaceUserName,
                    _guestUserId.ToString(CultureInfo.InvariantCulture),
                    _guestGroupId.ToString(CultureInfo.InvariantCulture),
                    DefaultGuestWorkingDirectory,
                ],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!provision.IsSuccess)
        {
            return false;
        }

        var validate = await RunAsync(
                [
                    "exec",
                    "--user",
                    _guestUserId.ToString(CultureInfo.InvariantCulture),
                    "--env",
                    $"HOME={DefaultGuestWorkingDirectory}",
                    "--workdir",
                    DefaultGuestWorkingDirectory,
                    resourceName,
                    _guestShellExecutable,
                    "-c",
                    "test -d \"$HOME\" && test -w \"$HOME\" && "
                    + "{ ! command -v sudo >/dev/null 2>&1 || sudo -n true; }",
                ],
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return validate.IsSuccess;
    }

    private static bool TryMapCommandFailure(
        AppleContainerCommandResult result,
        out WorkspaceIsolationErrorCode error)
    {
        if (result.IsSuccess)
        {
            error = WorkspaceIsolationErrorCode.None;
            return false;
        }

        error = MapLifecycleFailure(result, WorkspaceIsolationErrorCode.RuntimeUnavailable);
        return true;
    }

    private static WorkspaceIsolationErrorCode MapLifecycleFailure(
        AppleContainerCommandResult result,
        WorkspaceIsolationErrorCode fallback) =>
        result.Outcome switch
        {
            AppleContainerCommandOutcome.StartFailed
                when result.StartFailure == AppleContainerCommandStartFailure.NotFound =>
                WorkspaceIsolationErrorCode.RuntimeMissing,
            AppleContainerCommandOutcome.Cancelled => WorkspaceIsolationErrorCode.Cancelled,
            AppleContainerCommandOutcome.TimedOut => WorkspaceIsolationErrorCode.Timeout,
            _ when result.StandardError.Contains(
                "failed to find target executable /sbin/init",
                StringComparison.Ordinal) => WorkspaceIsolationErrorCode.ImageNotBootable,
            _ => fallback,
        };

    private static bool TryReadClientVersion(string json, out Version version)
    {
        version = new Version();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("appName", out var appName)
                    || appName.ValueKind != JsonValueKind.String
                    || !string.Equals(appName.GetString(), "container", StringComparison.Ordinal)
                    || !item.TryGetProperty("version", out var value)
                    || value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { } versionText)
                {
                    continue;
                }

                var semanticCore = versionText.Split(['-', '+'], 2)[0];
                return Version.TryParse(semanticCore, out version!);
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool IsExpectedContainer(
        string json,
        WorkspaceIsolationPrepareRequest request,
        string resourceName) =>
        TryReadExpectedContainerConfiguration(
            json,
            resourceName,
            out var configuredMounts,
            out var configuredImage,
            out var configuredBaseImage,
            out var forwardsSshAgent)
        && MountsEqual(configuredMounts, request.Mounts)
        && forwardsSshAgent
        && ImageConfigurationMatches(
            request,
            resourceName,
            configuredImage,
            configuredBaseImage);

    private bool TryReadExpectedContainerMounts(
        string json,
        string resourceName,
        out IReadOnlyList<WorkspaceIsolationMount> configuredMounts) =>
        TryReadExpectedContainerConfiguration(
            json,
            resourceName,
            out configuredMounts,
            out _,
            out _,
            out _);

    private bool TryReadExpectedContainerConfiguration(
        string json,
        string resourceName,
        out IReadOnlyList<WorkspaceIsolationMount> configuredMounts,
        out string configuredImage,
        out string? configuredBaseImage,
        out bool forwardsSshAgent)
    {
        configuredMounts = [];
        configuredImage = string.Empty;
        configuredBaseImage = null;
        forwardsSshAgent = false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() != 1)
            {
                return false;
            }

            var snapshot = document.RootElement[0];
            if (!snapshot.TryGetProperty("configuration", out var configuration)
                || !configuration.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), resourceName, StringComparison.Ordinal)
                || !configuration.TryGetProperty("labels", out var labels)
                || !HasLabel(labels, OwnershipLabel, resourceName)
                || !HasLabel(labels, SchemaLabel, IsolationSchemaVersion)
                || !configuration.TryGetProperty("image", out var image)
                || !image.TryGetProperty("reference", out var imageReference)
                || imageReference.ValueKind != JsonValueKind.String
                || !configuration.TryGetProperty("resources", out var resources)
                || !resources.TryGetProperty("cpus", out var cpus)
                || !cpus.TryGetInt32(out var cpuCount)
                || cpuCount != WorkspaceCpuCount
                || !resources.TryGetProperty("memoryInBytes", out var memory)
                || !memory.TryGetUInt64(out var memoryBytes)
                || memoryBytes != WorkspaceMemoryBytes
                || !configuration.TryGetProperty("ssh", out var ssh)
                || ssh.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !HasExpectedNetworkConfiguration(configuration, resourceName)
                || !configuration.TryGetProperty("useInit", out var useInit)
                || useInit.ValueKind is not JsonValueKind.False
                || !configuration.TryGetProperty("initProcess", out var initProcess)
                || !initProcess.TryGetProperty("executable", out var initExecutable)
                || initExecutable.ValueKind != JsonValueKind.String
                || !string.Equals(
                    initExecutable.GetString(),
                    _useHostOnlyNetwork
                        ? GuestPacketGatewayExecutable
                        : "/sbin/init",
                    StringComparison.Ordinal)
                || !HasExpectedInitArguments(initProcess, resourceName)
                || !HasExpectedPacketTransportConfiguration(configuration, resourceName)
                || !configuration.TryGetProperty("mounts", out var mounts)
                || mounts.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            configuredImage = imageReference.GetString()!;
            forwardsSshAgent = ssh.GetBoolean();
            if (labels.TryGetProperty(BaseImageLabel, out var baseImage)
                && baseImage.ValueKind == JsonValueKind.String)
            {
                configuredBaseImage = baseImage.GetString();
            }

            var mountsFromRuntime = new List<WorkspaceIsolationMount>();
            var internalHelperMounts = 0;
            foreach (var mount in mounts.EnumerateArray())
            {
                if (!TryReadMount(mount, out var configured))
                {
                    return false;
                }

                if (_useHostOnlyNetwork
                    && string.Equals(
                        configured.HostSource,
                        _guestPacketGatewayHelperDirectory,
                        StringComparison.Ordinal)
                    && string.Equals(
                        configured.GuestDestination,
                        GuestPacketGatewayDirectory,
                        StringComparison.Ordinal)
                    && configured.IsReadOnly)
                {
                    internalHelperMounts++;
                    continue;
                }

                mountsFromRuntime.Add(new WorkspaceIsolationMount(
                    configured.HostSource,
                    configured.GuestDestination,
                    configured.IsReadOnly));
            }

            if (_useHostOnlyNetwork && internalHelperMounts != 1)
            {
                return false;
            }

            configuredMounts = mountsFromRuntime.AsReadOnly();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private bool HasExpectedInitArguments(JsonElement initProcess, string resourceName)
    {
        if (!_useHostOnlyNetwork)
        {
            return true;
        }

        if (!_resources.TryGetValue(resourceName, out var resource)
            || resource.Network is null
            || !initProcess.TryGetProperty("arguments", out var arguments)
            || arguments.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string[] expected =
        [
            "init",
            "--gateway",
            resource.Network.Ipv4Gateway,
            "--",
            "/sbin/init",
        ];
        if (arguments.GetArrayLength() != expected.Length)
        {
            return false;
        }

        var index = 0;
        foreach (var argument in arguments.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String
                || !string.Equals(argument.GetString(), expected[index], StringComparison.Ordinal))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private bool HasExpectedPacketTransportConfiguration(
        JsonElement configuration,
        string resourceName)
    {
        if (!_useHostOnlyNetwork)
        {
            return true;
        }

        if (!_resources.TryGetValue(resourceName, out var resource)
            || resource.Network is null
            || !configuration.TryGetProperty("publishedSockets", out var sockets)
            || sockets.ValueKind != JsonValueKind.Array
            || sockets.GetArrayLength() != 1)
        {
            return false;
        }

        var socket = sockets[0];
        return socket.TryGetProperty("hostPath", out var hostPath)
               && hostPath.ValueKind == JsonValueKind.String
               && string.Equals(
                   hostPath.GetString(),
                   resource.Network.PacketSocketPath,
                   StringComparison.Ordinal)
               && socket.TryGetProperty("containerPath", out var guestPath)
               && guestPath.ValueKind == JsonValueKind.String
               && string.Equals(
                   guestPath.GetString(),
                   resource.Network.GuestSocketPath,
                   StringComparison.Ordinal)
               && HasNoConfiguredDns(configuration);
    }

    private static bool HasNoConfiguredDns(JsonElement configuration)
    {
        // Apple container 1.3 omits the dns object when --no-dns is used. Older
        // releases represented the same configuration as an empty nameserver list.
        if (!configuration.TryGetProperty("dns", out var dns))
        {
            return true;
        }

        return dns.ValueKind == JsonValueKind.Object
            && dns.TryGetProperty("nameservers", out var nameservers)
            && nameservers.ValueKind == JsonValueKind.Array
            && nameservers.GetArrayLength() == 0;
    }

    private bool HasExpectedNetworkConfiguration(
        JsonElement configuration,
        string resourceName)
    {
        if (!_useHostOnlyNetwork)
        {
            return true;
        }

        return configuration.TryGetProperty("networks", out var networks)
               && networks.ValueKind == JsonValueKind.Array
               && networks.GetArrayLength() == 1
               && networks[0].TryGetProperty("network", out var network)
               && network.ValueKind == JsonValueKind.String
               && string.Equals(
                   network.GetString(),
                   $"{resourceName}-network",
                   StringComparison.Ordinal);
    }

    private bool ImageConfigurationMatches(
        WorkspaceIsolationPrepareRequest request,
        string resourceName,
        string configuredImage,
        string? configuredBaseImage)
    {
        if (request.ImageReference is null)
        {
            // Definitions saved before image selection existed inherit an already-created
            // environment. New environments still use the Ubuntu default.
            return string.Equals(configuredBaseImage, _imageReference, StringComparison.Ordinal)
                || string.Equals(
                    configuredBaseImage,
                    LegacyAlpineImageReference,
                    StringComparison.Ordinal)
                || string.Equals(configuredImage, _imageReference, StringComparison.Ordinal)
                || string.Equals(
                    configuredImage,
                    LegacyAlpineImageReference,
                    StringComparison.Ordinal)
                || (string.Equals(
                        configuredImage,
                        SnapshotImageReference(resourceName),
                        StringComparison.Ordinal)
                    && (configuredBaseImage is null
                        || string.Equals(
                            configuredBaseImage,
                            _imageReference,
                            StringComparison.Ordinal)
                        || string.Equals(
                            configuredBaseImage,
                            LegacyAlpineImageReference,
                            StringComparison.Ordinal)));
        }

        return string.Equals(
                configuredBaseImage,
                request.ImageReference,
                StringComparison.Ordinal)
            && (string.Equals(
                    configuredImage,
                    request.ImageReference,
                    StringComparison.Ordinal)
                || string.Equals(
                    configuredImage,
                    SnapshotImageReference(resourceName),
                    StringComparison.Ordinal));
    }

    private static bool HasLabel(JsonElement labels, string name, string expectedValue) =>
        labels.ValueKind == JsonValueKind.Object
        && labels.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expectedValue, StringComparison.Ordinal);

    private static bool IsStoppedContainer(string json, string resourceName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() != 1)
            {
                return false;
            }

            var snapshot = document.RootElement[0];
            return snapshot.TryGetProperty("configuration", out var configuration)
                   && configuration.TryGetProperty("id", out var id)
                   && id.ValueKind == JsonValueKind.String
                   && string.Equals(id.GetString(), resourceName, StringComparison.Ordinal)
                   && snapshot.TryGetProperty("status", out var status)
                   && IsStoppedStatus(status);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ConfirmsContainerAbsent(string json, string expectedName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var container in document.RootElement.EnumerateArray())
            {
                if (container.ValueKind != JsonValueKind.Object
                    || !container.TryGetProperty("configuration", out var configuration)
                    || configuration.ValueKind != JsonValueKind.Object
                    || !configuration.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { } resourceName)
                {
                    return false;
                }

                if (string.Equals(resourceName, expectedName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool TryReadOwnedNetwork(
        string json,
        string containerResourceName,
        out WorkspaceIsolationNetworkBinding network)
    {
        network = null!;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() != 1)
            {
                return false;
            }

            var expectedName = $"{containerResourceName}-network";
            var snapshot = document.RootElement[0];
            if (!snapshot.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), expectedName, StringComparison.Ordinal)
                || !snapshot.TryGetProperty("configuration", out var configuration)
                || !configuration.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || !string.Equals(name.GetString(), expectedName, StringComparison.Ordinal)
                || !configuration.TryGetProperty("mode", out var mode)
                || mode.ValueKind != JsonValueKind.String
                || !string.Equals(mode.GetString(), "hostOnly", StringComparison.Ordinal)
                || !configuration.TryGetProperty("labels", out var labels)
                || !HasLabel(labels, OwnershipLabel, containerResourceName)
                || !HasLabel(labels, SchemaLabel, HostOnlyNetworkSchemaVersion)
                || !snapshot.TryGetProperty("status", out var status)
                || !status.TryGetProperty("ipv4Gateway", out var gateway)
                || gateway.ValueKind != JsonValueKind.String
                || gateway.GetString() is not { } gatewayValue
                || !status.TryGetProperty("ipv4Subnet", out var subnet)
                || subnet.ValueKind != JsonValueKind.String
                || subnet.GetString() is not { } subnetValue)
            {
                return false;
            }

            network = new WorkspaceIsolationNetworkBinding(
                expectedName,
                gatewayValue,
                subnetValue,
                PacketSocketPath(containerResourceName),
                GuestPacketGatewaySocket,
                GuestPacketGatewayExecutable);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static bool ConfirmsNetworkAbsent(string json, string expectedName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var network in document.RootElement.EnumerateArray())
            {
                if (network.ValueKind != JsonValueKind.Object
                    || !network.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { } resourceName)
                {
                    return false;
                }

                if (string.Equals(resourceName, expectedName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsOwnedContainer(string json, string resourceName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() != 1)
            {
                return false;
            }

            var container = document.RootElement[0];
            return container.TryGetProperty("configuration", out var configuration)
                && configuration.ValueKind == JsonValueKind.Object
                && configuration.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), resourceName, StringComparison.Ordinal)
                && configuration.TryGetProperty("labels", out var labels)
                && HasLabel(labels, OwnershipLabel, resourceName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsStoppedStatus(JsonElement status)
    {
        if (status.ValueKind == JsonValueKind.String)
        {
            return string.Equals(status.GetString(), "stopped", StringComparison.Ordinal);
        }

        return status.ValueKind == JsonValueKind.Object
               && status.TryGetProperty("state", out var state)
               && state.ValueKind == JsonValueKind.String
               && string.Equals(state.GetString(), "stopped", StringComparison.Ordinal);
    }

    private static bool TryReadMount(JsonElement mount, out InspectedMount configured)
    {
        configured = default;
        if (!mount.TryGetProperty("source", out var source)
            || source.ValueKind != JsonValueKind.String
            || source.GetString() is not { } hostSource
            || !mount.TryGetProperty("destination", out var destination)
            || destination.ValueKind != JsonValueKind.String
            || destination.GetString() is not { } guestDestination)
        {
            return false;
        }

        var isReadOnly = false;
        if (mount.TryGetProperty("options", out var options))
        {
            if (options.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                isReadOnly |= option.GetString() is "ro" or "readonly";
            }
        }

        configured = new InspectedMount(hostSource, guestDestination, isReadOnly);
        return true;
    }

    private static bool MountsEqual(
        IReadOnlyList<WorkspaceIsolationMount> left,
        IReadOnlyList<WorkspaceIsolationMount> right) =>
        left.Count == right.Count
        && left.All(expected => right.Any(actual => actual == expected));

    private static string ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Workspace isolation values cannot contain NUL characters.", parameterName);
        }

        return value;
    }

    private static string ValidateAbsolutePath(string value, string parameterName)
    {
        var path = ValidateText(value, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "Workspace isolation paths must be absolute.",
                parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string BoundedDiagnostic(string value)
    {
        var diagnostic = value.Trim();
        if (diagnostic.Length == 0)
        {
            return "the runtime did not provide an error message";
        }

        return diagnostic.Length <= MaximumDiagnosticCharacters
            ? diagnostic
            : diagnostic[..MaximumDiagnosticCharacters];
    }

    private static string ValidateArgument(string? value, string parameterName)
    {
        if (value is null || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Workspace isolation arguments cannot be null or contain NUL characters.",
                parameterName);
        }

        return value;
    }

    private static uint CurrentUserId() => OperatingSystem.IsMacOS()
        ? GetEffectiveUserId()
        : 1000;

    private static uint CurrentGroupId() => OperatingSystem.IsMacOS()
        ? GetEffectiveGroupId()
        : 1000;

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEffectiveGroupId();

    private static async ValueTask<AppleContainerCommandResult> RunCommandAsync(
        AppleContainerCommand command,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return AppleContainerCommandResult.Cancelled;
        }

        using var process = CreateProcess(command);
        try
        {
            if (!process.Start())
            {
                return AppleContainerCommandResult.StartFailed(
                    AppleContainerCommandStartFailure.Unknown);
            }
        }
        catch (Win32Exception exception)
        {
            return AppleContainerCommandResult.StartFailed(exception.NativeErrorCode switch
            {
                2 or 3 => AppleContainerCommandStartFailure.NotFound,
                5 or 13 => AppleContainerCommandStartFailure.PermissionDenied,
                _ => AppleContainerCommandStartFailure.Unknown,
            });
        }
        catch (FileNotFoundException)
        {
            return AppleContainerCommandResult.StartFailed(
                AppleContainerCommandStartFailure.NotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return AppleContainerCommandResult.StartFailed(
                AppleContainerCommandStartFailure.PermissionDenied);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TryKill(process);
            return AppleContainerCommandResult.ExecutionFailed;
        }

        var stdoutTask = Task.FromResult(string.Empty);
        var stderrTask = Task.FromResult(string.Empty);
        try
        {
            using var timeout = command.Timeout is { } timeoutValue
                ? new CancellationTokenSource(timeoutValue)
                : null;
            using var linked = timeout is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
            stdoutTask = ReadBoundedAsync(
                process.StandardOutput,
                linked.Token,
                command.OutputProgress);
            stderrTask = ReadBoundedAsync(
                process.StandardError,
                linked.Token,
                command.OutputProgress);
            await Task.WhenAll(
                    process.WaitForExitAsync(linked.Token),
                    stdoutTask,
                    stderrTask)
                .ConfigureAwait(false);
            return AppleContainerCommandResult.Exited(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitDrainAfterCancellationAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? AppleContainerCommandResult.Cancelled
                : command.Timeout is not null
                    ? AppleContainerCommandResult.TimedOut
                    : AppleContainerCommandResult.ExecutionFailed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TryKill(process);
            await AwaitDrainAfterCancellationAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            return AppleContainerCommandResult.ExecutionFailed;
        }
    }

    private static Process CreateProcess(AppleContainerCommand command)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        var result = new char[MaximumCapturedCharacters];
        var scratch = new char[2048];
        var written = 0;
        while (true)
        {
            var read = await reader.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new string(result, 0, written);
            }

            progress?.Report(new string(scratch, 0, read));

            var remaining = result.Length - written;
            if (remaining > 0)
            {
                var copy = Math.Min(read, remaining);
                scratch.AsSpan(0, copy).CopyTo(result.AsSpan(written));
                written += copy;
            }
        }
    }

    private static async Task AwaitDrainAfterCancellationAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The cancellation outcome is represented by the command result.
        }
        catch (IOException)
        {
            // Closing redirected streams is expected after forced process teardown.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The command outcome already records the execution failure. Stream-drain cleanup
            // must not allow a secondary reader fault to escape that typed boundary.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the kill request.
        }
        catch (Win32Exception)
        {
            // The operating system completed or denied teardown; the result stays typed.
        }
    }

    private sealed class AppleContainerRuntimeProgress(
        IProgress<WorkspaceIsolationProgress> progress) : IProgress<string>
    {
        private const int MaximumTailCharacters = 4096;
        private readonly object _gate = new();
        private string _tail = string.Empty;
        private string? _lastStatus;

        public void Report(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (_gate)
            {
                _tail += value;
                if (_tail.Length > MaximumTailCharacters)
                {
                    _tail = _tail[^MaximumTailCharacters..];
                }

                var status = CurrentStatus(_tail);
                if (status is null || string.Equals(status, _lastStatus, StringComparison.Ordinal))
                {
                    return;
                }

                _lastStatus = status;
                progress.Report(new WorkspaceIsolationProgress(status));
            }
        }

        private static string? CurrentStatus(string output)
        {
            var bestIndex = -1;
            string? status = null;
            Consider(
                output,
                "Launching container-apiserver",
                "Starting Apple container services…",
                ref bestIndex,
                ref status);
            Consider(
                output,
                "Testing access to container-apiserver",
                "Waiting for Apple container services…",
                ref bestIndex,
                ref status);
            Consider(
                output,
                "Verifying machine API server is running",
                "Checking the Apple container machine service…",
                ref bestIndex,
                ref status);
            Consider(
                output,
                "Installing kernel",
                "Downloading and installing the Apple container Linux kernel…",
                ref bestIndex,
                ref status);
            Consider(
                output,
                "Verifying kernel archive",
                "Verifying the Apple container Linux kernel…",
                ref bestIndex,
                ref status);
            Consider(
                output,
                "Unpacking kernel",
                "Unpacking the Apple container Linux kernel…",
                ref bestIndex,
                ref status);

            const string downloadMarker = "Downloading kernel ";
            var downloadIndex = output.LastIndexOf(downloadMarker, StringComparison.Ordinal);
            if (downloadIndex <= bestIndex)
            {
                return status;
            }

            var valueStart = downloadIndex + downloadMarker.Length;
            var valueEnd = valueStart;
            while (valueEnd < output.Length && char.IsAsciiDigit(output[valueEnd]))
            {
                valueEnd++;
            }

            return valueEnd > valueStart
                   && valueEnd < output.Length
                   && output[valueEnd] == '%'
                ? $"Downloading the Apple container Linux kernel… {output[valueStart..valueEnd]}%"
                : "Downloading the Apple container Linux kernel…";
        }

        private static void Consider(
            string output,
            string marker,
            string candidate,
            ref int bestIndex,
            ref string? status)
        {
            var index = output.LastIndexOf(marker, StringComparison.Ordinal);
            if (index <= bestIndex)
            {
                return;
            }

            bestIndex = index;
            status = candidate;
        }
    }

    private sealed class AppleContainerImageProgress(
        IProgress<WorkspaceIsolationProgress> progress) : IProgress<string>
    {
        private const int MaximumTailCharacters = 1024;
        private readonly object _gate = new();
        private string _tail = string.Empty;
        private string? _lastStatus;

        public void Report(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (_gate)
            {
                _tail += value;
                if (_tail.Length > MaximumTailCharacters)
                {
                    _tail = _tail[^MaximumTailCharacters..];
                }

                var status = CurrentStatus(_tail);
                if (string.Equals(status, _lastStatus, StringComparison.Ordinal))
                {
                    return;
                }

                _lastStatus = status;
                progress.Report(new WorkspaceIsolationProgress(status));
            }
        }

        private static string CurrentStatus(string output)
        {
            const string marker = "Fetching image ";
            var markerIndex = output.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return "Downloading the workspace image…";
            }

            var valueStart = markerIndex + marker.Length;
            var valueEnd = valueStart;
            while (valueEnd < output.Length && char.IsAsciiDigit(output[valueEnd]))
            {
                valueEnd++;
            }

            return valueEnd > valueStart
                   && valueEnd < output.Length
                   && output[valueEnd] == '%'
                ? $"Downloading the workspace image… {output[valueStart..valueEnd]}%"
                : "Downloading the workspace image…";
        }
    }

    private sealed class ResourceLeaseState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public HashSet<Guid> ActiveLeases { get; } = [];

        public IReadOnlyList<WorkspaceIsolationMount>? Mounts { get; set; }

        public string? ImageReference { get; set; }

        public string? RuntimeImageReference { get; set; }

        public WorkspaceIsolationNetworkBinding? Network { get; set; }
    }

    private readonly record struct InspectedMount(
        string HostSource,
        string GuestDestination,
        bool IsReadOnly);
}

internal delegate ValueTask<AppleContainerCommandResult> AppleContainerCommandRunner(
    AppleContainerCommand command,
    CancellationToken cancellationToken);

internal sealed record AppleContainerCommand
{
    public AppleContainerCommand(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout,
        IProgress<string>? outputProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, null);
        }

        Executable = executable;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        Timeout = timeout;
        OutputProgress = outputProgress;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public TimeSpan? Timeout { get; }

    public IProgress<string>? OutputProgress { get; }
}

internal enum AppleContainerCommandOutcome
{
    Exited = 1,
    StartFailed = 2,
    TimedOut = 3,
    Cancelled = 4,
    ExecutionFailed = 5,
}

internal enum AppleContainerCommandStartFailure
{
    None = 0,
    NotFound = 1,
    PermissionDenied = 2,
    Unknown = 3,
}

internal sealed record AppleContainerCommandResult
{
    private AppleContainerCommandResult(
        AppleContainerCommandOutcome outcome,
        int? exitCode,
        string standardOutput,
        string standardError,
        AppleContainerCommandStartFailure startFailure)
    {
        Outcome = outcome;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        StartFailure = startFailure;
    }

    public AppleContainerCommandOutcome Outcome { get; }

    public int? ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public AppleContainerCommandStartFailure StartFailure { get; }

    public bool IsSuccess => Outcome == AppleContainerCommandOutcome.Exited && ExitCode == 0;

    public static AppleContainerCommandResult Cancelled { get; } =
        new(AppleContainerCommandOutcome.Cancelled, null, string.Empty, string.Empty,
            AppleContainerCommandStartFailure.None);

    public static AppleContainerCommandResult TimedOut { get; } =
        new(AppleContainerCommandOutcome.TimedOut, null, string.Empty, string.Empty,
            AppleContainerCommandStartFailure.None);

    public static AppleContainerCommandResult ExecutionFailed { get; } =
        new(AppleContainerCommandOutcome.ExecutionFailed, null, string.Empty, string.Empty,
            AppleContainerCommandStartFailure.None);

    public static AppleContainerCommandResult Exited(
        int exitCode,
        string standardOutput = "",
        string standardError = "") =>
        new(AppleContainerCommandOutcome.Exited, exitCode, standardOutput, standardError,
            AppleContainerCommandStartFailure.None);

    public static AppleContainerCommandResult StartFailed(
        AppleContainerCommandStartFailure failure) =>
        new(AppleContainerCommandOutcome.StartFailed, null, string.Empty, string.Empty, failure);
}

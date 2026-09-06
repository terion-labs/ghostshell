using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Owns SDK workspace VM leases and durable disks. A route cannot be enabled by a
/// guest command: the only virtual NIC terminates at the native host runtime.
/// Runtime state is outside the replaceable bundle. CLI workspace disks are not used.
/// </summary>
public sealed partial class WorkspaceSdkIsolationProvider : IWorkspaceIsolationProvider
{
    public static WorkspaceIsolationProviderDescriptor ProviderDescriptor { get; } = new(
        new WorkspaceIsolationProviderId("apple-containerization"),
        "Apple Containerization",
        WorkspaceIsolationCapability.PersistentRootFileSystem
        | WorkspaceIsolationCapability.DedicatedKernel
        | WorkspaceIsolationCapability.DedicatedNetworkNamespace
        | WorkspaceIsolationCapability.HostBindMounts
        | WorkspaceIsolationCapability.StructuredProcessExecution);

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private const string GuestHome = "/home/ghostshell";
    private readonly string _executable;
    private readonly string _stateRoot;
    private readonly string _socketRoot;
    private readonly string _gatewayExecutable;
    private readonly IWorkspaceGatewayProcessRunner _processes;
    private readonly uint _uid;
    private readonly uint _gid;
    private readonly Func<IProgress<WorkspaceIsolationProgress>?, CancellationToken, Task<string>> _prepareBootAssets;
    private string? _bootDirectory;
    private readonly ConcurrentDictionary<WorkspaceId, WorkspaceState> _workspaces = new();

    public WorkspaceSdkIsolationProvider(string executable)
        : this(
            executable,
            Path.Combine(GhostShellDataPaths.CreateDefault().DataDirectory, "sdk-workspaces"),
            BundledWorkspacePacketGatewayBackend.ResolveHostHelperExecutable(new PathConnectionExecutableLocator())
                ?? Path.Combine(AppContext.BaseDirectory, "workspace-network-gateway"),
            new WorkspaceGatewayProcessRunner(),
            OperatingSystem.IsMacOS() ? GetEffectiveUserId() : 1000,
            OperatingSystem.IsMacOS() ? GetEffectiveGroupId() : 1000)
    {
    }

    internal WorkspaceSdkIsolationProvider(
        string executable,
        string stateRoot,
        string gatewayExecutable,
        IWorkspaceGatewayProcessRunner processes,
        uint uid,
        uint gid,
        Func<IProgress<WorkspaceIsolationProgress>?, CancellationToken, Task<string>>? prepareBootAssets = null)
    {
        _executable = Path.GetFullPath(executable);
        _stateRoot = Path.GetFullPath(stateRoot);
        _gatewayExecutable = Path.GetFullPath(gatewayExecutable);
        _processes = processes;
        _uid = uid;
        _gid = gid;
        _prepareBootAssets = prepareBootAssets ?? PrepareBootAssetsAsync;
        // macOS Unix socket paths are short. The private root identity separates users
        // and installations without exposing workspace names in /tmp.
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_stateRoot)))[..16];
        _socketRoot = Path.Combine(Path.GetTempPath(), $"gs-sdk-{identity}");
        if (Encoding.UTF8.GetByteCount(_socketRoot) > 55)
        {
            _socketRoot = Path.Combine("/tmp", $"gs-sdk-{identity}");
        }
    }

    public WorkspaceIsolationProviderDescriptor Descriptor => ProviderDescriptor;

    public static string? FindBundledRuntime()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-arm64",
            "workspace-runtime", "workspace-runtime");
        return File.Exists(candidate) ? candidate : null;
    }

    public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
        WorkspaceIsolationPrepareRequest request,
        CancellationToken cancellationToken) => PrepareAsync(request, null, cancellationToken);

    public async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
        WorkspaceIsolationPrepareRequest request,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = _workspaces.GetOrAdd(request.WorkspaceId, static _ => new WorkspaceState());
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var image = request.ImageReference ?? WorkspaceIsolationImages.Default;
            if (state.Process is { HasExited: false })
            {
                if (!string.Equals(state.Image, image, StringComparison.Ordinal)
                    || !state.Mounts.SequenceEqual(request.Mounts))
                {
                    return Failure("Stop the workspace before changing its image or mounts.");
                }

                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(Acquire(request, state));
            }

            if (state.Process is not null)
            {
                await state.Process.DisposeAsync().ConfigureAwait(false);
                state.Process = null;
                state.Leases.Clear();
            }

            var directory = WorkspaceDirectory(request.WorkspaceId);
            CreatePrivateDirectory(_stateRoot);
            CreatePrivateDirectory(directory);
            CreatePrivateDirectory(_socketRoot);
            var socketDirectory = SocketDirectory(request.WorkspaceId);
            CreatePrivateDirectory(socketDirectory);
            _bootDirectory = await _prepareBootAssets(progress, cancellationToken).ConfigureAwait(false);
            var rootfs = Path.Combine(directory, "rootfs.ext4");
            var imageMarker = Path.Combine(directory, "image.txt");
            if (File.Exists(imageMarker))
            {
                if (!string.Equals(await File.ReadAllTextAsync(imageMarker, cancellationToken).ConfigureAwait(false), image, StringComparison.Ordinal))
                {
                    return Failure("The persistent or staged workspace uses another image. Recreate the environment to change it.");
                }
            }
            else
            {
                if (File.Exists(rootfs) || File.Exists(Path.Combine(directory, "preparing.ext4")))
                {
                    return Failure("The workspace disk has no verified image identity. Recreate the environment to start a fresh SDK workspace.");
                }

                // Commit identity before unpacking starts. An interrupted image download
                // cannot subsequently be resumed under a different requested image.
                var pendingMarker = Path.Combine(directory, $"image-{Guid.NewGuid():N}.tmp");
                await WritePrivateAsync(pendingMarker, Encoding.UTF8.GetBytes(image), cancellationToken).ConfigureAwait(false);
                File.Move(pendingMarker, imageMarker, overwrite: false);
            }

            if (!File.Exists(rootfs))
            {
                progress?.Report(new WorkspaceIsolationProgress("Preparing the SDK workspace disk…"));
                await PrepareDiskAsync(request, rootfs, progress, cancellationToken).ConfigureAwait(false);
            }

            state.Mounts = request.Mounts;
            state.Image = image;
            // A precomputed launch must never target a replacement VM after restart.
            var generation = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..16];
            state.Network = new WorkspaceIsolationNetworkBinding(
                ResourceName(request.WorkspaceId), "100.64.0.1", "100.64.0.0/30",
                hostAttachment: new WorkspaceHostNetworkAttachment(
                    Path.Combine(socketDirectory, $"c-{generation}.sock"), Path.Combine(socketDirectory, $"p-{generation}.sock")));
            progress?.Report(new WorkspaceIsolationProgress("Starting the SDK workspace…"));
            var configuration = Configuration(request, rootfs, state.Network.HostAttachment!.ControlSocketPath, ["/sbin/init"]);
            state.Process = await StartRuntimeAsync(configuration, directory, cancellationToken).ConfigureAwait(false);
            state.Groups = await ProvisionUserAsync(state.Network.HostAttachment.ControlSocketPath, cancellationToken).ConfigureAwait(false);
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(Acquire(request, state));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (state.Process is not null)
            {
                await state.Process.DisposeAsync().ConfigureAwait(false);
                state.Process = null;
            }

            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(WorkspaceIsolationErrorCode.Cancelled);
            }

            SecretSafeDiagnosticProjection.WriteTrace("workspace.sdk.prepare.failed", exception);
            return Failure(exception.Message);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
        WorkspaceIsolationBinding binding,
        WorkspaceIsolationProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBinding(binding);
        if (!_workspaces.TryGetValue(binding.WorkspaceId, out var state)
            || !state.Leases.TryGetValue(binding.LeaseId, out var lease)
            || lease.Attachment != binding.Network!.HostAttachment)
        {
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(WorkspaceIsolationErrorCode.RuntimeUnavailable);
        }

        if (request.UsesHostCredentialBroker)
        {
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(WorkspaceIsolationErrorCode.HostCredentialBrokerUnavailable);
        }

        if (!AppleContainerWorkspaceIsolationProvider.TryMapWorkingDirectory(
                binding.Mounts, request.HostWorkingDirectory, out var workingDirectory))
        {
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(WorkspaceIsolationErrorCode.WorkingDirectoryNotMounted);
        }

        var terminal = (request.Mode & WorkspaceProcessMode.AllocateTerminal) != 0;
        IReadOnlyList<string> arguments;
        switch (request.ConnectionKind)
        {
            case ConnectionKind.Local:
                arguments = terminal
                    ? ["/bin/sh", "-c", AppleContainerWorkspaceIsolationProvider.InteractiveShellBootstrapScript]
                    : request.HostExecutable.StartsWith('/')
                        ? [request.HostExecutable, .. request.Arguments]
                        : ["/usr/bin/env", "--", request.HostExecutable, .. request.Arguments];
                break;
            case ConnectionKind.Ssh when string.Equals(Path.GetFileName(request.HostExecutable), "ssh", StringComparison.Ordinal):
                if (!AppleContainerWorkspaceIsolationProvider.TryPrepareSshTrust(
                        request.Arguments, out var sshArguments, out var knownHosts, out var trust))
                {
                    return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(WorkspaceIsolationErrorCode.SshHostKeyTrustUnavailable);
                }

                arguments = ["/bin/sh", "-c", AppleContainerWorkspaceIsolationProvider.SshBootstrapScript,
                    "ghostshell-ssh", "/usr/bin/ssh", knownHosts, trust, .. sshArguments];
                break;
            default:
                return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(WorkspaceIsolationErrorCode.UnsupportedConnectionKind);
        }

        var environment = new Dictionary<string, string>(request.Environment, StringComparer.Ordinal)
        {
            ["HOME"] = GuestHome,
            ["USER"] = "ghostshell",
            ["LOGNAME"] = "ghostshell",
        };
        environment.Remove("SHELL");
        return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(ExecLaunch(
            binding.Network!.HostAttachment!.ControlSocketPath,
            new WorkspaceSdkExecRequest(arguments, environment, workingDirectory ?? GuestHome, _uid, _gid, terminal,
                SupplementaryGroups: lease.Groups)));
    }

    public async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        ValidateBinding(binding);
        if (!_workspaces.TryGetValue(binding.WorkspaceId, out var state))
        {
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
        }

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!state.Leases.TryGetValue(binding.LeaseId, out var lease))
            {
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
            }

            if (lease.Attachment != binding.Network!.HostAttachment)
            {
                throw new ArgumentException("The binding does not match its active SDK lease.", nameof(binding));
            }

            if (state.Leases.Count > 1)
            {
                state.Leases.TryRemove(binding.LeaseId, out _);
                return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
            }

            if (state.Process is { HasExited: false })
            {
                var stopped = await _processes.RunAsync(
                        new WorkspaceGatewayProcessRequest(_executable,
                            ["stop", "--socket", binding.Network!.HostAttachment!.ControlSocketPath], ReadOnlyMemory<byte>.Empty),
                        TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
                if (stopped.ExitCode != 0)
                {
                    return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(WorkspaceIsolationErrorCode.StopFailed, binding);
                }
            }

            if (state.Process is not null)
            {
                await state.Process.DisposeAsync().ConfigureAwait(false);
                state.Process = null;
            }

            state.Leases.TryRemove(binding.LeaseId, out _);
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            SecretSafeDiagnosticProjection.WriteTrace("workspace.sdk.stop.failed", exception);
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(WorkspaceIsolationErrorCode.StopFailed, binding);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async ValueTask<WorkspaceIsolationResult<Unit>> RecreateAsync(
        WorkspaceIsolationPrepareRequest request,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = _workspaces.GetOrAdd(request.WorkspaceId, static _ => new WorkspaceState());
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Process is { HasExited: false })
            {
                var stopped = await _processes.RunAsync(new WorkspaceGatewayProcessRequest(_executable,
                        ["stop", "--socket", state.Network!.HostAttachment!.ControlSocketPath], ReadOnlyMemory<byte>.Empty),
                        TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                if (stopped.ExitCode != 0)
                {
                    return WorkspaceIsolationResult<Unit>.Fail(WorkspaceIsolationErrorCode.StopFailed);
                }
            }

            if (state.Process is not null)
            {
                await state.Process.DisposeAsync().ConfigureAwait(false);
                state.Process = null;
            }

            state.Leases.Clear();
            var directory = WorkspaceDirectory(request.WorkspaceId);
            if (Directory.Exists(directory))
            {
                // Recreate is a user-requested reset, but retain the disk as a recovery
                // archive instead of making guest-only files impossible to recover.
                var archive = $"{directory}.retired-{Guid.NewGuid():N}";
                Directory.Move(directory, archive);
                progress?.Report(new WorkspaceIsolationProgress($"Previous workspace disk retained at {archive}"));
            }

            CreatePrivateDirectory(directory);
            return WorkspaceIsolationResult<Unit>.Succeed(Unit.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            SecretSafeDiagnosticProjection.WriteTrace("workspace.sdk.recreate.failed", exception);
            return WorkspaceIsolationResult<Unit>.Fail(WorkspaceIsolationErrorCode.PrepareFailed);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private WorkspaceIsolationBinding Acquire(WorkspaceIsolationPrepareRequest request, WorkspaceState state)
    {
        var lease = Guid.NewGuid();
        state.Leases.TryAdd(lease, (state.Network!.HostAttachment!, state.Groups));
        // Intent checks compare the saved choice, where null means the default.
        // Keep that separate from the concrete image used to boot this VM.
        return new WorkspaceIsolationBinding(request.WorkspaceId, Descriptor.Id, Descriptor.Capabilities,
            ResourceName(request.WorkspaceId), state.Mounts, lease,
            imageReference: request.ImageReference, runtimeImageReference: state.Image, network: state.Network);
    }

    private void ValidateBinding(WorkspaceIsolationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Provider != ProviderDescriptor.Id
            || !string.Equals(binding.ResourceName, ResourceName(binding.WorkspaceId), StringComparison.Ordinal)
            || binding.Network?.HostAttachment is not { } attachment
            || !IsOwnedSocketPair(binding.WorkspaceId, attachment))
        {
            throw new ArgumentException("The binding is not an SDK workspace owned by this provider.", nameof(binding));
        }
    }

    private bool IsOwnedSocketPair(WorkspaceId workspaceId, WorkspaceHostNetworkAttachment attachment)
    {
        var directory = SocketDirectory(workspaceId);
        var controlName = Path.GetFileName(attachment.ControlSocketPath);
        return string.Equals(Path.GetDirectoryName(attachment.ControlSocketPath), directory, StringComparison.Ordinal)
            && string.Equals(Path.GetDirectoryName(attachment.PacketSocketPath), directory, StringComparison.Ordinal)
            && controlName.Length == 23
            && controlName.StartsWith("c-", StringComparison.Ordinal)
            && controlName.EndsWith(".sock", StringComparison.Ordinal)
            && controlName.AsSpan(2, 16).IndexOfAnyExcept("0123456789abcdef") < 0
            && string.Equals(Path.GetFileName(attachment.PacketSocketPath), "p-" + controlName[2..], StringComparison.Ordinal);
    }

    private static string ResourceName(WorkspaceId workspaceId) => AppleContainerWorkspaceIsolationProvider.ResourceName(workspaceId);

    private string WorkspaceDirectory(WorkspaceId workspaceId) => Path.Combine(_stateRoot, ResourceName(workspaceId));

    private string SocketDirectory(WorkspaceId workspaceId) => Path.Combine(_socketRoot, ResourceName(workspaceId)[^20..]);

    private WorkspaceSdkConfiguration Configuration(
        WorkspaceIsolationPrepareRequest request, string rootfs, string socket, IReadOnlyList<string> initialArguments)
    {
        var assetDirectory = _bootDirectory ?? throw new InvalidOperationException("Boot images must be provisioned before starting a workspace.");
        return new(ResourceName(request.WorkspaceId), socket, rootfs,
            Path.Combine(assetDirectory, "kernel.bin"),
            Path.Combine(assetDirectory, "initfs.ext4"), _gatewayExecutable,
            1, 1024UL * 1024 * 1024, ResourceName(request.WorkspaceId),
            [.. request.Mounts.Select(static mount => new WorkspaceSdkMount(mount.HostSource, mount.GuestDestination, mount.IsReadOnly))],
            initialArguments);
    }

    private Task<string> PrepareBootAssetsAsync(IProgress<WorkspaceIsolationProgress>? progress, CancellationToken cancellationToken)
    {
        var runtimeDirectory = Path.GetDirectoryName(_executable)!;
        var executableDirectory = new DirectoryInfo(runtimeDirectory).Parent?.Parent?.Parent;
        var assetDirectory = runtimeDirectory;
        if (executableDirectory is { Name: "MacOS", Parent.Name: "Contents" }
            && string.Equals(runtimeDirectory, Path.Combine(executableDirectory.FullName,
                "runtimes", "osx-arm64", "workspace-runtime"), StringComparison.Ordinal))
        {
            // Only the pinned descriptor and Swift resources live in the bundle.
            assetDirectory = Path.Combine(executableDirectory.Parent.FullName, "Resources",
                "runtimes", "osx-arm64", "workspace-runtime");
        }
        var version = typeof(WorkspaceSdkIsolationProvider).Assembly.GetName().Version!.ToString(3);
        var assets = new WorkspaceBootAssets(Path.Combine(assetDirectory, "boot-assets.json"), Path.Combine(_stateRoot, "boot"),
            new Uri($"https://github.com/terion-labs/ghostshell/releases/download/v{version}/{WorkspaceBootAssets.ArchiveName}"));
        // The development launcher supplies the locally built sidecar. It is still
        // verified against the descriptor; production never requires this override.
        return assets.EnsureAsync(progress, cancellationToken,
            localArchive: Environment.GetEnvironmentVariable("GHOSTSHELL_WORKSPACE_BOOT_ARCHIVE"));
    }

    private async ValueTask<IWorkspaceGatewayProcess> StartRuntimeAsync(
        WorkspaceSdkConfiguration configuration, string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "runtime.json");
        await WritePrivateAsync(path, JsonSerializer.SerializeToUtf8Bytes(configuration,
            WorkspaceSdkJsonContext.Default.WorkspaceSdkConfiguration), cancellationToken).ConfigureAwait(false);
        var started = await _processes.StartAsync(
                new WorkspaceGatewayProcessRequest(_executable, ["serve", "--config", path], ReadOnlyMemory<byte>.Empty),
                StartupTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(started.ReadinessLine, "READY v1", StringComparison.Ordinal))
        {
            await started.Process.DisposeAsync().ConfigureAwait(false);
            throw new IOException("The bundled workspace runtime returned an invalid startup response.");
        }

        return started.Process;
    }

    private WorkspaceProcessLaunch ExecLaunch(string socket, WorkspaceSdkExecRequest request) =>
        new(_executable, ["exec", "--socket", socket, "--request",
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request, WorkspaceSdkJsonContext.Default.WorkspaceSdkExecRequest))],
            EmptyEnvironment, null);

    private async ValueTask<IReadOnlyList<uint>> ProvisionUserAsync(string socket, CancellationToken cancellationToken)
    {
        var request = new WorkspaceSdkExecRequest(
            ["/bin/sh", "-c", AppleContainerWorkspaceIsolationProvider.GuestProvisioningScript, "ghostshell-provision", "ghostshell",
                _uid.ToString(CultureInfo.InvariantCulture), _gid.ToString(CultureInfo.InvariantCulture), GuestHome],
            EmptyEnvironment, "/", 0, 0);
        var launch = ExecLaunch(socket, request);
        var result = await _processes.RunAsync(
                new WorkspaceGatewayProcessRequest(launch.Executable, launch.Arguments, ReadOnlyMemory<byte>.Empty),
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException("The SDK workspace could not prepare its user account.");
        }

        // The SDK accepts numeric credentials, unlike CLI --user which resolves
        // supplementary groups. Preserve membership needed by tools such as Docker.
        var groupLaunch = ExecLaunch(socket, new WorkspaceSdkExecRequest(
            ["/usr/bin/id", "-G", _uid.ToString(CultureInfo.InvariantCulture)], EmptyEnvironment, "/", 0, 0));
        var groupResult = await _processes.RunAsync(new WorkspaceGatewayProcessRequest(
                groupLaunch.Executable, groupLaunch.Arguments, ReadOnlyMemory<byte>.Empty), TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        var groupText = groupResult.Diagnostic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (groupResult.ExitCode != 0 || groupText.Length is 0 or > 256)
        {
            throw new IOException("The SDK workspace could not resolve its user's supplementary groups.");
        }

        var groups = new List<uint>(groupText.Length);
        foreach (var value in groupText)
        {
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var group))
            {
                throw new IOException("The SDK workspace returned invalid supplementary groups.");
            }

            groups.Add(group);
        }

        return groups;
    }

    private static WorkspaceIsolationResult<WorkspaceIsolationBinding> Failure(string message) =>
        new WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure(new WorkspaceIsolationError(
            WorkspaceIsolationErrorCode.PrepareFailed, "workspace_sdk_prepare_failed", message, true, WorkspaceIsolationRecoveryAction.Retry));

    private static void CreatePrivateDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if (directory.LinkTarget is not null)
        {
            throw new IOException("The workspace runtime directory must not be a symbolic link.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async ValueTask WritePrivateAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException("The workspace runtime file must not be a symbolic link.");
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var file = new FileStream(path, options);
        await file.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEffectiveGroupId();

    private sealed class WorkspaceState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ConcurrentDictionary<Guid, (WorkspaceHostNetworkAttachment Attachment, IReadOnlyList<uint> Groups)> Leases { get; } = new();
        public IWorkspaceGatewayProcess? Process { get; set; }
        public IReadOnlyList<WorkspaceIsolationMount> Mounts { get; set; } = [];
        public string? Image { get; set; }
        public WorkspaceIsolationNetworkBinding? Network { get; set; }
        public IReadOnlyList<uint> Groups { get; set; } = [];
    }
}

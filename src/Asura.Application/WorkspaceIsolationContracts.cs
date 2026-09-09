using System.Collections.ObjectModel;
using Asura.Core;

namespace Asura.Application;

public static class WorkspaceIsolationImages
{
    public const string Default = "ubuntu:24.04";

    public static string ForDisplay(string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        var normalized = imageReference.Trim();
        const string dockerHubPrefix = "docker.io/";
        if (!normalized.StartsWith(dockerHubPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        normalized = normalized[dockerHubPrefix.Length..];
        const string officialImagesPrefix = "library/";
        if (normalized.StartsWith(officialImagesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[officialImagesPrefix.Length..];
        }

        var digestSeparator = normalized.IndexOf('@', StringComparison.Ordinal);
        if (digestSeparator >= 0)
        {
            normalized = normalized[..digestSeparator];
        }

        const string latestTag = ":latest";
        return normalized.EndsWith(latestTag, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^latestTag.Length]
            : normalized;
    }
}

public readonly record struct WorkspaceIsolationProviderId
{
    public WorkspaceIsolationProviderId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64
            || !char.IsAsciiLetter(value[0])
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')))
        {
            throw new ArgumentException(
                "A workspace isolation provider ID must be a short semantic identifier.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

[Flags]
public enum WorkspaceIsolationCapability : uint
{
    None = 0,
    PersistentRootFileSystem = 1 << 0,
    DedicatedKernel = 1 << 1,
    DedicatedNetworkNamespace = 1 << 2,
    HostBindMounts = 1 << 3,
    StructuredProcessExecution = 1 << 4,
}

public sealed record WorkspaceIsolationProviderDescriptor
{
    public WorkspaceIsolationProviderDescriptor(
        WorkspaceIsolationProviderId id,
        string displayName,
        WorkspaceIsolationCapability capabilities)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException(
                "A workspace isolation provider descriptor requires an ID.",
                nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        const WorkspaceIsolationCapability allCapabilities =
            WorkspaceIsolationCapability.PersistentRootFileSystem
            | WorkspaceIsolationCapability.DedicatedKernel
            | WorkspaceIsolationCapability.DedicatedNetworkNamespace
            | WorkspaceIsolationCapability.HostBindMounts
            | WorkspaceIsolationCapability.StructuredProcessExecution;
        if ((capabilities & ~allCapabilities) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capabilities), capabilities, null);
        }

        Id = id;
        DisplayName = displayName.Trim();
        Capabilities = capabilities;
    }

    public WorkspaceIsolationProviderId Id { get; }

    public string DisplayName { get; }

    public WorkspaceIsolationCapability Capabilities { get; }
}

public sealed record WorkspaceIsolationMount
{
    private static readonly string[] ProviderOwnedGuestPaths =
    [
        "/bin",
        "/boot",
        "/dev",
        "/etc",
        "/lib",
        "/lib64",
        "/proc",
        "/root",
        "/run",
        "/sbin",
        "/sys",
        "/usr",
        "/var",
    ];

    public WorkspaceIsolationMount(
        string hostSource,
        string guestDestination,
        bool isReadOnly)
    {
        HostSource = NormalizeHostSource(hostSource);
        GuestDestination = NormalizeGuestDestination(guestDestination);
        IsReadOnly = isReadOnly;
    }

    public string HostSource { get; }

    public string GuestDestination { get; }

    public bool IsReadOnly { get; }

    private static string NormalizeHostSource(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A host mount source cannot contain NUL characters.", nameof(value));
        }

        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("A host mount source must be an absolute path.", nameof(value));
        }

        var fullPath = Path.GetFullPath(value);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static string NormalizeGuestDestination(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A guest mount destination cannot contain NUL characters.", nameof(value));
        }

        if (!value.StartsWith('/'))
        {
            throw new ArgumentException(
                "A guest mount destination must be an absolute path below the guest root.",
                nameof(value));
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "A guest mount destination cannot contain '.' or '..' path segments.",
                nameof(value));
        }

        var normalized = $"/{string.Join('/', segments)}";
        if (ProviderOwnedGuestPaths.Any(path =>
                string.Equals(normalized, path, StringComparison.Ordinal)
                || normalized.StartsWith($"{path}/", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A guest mount destination cannot replace a provider-owned system path.",
                nameof(value));
        }

        return normalized;
    }
}

public sealed record WorkspaceIsolationPrepareRequest
{
    public WorkspaceIsolationPrepareRequest(
        WorkspaceId workspaceId,
        IReadOnlyList<WorkspaceIsolationMount>? mounts = null,
        string? imageReference = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException("A workspace identifier is required.", nameof(workspaceId));
        }

        WorkspaceId = workspaceId;
        Mounts = SnapshotMounts(mounts);
        ImageReference = NormalizeImageReference(imageReference);
    }

    public WorkspaceId WorkspaceId { get; }

    /// <summary>
    /// Host directories available inside the isolate. An empty list creates a guest-only
    /// workspace whose persistent root file system remains usable.
    /// </summary>
    public IReadOnlyList<WorkspaceIsolationMount> Mounts { get; }

    public string? ImageReference { get; }

    private static string? NormalizeImageReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > WorkspaceDefinition.MaximumIsolationImageReferenceLength
            || normalized.Any(char.IsWhiteSpace)
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An isolation image must be a valid OCI image reference without whitespace or control characters.",
                nameof(value));
        }

        return normalized;
    }

    private static IReadOnlyList<WorkspaceIsolationMount> SnapshotMounts(
        IReadOnlyList<WorkspaceIsolationMount>? mounts)
    {
        if (mounts is null || mounts.Count == 0)
        {
            return Array.AsReadOnly(Array.Empty<WorkspaceIsolationMount>());
        }

        if (mounts.Count > WorkspaceDefinition.MaximumIsolationMountCount)
        {
            throw new ArgumentException(
                $"A workspace cannot define more than {WorkspaceDefinition.MaximumIsolationMountCount} isolation mounts.",
                nameof(mounts));
        }

        var snapshot = new WorkspaceIsolationMount[mounts.Count];
        var guestDestinations = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < mounts.Count; index++)
        {
            var mount = mounts[index]
                ?? throw new ArgumentException("Workspace mounts cannot contain null values.", nameof(mounts));
            snapshot[index] = new WorkspaceIsolationMount(
                mount.HostSource,
                mount.GuestDestination,
                mount.IsReadOnly);
            if (!guestDestinations.Add(snapshot[index].GuestDestination))
            {
                throw new ArgumentException(
                    "Workspace mounts cannot use the same guest destination more than once.",
                    nameof(mounts));
            }
        }

        return Array.AsReadOnly(snapshot);
    }
}

public sealed record WorkspaceIsolationProgress
{
    public WorkspaceIsolationProgress(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (status.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A workspace isolation progress status cannot contain NUL characters.",
                nameof(status));
        }

        Status = status;
    }

    public string Status { get; }
}

public sealed record WorkspaceIsolationBinding
{
    public WorkspaceIsolationBinding(
        WorkspaceId workspaceId,
        WorkspaceIsolationProviderId provider,
        WorkspaceIsolationCapability capabilities,
        string resourceName,
        IReadOnlyList<WorkspaceIsolationMount> mounts,
        Guid leaseId,
        string? imageReference = null,
        string? runtimeImageReference = null,
        WorkspaceIsolationNetworkBinding? network = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException("A workspace identifier is required.", nameof(workspaceId));
        }

        if (string.IsNullOrWhiteSpace(provider.Value))
        {
            throw new ArgumentException(
                "A workspace isolation provider ID is required.",
                nameof(provider));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(mounts);
        if (leaseId == Guid.Empty)
        {
            throw new ArgumentException("A workspace isolation lease identifier is required.", nameof(leaseId));
        }

        WorkspaceId = workspaceId;
        Provider = provider;
        Capabilities = capabilities;
        ResourceName = resourceName;
        var request = new WorkspaceIsolationPrepareRequest(
            workspaceId,
            mounts,
            imageReference);
        Mounts = request.Mounts;
        ImageReference = request.ImageReference;
        RuntimeImageReference = string.IsNullOrWhiteSpace(runtimeImageReference)
            ? request.ImageReference
            : runtimeImageReference.Trim();
        Network = network;
        LeaseId = leaseId;
    }

    public WorkspaceId WorkspaceId { get; }

    public WorkspaceIsolationProviderId Provider { get; }

    public WorkspaceIsolationCapability Capabilities { get; }

    public string ResourceName { get; }

    public IReadOnlyList<WorkspaceIsolationMount> Mounts { get; }

    public string? ImageReference { get; }

    /// <summary>The concrete OCI image used by the running environment.</summary>
    public string? RuntimeImageReference { get; }

    /// <summary>The host-only network and host gateway assigned to this environment.</summary>
    public WorkspaceIsolationNetworkBinding? Network { get; }

    /// <summary>
    /// Identifies one acquire of a shared persistent isolate. Releasing the same lease more
    /// than once is idempotent and cannot consume another window's lease.
    /// </summary>
    public Guid LeaseId { get; }
}

[Flags]
public enum WorkspaceProcessMode
{
    None = 0,
    Interactive = 1 << 0,
    AllocateTerminal = 1 << 1,
}

/// <summary>
/// A structured request to run an existing connection launch inside a workspace isolate.
/// The executable is a host-side input; a provider must map it to a guest executable.
/// </summary>
public sealed record WorkspaceIsolationProcessRequest
{
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    public WorkspaceIsolationProcessRequest(
        ConnectionKind connectionKind,
        string hostExecutable,
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? hostWorkingDirectory = null,
        WorkspaceProcessMode mode = WorkspaceProcessMode.None,
        bool usesHostCredentialBroker = false)
    {
        if (!Enum.IsDefined(connectionKind))
        {
            throw new ArgumentOutOfRangeException(nameof(connectionKind), connectionKind, null);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(hostExecutable);
        ValidateText(hostExecutable, nameof(hostExecutable));
        ValidateText(hostWorkingDirectory, nameof(hostWorkingDirectory));
        if ((mode & ~(WorkspaceProcessMode.Interactive | WorkspaceProcessMode.AllocateTerminal)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        ConnectionKind = connectionKind;
        HostExecutable = hostExecutable;
        Arguments = SnapshotArguments(arguments);
        Environment = SnapshotEnvironment(environment);
        HostWorkingDirectory = hostWorkingDirectory;
        Mode = mode;
        UsesHostCredentialBroker = usesHostCredentialBroker;
    }

    public ConnectionKind ConnectionKind { get; }

    public string HostExecutable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }

    public string? HostWorkingDirectory { get; }

    public WorkspaceProcessMode Mode { get; }

    public bool UsesHostCredentialBroker { get; }

    private static IReadOnlyList<string> SnapshotArguments(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return Array.AsReadOnly(Array.Empty<string>());
        }

        var snapshot = new string[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            snapshot[index] = arguments[index]
                ?? throw new ArgumentException("Process arguments cannot contain null values.", nameof(arguments));
            ValidateText(snapshot[index], nameof(arguments));
        }

        return Array.AsReadOnly(snapshot);
    }

    private static IReadOnlyDictionary<string, string> SnapshotEnvironment(
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
        {
            return EmptyEnvironment;
        }

        var snapshot = new Dictionary<string, string>(environment.Count, StringComparer.Ordinal);
        foreach (var (name, value) in environment)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Environment variable names cannot be empty or contain '='.",
                    nameof(environment));
            }

            ValidateText(name, nameof(environment));
            if (value is null)
            {
                throw new ArgumentException(
                    "Environment variable values cannot be null.",
                    nameof(environment));
            }

            ValidateText(value, nameof(environment));
            snapshot.Add(name, value);
        }

        return new ReadOnlyDictionary<string, string>(snapshot);
    }

    private static void ValidateText(string? value, string parameterName)
    {
        if (value?.Contains('\0', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("Process launch values cannot contain NUL characters.", parameterName);
        }
    }
}

/// <summary>
/// A host process launch. Executable and arguments are passed directly to the process API,
/// never composed into a shell command.
/// </summary>
public sealed record WorkspaceProcessLaunch
{
    public WorkspaceProcessLaunch(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? hostWorkingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ValidateText(executable, nameof(executable));
        ValidateText(hostWorkingDirectory, nameof(hostWorkingDirectory));
        var argumentSnapshot = new string[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            argumentSnapshot[index] = arguments[index]
                ?? throw new ArgumentException("Process arguments cannot contain null values.", nameof(arguments));
            ValidateText(argumentSnapshot[index], nameof(arguments));
        }

        var environmentSnapshot = new Dictionary<string, string>(environment.Count, StringComparer.Ordinal);
        foreach (var (name, value) in environment)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Environment variable names cannot be empty or contain '='.",
                    nameof(environment));
            }

            ValidateText(name, nameof(environment));
            if (value is null)
            {
                throw new ArgumentException(
                    "Environment variable values cannot be null.",
                    nameof(environment));
            }

            ValidateText(value, nameof(environment));
            environmentSnapshot.Add(name, value);
        }

        Executable = executable;
        Arguments = Array.AsReadOnly(argumentSnapshot);
        Environment = new ReadOnlyDictionary<string, string>(environmentSnapshot);
        HostWorkingDirectory = hostWorkingDirectory;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }

    public string? HostWorkingDirectory { get; }

    private static void ValidateText(string? value, string parameterName)
    {
        if (value?.Contains('\0', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("Process launch values cannot contain NUL characters.", parameterName);
        }
    }
}

public enum WorkspaceIsolationErrorCode
{
    None = 0,
    RuntimeMissing = 1,
    RuntimeVersionTooOld = 2,
    RuntimeUnavailable = 3,
    PrepareFailed = 4,
    StopFailed = 5,
    WorkingDirectoryNotMounted = 6,
    UnsupportedConnectionKind = 7,
    HostCredentialBrokerUnavailable = 8,
    Cancelled = 9,
    Timeout = 10,
    ExecutableMappingUnavailable = 11,
    PersistentEnvironmentResetRequired = 12,
    SshHostKeyTrustUnavailable = 13,
    ImageNotBootable = 14,
}

public enum WorkspaceIsolationRecoveryAction
{
    None = 0,
    InstallRuntime = 1,
    UpdateRuntime = 2,
    StartRuntime = 3,
    ChooseMountedDirectory = 4,
    DisableIsolation = 5,
    Retry = 6,
    ResetPersistentEnvironment = 7,
}

public sealed record WorkspaceIsolationError(
    WorkspaceIsolationErrorCode Code,
    string StableCode,
    string Message,
    bool Retryable,
    WorkspaceIsolationRecoveryAction RecoveryAction)
{
    public static WorkspaceIsolationError Create(WorkspaceIsolationErrorCode code) => code switch
    {
        WorkspaceIsolationErrorCode.RuntimeMissing =>
            New(code, "workspace_isolation_runtime_missing", "The workspace isolation runtime is not installed.", false,
                WorkspaceIsolationRecoveryAction.InstallRuntime),
        WorkspaceIsolationErrorCode.RuntimeVersionTooOld =>
            New(code, "workspace_isolation_runtime_too_old", "The workspace isolation runtime must be updated before isolation can run.", false,
                WorkspaceIsolationRecoveryAction.UpdateRuntime),
        WorkspaceIsolationErrorCode.RuntimeUnavailable =>
            New(code, "workspace_isolation_runtime_unavailable", "The workspace isolation runtime is not running.", true,
                WorkspaceIsolationRecoveryAction.StartRuntime),
        WorkspaceIsolationErrorCode.PrepareFailed =>
            New(code, "workspace_isolation_prepare_failed", "The persistent workspace isolate could not be prepared.", true,
                WorkspaceIsolationRecoveryAction.Retry),
        WorkspaceIsolationErrorCode.StopFailed =>
            New(code, "workspace_isolation_stop_failed", "The workspace isolate could not be stopped.", true,
                WorkspaceIsolationRecoveryAction.Retry),
        WorkspaceIsolationErrorCode.WorkingDirectoryNotMounted =>
            New(code, "workspace_isolation_directory_not_mounted", "The working directory is not available through a configured workspace mount.", false,
                WorkspaceIsolationRecoveryAction.ChooseMountedDirectory),
        WorkspaceIsolationErrorCode.UnsupportedConnectionKind =>
            New(code, "workspace_isolation_connection_unsupported", "This connection type cannot run inside the selected workspace isolate.", false,
                WorkspaceIsolationRecoveryAction.DisableIsolation),
        WorkspaceIsolationErrorCode.HostCredentialBrokerUnavailable =>
            New(code, "workspace_isolation_credential_broker_unavailable", "This connection requires a host credential helper that is not available inside the isolate.", false,
                WorkspaceIsolationRecoveryAction.DisableIsolation),
        WorkspaceIsolationErrorCode.Cancelled =>
            New(code, "workspace_isolation_cancelled", "Workspace isolation was cancelled.", false,
                WorkspaceIsolationRecoveryAction.None),
        WorkspaceIsolationErrorCode.Timeout =>
            New(code, "workspace_isolation_timeout", "The workspace isolation runtime timed out.", true,
                WorkspaceIsolationRecoveryAction.Retry),
        WorkspaceIsolationErrorCode.ExecutableMappingUnavailable =>
            New(code, "workspace_isolation_executable_unmapped", "The host executable has no equivalent inside the workspace isolate.", false,
                WorkspaceIsolationRecoveryAction.DisableIsolation),
        WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired =>
            New(code, "workspace_isolation_reset_required", "The existing workspace environment uses an older or different isolation configuration. Use Recreate environment in workspace settings; recreating removes installed packages and guest-only files, but not host-mounted files.", false,
                WorkspaceIsolationRecoveryAction.ResetPersistentEnvironment),
        WorkspaceIsolationErrorCode.SshHostKeyTrustUnavailable =>
            New(code, "workspace_isolation_ssh_trust_unavailable", "Verified SSH is unavailable until host-key inspection and trust storage run inside the workspace isolate.", false,
                WorkspaceIsolationRecoveryAction.DisableIsolation),
        WorkspaceIsolationErrorCode.ImageNotBootable =>
            New(code, "workspace_isolation_image_not_bootable", "The selected OCI image cannot boot as a workspace environment. It must provide /sbin/init and support a normal Linux boot.", false,
                WorkspaceIsolationRecoveryAction.None),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    private static WorkspaceIsolationError New(
        WorkspaceIsolationErrorCode code,
        string stableCode,
        string message,
        bool retryable,
        WorkspaceIsolationRecoveryAction recoveryAction) =>
        new(code, stableCode, message, retryable, recoveryAction);
}

public abstract record WorkspaceIsolationResult<T>
{
    private WorkspaceIsolationResult()
    {
    }

    public sealed record Success(T Value) : WorkspaceIsolationResult<T>;

    /// <summary>
    /// Represents a failed operation. A provider can return an owned cleanup value when it
    /// mutated runtime state before failing; the caller must release that value through the
    /// same provider and retain it for retry if cleanup fails.
    /// </summary>
    public sealed record Failure(
        WorkspaceIsolationError Error,
        T? CleanupValue = default) : WorkspaceIsolationResult<T>;

    public static WorkspaceIsolationResult<T> Succeed(T value) => new Success(value);

    public static WorkspaceIsolationResult<T> Fail(WorkspaceIsolationErrorCode code) =>
        new Failure(WorkspaceIsolationError.Create(code));

    public static WorkspaceIsolationResult<T> Fail(
        WorkspaceIsolationErrorCode code,
        T cleanupValue)
    {
        ArgumentNullException.ThrowIfNull(cleanupValue);
        return new Failure(WorkspaceIsolationError.Create(code), cleanupValue);
    }
}

public interface IWorkspaceIsolationProvider
{
    WorkspaceIsolationProviderDescriptor Descriptor { get; }

    WorkspaceIsolationCapability Capabilities => Descriptor.Capabilities;

    string DefaultImageReference => WorkspaceIsolationImages.Default;

    ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
        WorkspaceIsolationPrepareRequest request,
        CancellationToken cancellationToken);

    ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
        WorkspaceIsolationPrepareRequest request,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken) =>
        PrepareAsync(request, cancellationToken);

    WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
        WorkspaceIsolationBinding binding,
        WorkspaceIsolationProcessRequest request);

    ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken);

    ValueTask<WorkspaceIsolationResult<Unit>> RecreateAsync(
        WorkspaceIsolationPrepareRequest request,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(WorkspaceIsolationResult<Unit>.Fail(
            WorkspaceIsolationErrorCode.PrepareFailed));
}

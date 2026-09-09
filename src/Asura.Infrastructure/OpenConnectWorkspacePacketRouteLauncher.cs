using System.Net;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

internal sealed record WorkspaceVpnPacketRouteRequest(
    WorkspaceIsolationBinding Isolation,
    NetworkConnectionProfile Connection,
    SecretMaterial? TransientPassword,
    ReadOnlyMemory<byte> AuthenticationKey);

internal interface IWorkspaceVpnPacketRouteLauncher
{
    ValueTask<NetworkConnectionResult<WorkspaceGatewayProcessStart>> StartAsync(
        WorkspaceVpnPacketRouteRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs OpenConnect without a host TUN. Its script child inherits OpenConnect's packet-oriented
/// VPNFD and bridges those raw IP datagrams directly to the authenticated guest packet channel.
/// </summary>
internal sealed partial class WorkspaceVpnPacketRouteLauncher :
    IWorkspaceVpnPacketRouteLauncher
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);
    private readonly ISecretVault _secretVault;
    private readonly IWorkspaceGatewayProcessRunner _processes;
    private readonly string? _openConnectExecutable;
    private readonly string? _gatewayExecutable;
    private readonly string? _openVpnExecutable;

    public WorkspaceVpnPacketRouteLauncher(
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator,
        IWorkspaceGatewayProcessRunner processes,
        string? gatewayExecutable)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        ArgumentNullException.ThrowIfNull(executableLocator);
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _openConnectExecutable = executableLocator.Find("openconnect");
        _openVpnExecutable = executableLocator.Find("asura-openvpn-engine");
        _gatewayExecutable = gatewayExecutable;
    }

    public async ValueTask<NetworkConnectionResult<WorkspaceGatewayProcessStart>> StartAsync(
        WorkspaceVpnPacketRouteRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Connection.Configuration is NetworkConnectionConfiguration.WireGuard
            or NetworkConnectionConfiguration.OpenVpn)
        {
            return await StartFileConfiguredAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        if (request.Connection.Configuration is not NetworkConnectionConfiguration.AnyConnect
            configuration)
        {
            return Fail(
                NetworkConnectionErrorCode.InvalidConfiguration,
                "workspace_anyconnect_configuration_invalid",
                "The selected connection does not contain a Cisco AnyConnect configuration.",
                retryable: false);
        }

        if (OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(_openConnectExecutable)
            || string.IsNullOrWhiteSpace(_gatewayExecutable))
        {
            return Fail(
                NetworkConnectionErrorCode.RuntimeMissing,
                "workspace_anyconnect_raw_runtime_missing",
                "The isolated Cisco AnyConnect route requires OpenConnect and the bundled workspace network helper on the host.",
                retryable: false);
        }

        var packetSocketPath = request.Isolation.Network?.PacketSocketPath;
        if (string.IsNullOrWhiteSpace(packetSocketPath)
            || !Path.IsPathFullyQualified(packetSocketPath))
        {
            return Fail(
                NetworkConnectionErrorCode.InvalidConfiguration,
                "workspace_anyconnect_packet_socket_missing",
                "The workspace environment does not expose its packet gateway socket.",
                retryable: false);
        }

        byte[]? password = null;
        byte[]? certificate = null;
        byte[]? standardInput = null;
        var directory = SecureRouteDirectory.Create();
        try
        {
            var passwordResult = await ResolvePasswordAsync(
                    request.Connection.Id,
                    configuration.PasswordSecret,
                    request.TransientPassword,
                    "Cisco AnyConnect password",
                    cancellationToken)
                .ConfigureAwait(false);
            if (passwordResult is NetworkConnectionResult<byte[]>.Failure passwordFailure)
            {
                return NetworkConnectionResult<WorkspaceGatewayProcessStart>.Fail(
                    passwordFailure.Error);
            }

            password = ((NetworkConnectionResult<byte[]>.Success)passwordResult).Value;
            if (password.Length > 0)
            {
                standardInput = new byte[password.Length + 1];
                password.CopyTo(standardInput, 0);
                standardInput[^1] = (byte)'\n';
            }

            string? certificatePath = null;
            if (configuration.ClientCertificateSecret is { } certificateReference)
            {
                var resolved = await ResolveSecretAsync(
                        request.Connection.Id,
                        certificateReference,
                        "Cisco AnyConnect client certificate",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (resolved is NetworkConnectionResult<byte[]>.Failure certificateFailure)
                {
                    return NetworkConnectionResult<WorkspaceGatewayProcessStart>.Fail(
                        certificateFailure.Error);
                }

                certificate = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
                certificatePath = await directory.WriteAsync(
                        "client-certificate",
                        certificate,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var keyPath = await directory.WriteAsync(
                    "packet-channel-key",
                    request.AuthenticationKey,
                    cancellationToken)
                .ConfigureAwait(false);
            var scriptCommand = string.Join(
                ' ',
                ShellQuote(_gatewayExecutable),
                "openconnect-vpnfd",
                "--socket",
                ShellQuote(packetSocketPath),
                "--key-file",
                ShellQuote(keyPath));
            var arguments = new List<string>
            {
                "--protocol=anyconnect",
                "--script-tun",
                "--script",
                scriptCommand,
                "--non-inter",
                "--force-dpd=30",
                "--reconnect-timeout=300",
            };
            if (OperatingSystem.IsMacOS())
            {
                // The bundled OpenSSL has no install-time trust directory. Use the
                // CA bundle maintained by macOS instead of disabling verification.
                arguments.Add("--cafile=/etc/ssl/cert.pem");
            }

            if (configuration.Username is not null)
            {
                arguments.Add("--user");
                arguments.Add(configuration.Username);
            }

            if (configuration.AuthenticationGroup is not null)
            {
                arguments.Add("--authgroup");
                arguments.Add(configuration.AuthenticationGroup);
            }

            if (certificatePath is not null)
            {
                arguments.Add("--certificate");
                arguments.Add(certificatePath);
            }

            if (standardInput is not null)
            {
                arguments.Add("--passwd-on-stdin");
            }

            arguments.Add(configuration.Gateway.AbsoluteUri);
            progress?.Report(new NetworkConnectionProgress(
                "Connecting Cisco AnyConnect to the workspace packet route…"));
            WorkspaceGatewayProcessStart started;
            try
            {
                started = await _processes.StartAsync(
                        new WorkspaceGatewayProcessRequest(
                            _openConnectExecutable,
                            arguments,
                            standardInput ?? ReadOnlyMemory<byte>.Empty),
                        ReadinessTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Fail(
                    NetworkConnectionErrorCode.Cancelled,
                    "workspace_anyconnect_start_cancelled",
                    "Connecting Cisco AnyConnect was cancelled.",
                    retryable: false);
            }
            catch (IOException exception)
            {
                SecretSafeDiagnosticProjection.WriteTrace(
                    "workspace.anyconnect.raw-route.failed",
                    exception);
                if (VpnAuthenticationRejection.IsReported(exception.Message))
                {
                    return Fail(NetworkConnectionErrorCode.AuthenticationRejected,
                        "workspace_anyconnect_authentication_rejected",
                        "Cisco AnyConnect rejected the supplied authentication.", retryable: false);
                }

                return Fail(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    "workspace_anyconnect_raw_route_failed",
                    "Cisco AnyConnect could not establish the workspace packet route.",
                    retryable: true);
            }
            finally
            {
                directory.DeleteFile("packet-channel-key");
            }

            var owned = directory.TransferOwnership(started.Process);
            return NetworkConnectionResult<WorkspaceGatewayProcessStart>.Succeed(
                started with { Process = owned });
        }
        finally
        {
            Clear(password);
            Clear(certificate);
            Clear(standardInput);
            await directory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<NetworkConnectionResult<byte[]>> ResolvePasswordAsync(
        NetworkConnectionId connectionId,
        SecretRef? passwordReference,
        SecretMaterial? transientPassword,
        string label,
        CancellationToken cancellationToken)
    {
        if (passwordReference is { } reference)
        {
            return await ResolveSecretAsync(
                    connectionId,
                    reference,
                    label,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (transientPassword is null)
        {
            return NetworkConnectionResult<byte[]>.Succeed([]);
        }

        var password = GC.AllocateUninitializedArray<byte>(transientPassword.Length);
        transientPassword.CopyTo(password);
        return NetworkConnectionResult<byte[]>.Succeed(password);
    }

    private async ValueTask<NetworkConnectionResult<byte[]>> ResolveSecretAsync(
        NetworkConnectionId connectionId,
        SecretRef reference,
        string label,
        CancellationToken cancellationToken)
    {
        SecretVaultResult<SecretMaterial> result;
        try
        {
            result = await _secretVault.ResolveAsync(
                    new ResolveSecretRequest(
                        reference,
                        new SecretScope(
                            SecretScopeKind.NetworkConnection,
                            connectionId.Value),
                        new SecretUsePurpose(
                            SecretUseKind.NetworkConnectionAuthentication,
                            connectionId.Value)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail<byte[]>(
                NetworkConnectionErrorCode.Cancelled,
                "workspace_anyconnect_secret_cancelled",
                $"Access to the {label} was cancelled.",
                retryable: false);
        }

        if (result is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            return Fail<byte[]>(
                NetworkConnectionErrorCode.AuthenticationRequired,
                "workspace_anyconnect_secret_access_required",
                $"Authentication is required to access the {label}.",
                failure.Error.Retryable);
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = GC.AllocateUninitializedArray<byte>(material.Length);
        material.CopyTo(bytes);
        return NetworkConnectionResult<byte[]>.Succeed(bytes);
    }

    private static NetworkConnectionResult<WorkspaceGatewayProcessStart> Fail(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) => Fail<WorkspaceGatewayProcessStart>(
        code,
        stableCode,
        message,
        retryable);

    private static NetworkConnectionResult<T> Fail<T>(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) => NetworkConnectionResult<T>.Fail(
        new NetworkConnectionError(code, stableCode, message, retryable));

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static void Clear(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private sealed class SecureRouteDirectory : IAsyncDisposable
    {
        private const string Prefix = "asura-anyconnect-route-";
        private bool _owned = true;

        private SecureRouteDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static SecureRouteDirectory Create()
        {
            var directory = Directory.CreateTempSubdirectory(Prefix);
            if (!OperatingSystem.IsWindows())
            {
                directory.UnixFileMode = UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute;
            }

            return new SecureRouteDirectory(directory.FullName);
        }

        public async ValueTask<string> WriteAsync(
            string fileName,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using var stream = new FileStream(path, options);
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return path;
        }

        public void DeleteFile(string fileName)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SecretSafeDiagnosticProjection.WriteTrace(
                    "workspace.anyconnect.key.delete.failed",
                    exception);
            }
        }

        public IWorkspaceGatewayProcess TransferOwnership(IWorkspaceGatewayProcess process)
        {
            _owned = false;
            return new DirectoryOwningProcess(process, Path);
        }

        public ValueTask DisposeAsync()
        {
            if (_owned)
            {
                DeleteDirectory(Path);
            }

            return ValueTask.CompletedTask;
        }

        private static void DeleteDirectory(string path)
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            var temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!fullPath.StartsWith(temporaryRoot, StringComparison.Ordinal)
                || !System.IO.Path.GetFileName(fullPath).StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a directory not created for an AnyConnect packet route.");
            }

            try
            {
                Directory.Delete(fullPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SecretSafeDiagnosticProjection.WriteTrace(
                    "workspace.anyconnect.directory.delete.failed",
                    exception);
            }
        }

        private sealed class DirectoryOwningProcess(
            IWorkspaceGatewayProcess inner,
            string directory) : IWorkspaceGatewayProcess
        {
            private int _disposed;

            public bool HasExited => inner.HasExited;

            public int? ExitCode => inner.ExitCode;

            public string Diagnostic => inner.Diagnostic;

            public event EventHandler? Exited
            {
                add => inner.Exited += value;
                remove => inner.Exited -= value;
            }

            public void Stop() => inner.Stop();

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    DeleteDirectory(directory);
                }
            }
        }
    }
}

using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using SMBLibrary;
using SMBLibrary.Client;
using SmbFileAttributes = SMBLibrary.FileAttributes;

namespace GhostShell.Files;

/// <summary>
/// Confines SMBLibrary's synchronous SMB 2/3 client and resolved password material to the
/// infrastructure boundary. Every file-provider operation owns a fresh network session.
/// </summary>
internal sealed class SmbLibrarySessionFactory(
    ISecretVault secretVault,
    SmbFileProviderOptions options,
    IWorkspaceNetworkConnector? networkConnector = null) :
    IRemoteHierarchicalFileSessionFactory
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async ValueTask<IRemoteHierarchicalFileSession> OpenAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (networkConnector?.Egress == WorkspaceNetworkEgress.Blocked)
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.Unsupported,
                "The workspace network kill switch is blocking SMB traffic.");
        }

        if (networkConnector is not null && !RoutedSmb2Client.IsCompatible)
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.Unsupported,
                "The installed SMBLibrary version cannot use the active workspace network route.");
        }

        ResolvedSmbCredential credential;
        try
        {
            credential = await ResolveCredentialAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RemoteFileSessionException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.AuthenticationFailed,
                "The SMB credential could not be resolved.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var timeout = checked((int)options.ResponseTimeout.TotalMilliseconds);
        SmbLibraryRoutedTransport? routedTransport = null;
        SMB2Client client = networkConnector is null
            ? new SMB2Client(timeout, enableSMB311Support: true)
            : new RoutedSmb2Client(timeout);
        try
        {
            if (networkConnector is not null)
            {
                routedTransport = await SmbLibraryRoutedTransport.OpenAsync(
                        networkConnector,
                        options.Server,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var opening = new OpeningSmbSession(
                this,
                client,
                credential,
                routedTransport);
            var openTask = Task.Run(opening.Open, CancellationToken.None);
            using var cancellation = cancellationToken.UnsafeRegister(
                static state => ((OpeningSmbSession)state!).Abort(),
                opening);
            var session = await openTask.ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                return session;
            }

            await session.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbortClient(client, routedTransport);
            throw;
        }
        catch (RemoteFileSessionException)
        {
            AbortClient(client, routedTransport);
            throw;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            AbortClient(client, routedTransport);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            AbortClient(client, routedTransport);
            throw MapException(exception);
        }
    }

    private SmbLibrarySession OpenSession(
        SMB2Client client,
        ResolvedSmbCredential credential,
        SmbLibraryRoutedTransport? routedTransport)
    {
        var connected = routedTransport is null
            ? client.Connect(options.Server, SMBTransportType.DirectTCPTransport)
            : ((RoutedSmb2Client)client).Connect(options.Server, routedTransport.LocalPort);
        if (!connected)
        {
            routedTransport?.ThrowIfFailed();
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.Transient,
                "The SMB server could not be reached.",
                retryable: true);
        }

        var loginStatus = client.Login(
            credential.Domain,
            credential.Username,
            credential.Password);
        if (loginStatus != NTStatus.STATUS_SUCCESS)
        {
            throw MapAuthenticationStatus(loginStatus);
        }

        var fileStore = client.TreeConnect(options.Share, out var treeStatus);
        if (fileStore is null || treeStatus != NTStatus.STATUS_SUCCESS)
        {
            throw SmbLibrarySession.MapStatus(treeStatus, "connect to the configured SMB share");
        }

        if (routedTransport is not null && IsDfsFileStore(fileStore))
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.Unsupported,
                "DFS referrals are unavailable through the active workspace network route.");
        }

        return new SmbLibrarySession(client, fileStore, routedTransport);
    }

    private static bool IsDfsFileStore(ISMBFileStore fileStore) => IsDfsFileStoreTypeName(
        fileStore.GetType().FullName);

    internal static bool IsDfsFileStoreTypeName(string? typeName) => string.Equals(
        typeName,
        "SMBLibrary.Client.DFS.SMB2DfsFileStore",
        StringComparison.Ordinal);

    private async ValueTask<ResolvedSmbCredential> ResolveCredentialAsync(
        CancellationToken cancellationToken)
    {
        if (options.Authentication is SmbAuthentication.Guest)
        {
            return new ResolvedSmbCredential(string.Empty, string.Empty, string.Empty);
        }

        var password = (SmbAuthentication.Password)options.Authentication;
        var result = await secretVault.ResolveAsync(
            CreateCredentialRequest(options, password.PasswordSecret),
            cancellationToken).ConfigureAwait(false);
        if (result is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            throw failure.Error.Code == SecretVaultErrorCode.Cancelled
                ? new OperationCanceledException(cancellationToken)
                : new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.AuthenticationFailed,
                    "The SMB credential could not be resolved.");
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = new byte[material.Length];
        try
        {
            material.CopyTo(bytes);
            string decoded;
            try
            {
                decoded = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.InvalidConfiguration,
                    "The SMB password must be stored as valid UTF-8 text.");
            }

            return new ResolvedSmbCredential(password.Domain, password.Username, decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static ResolveSecretRequest CreateCredentialRequest(
        SmbFileProviderOptions providerOptions,
        GhostShell.Core.SecretRef reference) => new(
            reference,
            new SecretScope(SecretScopeKind.FileProvider, providerOptions.ProfileId.Value),
            new SecretUsePurpose(
                SecretUseKind.FileProviderAuthentication,
                providerOptions.ProfileId.Value));

    private static RemoteFileSessionException MapAuthenticationStatus(NTStatus status) => status switch
    {
        NTStatus.STATUS_WRONG_PASSWORD
            or NTStatus.STATUS_LOGON_FAILURE
            or NTStatus.STATUS_ACCOUNT_RESTRICTION
            or NTStatus.STATUS_INVALID_LOGON_HOURS
            or NTStatus.STATUS_INVALID_WORKSTATION
            or NTStatus.STATUS_PASSWORD_EXPIRED
            or NTStatus.STATUS_ACCOUNT_DISABLED
            or NTStatus.STATUS_LOGON_TYPE_NOT_GRANTED
            or NTStatus.STATUS_ACCOUNT_EXPIRED
            or NTStatus.STATUS_PASSWORD_MUST_CHANGE
            or NTStatus.STATUS_ACCOUNT_LOCKED_OUT => new RemoteFileSessionException(
                RemoteFileSessionErrorCode.AuthenticationFailed,
                "The SMB server rejected authentication."),
        _ => SmbLibrarySession.MapStatus(status, "authenticate to the SMB server"),
    };

    private static RemoteFileSessionException MapException(Exception exception) => exception switch
    {
        SocketException => new RemoteFileSessionException(
            RemoteFileSessionErrorCode.Transient,
            "The SMB network connection failed.",
            retryable: true),
        WorkspaceNetworkBlockedException => new RemoteFileSessionException(
            RemoteFileSessionErrorCode.Transient,
            "The workspace network kill switch is blocking SMB traffic.",
            retryable: true),
        IOException => new RemoteFileSessionException(
            RemoteFileSessionErrorCode.Transient,
            "The SMB transport failed.",
            retryable: true),
        _ => new RemoteFileSessionException(
            RemoteFileSessionErrorCode.IoFailure,
            "The SMB adapter failed to open a session."),
    };

    private static void AbortClient(
        SMB2Client client,
        SmbLibraryRoutedTransport? routedTransport = null)
    {
        try
        {
            client.Disconnect();
        }
        catch (Exception)
        {
            // Cancellation and failure cleanup must not replace the sanitized primary error.
        }

        routedTransport?.Dispose();
    }

    private sealed class OpeningSmbSession(
        SmbLibrarySessionFactory factory,
        SMB2Client client,
        ResolvedSmbCredential credential,
        SmbLibraryRoutedTransport? routedTransport)
    {
        public SmbLibrarySession Open() => factory.OpenSession(
            client,
            credential,
            routedTransport);

        public void Abort() => AbortClient(client, routedTransport);
    }

    private sealed record ResolvedSmbCredential(string Domain, string Username, string Password)
    {
        public override string ToString() => "[resolved SMB credential]";
    }
}

internal sealed class SmbLibrarySession(
    SMB2Client client,
    ISMBFileStore fileStore,
    IDisposable? routedTransport = null) : IRemoteHierarchicalFileSession
{
    private const CreateOptions SafeOpenOptions =
        CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT
        | CreateOptions.FILE_OPEN_REPARSE_POINT;
    private const ShareAccess CooperativeShareAccess =
        ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete;
    private int _aborted;
    private int _disposed;

    public ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(
        string path,
        CancellationToken cancellationToken) => ExecuteAsync<IReadOnlyList<RemoteFileEntry>>(
        () =>
        {
            var handle = OpenHandle(
                path,
                (AccessMask)DirectoryAccessMask.FILE_LIST_DIRECTORY
                | (AccessMask)DirectoryAccessMask.FILE_READ_ATTRIBUTES
                | AccessMask.SYNCHRONIZE,
                SmbFileAttributes.Directory,
                CreateDisposition.FILE_OPEN,
                SafeOpenOptions | CreateOptions.FILE_DIRECTORY_FILE);
            try
            {
                var status = fileStore.QueryDirectory(
                    out var listed,
                    handle,
                    "*",
                    FileInformationClass.FileIdBothDirectoryInformation);
                if (status is not (NTStatus.STATUS_SUCCESS or NTStatus.STATUS_NO_MORE_FILES))
                {
                    throw MapStatus(status, "list an SMB directory");
                }

                return [.. listed
                    .OfType<FileIdBothDirectoryInformation>()
                    .Where(entry => entry.FileName is not ("." or ".."))
                    .Select(ToRemoteEntry)];
            }
            finally
            {
                CloseHandleQuietly(handle);
            }
        },
        cancellationToken);

    public ValueTask<RemoteFileEntry?> StatAsync(
        string path,
        CancellationToken cancellationToken) => ExecuteAsync<RemoteFileEntry?>(
        () =>
        {
            var smbPath = ToSmbPath(path);
            var status = fileStore.CreateFile(
                out var handle,
                out _,
                smbPath,
                (AccessMask)FileAccessMask.FILE_READ_ATTRIBUTES | AccessMask.SYNCHRONIZE,
                0,
                CooperativeShareAccess,
                CreateDisposition.FILE_OPEN,
                SafeOpenOptions,
                securityContext: null!);
            if (IsNotFound(status))
            {
                return null;
            }

            ThrowForStatus(status, "inspect an SMB entry");
            try
            {
                status = fileStore.GetFileInformation(
                    out var information,
                    handle,
                    FileInformationClass.FileNetworkOpenInformation);
                ThrowForStatus(status, "read SMB entry metadata");
                if (information is not FileNetworkOpenInformation metadata)
                {
                    throw new RemoteFileSessionException(
                        RemoteFileSessionErrorCode.IoFailure,
                        "The SMB server returned unsupported metadata.");
                }

                status = fileStore.GetFileInformation(
                    out information,
                    handle,
                    FileInformationClass.FileInternalInformation);
                ThrowForStatus(status, "read the SMB entry identity");
                if (information is not FileInternalInformation identity)
                {
                    throw new RemoteFileSessionException(
                        RemoteFileSessionErrorCode.IoFailure,
                        "The SMB server returned an unsupported entry identity.");
                }

                var name = string.Equals(path, "/", StringComparison.Ordinal) ? string.Empty : path[(path.LastIndexOf('/') + 1)..];
                return ToRemoteEntry(name, metadata, identity);
            }
            finally
            {
                CloseHandleQuietly(handle);
            }
        },
        cancellationToken);

    public async ValueTask<Stream> OpenReadAsync(
        string path,
        long offset,
        CancellationToken cancellationToken)
    {
        var handle = await ExecuteAsync(
            () => OpenHandle(
                path,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                SmbFileAttributes.Normal,
                CreateDisposition.FILE_OPEN,
                SafeOpenOptions
                | CreateOptions.FILE_NON_DIRECTORY_FILE
                | CreateOptions.FILE_SEQUENTIAL_ONLY),
            cancellationToken).ConfigureAwait(false);
        return SmbLibraryStream.CreateReader(this, handle, offset, fileStore.MaxReadSize);
    }

    public async ValueTask<Stream> OpenCreateNewAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var handle = await ExecuteAsync(
            () => OpenHandle(
                path,
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                SmbFileAttributes.Normal,
                CreateDisposition.FILE_CREATE,
                CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT
                | CreateOptions.FILE_NON_DIRECTORY_FILE
                | CreateOptions.FILE_SEQUENTIAL_ONLY),
            cancellationToken).ConfigureAwait(false);
        return SmbLibraryStream.CreateWriter(this, handle, fileStore.MaxWriteSize);
    }

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        ExecuteAsync(
            () =>
            {
                var handle = OpenHandle(
                    path,
                    (AccessMask)DirectoryAccessMask.FILE_ADD_SUBDIRECTORY | AccessMask.SYNCHRONIZE,
                    SmbFileAttributes.Directory,
                    CreateDisposition.FILE_CREATE,
                    CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT
                    | CreateOptions.FILE_DIRECTORY_FILE);
                CloseHandle(handle, "close a newly created SMB directory");
            },
            cancellationToken);

    public ValueTask RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) => ExecuteAsync(
        () =>
        {
            var handle = OpenHandle(
                sourcePath,
                AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                0,
                CreateDisposition.FILE_OPEN,
                SafeOpenOptions);
            var renamed = false;
            try
            {
                var rename = new FileRenameInformationType2
                {
                    ReplaceIfExists = false,
                    FileName = ToSmbPath(destinationPath),
                };
                var status = fileStore.SetFileInformation(handle, rename);
                ThrowForStatus(status, "rename an SMB entry");
                renamed = true;
            }
            finally
            {
                if (renamed)
                {
                    CloseHandle(handle, "close a renamed SMB entry");
                }
                else
                {
                    CloseHandleQuietly(handle);
                }
            }
        },
        cancellationToken);

    public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        DeleteAsync(path, CreateOptions.FILE_NON_DIRECTORY_FILE, cancellationToken);

    public ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken) =>
        DeleteAsync(path, CreateOptions.FILE_DIRECTORY_FILE, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Abort();
        return ValueTask.CompletedTask;
    }

    internal ValueTask<byte[]> ReadAsync(
        object handle,
        long offset,
        int count,
        CancellationToken cancellationToken) => ExecuteAsync(
        () =>
        {
            var status = fileStore.ReadFile(out var bytes, handle, offset, count);
            if (status == NTStatus.STATUS_END_OF_FILE)
            {
                return [];
            }

            ThrowForStatus(status, "read an SMB file");
            return bytes ?? [];
        },
        cancellationToken);

    internal ValueTask<int> WriteAsync(
        object handle,
        long offset,
        byte[] bytes,
        CancellationToken cancellationToken) => ExecuteAsync(
        () =>
        {
            var status = fileStore.WriteFile(out var written, handle, offset, bytes);
            ThrowForStatus(status, "write an SMB file");
            return written;
        },
        cancellationToken);

    internal ValueTask FlushAsync(object handle, CancellationToken cancellationToken) => ExecuteAsync(
        () =>
        {
            var status = fileStore.FlushFileBuffers(handle);
            ThrowForStatus(status, "flush an SMB file");
        },
        cancellationToken);

    internal async ValueTask CloseAsync(object handle)
    {
        if (Volatile.Read(ref _aborted) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await ExecuteAsync(
            () => CloseHandle(handle, "close an SMB file"),
            CancellationToken.None).ConfigureAwait(false);
    }

    internal void CloseSynchronously(object handle) => CloseHandleQuietly(handle);

    internal static string ToSmbPath(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        if (!remotePath.StartsWith('/')
            || remotePath.Any(character => character == '\\' || char.IsControl(character)))
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.InvalidName,
                "The SMB path is invalid.");
        }

        var components = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in components)
        {
            ValidateSmbComponent(component);
        }

        var smbPath = string.Join('\\', components);
        if (smbPath.Length > 32_767)
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.InvalidName,
                "The SMB path exceeds the supported length.");
        }

        return smbPath;
    }

    internal static RemoteFileSessionException MapStatus(NTStatus status, string operation) => status switch
    {
        NTStatus.STATUS_OBJECT_NAME_NOT_FOUND
            or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND
            or NTStatus.STATUS_NO_SUCH_FILE
            or NTStatus.STATUS_NOT_FOUND
            or NTStatus.STATUS_BAD_NETWORK_NAME => Error(
                RemoteFileSessionErrorCode.NotFound,
                $"The SMB server could not {operation} because the entry was not found."),
        NTStatus.STATUS_OBJECT_NAME_EXISTS
            or NTStatus.STATUS_OBJECT_NAME_COLLISION => Error(
                RemoteFileSessionErrorCode.AlreadyExists,
                $"The SMB server could not {operation} because the destination exists."),
        NTStatus.STATUS_ACCESS_DENIED
            or NTStatus.STATUS_PRIVILEGE_NOT_HELD
            or NTStatus.STATUS_MEDIA_WRITE_PROTECTED => Error(
                RemoteFileSessionErrorCode.AccessDenied,
                $"The SMB server denied permission to {operation}."),
        NTStatus.STATUS_NOT_A_DIRECTORY
            or NTStatus.STATUS_OBJECT_PATH_INVALID => Error(
                RemoteFileSessionErrorCode.NotDirectory,
                $"The SMB server could not {operation} because a path component is not a directory."),
        NTStatus.STATUS_FILE_IS_A_DIRECTORY => Error(
                RemoteFileSessionErrorCode.IsDirectory,
                $"The SMB server could not {operation} because the entry is a directory."),
        NTStatus.STATUS_DIRECTORY_NOT_EMPTY => Error(
                RemoteFileSessionErrorCode.DirectoryNotEmpty,
                $"The SMB server could not {operation} because the directory is not empty."),
        NTStatus.STATUS_NOT_SUPPORTED
            or NTStatus.STATUS_NOT_IMPLEMENTED
            or NTStatus.STATUS_INVALID_INFO_CLASS => Error(
                RemoteFileSessionErrorCode.Unsupported,
                $"The SMB server does not support the operation required to {operation}."),
        NTStatus.STATUS_OBJECT_NAME_INVALID
            or NTStatus.STATUS_OBJECT_PATH_SYNTAX_BAD
            or NTStatus.STATUS_INVALID_PARAMETER => Error(
                RemoteFileSessionErrorCode.InvalidName,
                $"The SMB server rejected the name used to {operation}."),
        NTStatus.STATUS_IO_TIMEOUT
            or NTStatus.STATUS_NETWORK_NAME_DELETED
            or NTStatus.STATUS_USER_SESSION_DELETED
            or NTStatus.STATUS_INVALID_SMB
            or NTStatus.STATUS_CANCELLED => Error(
                RemoteFileSessionErrorCode.Transient,
                $"The SMB connection failed while attempting to {operation}.",
                retryable: true),
        NTStatus.STATUS_SHARING_VIOLATION
            or NTStatus.STATUS_FILE_LOCK_CONFLICT
            or NTStatus.STATUS_LOCK_NOT_GRANTED
            or NTStatus.STATUS_DELETE_PENDING
            or NTStatus.STATUS_CANNOT_DELETE => Error(
                RemoteFileSessionErrorCode.IoFailure,
                $"The SMB entry is busy and could not {operation}.",
                retryable: true),
        _ => Error(
                RemoteFileSessionErrorCode.IoFailure,
                $"The SMB server failed to {operation}."),
    };

    internal static RemoteFileSessionException MapStatus(uint status, string operation) =>
        MapStatus((NTStatus)status, operation);

    private static bool IsNotFound(NTStatus status) => status is
        NTStatus.STATUS_OBJECT_NAME_NOT_FOUND
        or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND
        or NTStatus.STATUS_NO_SUCH_FILE
        or NTStatus.STATUS_NOT_FOUND;

    private static RemoteFileEntry ToRemoteEntry(FileIdBothDirectoryInformation entry)
    {
        ValidateSmbComponent(entry.FileName);
        var kind = ToEntryKind(entry.FileAttributes);
        long? size = kind == FileEntryKind.File ? entry.EndOfFile : null;
        var revision = CreateRevision(
            kind,
            size,
            entry.LastWriteTime,
            entry.ChangeTime,
            entry.FileId);
        return new RemoteFileEntry(
            entry.FileName,
            kind,
            size,
            ToTimestamp(entry.LastWriteTime),
            revision);
    }

    private static RemoteFileEntry ToRemoteEntry(
        string name,
        FileNetworkOpenInformation metadata,
        FileInternalInformation identity)
    {
        var kind = ToEntryKind(metadata.FileAttributes, metadata.IsDirectory);
        long? size = kind == FileEntryKind.File ? metadata.EndOfFile : null;
        var revision = CreateRevision(
            kind,
            size,
            metadata.LastWriteTime,
            metadata.ChangeTime,
            unchecked((ulong)identity.IndexNumber));
        return new RemoteFileEntry(
            name,
            kind,
            size,
            ToTimestamp(metadata.LastWriteTime),
            revision);
    }

    private static FileEntryKind ToEntryKind(
        SmbFileAttributes attributes,
        bool standardDirectory = false) => (attributes & SmbFileAttributes.ReparsePoint) != 0
        ? FileEntryKind.Link
        : standardDirectory || (attributes & SmbFileAttributes.Directory) != 0
            ? FileEntryKind.Directory
            : FileEntryKind.File;

    private static string CreateRevision(
        FileEntryKind kind,
        long? size,
        DateTime? lastWrite,
        DateTime? changed,
        ulong fileId) => FormattableString.Invariant(
        $"smb:{(int)kind}:{size ?? -1}:{UtcTicks(lastWrite)}:{UtcTicks(changed)}:{fileId}");

    private static long UtcTicks(DateTime? timestamp) => timestamp is null
        ? 0
        : NormalizeUtc(timestamp.Value).Ticks;

    private static DateTimeOffset? ToTimestamp(DateTime? timestamp) => timestamp is null
        ? null
        : new DateTimeOffset(NormalizeUtc(timestamp.Value));

    private static DateTime NormalizeUtc(DateTime timestamp) => timestamp.Kind switch
    {
        DateTimeKind.Utc => timestamp,
        DateTimeKind.Local => timestamp.ToUniversalTime(),
        _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
    };

    private static void ValidateSmbComponent(string component)
    {
        if (component is "." or ".."
            || component.Length is 0 or > 255
            || component.EndsWith(' ')
            || component.EndsWith('.')
            || component.Any(character => character is '"' or '*' or ':' or '<' or '>' or '?' or '|'
                || char.IsControl(character)))
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.InvalidName,
                "The SMB path contains a name unsupported by the portable provider boundary.");
        }
    }

    private ValueTask DeleteAsync(
        string path,
        CreateOptions kind,
        CancellationToken cancellationToken) => ExecuteAsync(
        () =>
        {
            var handle = OpenHandle(
                path,
                AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                0,
                CreateDisposition.FILE_OPEN,
                SafeOpenOptions | kind);
            var deletePending = false;
            try
            {
                var disposition = new FileDispositionInformation { DeletePending = true };
                var status = fileStore.SetFileInformation(handle, disposition);
                ThrowForStatus(status, "delete an SMB entry");
                deletePending = true;
            }
            finally
            {
                if (deletePending)
                {
                    // SMB completes delete-on-close only after the handle closes successfully.
                    CloseHandle(handle, "commit an SMB delete");
                }
                else
                {
                    CloseHandleQuietly(handle);
                }
            }
        },
        cancellationToken);

    private object OpenHandle(
        string path,
        AccessMask access,
        SmbFileAttributes attributes,
        CreateDisposition disposition,
        CreateOptions createOptions)
    {
        var status = fileStore.CreateFile(
            out var handle,
            out _,
            ToSmbPath(path),
            access,
            attributes,
            CooperativeShareAccess,
            disposition,
            createOptions,
            securityContext: null!);
        ThrowForStatus(status, "open an SMB entry");
        return handle;
    }

    private void CloseHandle(object handle, string operation)
    {
        var status = fileStore.CloseFile(handle);
        ThrowForStatus(status, operation);
    }

    private void CloseHandleQuietly(object handle)
    {
        if (Volatile.Read(ref _aborted) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            fileStore.CloseFile(handle);
        }
        catch (Exception)
        {
            // Closing an SMB handle must not replace the operation's classified failure.
        }
    }

    private async ValueTask<T> ExecuteAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        try
        {
            var task = Task.Run(operation, CancellationToken.None);
            using var cancellation = cancellationToken.UnsafeRegister(
                static state => ((SmbLibrarySession)state!).Abort(),
                this);
            var result = await task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RemoteFileSessionException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RemoteFileSessionException)
        {
            throw;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (SocketException)
        {
            throw Error(
                RemoteFileSessionErrorCode.Transient,
                "The SMB network connection failed.",
                retryable: true);
        }
        catch (IOException)
        {
            throw Error(
                RemoteFileSessionErrorCode.Transient,
                "The SMB transport failed.",
                retryable: true);
        }
        catch (Exception)
        {
            throw Error(RemoteFileSessionErrorCode.IoFailure, "The SMB adapter failed.");
        }
    }

    private async ValueTask ExecuteAsync(Action operation, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            () =>
            {
                operation();
                return true;
            },
            cancellationToken).ConfigureAwait(false);

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(SmbLibrarySession));
        }

        if (Volatile.Read(ref _aborted) != 0)
        {
            throw Error(
                RemoteFileSessionErrorCode.Transient,
                "The SMB connection is no longer available.",
                retryable: true);
        }
    }

    private void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
        {
            return;
        }

        try
        {
            client.Disconnect();
        }
        catch (Exception)
        {
            // Abort is used by cancellation and disposal and must remain best effort.
        }

        routedTransport?.Dispose();
    }

    private static void ThrowForStatus(NTStatus status, string operation)
    {
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw MapStatus(status, operation);
        }
    }

    private static RemoteFileSessionException Error(
        RemoteFileSessionErrorCode code,
        string message,
        bool retryable = false) => new(code, message, retryable);
}

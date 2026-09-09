using System.Diagnostics;
using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;
using Asura.Files;
using ProviderId = Asura.Files.FileProviderProfileId;

namespace Asura.ConnectionBackend;

/// <summary>
/// Keeps catalog generations, transfer governance and host-local files in the desktop.
/// Remote provider operations run in an owned guest process using the same SDK adapters.
/// </summary>
internal sealed class WorkspaceFileProviderFactory(ISecretVault vault, ISshHostKeyTrustStore knownHosts,
    IConnectionRuntime? connectionRuntime, Func<CancellationToken, Task<DatabaseWorkspaceOperationLaunch>> launch,
    bool localInWorkspace = false)
{
    internal async ValueTask<OwnedFileProviderRegistration> CreateAsync(FileProviderProfile profile,
        IReadOnlyDictionary<ConnectionId, ConnectionProfile> connections, CancellationToken token)
    {
        // A guest-local path need not exist on the Mac. Use only a filesystem-root
        // adapter's static capability description; the real path is opened in the guest.
        var description = localInWorkspace && profile.Configuration is FileProviderConfiguration.Local
            ? new FileProviderProfile(profile.Id, profile.SchemaVersion, profile.Name,
                new FileProviderConfiguration.Local(Path.GetPathRoot(AppContext.BaseDirectory)!)) : profile;
        var template = await new FileProviderAdapterFactory(vault, knownHosts, connectionRuntime)
            .CreateAsync(description, connections, token).ConfigureAwait(false);
        if (profile.Configuration is FileProviderConfiguration.Local && !localInWorkspace) { return template; }
        using (template)
        {
            var registration = template.Registration;
            var connection = profile.Configuration is FileProviderConfiguration.Sftp sftp ? connections[sftp.ConnectionId] : null;
            var provider = new WorkspaceFileProvider(profile, connection, registration.Provider.Capabilities,
                vault, knownHosts, connectionRuntime, launch);
            return new(profile.Id, new FileProviderRegistration(registration.Name, registration.Family, provider,
                registration.Root, registration.GovernedMutationCapabilities, registration.Start), [provider]);
        }
    }
}

/// <summary>
/// A provider facade, deliberately not ILocalFilePathSource: a guest path must never
/// be offered to the host preview cache as if it were a local file.
/// </summary>
internal sealed partial class WorkspaceFileProvider(FileProviderProfile profile, ConnectionProfile? connection,
    FileProviderCapabilities capabilities, ISecretVault vault, ISshHostKeyTrustStore knownHosts,
    IConnectionRuntime? connectionRuntime, Func<CancellationToken, Task<DatabaseWorkspaceOperationLaunch>> launch) : IFileProvider, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private FileWorkspaceSession? _session;
    private string _cursorPrefix = string.Empty;
    private int _disposed;
    public ProviderId ProfileId { get; } = new(profile.Id.Value);
    public FileProviderCapabilities Capabilities { get; } = capabilities;

    private async ValueTask<FileProviderResult<T>> InvokeAsync<T>(FileWorkspaceRequest request, Func<FileWorkspaceMessage, T?> result,
        CancellationToken token, Stream? upload = null, Stream? download = null, IProgress<FileTransferProgress>? progress = null) where T : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        FileWorkspaceChild.Validate(request);
        await _operations.WaitAsync(token).ConfigureAwait(false);
        var dispatched = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_session is null || _session.IsClosed)
            {
                if (request.List?.ContinuationToken is not null)
                {
                    return FileProviderResult<T>.Failure(FileProviderError.Create(FileProviderErrorCode.InvalidLocation,
                        "The file listing expired when its connection closed. Refresh the folder to continue."));
                }
                _session = await FileWorkspaceSession.OpenAsync(launch,
                    new FileWorkspaceHostCredentials(profile, connection, vault, knownHosts, connectionRuntime),
                    _lifetime.Token, token).ConfigureAwait(false);
                _cursorPrefix = $"{Guid.NewGuid():N}:";
            }
            if (request.List is { ContinuationToken: { } continuation } listing)
            {
                if (!continuation.Value.StartsWith(_cursorPrefix, StringComparison.Ordinal))
                {
                    return FileProviderResult<T>.Failure(FileProviderError.Create(FileProviderErrorCode.InvalidLocation,
                        "The file listing belongs to an earlier connection. Refresh the folder to continue."));
                }
                request = request with { List = new(listing.Location, listing.PageSize, new FilePageToken(continuation.Value[_cursorPrefix.Length..])) };
            }
            var session = _session;
            ValidateExecutionLocation(profile, session.Location);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, session.Lifetime);
            var process = session.Process;
            session.Credentials.BeginOperation();
            await BackendJsonFrames.WriteAsync(process.StandardInput.BaseStream, request with { Connection = session.Credentials.GuestConnection },
                FileWorkspaceJsonContext.Default.FileWorkspaceRequest, cancellation.Token).ConfigureAwait(false);
            var ready = await BackendJsonFrames.ReadAsync(process.StandardOutput.BaseStream,
                FileWorkspaceJsonContext.Default.FileWorkspaceMessage, cancellation.Token).ConfigureAwait(false);
            if (ready != new FileWorkspaceMessage(FileWorkspaceMessageKind.Ready)) { throw new InvalidDataException("The file backend could not prepare the operation."); }
            cancellation.Token.ThrowIfCancellationRequested();
            dispatched = true;
            await BackendJsonFrames.WriteAsync(process.StandardInput.BaseStream, new FileWorkspaceReply(FileWorkspaceMessageKind.Execute),
                FileWorkspaceJsonContext.Default.FileWorkspaceReply, cancellation.Token).ConfigureAwait(false);
            var response = await ReadResultAsync(process, session.Credentials, request, upload, download, progress, cancellation.Token).ConfigureAwait(false);
            if (response.Page is { ContinuationToken: { } next } page)
            {
                response = response with { Page = page with { ContinuationToken = new FilePageToken(_cursorPrefix + next.Value) } };
            }
            if (response.Error is { } error)
            {
                if (!Enum.IsDefined(error.Code)) { throw new InvalidDataException("The file backend returned an invalid error."); }
                return FileProviderResult<T>.Failure(error);
            }
            var value = result(response) ?? throw new InvalidDataException("The file backend omitted its operation result.");
            return FileProviderResult<T>.Success(value);
        }
        catch (Exception) when (dispatched)
        {
            if (_session is not null) { await _session.CloseAfterFailureAsync().ConfigureAwait(false); }
            return FileProviderResult<T>.Failure(FileWorkspaceChild.FailureAfterDispatch(request.Operation));
        }
        catch
        {
            if (_session is not null) { await _session.CloseAfterFailureAsync().ConfigureAwait(false); }
            throw;
        }
        finally { _operations.Release(); }
    }

    internal static void ValidateExecutionLocation(FileProviderProfile profile, BackendExecutionLocation location)
    {
        if (profile.Configuration is FileProviderConfiguration.Ftp { ConnectionMode: FtpConnectionMode.Active }
            && location != BackendExecutionLocation.HostDirect)
        {
            throw new NotSupportedException("Active FTP requires an inbound data connection that the workspace gateway does not expose. Select Passive or Auto passive mode for this provider.");
        }
    }

    private static async Task<FileWorkspaceMessage> ReadResultAsync(Process process, FileWorkspaceHostCredentials credentials,
        FileWorkspaceRequest request, Stream? upload, Stream? download, IProgress<FileTransferProgress>? progress, CancellationToken token)
    {
        long uploaded = 0;
        long downloaded = 0;
        while (true)
        {
            var message = await BackendJsonFrames.ReadAsync(process.StandardOutput.BaseStream, FileWorkspaceJsonContext.Default.FileWorkspaceMessage, token).ConfigureAwait(false);
            if (message.Kind == FileWorkspaceMessageKind.Result)
            {
                if (message.Secret is not null || message.HostKey is not null || message.Bytes is not null || message.Progress is not null
                    || message.Identity != 0 || message.Count != 0)
                { throw new InvalidDataException("The file backend result contains a callback."); }
                var values = new object?[] { message.Page, message.Entry, message.Read, message.Write, message.Transfer, message.Delete, message.AccessControl };
                if (values.Count(value => value is not null) != (message.Error is null ? 1 : 0))
                { throw new InvalidDataException("The file backend result contains contradictory values."); }
                if (message.Read is { } read && read.BytesRead != downloaded) { throw new InvalidDataException("The file download receipt does not match its content."); }
                if (message.Write is { } write && write.BytesWritten != uploaded) { throw new InvalidDataException("The file upload receipt does not match its content."); }
                return message;
            }
            FileWorkspaceReply reply;
            var empty = new FileWorkspaceMessage(message.Kind);
            switch (message.Kind)
            {
                case FileWorkspaceMessageKind.Upload:
                    if (message with { Count = 0 } != empty || upload is null || request.Write is null
                        || message.Count is < 1 or > FileWorkspaceCallbacks.MaximumChunkBytes)
                    { throw new InvalidDataException("The file backend requested unauthorized upload content."); }
                    var count = (int)Math.Min(message.Count, request.Write.ContentLength - uploaded);
                    var bytes = new byte[count];
                    try
                    {
                        var readCount = await upload.ReadAsync(bytes, token).ConfigureAwait(false);
                        uploaded += readCount;
                        reply = new(message.Kind, Bytes: bytes.AsSpan(0, readCount).ToArray());
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                    break;
                case FileWorkspaceMessageKind.Download:
                    if (message with { Bytes = null } != empty || download is null || request.Read is null
                        || message.Bytes is not { Length: <= FileWorkspaceCallbacks.MaximumChunkBytes } content
                        || downloaded > request.Read.MaximumBytes - content.Length)
                    { throw new InvalidDataException("The file backend exceeded its authorized download range."); }
                    try
                    {
                        await download.WriteAsync(content, token).ConfigureAwait(false);
                        downloaded += content.Length;
                    }
                    finally { CryptographicOperations.ZeroMemory(content); }
                    reply = new(message.Kind);
                    break;
                case FileWorkspaceMessageKind.Progress:
                    if (message with { Progress = null } != empty || message.Progress is not { } update || !Enum.IsDefined(update.Stage)
                        || update.BytesTransferred < 0 || update.TotalBytes < 0)
                    { throw new InvalidDataException("The file backend progress is invalid."); }
                    progress?.Report(update);
                    reply = new(message.Kind);
                    break;
                default:
                    reply = await credentials.HandleAsync(message, token).ConfigureAwait(false);
                    break;
            }
            try
            {
                await BackendJsonFrames.WriteAsync(process.StandardInput.BaseStream, reply, FileWorkspaceJsonContext.Default.FileWorkspaceReply, token).ConfigureAwait(false);
            }
            finally { if (reply.Bytes is not null) { CryptographicOperations.ZeroMemory(reply.Bytes); } }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _lifetime.Cancel();
        // In-flight calls retain their own linked tokens and release their process
        // leases; workspace teardown awaits those leases before stopping the VM.
        _lifetime.Dispose();
    }
}

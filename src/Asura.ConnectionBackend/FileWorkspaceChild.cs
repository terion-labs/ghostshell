using Asura.Core;
using Asura.Files;

namespace Asura.ConnectionBackend;

internal static class FileWorkspaceChild
{
    internal static bool Mutates(FileWorkspaceOperation operation) => operation is not
        (FileWorkspaceOperation.List or FileWorkspaceOperation.Stat or FileWorkspaceOperation.Read or FileWorkspaceOperation.GetAccessControl);

    internal static async Task RunAsync(Stream input, Stream output, CancellationToken token)
    {
        var request = await BackendJsonFrames.ReadAsync(input, FileWorkspaceJsonContext.Default.FileWorkspaceRequest, token).ConfigureAwait(false);
        Validate(request);
        using var callbacks = new FileWorkspaceCallbacks(input, output);
        var connections = request.Connection is { } connection
            ? new Dictionary<ConnectionId, ConnectionProfile> { [connection.Id] = connection }
            : [];
        var factory = new FileProviderAdapterFactory(callbacks, callbacks, agentIdentitySource: callbacks);
        using var registration = await factory.CreateAsync(request.Profile, connections, token).ConfigureAwait(false);
        var initial = request;
        while (true)
        {
            await BackendJsonFrames.WriteAsync(output, new FileWorkspaceMessage(FileWorkspaceMessageKind.Ready),
                FileWorkspaceJsonContext.Default.FileWorkspaceMessage, token).ConfigureAwait(false);
            var execute = await BackendJsonFrames.ReadAsync(input, FileWorkspaceJsonContext.Default.FileWorkspaceReply, token).ConfigureAwait(false);
            if (execute != new FileWorkspaceReply(FileWorkspaceMessageKind.Execute))
            {
                throw new InvalidDataException("The file backend execution acknowledgement is invalid.");
            }
            FileWorkspaceMessage result;
            try { result = await ExecuteAsync(registration.Registration.Provider, callbacks, request, token).ConfigureAwait(false); }
            catch (Exception)
            {
                result = new(FileWorkspaceMessageKind.Result, Error: FailureAfterDispatch(request.Operation));
            }
            await BackendJsonFrames.WriteAsync(output, result, FileWorkspaceJsonContext.Default.FileWorkspaceMessage, token).ConfigureAwait(false);
            try { request = await BackendJsonFrames.ReadAsync(input, FileWorkspaceJsonContext.Default.FileWorkspaceRequest, token).ConfigureAwait(false); }
            catch (EndOfStreamException) { break; }
            Validate(request);
            if (request.Profile != initial.Profile || request.Connection?.Id != initial.Connection?.Id
                || request.Connection?.Endpoint != initial.Connection?.Endpoint || request.Connection?.Authentication != initial.Connection?.Authentication
                || request.Connection?.HostKeyPolicy != initial.Connection?.HostKeyPolicy)
            {
                throw new InvalidDataException("A file backend session cannot change its provider authority.");
            }
        }
    }

    internal static FileProviderError FailureAfterDispatch(FileWorkspaceOperation operation) => FileProviderError.Create(
        Mutates(operation) ? FileProviderErrorCode.PartialTransfer : FileProviderErrorCode.IoFailure,
        Mutates(operation)
            ? "The file backend stopped before confirming the mutation. Changes may have completed. Reload and verify before retrying."
            : "The file backend could not complete the read operation.");

    internal static void Validate(FileWorkspaceRequest request)
    {
        if (request.Profile is null || !Enum.IsDefined(request.Operation)) { throw new InvalidDataException("The file backend request is invalid."); }
        if (request.Profile.Configuration is FileProviderConfiguration.Sftp sftp)
        {
            if (request.Connection is not { Endpoint: ConnectionEndpoint.Ssh } connection || connection.Id != sftp.ConnectionId)
            {
                throw new InvalidDataException("The file backend SSH connection does not match the provider.");
            }
        }
        else if (request.Connection is not null) { throw new InvalidDataException("This file provider cannot use an SSH authentication callback."); }
        object?[] operations = [request.List, request.Stat, request.Read, request.Write, request.CreateDirectory,
            request.Rename, request.Transfer, request.Delete, request.GetAccessControl, request.SetAccessControl];
        if (operations.Count(value => value is not null) != 1 || operations[(int)request.Operation] is null)
        {
            throw new InvalidDataException("The file backend request must contain exactly one matching operation.");
        }
    }

    private static async Task<FileWorkspaceMessage> ExecuteAsync(IFileProvider provider, FileWorkspaceCallbacks callbacks,
        FileWorkspaceRequest request, CancellationToken token)
    {
        var response = new FileWorkspaceMessage(FileWorkspaceMessageKind.Result);
        switch (request.Operation)
        {
            case FileWorkspaceOperation.List:
                var list = await provider.ListAsync(request.List!, token).ConfigureAwait(false);
                return response with { Error = list.Error, Page = list.Value is { } page ? new([.. page.Items], page.ContinuationToken) : null };
            case FileWorkspaceOperation.Stat:
                var stat = await provider.StatAsync(request.Stat!, token).ConfigureAwait(false);
                return response with { Error = stat.Error, Entry = stat.Value };
            case FileWorkspaceOperation.Read:
                using (var destination = new FileWorkspaceTransferStream(callbacks, upload: false))
                {
                    var read = await provider.ReadAsync(request.Read!, destination, callbacks, token).ConfigureAwait(false);
                    return response with { Error = read.Error, Read = read.Value };
                }
            case FileWorkspaceOperation.Write:
                using (var source = new FileWorkspaceTransferStream(callbacks, upload: true))
                {
                    var write = await provider.WriteAsync(request.Write!, source, callbacks, token).ConfigureAwait(false);
                    return response with { Error = write.Error, Write = write.Value };
                }
            case FileWorkspaceOperation.CreateDirectory:
                var directory = await provider.CreateDirectoryAsync(request.CreateDirectory!, token).ConfigureAwait(false);
                return response with { Error = directory.Error, Entry = directory.Value };
            case FileWorkspaceOperation.Rename:
                var rename = await provider.RenameAsync(request.Rename!, token).ConfigureAwait(false);
                return response with { Error = rename.Error, Entry = rename.Value };
            case FileWorkspaceOperation.Transfer:
                var transfer = await provider.TransferAsync(request.Transfer!, callbacks, token).ConfigureAwait(false);
                return response with { Error = transfer.Error, Transfer = transfer.Value };
            case FileWorkspaceOperation.Delete:
                var delete = await provider.DeleteAsync(request.Delete!, token).ConfigureAwait(false);
                return response with { Error = delete.Error, Delete = delete.Value };
            case FileWorkspaceOperation.GetAccessControl:
                var access = await provider.GetAccessControlAsync(request.GetAccessControl!, token).ConfigureAwait(false);
                return response with { Error = access.Error, AccessControl = access.Value };
            case FileWorkspaceOperation.SetAccessControl:
                var updatedAccess = await provider.SetAccessControlAsync(request.SetAccessControl!, token).ConfigureAwait(false);
                return response with { Error = updatedAccess.Error, AccessControl = updatedAccess.Value };
            default:
                throw new InvalidDataException("The file backend operation is invalid.");
        }
    }
}

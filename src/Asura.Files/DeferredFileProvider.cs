namespace Asura.Files;

/// <summary>
/// Delays credential resolution and SDK-client construction until the first provider operation.
/// Successful materialization is shared for the generation lifetime; failures remain retryable.
/// </summary>
internal sealed class DeferredFileProvider : IFileProvider, IDisposable
{
    private readonly SemaphoreSlim _materializationGate = new(1, 1);
    private readonly Func<CancellationToken, ValueTask<MaterializedFileProvider>> _materialize;
    private MaterializedFileProvider? _materialized;
    private bool _disposed;

    public DeferredFileProvider(
        FileProviderProfileId profileId,
        FileProviderCapabilities capabilities,
        Func<CancellationToken, ValueTask<MaterializedFileProvider>> materialize)
    {
        ProfileId = profileId;
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
    }

    public FileProviderProfileId ProfileId { get; }

    public FileProviderCapabilities Capabilities { get; }

    public ValueTask<FileProviderResult<FilePage>> ListAsync(
        FileListRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (provider, token) => provider.ListAsync(request, token),
            cancellationToken);

    public ValueTask<FileProviderResult<FileEntry>> StatAsync(
        FileStatRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (provider, token) => provider.StatAsync(request, token),
            cancellationToken);

    public ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (provider, token) => provider.ReadAsync(request, destination, progress, token),
            cancellationToken);

    public ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (provider, token) => provider.WriteAsync(request, source, progress, token),
            cancellationToken);

    public ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
        FileCreateDirectoryRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (provider, token) => provider.CreateDirectoryAsync(request, token),
            cancellationToken);

    public ValueTask<FileProviderResult<FileEntry>> RenameAsync(
        FileRenameRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (provider, token) => provider.RenameAsync(request, token),
            cancellationToken);

    public ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (provider, token) => provider.TransferAsync(request, progress, token),
            cancellationToken);

    public ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (provider, token) => provider.DeleteAsync(request, token),
            cancellationToken);

    public ValueTask<FileProviderResult<FileAccessControl>> GetAccessControlAsync(
        FileAccessControlRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (provider, token) => provider.GetAccessControlAsync(request, token),
            cancellationToken);

    public ValueTask<FileProviderResult<FileAccessControl>> SetAccessControlAsync(
        FileSetAccessControlRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            (provider, token) => provider.SetAccessControlAsync(request, token),
            cancellationToken);

    private async ValueTask<FileProviderResult<T>> ExecuteAsync<T>(
        Func<IFileProvider, CancellationToken, ValueTask<FileProviderResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var provider = await MaterializeAsync(cancellationToken).ConfigureAwait(false);
            return await operation(provider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<T>(
                FileProviderErrorCode.Cancelled,
                "The provider operation was cancelled.");
        }
        catch (FileProviderAdapterConfigurationException)
        {
            return Failure<T>(
                FileProviderErrorCode.IoFailure,
                "The provider could not be initialized. Review its endpoint and credentials.");
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure<T>(
                FileProviderErrorCode.IoFailure,
                "The provider could not be initialized or reached.",
                retryable: true);
        }
    }

    private static FileProviderResult<T> Failure<T>(
        FileProviderErrorCode code,
        string message,
        bool retryable = false) =>
        FileProviderResult<T>.Failure(FileProviderError.Create(code, message, retryable));

    internal async ValueTask<IFileProvider> MaterializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _materialized) is { } existing)
        {
            return existing.Provider;
        }

        await _materializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_materialized is null)
            {
                var materialized = await _materialize(cancellationToken).ConfigureAwait(false);
                if (materialized.Provider.ProfileId != ProfileId
                    || materialized.Provider.Capabilities != Capabilities)
                {
                    materialized.Dispose();
                    throw new InvalidOperationException(
                        "The deferred provider materialized with a different identity or capability set.");
                }

                Volatile.Write(ref _materialized, materialized);
            }

            return _materialized.Provider;
        }
        finally
        {
            _materializationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _materialized, null)?.Dispose();
        _materializationGate.Dispose();
    }
}

internal sealed record MaterializedFileProvider(
    IFileProvider Provider,
    IReadOnlyList<IDisposable> Owners) : IDisposable
{
    public void Dispose()
    {
        foreach (var owner in Owners.Reverse())
        {
            owner.Dispose();
        }
    }
}

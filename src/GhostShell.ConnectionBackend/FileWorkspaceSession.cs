using System.Diagnostics;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.ConnectionBackend;

/// <summary>Owns one lazy provider-generation process, retaining SDK pagination cursors until route loss or disposal.</summary>
internal sealed class FileWorkspaceSession : IAsyncDisposable
{
    private readonly DatabaseWorkspaceOperationLaunch _owned;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _termination;
    private readonly Task _errors;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closing;

    private FileWorkspaceSession(DatabaseWorkspaceOperationLaunch owned, FileWorkspaceHostCredentials credentials, CancellationToken owner)
    {
        _owned = owned;
        Credentials = credentials;
        var start = owned.StartInfo;
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        Process = Process.Start(start) ?? throw new IOException("The file backend could not start.");
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner, owned.Lifetime);
        Lifetime = _lifetime.Token;
        _errors = DrainErrorsAsync();
        _termination = Lifetime.Register(() =>
        {
            DatabaseOperationWorker.StopOwnedProcess(Process);
            _ = CloseAfterFailureAsync();
        });
    }

    internal Process Process { get; }
    internal BackendExecutionLocation Location => _owned.Location;
    internal FileWorkspaceHostCredentials Credentials { get; }
    internal CancellationToken Lifetime { get; }
    internal bool IsClosed => Volatile.Read(ref _closing) != 0;

    internal static async Task<FileWorkspaceSession> OpenAsync(Func<CancellationToken, Task<DatabaseWorkspaceOperationLaunch>> launch,
        FileWorkspaceHostCredentials credentials, CancellationToken owner, CancellationToken token)
    {
        DatabaseWorkspaceOperationLaunch? owned = null;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner, token);
            owned = await launch(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            owned.Lifetime.ThrowIfCancellationRequested();
            return new(owned, credentials, owner);
        }
        catch
        {
            credentials.Dispose();
            if (owned is not null) { await RedisWorkspaceSession.CleanupAfterFailureAsync(owned.CleanupAsync).ConfigureAwait(false); }
            throw;
        }
    }

    internal async Task CloseAfterFailureAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception)
        {
            SecretSafeDiagnosticProjection.WriteStandardError("files.workspace.cleanup.failed", SecretSafeDiagnosticKind.Unexpected);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0) { _ = CloseAsync(); }
        return new(_closed.Task);
    }

    private async Task CloseAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            DatabaseOperationWorker.StopOwnedProcess(Process);
            try
            {
                await Process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                await _errors.ConfigureAwait(false);
            }
            finally
            {
                await _termination.DisposeAsync().ConfigureAwait(false);
                Process.Dispose();
                Credentials.Dispose();
                try { await _owned.CleanupAsync().ConfigureAwait(false); }
                finally { _lifetime.Dispose(); }
            }
            _closed.TrySetResult();
        }
        catch (Exception exception) { _closed.TrySetException(exception); }
    }

    private async Task DrainErrorsAsync()
    {
        try { await Process.StandardError.BaseStream.CopyToAsync(Stream.Null, Lifetime).ConfigureAwait(false); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (IOException) when (Lifetime.IsCancellationRequested) { }
    }
}

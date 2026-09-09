using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.ConnectionBackend;
using GhostShell.Core;
using GhostShell.Files;
using GhostShell.Infrastructure;

namespace GhostShell.Desktop;

/// <summary>Installs a pinned, UI-free Linux payload and plans private SDK exec, never a guest TCP listener.</summary>
internal sealed class WorkspaceDatabaseBackend(IConnectionCommandRuntime commands,
    string? descriptorPath = null, string? cacheRoot = null, string architecture = "arm64") : IAsyncDisposable
{
    internal const string ArchiveName = "GhostShell-workspace-backend-arm64.tar.gz";
    private readonly SemaphoreSlim _installation = new(1, 1);
    private readonly object _operationGate = new();
    private readonly HashSet<TaskCompletionSource> _operations = [];
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeStarted;
    private bool _disposed;
    private string? _executable;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            // The owner cancels workers first. Keep SDK planning alive until
            // their independent scratch cleanup has finished.
            await _installation.WaitAsync().ConfigureAwait(false);
            _disposed = true;
            Task[] pending;
            lock (_operationGate) { pending = [.. _operations.Select(operation => operation.Task)]; }
            _installation.Release();
            await Task.WhenAll(pending).ConfigureAwait(false);
            _installation.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception) { _disposeCompletion.TrySetException(exception); throw; }
    }

    // Only a validated release hash and an app-owned destination enter argv.
    // The temporary extraction is never executable until both checks succeed.
    internal const string InstallScript = """
        set -eu
        umask 077
        root=/home/ghostshell/.cache/ghostshell/backend
        mkdir -p "$root"
        target="$root/$1"
        staging="$root/install.$2"
        mkdir "$staging"
        trap 'rm -rf -- "$staging"' EXIT HUP INT TERM
        cat > "$staging/payload.tar.gz"
        printf '%s  %s\n' "$1" "$staging/payload.tar.gz" | sha256sum -c - >/dev/null
        mkdir "$staging/files"
        tar --no-same-owner --no-same-permissions -xzf "$staging/payload.tar.gz" -C "$staging/files"
        (cd "$staging/files" && sha256sum -c MANIFEST.sha256 >/dev/null)
        chmod 700 "$staging/files/GhostShell.Backend"
        if [ -e "$target" ]; then
            cmp -s "$staging/files/MANIFEST.sha256" "$target/MANIFEST.sha256"
            (cd "$target" && sha256sum -c MANIFEST.sha256 >/dev/null)
        else
            mv "$staging/files" "$target"
        fi
        """;

    internal Task<DatabaseWorkspaceOperationLaunch> PlanAsync(CancellationToken cancellationToken) =>
        PlanAsync("database", cancellationToken);

    internal async Task<DatabaseWorkspaceOperationLaunch> PlanAsync(string capability, CancellationToken cancellationToken)
    {
        if (capability is not ("database" or "redis" or "files" or "http"))
        {
            throw new ArgumentException("The workspace backend capability is not supported.", nameof(capability));
        }
        await _installation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_executable is null)
            {
                var archive = await EnsureDefaultArchiveAsync(architecture, cancellationToken, descriptorPath, cacheRoot).ConfigureAwait(false);
                var installationId = Guid.NewGuid().ToString("N");
                var start = await PlanCommandAsync("/bin/sh", ["-c", InstallScript, "ghostshell-backend-install", archive.Hash, installationId], cancellationToken).ConfigureAwait(false);
                using var process = Process.Start(start) ?? throw new IOException("The workspace backend installer could not start.");
                using var stop = cancellationToken.Register(() => DatabaseOperationWorker.StopOwnedProcess(process));
                var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
                var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
                var installationFailed = false;
                try
                {
                    await using var input = File.OpenRead(archive.Path);
                    await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    await Task.WhenAll(output, error).ConfigureAwait(false);
                    if (process.ExitCode != 0)
                    {
                        throw new IOException("The workspace backend could not be installed or verified. Reopen the workspace and retry.");
                    }
                }
                catch { installationFailed = true; throw; }
                finally
                {
                    await DatabaseOperationWorker.RunCleanupAsync(installationFailed,
                        () => { DatabaseOperationWorker.StopOwnedProcess(process); return Task.CompletedTask; },
                        async () => await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false),
                        async () =>
                        {
                            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                            catch (IOException) when (cancellationToken.IsCancellationRequested) { }
                        },
                        () => CleanupInstallationAsync(installationId)).ConfigureAwait(false);
                }
                _executable = $"/home/ghostshell/.cache/ghostshell/backend/{archive.Hash}/GhostShell.Backend";
            }
            var operationId = Guid.NewGuid().ToString("N");
            try
            {
                await RunBackendControlAsync("prepare", operationId, cancellationToken).ConfigureAwait(false);
                var start = await PlanCommandAsync(_executable, [capability, operationId], cancellationToken).ConfigureAwait(false);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_operationGate) { _operations.Add(completion); }
                var cleaned = 0;
                return new(start, async () =>
                {
                    if (Interlocked.Exchange(ref cleaned, 1) != 0) { await completion.Task.ConfigureAwait(false); return; }
                    try { await CleanupOperationAsync(operationId).ConfigureAwait(false); }
                    finally
                    {
                        lock (_operationGate) { _operations.Remove(completion); }
                        completion.TrySetResult();
                    }
                });
            }
            catch
            {
                await DatabaseOperationWorker.RunCleanupAsync(operationFailed: true,
                    () => CleanupOperationAsync(operationId)).ConfigureAwait(false);
                throw;
            }
        }
        finally { _installation.Release(); }
    }

    private async Task CleanupOperationAsync(string operationId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await RunBackendControlAsync("cleanup", operationId, timeout.Token).ConfigureAwait(false);
    }

    private async Task CleanupInstallationAsync(string installationId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var start = await PlanCommandAsync("/bin/sh",
            ["-c", "rm -rf -- \"/home/ghostshell/.cache/ghostshell/backend/install.$1\"", "ghostshell-backend-cleanup", installationId],
            timeout.Token).ConfigureAwait(false);
        await RunControlCommandAsync(start, timeout.Token).ConfigureAwait(false);
    }

    private async Task RunBackendControlAsync(string operation, string operationId, CancellationToken token)
    {
        var start = await PlanCommandAsync(_executable!, [operation, operationId], token).ConfigureAwait(false);
        await RunControlCommandAsync(start, token).ConfigureAwait(false);
    }

    private static async Task RunControlCommandAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = Process.Start(start) ?? throw new IOException("The workspace backend control process could not start.");
        using var stop = token.Register(() => DatabaseOperationWorker.StopOwnedProcess(process));
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        var failed = false;
        try
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) { throw new IOException("The workspace backend scratch operation failed."); }
        }
        catch { failed = true; throw; }
        finally
        {
            await DatabaseOperationWorker.RunCleanupAsync(failed,
                () => { DatabaseOperationWorker.StopOwnedProcess(process); return Task.CompletedTask; },
                async () => await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false),
                async () =>
                {
                    try { await Task.WhenAll(output, error).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (IOException) when (token.IsCancellationRequested) { }
                }).ConfigureAwait(false);
        }
    }

    private async Task<ProcessStartInfo> PlanCommandAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var plan = await commands.PlanDuplexCommandAsync(BuiltInConnections.Local, executable, arguments, token).ConfigureAwait(false);
        if (plan is not ConnectionRuntimeResult<TerminalLaunchRequest>.Success success || success.Value.Executable is not { } hostExecutable)
        {
            throw new IOException("The workspace backend control channel is unavailable.");
        }
        var start = new ProcessStartInfo(hostExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = success.Value.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in success.Value.Arguments) { start.ArgumentList.Add(argument); }
        foreach (var (name, value) in success.Value.Environment) { start.Environment[name] = value; }
        return start;
    }

    internal static Task<(string Path, string Hash)> EnsureDefaultArchiveAsync(string architecture, CancellationToken token,
        string? descriptorPath = null, string? cacheRoot = null)
    {
        if (architecture is not ("arm64" or "x64")) { throw new ArgumentException("Unsupported backend architecture.", nameof(architecture)); }
        var root = AppContext.BaseDirectory;
        var baseDirectory = new DirectoryInfo(root);
        if (baseDirectory.Parent is { } contents && baseDirectory.Name is "MacOS" && contents.Name is "Contents")
        {
            root = Path.Combine(contents.FullName, "Resources");
        }
        var descriptor = Path.Combine(root, "runtimes", "linux-" + architecture, "workspace-backend", "backend-assets.json");
        var version = typeof(WorkspaceDatabaseBackend).Assembly.GetName().Version!.ToString(3);
        return EnsureArchiveAsync(descriptorPath ?? descriptor,
            cacheRoot ?? Path.Combine(GhostShellDataPaths.CreateDefault().DataDirectory, "workspace-backends"),
            new Uri($"https://github.com/terion-labs/ghostshell/releases/download/v{version}/GhostShell-workspace-backend-{architecture}.tar.gz"),
            Environment.GetEnvironmentVariable(architecture is "arm64" ? "GHOSTSHELL_WORKSPACE_BACKEND_ARCHIVE" : "GHOSTSHELL_WORKSPACE_BACKEND_X64_ARCHIVE"), token);
    }

    internal static async Task<(string Path, string Hash)> EnsureArchiveAsync(string descriptorPath, string cacheRoot,
        Uri uri, string? localArchive, CancellationToken token, HttpMessageHandler? handler = null)
    {
        if (!File.Exists(descriptorPath))
        {
            throw new IOException("The workspace backend descriptor is missing. Build the workspace backend payload or reinstall GhostShell.");
        }
        using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(descriptorPath, token).ConfigureAwait(false));
        var hash = descriptor.RootElement.GetProperty("sha256").GetString();
        var size = descriptor.RootElement.GetProperty("size").GetInt64();
        if (hash is not { Length: 64 } || hash.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0 || size is <= 0 or > 536_870_912)
        {
            throw new IOException("The workspace backend descriptor has an invalid checksum or size.");
        }
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(cacheRoot); }
        else { Directory.CreateDirectory(cacheRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        PrivateContentPathGuard.ValidatePrivateDirectory(cacheRoot);
        var destination = Path.Combine(cacheRoot, hash + ".tar.gz");
        if (await MatchesAsync(destination, hash, size, token).ConfigureAwait(false)) { return (destination, hash); }
        var pending = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var response = localArchive is null
                ? await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false) : null;
            response?.EnsureSuccessStatusCode();
            await using var input = localArchive is null ? await response!.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false) : File.OpenRead(localArchive);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
            await using (var file = new FileStream(pending, options))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > size) { throw new IOException("The workspace backend download exceeds its pinned size."); }
                    await file.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                }
            }
            if (!await MatchesAsync(pending, hash, size, token).ConfigureAwait(false))
            {
                throw new IOException("The workspace backend download failed its integrity check.");
            }
            File.Move(pending, destination, overwrite: true);
            return (destination, hash);
        }
        finally { File.Delete(pending); }
    }

    private static async Task<bool> MatchesAsync(string path, string hash, long size, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) { return false; }
        await using var input = File.OpenRead(path);
        return string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)), hash, StringComparison.Ordinal);
    }
}

using System.ComponentModel;
using System.Diagnostics;

namespace GhostShell.Infrastructure;

public sealed class ProcessConnectionCommandRunner : IConnectionCommandRunner
{
    private const int MaximumCapturedCharacters = 16 * 1024;

    public async ValueTask<ConnectionProbeResult> RunAsync(
        ConnectionProbeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (cancellationToken.IsCancellationRequested)
        {
            return new ConnectionProbeResult(
                ConnectionProbeOutcome.Cancelled,
                null,
                string.Empty);
        }

        using var process = CreateProcess(command);
        try
        {
            if (!process.Start())
            {
                return StartFailed(ConnectionProbeStartFailure.Unknown);
            }
        }
        catch (Win32Exception exception)
        {
            return StartFailed(exception.NativeErrorCode switch
            {
                2 or 3 => ConnectionProbeStartFailure.NotFound,
                5 or 13 => ConnectionProbeStartFailure.PermissionDenied,
                _ => ConnectionProbeStartFailure.Unknown,
            });
        }
        catch (FileNotFoundException)
        {
            return StartFailed(ConnectionProbeStartFailure.NotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return StartFailed(ConnectionProbeStartFailure.PermissionDenied);
        }

        using var timeout = new CancellationTokenSource(command.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, linked.Token);
        var stdoutTask = DrainAsync(process.StandardOutput, linked.Token);

        try
        {
            await Task.WhenAll(
                    process.WaitForExitAsync(linked.Token),
                    stderrTask,
                    stdoutTask)
                .ConfigureAwait(false);
            return new ConnectionProbeResult(
                ConnectionProbeOutcome.Exited,
                process.ExitCode,
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            // Cancellation is not complete until this owned child is reaped.
            // Snapshot callers must not remove a destination while cp can still
            // write it. Keep cleanup bounded even if termination was denied.
            using var reapTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(reapTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                throw new IOException("The owned process did not exit within its cleanup deadline.", exception);
            }
            await AwaitDrainAfterCancellationAsync(stderrTask, stdoutTask).ConfigureAwait(false);
            var outcome = cancellationToken.IsCancellationRequested
                ? ConnectionProbeOutcome.Cancelled
                : ConnectionProbeOutcome.TimedOut;
            return new ConnectionProbeResult(outcome, null, string.Empty);
        }
    }

    private static Process CreateProcess(ConnectionProbeCommand command)
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
        CancellationToken cancellationToken)
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

            var remaining = result.Length - written;
            if (remaining > 0)
            {
                var copy = Math.Min(read, remaining);
                scratch.AsSpan(0, copy).CopyTo(result.AsSpan(written));
                written += copy;
            }
        }
    }

    private static async Task DrainAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
        {
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
            // Forced termination already has a typed cancellation/timeout outcome.
        }
        catch (IOException)
        {
            // A closed redirected stream is expected while the process tree is being torn down.
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
            // The process exited between the HasExited check and the kill request.
        }
        catch (Win32Exception)
        {
            // The operating system already completed or denied teardown; the probe result remains typed.
        }
    }

    private static ConnectionProbeResult StartFailed(ConnectionProbeStartFailure failure) =>
        new(ConnectionProbeOutcome.StartFailed, null, string.Empty, failure);
}

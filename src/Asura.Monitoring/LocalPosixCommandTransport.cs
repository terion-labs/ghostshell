using System.ComponentModel;
using System.Diagnostics;

namespace Asura.Monitoring;

/// <summary>
/// Local implementation of the monitor command transport. It never invokes a
/// shell, and it kills the complete child tree on timeout or cancellation.
/// </summary>
public sealed class LocalPosixCommandTransport : IPosixCommandTransport
{
    public async ValueTask<PosixCommandResult> ExecuteAsync(
        PosixCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        using var process = CreateProcess(command);
        try
        {
            // Process.Start performs fork/exec synchronously on Unix. Running it
            // on the UI thread can stall every composited panel while the
            // runtime and allocator prepare the child process.
            var started = await Task.Run(process.Start, cancellationToken)
                .ConfigureAwait(false);
            if (!started)
            {
                return StartFailed();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception exception) when (exception is
            Win32Exception or
            FileNotFoundException or
            UnauthorizedAccessException)
        {
            return StartFailed();
        }

        using var timeout = new CancellationTokenSource(command.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var stdout = ReadBoundedAsync(
            process.StandardOutput,
            command.MaximumOutputCharacters,
            linked.Token);
        var stderr = DrainAsync(process.StandardError, linked.Token);
        try
        {
            await Task.WhenAll(
                    process.WaitForExitAsync(linked.Token),
                    stdout,
                    stderr)
                .ConfigureAwait(false);
            return new PosixCommandResult(
                PosixCommandOutcome.Exited,
                process.ExitCode,
                await stdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitDrainAfterCancellationAsync(stdout, stderr).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? Cancelled()
                : new PosixCommandResult(
                    PosixCommandOutcome.TimedOut,
                    null,
                    string.Empty);
        }
    }

    private static Process CreateProcess(PosixCommand command)
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
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var result = new char[maximumCharacters];
        var scratch = new char[4096];
        var written = 0;
        while (true)
        {
            var read = await reader
                .ReadAsync(scratch, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return new string(result, 0, written);
            }

            var copy = Math.Min(read, maximumCharacters - written);
            if (copy > 0)
            {
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
        while (await reader
                   .ReadAsync(buffer, cancellationToken)
                   .ConfigureAwait(false) > 0)
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
            // Cancellation already has a typed command outcome.
        }
        catch (IOException)
        {
            // Redirected streams can close while the child tree is terminated.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process either exited concurrently or the host denied teardown.
        }
    }

    private static PosixCommandResult StartFailed() =>
        new(PosixCommandOutcome.StartFailed, null, string.Empty);

    private static PosixCommandResult Cancelled() =>
        new(PosixCommandOutcome.Cancelled, null, string.Empty);
}

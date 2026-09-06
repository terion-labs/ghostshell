using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GhostShell.Application;

namespace GhostShell.Infrastructure;

internal sealed record WorkspaceIsolationCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal interface IWorkspaceIsolationCommandRunner
{
    ValueTask<WorkspaceIsolationCommandResult> RunAsync(
        WorkspaceProcessLaunch launch,
        ReadOnlyMemory<byte> standardInput,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executes a structured isolate launch while keeping secret input out of process arguments
/// and draining bounded output so a noisy VPN client cannot deadlock its parent process.
/// </summary>
internal sealed class WorkspaceIsolationCommandRunner : IWorkspaceIsolationCommandRunner
{
    private const int MaximumCapturedCharacters = 64 * 1024;

    public async ValueTask<WorkspaceIsolationCommandResult> RunAsync(
        WorkspaceProcessLaunch launch,
        ReadOnlyMemory<byte> standardInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = CreateStartInfo(launch) };
        try
        {
            if (!process.Start())
            {
                throw new IOException("The workspace isolation command could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new IOException("The workspace isolation command could not be started.", exception);
        }

        var outputTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var errorTask = ReadBoundedAsync(process.StandardError, cancellationToken);
        var inputTask = WriteInputAsync(process.StandardInput, standardInput, cancellationToken);

        try
        {
            await Task.WhenAll(
                    process.WaitForExitAsync(cancellationToken),
                    inputTask,
                    outputTask,
                    errorTask)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new WorkspaceIsolationCommandResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private static ProcessStartInfo CreateStartInfo(WorkspaceProcessLaunch launch)
    {
        var start = new ProcessStartInfo
        {
            FileName = launch.Executable,
            WorkingDirectory = launch.HostWorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in launch.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment)
        {
            start.Environment[name] = value;
        }

        return start;
    }

    private static async Task WriteInputAsync(
        StreamWriter writer,
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!input.IsEmpty)
            {
                await writer.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
                await writer.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.Close();
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(MaximumCapturedCharacters);
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }

            var remaining = MaximumCapturedCharacters - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }
}

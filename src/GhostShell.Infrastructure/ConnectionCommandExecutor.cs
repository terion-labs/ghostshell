using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Converts a connection's prepared terminal launch into a one-shot command.
/// This deliberately reuses the connection runtime so credential brokerage and
/// private host-key bindings are identical to interactive terminal sessions.
/// </summary>
public sealed class ConnectionCommandExecutor(
    IConnectionRuntime connectionRuntime,
    IConnectionExecutableLocator executableLocator)
    : IConnectionCommandExecutor
{
    private const int SshControlPersistSeconds = 15;
    private static readonly string SshControlInstance =
        RandomNumberGenerator.GetHexString(4, lowercase: true);

    public async ValueTask<ConnectionCommandResult> ExecuteAsync(
        ConnectionCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        var commandPlan = await PlanCommandAsync(
                request.Connection,
                request.Executable,
                request.Arguments,
                cancellationToken)
            .ConfigureAwait(false);
        if (commandPlan is null)
        {
            return new ConnectionCommandResult(
                ConnectionCommandOutcome.ConnectionFailed,
                null,
                string.Empty);
        }

        var start = CreateStartInfo(
            commandPlan.Value.Launch,
            request,
            executableLocator,
            commandPlan.Value.UsesRuntimeExecutable);
        using var process = new Process { StartInfo = start };
        try
        {
            // Unix process creation performs fork/exec synchronously. Connection
            // commands include periodic monitor samples, so never perform that
            // work on a caller that may be Avalonia's main thread.
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
            Win32Exception or FileNotFoundException or UnauthorizedAccessException)
        {
            return StartFailed();
        }

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var stdout = ReadBoundedAsync(
            process.StandardOutput,
            request.MaximumOutputCharacters,
            linked.Token);
        var stderr = ReadBoundedAsync(
            process.StandardError,
            request.MaximumOutputCharacters,
            linked.Token);
        try
        {
            await Task.WhenAll(
                    process.WaitForExitAsync(linked.Token),
                    stdout,
                    stderr)
                .ConfigureAwait(false);
            var standardOutput = await stdout.ConfigureAwait(false);
            var standardError = await stderr.ConfigureAwait(false);
            return new ConnectionCommandResult(
                ConnectionCommandOutcome.Exited,
                process.ExitCode,
                standardOutput.Text,
                standardError.Text,
                standardOutput.Truncated || standardError.Truncated);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitDrainAsync(stdout, stderr).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? Cancelled()
                : new ConnectionCommandResult(
                    ConnectionCommandOutcome.TimedOut,
                    null,
                    string.Empty);
        }
    }

    public async ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(
        ConnectionBinaryCommand request,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteStreamingAsync<BinaryOutput>(
            request,
            (stream, token) => new ValueTask<BinaryOutput>(ReadBoundedBytesAsync(
                stream,
                request.MaximumOutputBytes,
                token)),
            cancellationToken).ConfigureAwait(false);
        var bytes = result.Value is { } output
            ? output.Bytes
            : ReadOnlyMemory<byte>.Empty;
        return new ConnectionBinaryCommandResult(
            result.Outcome,
            result.ExitCode,
            bytes,
            result.StandardError,
            result.Value?.Truncated == true);
    }

    public async ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(
        ConnectionBinaryCommand request,
        Func<Stream, CancellationToken, ValueTask<T>> consumeOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(consumeOutput);
        if (cancellationToken.IsCancellationRequested)
        {
            return StreamingCancelled<T>();
        }

        var commandPlan = await PlanCommandAsync(
                request.Connection,
                request.Executable,
                request.Arguments,
                cancellationToken)
            .ConfigureAwait(false);
        if (commandPlan is null)
        {
            return new ConnectionStreamingCommandResult<T>(
                ConnectionCommandOutcome.ConnectionFailed,
                null,
                default);
        }

        var start = CreateStartInfo(
            commandPlan.Value.Launch,
            request,
            executableLocator,
            commandPlan.Value.UsesRuntimeExecutable);
        using var process = new Process { StartInfo = start };
        try
        {
            var started = await Task.Run(process.Start, cancellationToken)
                .ConfigureAwait(false);
            if (!started)
            {
                return StreamingStartFailed<T>();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StreamingCancelled<T>();
        }
        catch (Exception exception) when (exception is
            Win32Exception or FileNotFoundException or UnauthorizedAccessException)
        {
            return StreamingStartFailed<T>();
        }

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var stdout = consumeOutput(process.StandardOutput.BaseStream, linked.Token).AsTask();
        var stderr = ReadBoundedAsync(process.StandardError, 64 * 1024, linked.Token);
        var exit = process.WaitForExitAsync(linked.Token);
        try
        {
            await Task.WhenAll(exit, stdout, stderr).ConfigureAwait(false);
            return new ConnectionStreamingCommandResult<T>(
                ConnectionCommandOutcome.Exited,
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                (await stderr.ConfigureAwait(false)).Text);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitDrainAsync(exit, stdout, stderr).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? StreamingCancelled<T>()
                : new ConnectionStreamingCommandResult<T>(
                    ConnectionCommandOutcome.TimedOut,
                    null,
                    default);
        }
        catch
        {
            linked.Cancel();
            TryKill(process);
            await AwaitDrainAsync(exit, stderr).ConfigureAwait(false);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        TerminalLaunchRequest launch,
        ConnectionCommand request,
        IConnectionExecutableLocator executableLocator,
        bool usesRuntimeExecutable)
    {
        var start = new ProcessStartInfo
        {
            FileName = !usesRuntimeExecutable
                && request.Connection.ConnectionKind == ConnectionKind.Local
                ? executableLocator.Find(request.Executable) ?? request.Executable
                : launch.Executable
                    ?? throw new InvalidOperationException("The connection plan has no executable."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in usesRuntimeExecutable
                     ? launch.Arguments
                     : CommandArguments(launch, request))
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment)
        {
            start.Environment[name] = value;
        }

        return start;
    }

    private static ProcessStartInfo CreateStartInfo(
        TerminalLaunchRequest launch,
        ConnectionBinaryCommand request,
        IConnectionExecutableLocator executableLocator,
        bool usesRuntimeExecutable)
    {
        var start = new ProcessStartInfo
        {
            FileName = !usesRuntimeExecutable
                && request.Connection.ConnectionKind == ConnectionKind.Local
                ? executableLocator.Find(request.Executable) ?? request.Executable
                : launch.Executable
                    ?? throw new InvalidOperationException("The connection plan has no executable."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in usesRuntimeExecutable
                     ? launch.Arguments
                     : CommandArguments(launch, request))
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment)
        {
            start.Environment[name] = value;
        }

        return start;
    }

    private async ValueTask<CommandLaunch?> PlanCommandAsync(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (connectionRuntime is IConnectionCommandRuntime commandRuntime)
        {
            var command = await commandRuntime.PlanCommandAsync(
                    connection,
                    executable,
                    arguments,
                    cancellationToken)
                .ConfigureAwait(false);
            return command is ConnectionRuntimeResult<TerminalLaunchRequest>.Success success
                ? new CommandLaunch(success.Value, UsesRuntimeExecutable: true)
                : null;
        }

        var plan = await connectionRuntime.PlanOpenAsync(
                connection,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        return plan is ConnectionRuntimeResult<ConnectionOpenPlan>.Success successPlan
            ? new CommandLaunch(successPlan.Value.Launch, UsesRuntimeExecutable: false)
            : null;
    }

    private static IReadOnlyList<string> CommandArguments(
        TerminalLaunchRequest launch,
        ConnectionCommand request) =>
        request.Connection.ConnectionKind switch
        {
            ConnectionKind.Local =>
                request.Arguments,
            ConnectionKind.Ssh =>
                SshArguments(
                    launch.Arguments,
                    request,
                    SshControlPath(request.Connection, ProxyIdentity(launch))),
            ConnectionKind.Docker =>
                DockerArguments(launch.Arguments, request),
            ConnectionKind.Wsl =>
                [.. launch.Arguments, "--exec", request.Executable, .. request.Arguments],
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Connection.ConnectionKind,
                "The connection kind cannot execute structured commands."),
        };

    private readonly record struct CommandLaunch(
        TerminalLaunchRequest Launch,
        bool UsesRuntimeExecutable);

    private static IReadOnlyList<string> CommandArguments(
        TerminalLaunchRequest launch,
        ConnectionBinaryCommand request) =>
        request.Connection.ConnectionKind switch
        {
            ConnectionKind.Local =>
                request.Arguments,
            ConnectionKind.Ssh =>
                SshArgumentsCore(
                    launch.Arguments,
                    request.Executable,
                    request.Arguments,
                    SshControlPath(request.Connection, ProxyIdentity(launch))),
            ConnectionKind.Docker =>
                DockerArgumentsCore(launch.Arguments, request.Executable, request.Arguments),
            ConnectionKind.Wsl =>
                [.. launch.Arguments, "--exec", request.Executable, .. request.Arguments],
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Connection.ConnectionKind,
                "The connection kind cannot execute structured commands."),
        };

    internal static IReadOnlyList<string> SshArguments(
        IReadOnlyList<string> launchArguments,
        ConnectionCommand request,
        string? controlPath) =>
        SshArgumentsCore(
            launchArguments,
            request.Executable,
            request.Arguments,
            controlPath);

    private static IReadOnlyList<string> SshArgumentsCore(
        IReadOnlyList<string> launchArguments,
        string executable,
        IReadOnlyList<string> commandArguments,
        string? controlPath)
    {
        var boundary = -1;
        for (var index = 0; index < launchArguments.Count; index++)
        {
            if (string.Equals(launchArguments[index], "--", StringComparison.Ordinal))
            {
                boundary = index;
                break;
            }
        }
        if (boundary < 0 || boundary + 1 >= launchArguments.Count)
        {
            throw new InvalidOperationException("The SSH connection plan is malformed.");
        }

        var arguments = launchArguments
            .Take(boundary)
            .Where(argument => !string.Equals(argument, "-tt", StringComparison.Ordinal))
            .ToList();
        if (controlPath is not null)
        {
            // A monitor samples every two seconds. OpenSSH multiplexing keeps those bounded
            // commands on one authenticated transport while preserving process isolation and
            // automatic reconnection when the master connection disappears.
            arguments.AddRange(
            [
                "-o",
                "ControlMaster=auto",
                "-o",
                $"ControlPersist={SshControlPersistSeconds}",
                "-o",
                $"ControlPath={controlPath}",
            ]);
        }

        arguments.Add(launchArguments[boundary]);
        arguments.Add(launchArguments[boundary + 1]);
        var command = new[] { executable }
            .Concat(commandArguments)
            .Select(QuotePosixShellWord);
        arguments.Add(string.Join(' ', command));
        return Array.AsReadOnly(arguments.ToArray());
    }

    private static string? ProxyIdentity(TerminalLaunchRequest launch) =>
        launch.Environment.TryGetValue("ALL_PROXY", out var proxy) ? proxy : null;

    internal static string? SshControlPath(ConnectionProfile connection, string? routeIdentity = null)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        var identity = new StringBuilder(connection.Id.Value)
            .Append('\0')
            .Append(connection.HostKeyPolicy);
        if (connection.Endpoint is ConnectionEndpoint.Ssh endpoint)
        {
            identity
                .Append('\0')
                .Append(endpoint.Host)
                .Append('\0')
                .Append(endpoint.Port)
                .Append('\0')
                .Append(endpoint.Username);
        }

        AppendAuthenticationIdentity(identity, connection.Authentication);
        // OpenSSH's %C excludes ProxyCommand. A master must never be shared
        // between workspace brokers, even when their SSH profiles are identical.
        identity.Append('\0').Append(routeIdentity);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()));
        var profileIdentity = Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
        return $"/tmp/ghostshell-{Environment.ProcessId}-{SshControlInstance}-{profileIdentity}-%C";
    }

    private static void AppendAuthenticationIdentity(
        StringBuilder identity,
        ConnectionAuthentication authentication)
    {
        identity.Append('\0').Append(authentication.GetType().Name);
        switch (authentication)
        {
            case ConnectionAuthentication.Password password:
                identity.Append('\0').Append(password.PasswordSecret.Value);
                break;
            case ConnectionAuthentication.PrivateKey privateKey:
                identity
                    .Append('\0')
                    .Append(privateKey.PrivateKeySecret.Value)
                    .Append('\0')
                    .Append(privateKey.PassphraseSecret?.Value);
                break;
        }
    }

    private static IReadOnlyList<string> DockerArguments(
        IReadOnlyList<string> launchArguments,
        ConnectionCommand request) =>
        DockerArgumentsCore(launchArguments, request.Executable, request.Arguments);

    private static IReadOnlyList<string> DockerArgumentsCore(
        IReadOnlyList<string> launchArguments,
        string executable,
        IReadOnlyList<string> commandArguments)
    {
        var arguments = launchArguments
            .Where(argument => argument is not "--interactive" and not "--tty")
            .ToList();
        if (arguments.Count == 0 || !string.Equals(arguments[^1], "/bin/sh", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Docker connection plan is malformed.");
        }

        arguments.RemoveAt(arguments.Count - 1);
        arguments.Add(executable);
        arguments.AddRange(commandArguments);
        return Array.AsReadOnly(arguments.ToArray());
    }

    private static string QuotePosixShellWord(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static async Task<BoundedText> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(maximumCharacters, 64 * 1024));
        var scratch = new char[4096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new BoundedText(result.ToString(), truncated);
            }

            var copy = Math.Min(read, maximumCharacters - result.Length);
            if (copy > 0)
            {
                result.Append(scratch, 0, copy);
            }

            truncated |= copy < read;
        }
    }

    private static async Task<BinaryOutput> ReadBoundedBytesAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 1024 * 1024));
        var scratch = new byte[64 * 1024];
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new BinaryOutput(output.ToArray(), truncated);
            }

            var remaining = maximumBytes - checked((int)output.Length);
            var copy = Math.Min(read, Math.Max(remaining, 0));
            if (copy > 0)
            {
                await output.WriteAsync(scratch.AsMemory(0, copy), cancellationToken)
                    .ConfigureAwait(false);
            }

            truncated |= copy < read;
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

    private static async Task AwaitDrainAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
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
        }
    }

    private static ConnectionCommandResult StartFailed() =>
        new(ConnectionCommandOutcome.StartFailed, null, string.Empty);

    private static ConnectionCommandResult Cancelled() =>
        new(ConnectionCommandOutcome.Cancelled, null, string.Empty);

    private static ConnectionStreamingCommandResult<T> StreamingStartFailed<T>() =>
        new(ConnectionCommandOutcome.StartFailed, null, default);

    private static ConnectionStreamingCommandResult<T> StreamingCancelled<T>() =>
        new(ConnectionCommandOutcome.Cancelled, null, default);

    private sealed record BinaryOutput(byte[] Bytes, bool Truncated);

    private sealed record BoundedText(string Text, bool Truncated);
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Asura.Desktop;

/// <summary>
/// Git supplies an arbitrary SSH destination. Resolve its SSH alias first, then
/// encode the effective hostname into a fixed ProxyCommand instead of exposing
/// OpenSSH's unescaped %h substitution to a shell.
/// </summary>
internal static class WorkspaceSshCommand
{
    internal const string Switch = "--asura-workspace-ssh";

    public static bool IsInvocation(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && string.Equals(arguments[0], Switch, StringComparison.Ordinal);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!IsInvocation(arguments)
            || !Uri.TryCreate(Environment.GetEnvironmentVariable("ALL_PROXY"), UriKind.Absolute, out var proxy)
            || proxy.Scheme is not ("socks5" or "http") || !proxy.IsLoopback
            || string.IsNullOrEmpty(proxy.UserInfo)
            || Environment.ProcessPath is not { } executable)
        {
            return 2;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var terminate = RegisterSignal(PosixSignal.SIGTERM, lifetime);
        using var interrupt = RegisterSignal(PosixSignal.SIGINT, lifetime);
        using var hangup = RegisterSignal(PosixSignal.SIGHUP, lifetime);
        cancellationToken = lifetime.Token;

        var sshArguments = arguments.Skip(1).ToArray();
        var resolution = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        resolution.ArgumentList.Add("-G");
        resolution.ArgumentList.Add("-o");
        resolution.ArgumentList.Add("ProxyCommand=none");
        resolution.ArgumentList.Add("-o");
        resolution.ArgumentList.Add("CanonicalizeHostname=no");
        foreach (var argument in sshArguments)
        {
            resolution.ArgumentList.Add(argument);
        }

        using var resolver = new Process { StartInfo = resolution };
        resolver.Start();
        string? host = null;
        var port = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (await resolver.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("hostname ", StringComparison.Ordinal))
                {
                    host = line[9..];
                }
                else if (line.StartsWith("port ", StringComparison.Ordinal))
                {
                    _ = int.TryParse(line.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out port);
                }
            }
            await resolver.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!resolver.HasExited)
            {
                resolver.Kill(entireProcessTree: true);
            }
            return 2;
        }

        if (resolver.ExitCode != 0 || string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            return 2;
        }

        var start = new ProcessStartInfo("ssh") { UseShellExecute = false };
        foreach (var argument in RoutedArguments(executable, proxy.Port, host, port, sshArguments))
        {
            start.ArgumentList.Add(argument);
        }
        using var ssh = new Process { StartInfo = start };
        ssh.Start();
        try
        {
            await ssh.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return ssh.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!ssh.HasExited)
            {
                ssh.Kill(entireProcessTree: true);
            }
            return 2;
        }
    }

    internal static IReadOnlyList<string> RoutedArguments(
        string executable,
        int proxyPort,
        string host,
        int port,
        IReadOnlyList<string> arguments)
    {
        var quotedExecutable = OperatingSystem.IsWindows() ? $"\"{executable}\""
            : $"'{executable.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
        var encodedHost = Convert.ToBase64String(Encoding.UTF8.GetBytes(host));
        return
        [
            "-o", $"ProxyCommand={quotedExecutable} --asura-workspace-socks-connect {proxyPort} {encodedHost} {port}",
            // An ambient master could have been opened outside this workspace.
            "-o", "ControlPath=none",
            "-o", "CanonicalizeHostname=no",
            "-o", "VerifyHostKeyDNS=no",
            .. arguments,
        ];
    }

    private static PosixSignalRegistration? RegisterSignal(PosixSignal signal, CancellationTokenSource lifetime) =>
        OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            lifetime.Cancel();
        });
}

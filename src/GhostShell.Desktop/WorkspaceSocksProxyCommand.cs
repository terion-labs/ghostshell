using System.Globalization;
using System.Text;

namespace GhostShell.Desktop;

internal static class WorkspaceSocksProxyCommand
{
    private const string Switch = "--ghostshell-workspace-socks-connect";

    public static bool IsInvocation(IReadOnlyList<string> arguments) =>
        arguments.Count == 4
        && string.Equals(arguments[0], Switch, StringComparison.Ordinal);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!IsInvocation(arguments)
            || !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var proxyPort)
            || !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var targetPort))
        {
            return 2;
        }

        string host;
        try
        {
            host = Encoding.UTF8.GetString(Convert.FromBase64String(arguments[2]));
        }
        catch (FormatException)
        {
            return 2;
        }

        var proxy = Environment.GetEnvironmentVariable("ALL_PROXY");
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri)
            || proxyUri.Port != proxyPort
            || string.IsNullOrEmpty(proxyUri.UserInfo))
        {
            return 2;
        }

        var userInfo = proxyUri.UserInfo.Split(':', 2);
        if (userInfo.Length != 2)
        {
            return 2;
        }

        var credentials = new GhostShell.Application.WorkspaceNetworkProxyCredentials(
            Uri.UnescapeDataString(userInfo[0]),
            Uri.UnescapeDataString(userInfo[1]));

        await using var stream = await WorkspaceSocksClient
            .ConnectAsync(
                proxyPort,
                credentials,
                host,
                targetPort,
                cancellationToken)
            .ConfigureAwait(false);
        var standardInput = Console.OpenStandardInput();
        var standardOutput = Console.OpenStandardOutput();
        using var uploadFinished = new ManualResetEvent(false);
        using var downloadFinished = new ManualResetEvent(false);
        var upload = StartCopyThread(
            standardInput,
            stream,
            uploadFinished,
            "GhostShell SSH proxy upload");
        var download = StartCopyThread(
            stream,
            standardOutput,
            downloadFinished,
            "GhostShell SSH proxy download");
        _ = WaitHandle.WaitAny([uploadFinished, downloadFinished]);
        await stream.DisposeAsync().ConfigureAwait(false);
        standardInput.Dispose();
        upload.Join();
        download.Join();
        return 0;
    }

    private static Thread StartCopyThread(
        Stream source,
        Stream destination,
        EventWaitHandle finished,
        string name)
    {
        var thread = new Thread(() =>
        {
            try
            {
                source.CopyTo(destination);
                destination.Flush();
            }
            catch (Exception exception) when (exception is
                IOException or ObjectDisposedException)
            {
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return thread;
    }
}

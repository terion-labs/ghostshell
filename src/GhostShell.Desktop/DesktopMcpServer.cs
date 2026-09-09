using System.Globalization;
using System.Security.Cryptography;
using GhostShell.Mcp.Server;

namespace GhostShell.Desktop;

/// <summary>Environment opt-in, with a profile-local token that is never logged.</summary>
internal static class DesktopMcpServer
{
    public static async Task StartIfEnabledAsync(
        WorkspaceMcpServer server, DesktopProfileConfiguration profile,
        CancellationToken cancellationToken)
    {
        var configuredPort = Environment.GetEnvironmentVariable("GHOSTSHELL_MCP_PORT");
        if (string.IsNullOrEmpty(configuredPort))
        {
            return;
        }

        if (!int.TryParse(configuredPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1024 or > 65535)
        {
            throw new InvalidOperationException("GHOSTSHELL_MCP_PORT must be a port from 1024 to 65535.");
        }

        var path = Path.Combine(profile.Data.DataDirectory, "mcp-token");
        Directory.CreateDirectory(profile.Data.DataDirectory);
        if (!File.Exists(path))
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using var stream = new FileStream(path, options);
            var bytes = System.Text.Encoding.UTF8.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        var info = new FileInfo(path);
        if (info.LinkTarget is not null || info.Length is < 32 or > 256)
        {
            throw new InvalidOperationException("The profile MCP token file is invalid.");
        }

        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
        {
            throw new InvalidOperationException("The profile MCP token file must be accessible only by its owner.");
        }

        var token = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        await server.StartAsync(port, token.Trim(), cancellationToken).ConfigureAwait(false);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Asura.Mcp.Server;

internal sealed class LocalMcpServerToken(string profileDirectory)
{
    private readonly string _path = Path.Combine(profileDirectory, "mcp-token");

    public async Task<string> ReadAsync()
    {
        if (!File.Exists(_path))
        {
            await WriteAsync(_path).ConfigureAwait(false);
        }

        var info = new FileInfo(_path);
        if (info.LinkTarget is not null || info.Length is < 32 or > 256)
        {
            throw new InvalidOperationException("The profile MCP token file is invalid.");
        }

        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(_path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
        {
            throw new InvalidOperationException("The profile MCP token file must be accessible only by its owner.");
        }

        var token = (await File.ReadAllTextAsync(_path).ConfigureAwait(false)).Trim();
        if (token.Length < 32 || token.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidOperationException("The profile MCP token is invalid.");
        }

        return token;
    }

    public async Task RotateAsync()
    {
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await WriteAsync(temporaryPath).ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private async Task WriteAsync(string path)
    {
        Directory.CreateDirectory(profileDirectory);
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
        var bytes = Encoding.UTF8.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        await stream.WriteAsync(bytes).ConfigureAwait(false);
    }
}

using System.Data.Common;
using System.Security.Cryptography;
using GhostShell.Application;
using GhostShell.Files;

namespace GhostShell.ConnectionBackend;

internal sealed partial class DatabaseConnectionMaterials
{
    internal async Task WriteAsync(Stream output, CancellationToken token)
    {
        foreach (var bytes in _contents)
        {
            try { await DatabaseOperationProtocol.WriteFrameAsync(output, bytes, token).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    internal static async Task<DatabaseWorkerConnection> ReceiveAsync(DatabaseWorkerConnection connection,
        DatabaseConnectionMaterial[] descriptors, Stream input, string scratchDirectory, CancellationToken token)
    {
        if (descriptors.Length is < 1 or > MaximumFiles) { throw new InvalidDataException("Invalid database material count."); }
        var total = 0;
        var builder = new DbConnectionStringBuilder { ConnectionString = connection.ConnectionString };
        var groups = new Dictionary<string, List<DatabaseConnectionMaterial>>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in descriptors)
        {
            if (material is null || string.IsNullOrEmpty(material.Option) || material.Option.Length > 64
                || (!IsFileOption(connection.DriverId, material.Option) && !IsDirectoryOption(connection.DriverId, material.Option))
                || material.Length is < 1 or > MaximumFileBytes || total > MaximumTotalBytes - material.Length
                || !builder.TryGetValue(material.Option, out var value) || !string.Equals(value as string, Placeholder, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid database certificate material.");
            }
            total += material.Length;
            var directory = IsDirectoryOption(connection.DriverId, material.Option);
            if (directory ? !OracleNames.Contains(material.Name, StringComparer.Ordinal)
                : string.IsNullOrEmpty(material.Name) || !material.Name.StartsWith("certificate", StringComparison.Ordinal)
                    || material.Name.Length > 27 || material.Name.Any(character => character != '.' && !char.IsAsciiLetterOrDigit(character)))
            {
                throw new InvalidDataException("Invalid database material filename.");
            }
            if (!groups.TryGetValue(material.Option, out var files)) { groups[material.Option] = files = []; }
            if ((!directory && files.Count > 0) || files.Any(file => string.Equals(file.Name, material.Name, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Duplicate database material.");
            }
            files.Add(material);
        }
        PrivateContentPathGuard.ValidatePrivateDirectory(scratchDirectory);
        var root = Path.Combine(scratchDirectory, "material");
        if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("Database material storage already exists."); }
        CreateMaterialDirectory(root);
        var index = 0;
        var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (option, _) in groups)
        {
            var directory = Path.Combine(root, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            CreateMaterialDirectory(directory);
            locations.Add(option, directory);
        }
        foreach (var material in descriptors)
        {
            var bytes = await DatabaseOperationProtocol.ReadFrameAsync(input, MaximumFileBytes, token).ConfigureAwait(false);
            try
            {
                if (bytes.Length != material.Length) { throw new InvalidDataException("Invalid database material length."); }
                if (material.Name.EndsWith(".ora", StringComparison.Ordinal)) { ValidateOracleConfiguration(bytes); }
                var path = Path.Combine(locations[material.Option], material.Name);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                await using var file = new FileStream(path, options);
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
                builder[material.Option] = IsDirectoryOption(connection.DriverId, material.Option) ? locations[material.Option] : path;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        RejectEmbeddedOraclePaths(connection.DriverId, builder);
        return connection with { ConnectionString = builder.ConnectionString };
    }

    private static void CreateMaterialDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); }
        else { Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        PrivateContentPathGuard.ValidatePrivateDirectory(path);
    }
}

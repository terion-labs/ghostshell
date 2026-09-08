using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Databases;

namespace GhostShell.ConnectionBackend;

internal sealed record DatabaseConnectionMaterial(string Option, string Name, int Length);

/// <summary>
/// Explicit host certificate options cross into a service VM as bounded bytes,
/// never mounts. Only those path options change; endpoint and TLS policy do not.
/// The operation's existing private scratch lease owns every imported file.
/// </summary>
internal sealed partial class DatabaseConnectionMaterials : IDisposable
{
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    internal const int MaximumTotalBytes = 16 * 1024 * 1024;
    internal const int MaximumFiles = 16;
    private static readonly string[] WalletNames = ["cwallet.sso", "ewallet.p12", "ewallet.pem"];
    private static readonly string[] OracleNames = [.. WalletNames, "tnsnames.ora", "sqlnet.ora"];
    private readonly List<byte[]> _contents = [];
    private readonly List<DatabaseConnectionMaterial> _descriptors = [];

    private DatabaseConnectionMaterials(DatabaseWorkerConnection connection) { Connection = connection; }
    internal DatabaseWorkerConnection Connection { get; private set; }
    internal DatabaseConnectionMaterial[] Descriptors => [.. _descriptors];

    internal static async Task<DatabaseConnectionMaterials> ReadHostAsync(DatabaseWorkerConnection connection, CancellationToken token)
    {
        var result = new DatabaseConnectionMaterials(connection);
        try
        {
            var driver = BuiltInDatabaseDrivers.All.Single(item => string.Equals(item.Descriptor.Id, connection.DriverId, StringComparison.Ordinal));
            var builder = new DbConnectionStringBuilder { ConnectionString = driver.NormalizeConnectionString(connection.ConnectionString) };
            foreach (var option in builder.Keys.Cast<string>().ToArray())
            {
                if (!IsFileOption(connection.DriverId, option) && !IsDirectoryOption(connection.DriverId, option)) { continue; }
                var path = Convert.ToString(builder[option], System.Globalization.CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(path)) { continue; }
                if (!Path.IsPathFullyQualified(path))
                {
                    throw new NotSupportedException("Service database certificate and wallet paths must be absolute host paths.");
                }
                if (IsDirectoryOption(connection.DriverId, option))
                {
                    if (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)
                    {
                        throw new NotSupportedException("The database wallet directory must be an existing, non-linked host directory.");
                    }
                    var allowed = string.Equals(NormalizeOption(option), "tnsadmin", StringComparison.Ordinal) ? OracleNames : WalletNames;
                    var count = result._contents.Count;
                    foreach (var name in allowed)
                    {
                        var file = Path.Combine(path, name);
                        if (File.Exists(file)) { await result.AddAsync(option, name, file, token).ConfigureAwait(false); }
                    }
                    if (result._contents.Count == count)
                    {
                        throw new NotSupportedException("The database wallet directory contains no supported wallet or Oracle configuration files.");
                    }
                }
                else
                {
                    var extension = Path.GetExtension(path);
                    if (extension.Length > 16 || extension.Any(character => character != '.' && !char.IsAsciiLetterOrDigit(character)))
                    {
                        throw new NotSupportedException("The database certificate filename has an unsupported extension.");
                    }
                    await result.AddAsync(option, "certificate" + extension, path, token).ConfigureAwait(false);
                }
                // The child's metadata needs no host filesystem path.
                builder[option] = Placeholder;
            }
            RejectEmbeddedOraclePaths(connection.DriverId, builder);
            result.Connection = connection with { ConnectionString = builder.ConnectionString };
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private async Task AddAsync(string option, string name, string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0
            || info.Length is < 1 or > MaximumFileBytes || _contents.Count >= MaximumFiles
            || _contents.Sum(static bytes => bytes.Length) > MaximumTotalBytes - info.Length)
        {
            throw new NotSupportedException("A database certificate or wallet file is missing, linked, empty, or exceeds the import limit.");
        }
        var bytes = new byte[checked((int)info.Length)];
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (file.Length != bytes.Length) { throw new IOException("The database certificate changed while being imported."); }
            if (name.EndsWith(".ora", StringComparison.Ordinal)) { ValidateOracleConfiguration(bytes); }
            _contents.Add(bytes);
            _descriptors.Add(new(option, name, bytes.Length));
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private const string Placeholder = "@ghostshell-private-material";
    private static string NormalizeOption(string value) => string.Concat(value.Where(static character => character is not (' ' or '_' or '-'))).ToLowerInvariant();

    private static bool IsFileOption(string driver, string option) => (driver, NormalizeOption(option)) switch
    {
        ("postgres" or "cockroach" or "redshift", "sslcertificate" or "sslkey" or "rootcertificate") => true,
        ("mysql" or "mariadb", "certificatefile" or "sslcert" or "sslkey" or "sslca" or "cacertificatefile") => true,
        ("sqlserver", "servercertificate") => true,
        _ => false,
    };

    private static bool IsDirectoryOption(string driver, string option) =>
        string.Equals(driver, "oracle", StringComparison.Ordinal) && NormalizeOption(option) is "walletlocation" or "tnsadmin";

    private static void RejectEmbeddedOraclePaths(string driver, DbConnectionStringBuilder builder)
    {
        if (!string.Equals(driver, "oracle", StringComparison.Ordinal)) { return; }
        foreach (var key in builder.Keys.Cast<string>())
        {
            if (NormalizeOption(key) is "tokenlocation")
            {
                throw new NotSupportedException("Service Oracle connections cannot import external token files. Use explicit supported credentials.");
            }
            if (NormalizeOption(key) is "datasource")
            {
                ValidateOracleConfiguration(Encoding.UTF8.GetBytes(Convert.ToString(builder[key], System.Globalization.CultureInfo.InvariantCulture)!));
            }
        }
    }

    private static void ValidateOracleConfiguration(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);
        // Includes, directory references, and substitution can escape the exact
        // imported set. Do not guess how to rewrite Oracle's nested grammar.
        string[] unsupported = ["IFILE", "DIRECTORY", "WALLET_LOCATION", "TNS_ADMIN", "TOKEN_LOCATION", "TRACE_FILE", "LOG_FILE", "$", "%"];
        if (unsupported.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException("Service Oracle configuration cannot contain includes, embedded wallet paths, file output paths, or environment substitutions. Set Wallet_Location/Tns_Admin explicitly and use self-contained configuration files.");
        }
    }

    public void Dispose()
    {
        foreach (var bytes in _contents) { CryptographicOperations.ZeroMemory(bytes); }
    }
}

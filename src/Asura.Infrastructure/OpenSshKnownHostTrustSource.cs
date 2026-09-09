using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Reads the current user's standard OpenSSH known-host files as an initial source of trust.
/// Asura still persists an exact per-connection pin before launching a verified session.
/// </summary>
internal sealed class OpenSshKnownHostTrustSource
{
    private const long MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumLineCharacters = 128 * 1024;
    private readonly IReadOnlyList<string> _filePaths;

    internal OpenSshKnownHostTrustSource(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        _filePaths = Array.AsReadOnly(filePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }

    internal static OpenSshKnownHostTrustSource CreateDefault()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return new OpenSshKnownHostTrustSource([]);
        }

        var sshDirectory = Path.Combine(userProfile, ".ssh");
        return new OpenSshKnownHostTrustSource([
            Path.Combine(sshDirectory, "known_hosts"),
            Path.Combine(sshDirectory, "known_hosts2"),
        ]);
    }

    internal async ValueTask<bool> ContainsAsync(
        ConnectionEndpoint.Ssh endpoint,
        SshHostKeyCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(candidate);
        var lookupHost = LookupHost(endpoint);
        var trusted = false;

        foreach (var path in _filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fileMatch = await InspectFileAsync(
                        path,
                        lookupHost,
                        candidate,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (fileMatch == OpenSshKnownHostEntryMatch.Revoked)
                {
                    return false;
                }

                trusted |= fileMatch == OpenSshKnownHostEntryMatch.Trusted;
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                InvalidDataException)
            {
                // The OpenSSH file is only an optional bootstrap source. Asura's own
                // per-connection trust store remains authoritative and fails closed separately.
            }
        }

        return trusted;
    }

    private static async ValueTask<OpenSshKnownHostEntryMatch> InspectFileAsync(
        string path,
        string lookupHost,
        SshHostKeyCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return OpenSshKnownHostEntryMatch.None;
        }

        if (new FileInfo(path).Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The OpenSSH known-host file is too large.");
        }

        var trusted = false;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > MaximumLineCharacters)
            {
                throw new InvalidDataException("The OpenSSH known-host file contains an oversized line.");
            }

            var lineMatch = OpenSshKnownHostEntry.Inspect(line, lookupHost, candidate);
            if (lineMatch == OpenSshKnownHostEntryMatch.Revoked)
            {
                return OpenSshKnownHostEntryMatch.Revoked;
            }

            trusted |= lineMatch == OpenSshKnownHostEntryMatch.Trusted;
        }

        return trusted
            ? OpenSshKnownHostEntryMatch.Trusted
            : OpenSshKnownHostEntryMatch.None;
    }

    private static string LookupHost(ConnectionEndpoint.Ssh endpoint)
    {
        var host = endpoint.Host;
        if (host.Length > 1 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return endpoint.Port == 22 ? host : $"[{host}]:{endpoint.Port}";
    }
}

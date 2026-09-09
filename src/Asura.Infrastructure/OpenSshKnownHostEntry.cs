using System.Security.Cryptography;
using System.Text;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// Matches one OpenSSH known-host entry without resolving names or widening endpoint identity.
/// </summary>
internal static class OpenSshKnownHostEntry
{
    internal static OpenSshKnownHostEntryMatch Inspect(
        string line,
        string lookupHost,
        SshHostKeyCandidate candidate)
    {
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3 || fields[0].StartsWith('#'))
        {
            return OpenSshKnownHostEntryMatch.None;
        }

        var markerOffset = fields[0].StartsWith('@') ? 1 : 0;
        if (fields.Length < markerOffset + 3
            || !HostListMatches(fields[markerOffset], lookupHost)
            || !string.Equals(
                fields[markerOffset + 1],
                candidate.Identity.Algorithm,
                StringComparison.Ordinal)
            || !string.Equals(
                fields[markerOffset + 2],
                candidate.PublicKeyBase64,
                StringComparison.Ordinal))
        {
            return OpenSshKnownHostEntryMatch.None;
        }

        return markerOffset == 0
            ? OpenSshKnownHostEntryMatch.Trusted
            : string.Equals(fields[0], "@revoked", StringComparison.OrdinalIgnoreCase)
                ? OpenSshKnownHostEntryMatch.Revoked
                : OpenSshKnownHostEntryMatch.None;
    }

    private static bool HostListMatches(string hostList, string lookupHost)
    {
        var positiveMatch = false;
        foreach (var rawPattern in hostList.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var negated = rawPattern[0] == '!';
            var pattern = negated ? rawPattern[1..] : rawPattern;
            if (!HostPatternMatches(pattern, lookupHost))
            {
                continue;
            }

            if (negated)
            {
                return false;
            }

            positiveMatch = true;
        }

        return positiveMatch;
    }

    private static bool HostPatternMatches(string pattern, string lookupHost) =>
        pattern.StartsWith("|1|", StringComparison.Ordinal)
            ? HashedHostMatches(pattern, lookupHost)
            : WildcardMatches(pattern, lookupHost);

    private static bool HashedHostMatches(string pattern, string lookupHost)
    {
        var fields = pattern.Split('|');
        if (fields.Length != 4 || fields[0].Length != 0 || !string.Equals(fields[1], "1", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(fields[2]);
            var expected = Convert.FromBase64String(fields[3]);
            var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(lookupHost));
            return expected.Length == actual.Length
                && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool WildcardMatches(string pattern, string value)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var wildcardIndex = -1;
        var wildcardValueIndex = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?'
                    || char.ToUpperInvariant(pattern[patternIndex])
                    == char.ToUpperInvariant(value[valueIndex])))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                wildcardIndex = patternIndex++;
                wildcardValueIndex = valueIndex;
                continue;
            }

            if (wildcardIndex < 0)
            {
                return false;
            }

            patternIndex = wildcardIndex + 1;
            valueIndex = ++wildcardValueIndex;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}

internal enum OpenSshKnownHostEntryMatch
{
    None,
    Trusted,
    Revoked,
}

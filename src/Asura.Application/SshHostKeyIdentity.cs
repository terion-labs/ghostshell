namespace Asura.Application;

/// <summary>Public SSH server identity metadata. Host keys are identity material, not credentials.</summary>
public sealed record SshHostKeyIdentity
{
    public SshHostKeyIdentity(string algorithm, string sha256Fingerprint)
    {
        Algorithm = RequirePrintable(algorithm, nameof(algorithm));
        Sha256Fingerprint = RequirePrintable(sha256Fingerprint, nameof(sha256Fingerprint));
        if (Algorithm.Any(character => character is <= ' ' or >= '\u007f'))
        {
            throw new ArgumentException(
                "An SSH host-key algorithm must be a single printable ASCII token.",
                nameof(algorithm));
        }

        const string prefix = "SHA256:";
        if (!Sha256Fingerprint.StartsWith(prefix, StringComparison.Ordinal)
            || Sha256Fingerprint.Length != prefix.Length + 43
            || Sha256Fingerprint[prefix.Length..].Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '+' or '/')))
        {
            throw new ArgumentException(
                "An SSH host-key fingerprint must use the SHA-256 representation.",
                nameof(sha256Fingerprint));
        }
    }

    public string Algorithm { get; }

    public string Sha256Fingerprint { get; }

    private static string RequirePrintable(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("An SSH host-key field must be bounded and printable.", parameterName);
        }

        return normalized;
    }
}

namespace GhostShell.Infrastructure;

internal static class VpnAuthenticationRejection
{
    // Match password-rejection messages from OpenConnect and our OpenVPN wrapper,
    // not generic TLS authentication, certificate, timeout or socket failures.
    public static bool IsReported(string diagnostic) =>
        diagnostic.Contains("AUTH_FAILED", StringComparison.Ordinal)
        || diagnostic.Contains("OpenVPN authentication failed", StringComparison.Ordinal)
        || diagnostic.Contains("Login failed.", StringComparison.Ordinal)
        || diagnostic.Split('\n').Any(line =>
            string.Equals(line.Trim(), "authentication failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(line.Trim(), "login failed", StringComparison.OrdinalIgnoreCase));
}

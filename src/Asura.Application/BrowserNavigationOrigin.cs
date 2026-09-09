using System.Globalization;

namespace Asura.Application;

/// <summary>
/// A host-selected top-level browser navigation boundary. It may constrain a
/// renderer operation to one canonical origin or explicitly allow every
/// address supported by <see cref="BrowserAddress"/>.
/// </summary>
public sealed record BrowserNavigationOrigin
{
    private BrowserNavigationOrigin(
        string scheme,
        string idnHost,
        int port,
        bool isBlank,
        bool isUnrestricted)
    {
        Scheme = scheme;
        IdnHost = idnHost;
        Port = port;
        IsBlank = isBlank;
        IsUnrestricted = isUnrestricted;
    }

    public static BrowserNavigationOrigin Unrestricted { get; } =
        new("*", string.Empty, port: -1, isBlank: false, isUnrestricted: true);

    public string Scheme { get; }

    public string IdnHost { get; }

    public int Port { get; }

    public bool IsBlank { get; }

    public bool IsUnrestricted { get; }

    /// <summary>
    /// Stable presentation and digest material for this navigation boundary.
    /// HTTP(S) ports are always explicit; unrestricted boundaries use '*'.
    /// </summary>
    public string CanonicalValue =>
        IsUnrestricted
            ? "*"
            : IsBlank
            ? BrowserAddress.Blank.ToString()
            : string.Concat(
                Scheme,
                "://",
                CanonicalHost(IdnHost),
                ":",
                Port.ToString(CultureInfo.InvariantCulture));

    public static BrowserNavigationOrigin FromAddress(BrowserAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var value = address.Value;
        if (value.AbsoluteUri.Equals(
                BrowserAddress.Blank.Value.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserNavigationOrigin(
                "about",
                string.Empty,
                port: -1,
                isBlank: true,
                isUnrestricted: false);
        }

        return new BrowserNavigationOrigin(
            value.Scheme.ToLowerInvariant(),
            value.IdnHost.ToLowerInvariant(),
            value.Port,
            isBlank: false,
            isUnrestricted: false);
    }

    public bool Allows(BrowserAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (IsUnrestricted)
        {
            return true;
        }

        var candidate = FromAddress(address);
        return IsBlank
            ? candidate.IsBlank
            : !candidate.IsBlank
              && string.Equals(
                  Scheme,
                  candidate.Scheme,
                  StringComparison.Ordinal)
              && string.Equals(
                  IdnHost,
                  candidate.IdnHost,
                  StringComparison.Ordinal)
              && Port == candidate.Port;
    }

    public override string ToString() => CanonicalValue;

    private static string CanonicalHost(string host) =>
        host.Contains(':', StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}

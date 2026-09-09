using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Asura.Application;

/// <summary>
/// An absolute address accepted by the desktop browser boundary.
/// </summary>
/// <remarks>
/// Embedded credentials are rejected so browser state, history, and audit
/// projections cannot accidentally carry URL user information.
/// </remarks>
public sealed record BrowserAddress
{
    public const int MaximumLength = 8_192;

    private static readonly Uri BlankUri = new("about:blank", UriKind.Absolute);

    [JsonConstructor]
    public BrowserAddress(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsSupported(value))
        {
            throw new ArgumentException(
                "A browser address must be an absolute HTTP(S) URL without embedded credentials, or about:blank.",
                nameof(value));
        }

        if (value.AbsoluteUri.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"A browser address cannot exceed {MaximumLength} characters.",
                nameof(value));
        }

        Value = value;
    }

    public Uri Value { get; }

    public static BrowserAddress Blank { get; } = new(BlankUri);

    /// <summary>
    /// The address of a local file the shell itself decided to show — a
    /// previewed page that has already been materialized.
    ///
    /// Deliberately not reachable through <see cref="TryParse"/>: navigation
    /// requested by a person or an agent arrives as a string, and a string must
    /// never be able to name a file on this machine. Only code holding a real
    /// path can build one of these.
    /// </summary>
    public static BrowserAddress ForLocalFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "A local page address needs a fully qualified path.",
                nameof(path));
        }

        var uri = new Uri(path, UriKind.Absolute);
        if (!uri.IsFile || !string.IsNullOrEmpty(uri.UserInfo)
            || !(string.IsNullOrEmpty(uri.Host)
                || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            // A UNC-style host would make "local file" mean a fetch from
            // somewhere else entirely.
            throw new ArgumentException(
                "A local page address must name a path on this machine.",
                nameof(path));
        }

        return new BrowserAddress(uri, localFile: true);
    }

    private BrowserAddress(Uri value, bool localFile)
    {
        IsLocalFile = localFile;
        Value = value;
    }

    /// <summary>
    /// A page carried as markup rather than fetched from anywhere: a previewed
    /// remote HTML file whose bytes are already in memory. The browser shows
    /// the markup directly, so the file is never written to this machine.
    ///
    /// Like <see cref="ForLocalFile"/>, deliberately unreachable from
    /// <see cref="TryParse"/>: only code already holding the content can build
    /// one, and the address it presents to history and audit is about:blank.
    /// </summary>
    public static BrowserAddress ForDocument(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        return new BrowserAddress(BlankUri, markup);
    }

    private BrowserAddress(Uri value, string document)
    {
        Value = value;
        Document = document;
    }

    /// <summary>
    /// The markup to show, when this address carries a page rather than naming
    /// one. Kept out of serialized state: a page's content is not part of
    /// where the browser has been.
    /// </summary>
    [JsonIgnore]
    public string? Document { get; }

    /// <summary>
    /// Whether this names a file on this machine that the shell itself chose to
    /// show. Only <see cref="ForLocalFile"/> can set it, so it marks an address
    /// the application built from a real path rather than one parsed from text.
    /// </summary>
    public bool IsLocalFile { get; }

    public static bool TryParse(
        string? text,
        [NotNullWhen(true)] out BrowserAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var candidate = text.Trim();
        if (candidate.Length > MaximumLength
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var value)
            || !IsSupported(value)
            || value.AbsoluteUri.Length > MaximumLength)
        {
            return false;
        }

        address = new BrowserAddress(value);
        return true;
    }

    public override string ToString() => Value.AbsoluteUri;

    private static bool IsSupported(Uri value)
    {
        if (!value.IsAbsoluteUri)
        {
            return false;
        }

        if (value.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || value.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(value.Host)
                && string.IsNullOrEmpty(value.UserInfo);
        }

        return value.AbsoluteUri.Equals(
            BlankUri.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);
    }
}

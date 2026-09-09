using System.Collections.Immutable;

namespace Asura.Application;

/// <summary>
/// Provider-neutral file identity exposed to presentation and future protocol clients.
/// Hierarchical paths and object keys remain distinct so callers never reinterpret one as the other.
/// </summary>
public sealed record FilePanelLocation
{
    public FilePanelLocation(
        string providerProfileId,
        string? authority,
        FilePanelAddress address,
        string? version = null)
    {
        ProviderProfileId = RequireProfileId(providerProfileId);
        Authority = ValidateAuthority(authority);
        Address = address ?? throw new ArgumentNullException(nameof(address));
        Version = ValidateVersion(version);
    }

    public string ProviderProfileId { get; }

    public string? Authority { get; }

    public FilePanelAddress Address { get; }

    public string? Version { get; }

    public FilePanelLocation Child(FilePanelPathSegment segment) =>
        Address is FilePanelAddress.Hierarchical hierarchical
            ? new FilePanelLocation(
                ProviderProfileId,
                Authority,
                new FilePanelAddress.Hierarchical(hierarchical.Path.Append(segment)),
                null)
            : throw new InvalidOperationException("Only hierarchical locations have path children.");

    public FilePanelLocation Parent =>
        Address is FilePanelAddress.Hierarchical hierarchical
            ? new FilePanelLocation(
                ProviderProfileId,
                Authority,
                new FilePanelAddress.Hierarchical(hierarchical.Path.Parent),
                null)
            : this;

    public FilePanelLocation WithVersion(string? version) =>
        new(ProviderProfileId, Authority, Address, version);

    /// <summary>
    /// Returns a bounded, non-recursive diagnostic identity. Record-generated formatting cannot be
    /// used because the computed <see cref="Parent"/> property would recursively format parents.
    /// </summary>
    public override string ToString()
    {
        var address = Address switch
        {
            FilePanelAddress.Hierarchical hierarchical => hierarchical.Path.IsRoot
                ? "/"
                : $"/{string.Join('/', hierarchical.Path.Segments)}",
            FilePanelAddress.ObjectKey objectKey => objectKey.Key,
            FilePanelAddress.ContainerRoot => "<container-root>",
            _ => "<unknown>",
        };
        var authority = Authority is null ? string.Empty : $"{Authority}:";
        var version = Version is null ? string.Empty : $"@{Version}";
        return $"{ProviderProfileId}:{authority}{address}{version}";
    }

    private static string RequireProfileId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException(
                "A file-provider profile ID may contain only ASCII letters, digits, '.', '_', and '-'.",
                nameof(value));
        }

        return value;
    }

    private static string? ValidateAuthority(string? value)
    {
        if (value is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 255 || value.Any(character =>
                character is '\0' or '/' or '\\' || char.IsControl(character)))
        {
            throw new ArgumentException(
                "A file authority must be an opaque name without path separators or control characters.",
                nameof(value));
        }

        return value;
    }

    private static string? ValidateVersion(string? value)
    {
        if (value is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 512 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A file version must be a bounded opaque value.", nameof(value));
        }

        return value;
    }
}

public abstract record FilePanelAddress
{
    private FilePanelAddress()
    {
    }

    public sealed record Hierarchical(FilePanelPath Path) : FilePanelAddress
    {
        public FilePanelPath Path { get; } = Path
            ?? throw new ArgumentNullException(nameof(Path));
    }

    public sealed record ObjectKey
        : FilePanelAddress
    {
        public ObjectKey(string key)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            Key = key;
        }

        public string Key { get; }
    }

    public sealed record ContainerRoot : FilePanelAddress;
}

public readonly record struct FilePanelPathSegment
{
    public FilePanelPathSegment(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value is "." or ".."
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A file path segment cannot be traversal, contain '/', or contain a null character.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed class FilePanelPath : IEquatable<FilePanelPath>
{
    private FilePanelPath(ImmutableArray<FilePanelPathSegment> segments) => Segments = segments;

    public static FilePanelPath Root { get; } = new([]);

    public ImmutableArray<FilePanelPathSegment> Segments { get; }

    public bool IsRoot => Segments.IsEmpty;

    public FilePanelPathSegment? Name => Segments.IsEmpty ? null : Segments[^1];

    public static FilePanelPath FromSegments(IEnumerable<FilePanelPathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var snapshot = segments.ToImmutableArray();
        return snapshot.IsEmpty ? Root : new FilePanelPath(snapshot);
    }

    public FilePanelPath Append(FilePanelPathSegment segment) => new(Segments.Add(segment));

    public FilePanelPath Parent => Segments.IsEmpty
        ? this
        : FromSegments(Segments.RemoveAt(Segments.Length - 1));

    public bool Equals(FilePanelPath? other) =>
        other is not null && Segments.SequenceEqual(other.Segments);

    public override bool Equals(object? obj) => obj is FilePanelPath other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in Segments)
        {
            hash.Add(segment);
        }

        return hash.ToHashCode();
    }
}

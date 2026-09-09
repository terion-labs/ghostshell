using System.Collections.Immutable;

namespace Asura.Files;

public sealed class FilePath : IEquatable<FilePath>
{
    private FilePath(ImmutableArray<FilePathSegment> segments) => Segments = segments;

    public static FilePath Root { get; } = new([]);

    public ImmutableArray<FilePathSegment> Segments { get; }

    public bool IsRoot => Segments.IsEmpty;

    public FilePathSegment? Name => Segments.IsEmpty ? null : Segments[^1];

    public static FilePath FromSegments(IEnumerable<FilePathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var materialized = segments.ToImmutableArray();
        return materialized.IsEmpty ? Root : new FilePath(materialized);
    }

    public FilePath Append(FilePathSegment segment) => new(Segments.Add(segment));

    public FilePath Parent => Segments.IsEmpty
        ? this
        : FromSegments(Segments.RemoveAt(Segments.Length - 1));

    public bool IsDescendantOf(FilePath candidateAncestor)
    {
        ArgumentNullException.ThrowIfNull(candidateAncestor);
        if (Segments.Length <= candidateAncestor.Segments.Length)
        {
            return false;
        }

        return Segments
            .Take(candidateAncestor.Segments.Length)
            .SequenceEqual(candidateAncestor.Segments);
    }

    public bool Equals(FilePath? other) =>
        other is not null && Segments.SequenceEqual(other.Segments);

    public override bool Equals(object? obj) => obj is FilePath other && Equals(other);

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

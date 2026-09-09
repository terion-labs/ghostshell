using System.Collections.ObjectModel;

namespace Asura.Application;

/// <summary>Current Kitty placement set and the image content it references.</summary>
public sealed record TerminalKittyGraphicsFrame
{
    private static readonly IReadOnlyList<TerminalKittyPlacement> NoPlacements =
        Array.AsReadOnly(Array.Empty<TerminalKittyPlacement>());
    private static readonly IReadOnlyDictionary<TerminalKittyImageKey, TerminalKittyImageContent> NoImages =
        new ReadOnlyDictionary<TerminalKittyImageKey, TerminalKittyImageContent>(
            new Dictionary<TerminalKittyImageKey, TerminalKittyImageContent>());

    public TerminalKittyGraphicsFrame(
        ulong Generation,
        IReadOnlyList<TerminalKittyPlacement>? Placements = null,
        IReadOnlyList<TerminalKittyImageContent>? Images = null)
    {
        var placements = SnapshotPlacements(Placements);
        var images = SnapshotImages(Images);
        if (Generation == 0 && (placements.Count != 0 || images.Count != 0))
        {
            throw new ArgumentException(
                "A populated Kitty graphics frame requires a non-zero storage generation.",
                nameof(Generation));
        }

        foreach (var placement in placements)
        {
            if (!images.ContainsKey(placement.Image))
            {
                throw new ArgumentException(
                    "Every Kitty placement must reference image content from the same frame.",
                    nameof(Placements));
            }
        }

        this.Generation = Generation;
        this.Placements = placements;
        this.Images = images;
    }

    /// <summary>The storage-wide mutation generation from libghostty-vt.</summary>
    public ulong Generation { get; }

    public IReadOnlyList<TerminalKittyPlacement> Placements { get; }

    public IReadOnlyDictionary<TerminalKittyImageKey, TerminalKittyImageContent> Images { get; }

    public static TerminalKittyGraphicsFrame Empty { get; } = new(0);

    private static IReadOnlyList<TerminalKittyPlacement> SnapshotPlacements(
        IReadOnlyList<TerminalKittyPlacement>? placements)
    {
        if (placements is null || placements.Count == 0)
        {
            return NoPlacements;
        }

        var snapshot = new TerminalKittyPlacement[placements.Count];
        for (var index = 0; index < placements.Count; index++)
        {
            snapshot[index] = placements[index]
                ?? throw new ArgumentException("Kitty placements cannot contain null values.", nameof(placements));
        }

        return new ReadOnlyCollection<TerminalKittyPlacement>(snapshot);
    }

    private static IReadOnlyDictionary<TerminalKittyImageKey, TerminalKittyImageContent> SnapshotImages(
        IReadOnlyList<TerminalKittyImageContent>? images)
    {
        if (images is null || images.Count == 0)
        {
            return NoImages;
        }

        var snapshot = new Dictionary<TerminalKittyImageKey, TerminalKittyImageContent>(images.Count);
        var generationsById = new Dictionary<uint, ulong>(images.Count);
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index]
                ?? throw new ArgumentException("Kitty image content cannot contain null values.", nameof(images));
            if (!snapshot.TryAdd(image.Key, image))
            {
                throw new ArgumentException("Kitty image keys must be unique within a frame.", nameof(images));
            }

            if (generationsById.TryGetValue(image.Key.ImageId, out var generation)
                && generation != image.Key.Generation)
            {
                throw new ArgumentException(
                    "A Kitty frame cannot contain multiple generations of one image ID.",
                    nameof(images));
            }

            generationsById[image.Key.ImageId] = image.Key.Generation;
        }

        return new ReadOnlyDictionary<TerminalKittyImageKey, TerminalKittyImageContent>(snapshot);
    }
}

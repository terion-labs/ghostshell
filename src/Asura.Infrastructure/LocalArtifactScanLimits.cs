using System.Runtime.InteropServices;

namespace Asura.Infrastructure;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct LocalArtifactScanLimits(
    int MaximumEntries,
    int MaximumDepth,
    long MaximumBytes)
{
    internal static LocalArtifactScanLimits Default { get; } = new(
        MaximumEntries: 4_096,
        MaximumDepth: 16,
        MaximumBytes: 8L * 1024 * 1024 * 1024);

    internal void Validate()
    {
        if (MaximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries));
        }

        if (MaximumDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDepth));
        }

        if (MaximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumBytes));
        }
    }
}

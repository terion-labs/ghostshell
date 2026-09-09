namespace Asura.Files;

public sealed record FileProviderCapabilities
{
    public FileProviderCapabilities(
        FileProviderCapability supported,
        FileNameComparison nameComparison,
        FileProviderLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if ((supported & ~KnownCapabilities) != FileProviderCapability.None)
        {
            throw new ArgumentOutOfRangeException(nameof(supported), supported, "Unknown file capability bits.");
        }

        Supported = supported;
        NameComparison = nameComparison;
        Limits = limits;
    }

    public static FileProviderCapability KnownCapabilities { get; } =
        Enum.GetValues<FileProviderCapability>().Aggregate(
            FileProviderCapability.None,
            (all, capability) => all | capability);

    public FileProviderCapability Supported { get; }

    public FileNameComparison NameComparison { get; }

    public FileProviderLimits Limits { get; }

    public bool Supports(FileProviderCapability capability) =>
        capability != FileProviderCapability.None && (Supported & capability) == capability;
}

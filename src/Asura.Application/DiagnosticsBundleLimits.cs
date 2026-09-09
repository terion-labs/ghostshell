namespace Asura.Application;

/// <summary>
/// Fixed export limits. They are intentionally not caller-configurable so every diagnostics path
/// has the same memory and disclosure ceiling.
/// </summary>
public static class DiagnosticsBundleLimits
{
    public const int MaximumArtifactCount = 32;
    public const int MaximumArtifactBytes = 1024 * 1024;
    public const int MaximumTotalArtifactBytes = 8 * 1024 * 1024;
    public const int MaximumArchiveBytes = 9 * 1024 * 1024;
    public const int MaximumRelativePathBytes = 180;
    public const int MaximumMetadataValueBytes = 256;
}

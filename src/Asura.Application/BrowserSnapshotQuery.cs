using System.Text;

namespace Asura.Application;

/// <summary>
/// Bounded query applied while Chromium's accessibility tree is projected.
/// Filtering before the snapshot cap prevents unrelated early page chrome
/// from hiding later matching or interactive elements.
/// </summary>
public sealed record BrowserSnapshotQuery
{
    public const int MaximumFilterBytes = 512;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public BrowserSnapshotQuery(
        bool interactiveOnly = false,
        string? filter = null,
        int? maximumDepth = null)
    {
        if (filter is not null)
        {
            if (string.IsNullOrWhiteSpace(filter)
                || !string.Equals(filter, filter.Trim(), StringComparison.Ordinal)
                || filter.Contains('\0', StringComparison.Ordinal)
                || GetByteCount(filter) > MaximumFilterBytes)
            {
                throw new ArgumentException(
                    $"A browser snapshot filter must be trimmed, non-empty, "
                    + $"NUL-free, and at most {MaximumFilterBytes} UTF-8 bytes.",
                    nameof(filter));
            }

            Filter = string.Concat(filter);
        }

        if (maximumDepth is < 0 or > BrowserSnapshotNode.MaximumDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        }

        InteractiveOnly = interactiveOnly;
        MaximumDepth = maximumDepth;
    }

    public static BrowserSnapshotQuery Lean { get; } = new();

    public bool InteractiveOnly { get; }

    public string? Filter { get; }

    public int? MaximumDepth { get; }

    private static int GetByteCount(string value)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A browser snapshot filter must contain valid Unicode.",
                nameof(value),
                exception);
        }
    }
}

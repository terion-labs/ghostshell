namespace Asura.Application;

internal static class SecretContract
{
    public const int MaximumLabelLength = 256;

    public static string RequireLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Length > MaximumLabelLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(label),
                label.Length,
                $"A secret label cannot exceed {MaximumLabelLength} characters.");
        }

        return label.Trim();
    }
}

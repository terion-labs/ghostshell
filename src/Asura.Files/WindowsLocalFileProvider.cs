namespace Asura.Files;

public sealed class WindowsLocalFileProvider : LocalFileProvider
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public WindowsLocalFileProvider(LocalFileProviderOptions options)
        : base(options, FileNameComparison.CaseInsensitive, StringComparison.OrdinalIgnoreCase)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows local file provider requires Windows.");
        }
    }

    protected override FileProviderError? ValidatePlatformSegment(FilePathSegment segment)
    {
        var value = segment.Value;
        var invalidCharacter = value.Any(character =>
            character < 32 || character is '<' or '>' or ':' or '"' or '\\' or '|' or '?' or '*');
        var baseName = value.Split('.', 2)[0];
        if (invalidCharacter
            || value.EndsWith(' ')
            || value.EndsWith('.')
            || ReservedNames.Contains(baseName))
        {
            return FileProviderError.Create(
                FileProviderErrorCode.InvalidName,
                "The location contains a name that is invalid on Windows.");
        }

        return null;
    }

    protected override bool IsHidden(FilePathSegment? name, FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.Hidden);
}

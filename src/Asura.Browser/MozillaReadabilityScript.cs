namespace Asura.Browser;

internal static class MozillaReadabilityScript
{
    private const string ResourceName =
        "Asura.Browser.Assets.Readability.js";

    public static string Source { get; } = Load();

    private static string Load()
    {
        var assembly = typeof(MozillaReadabilityScript).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "The embedded Mozilla Readability script is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

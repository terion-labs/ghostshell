namespace Asura.App;

internal static class ProductDocumentLocator
{
    internal const string ThirdPartyNoticesFileName = "THIRD-PARTY-NOTICES.md";

    public static string? FindThirdPartyNotices(string? applicationDirectory = null)
    {
        var root = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var candidates = new[]
        {
            Path.Combine(root, ThirdPartyNoticesFileName),
            Path.GetFullPath(Path.Combine(
                root,
                "..",
                "Resources",
                "Licenses",
                ThirdPartyNoticesFileName)),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

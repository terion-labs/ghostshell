namespace Asura.AccessibilityAcceptance;

internal sealed record MacOsPackagePublishOptions(
    string BuildLabel,
    string PackagePath,
    string DestinationPath)
{
    public static MacOsPackagePublishOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 6)
        {
            throw new UsageException(
                "macOS package publication requires --build-label, --package, and --output.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var name = arguments[index];
            if (name is not ("--build-label" or "--package" or "--output"))
            {
                throw new UsageException($"Unknown option {name}.");
            }

            if (!values.TryAdd(name, arguments[index + 1]))
            {
                throw new UsageException($"Option {name} was supplied more than once.");
            }
        }

        var buildLabel = Required(values, "--build-label");
        if (!EvidenceSanitizer.IsValidIdentifier(buildLabel))
        {
            throw new UsageException(
                "--build-label must contain 3-64 letters, digits, periods, underscores, or hyphens and begin with a letter or digit.");
        }

        return new MacOsPackagePublishOptions(
            buildLabel,
            Required(values, "--package"),
            Required(values, "--output"));
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"{name} is required.");
        }

        return value;
    }
}

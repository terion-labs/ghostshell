namespace Asura.AccessibilityAcceptance;

internal sealed record PackageInspectOptions(
    TargetPlatform Platform,
    string BuildLabel,
    string PackagePath)
{
    public static PackageInspectOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 6)
        {
            throw new UsageException(
                "Package inspection requires --platform, --build-label, and --package.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var name = arguments[index];
            if (name is not ("--platform" or "--build-label" or "--package"))
            {
                throw new UsageException($"Unknown option {name}.");
            }

            if (!values.TryAdd(name, arguments[index + 1]))
            {
                throw new UsageException($"Option {name} was supplied more than once.");
            }
        }

        var platformText = Required(values, "--platform");
        if (!Enum.TryParse<TargetPlatform>(
                platformText,
                ignoreCase: true,
                out var platform)
            || !Enum.IsDefined(platform))
        {
            throw new UsageException("--platform has an unsupported value.");
        }

        var buildLabel = Required(values, "--build-label");
        if (!EvidenceSanitizer.IsValidIdentifier(buildLabel))
        {
            throw new UsageException(
                "--build-label must contain 3-64 letters, digits, periods, underscores, or hyphens and begin with a letter or digit.");
        }

        return new PackageInspectOptions(
            platform,
            buildLabel,
            Required(values, "--package"));
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

namespace Asura.AccessibilityAcceptance;

internal sealed record RunOptions(
    TargetPlatform Platform,
    ScreenReaderKind ScreenReader,
    string SystemName,
    string Observer,
    string BuildLabel,
    string PackagePath,
    string EvidenceDirectory)
{
    private static readonly HashSet<string> GenericSystemNames = new(
        ["host", "localhost", "macos", "windows", "linux", "test"],
        StringComparer.OrdinalIgnoreCase);

    public static RunOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = ParsePairs(arguments);
        var platform = ParseEnum<TargetPlatform>(Required(values, "--platform"), "--platform");
        var screenReader = ParseEnum<ScreenReaderKind>(
            Required(values, "--screen-reader"),
            "--screen-reader");
        if (screenReader != AcceptanceEvidence.ScreenReaderFor(platform))
        {
            throw new UsageException(
                $"{platform} requires {AcceptanceEvidence.ScreenReaderFor(platform)}; platform/screen-reader substitutions are not accepted.");
        }

        var systemName = RequireIdentifier(values, "--system-name");
        if (GenericSystemNames.Contains(systemName))
        {
            throw new UsageException("--system-name must identify one specific named host.");
        }

        return new RunOptions(
            platform,
            screenReader,
            systemName,
            RequireIdentifier(values, "--observer"),
            RequireIdentifier(values, "--build-label"),
            Required(values, "--package"),
            values.GetValueOrDefault("--evidence-dir")
                ?? "artifacts/accessibility-acceptance");
    }

    private static Dictionary<string, string> ParsePairs(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || arguments.Count % 2 != 0)
        {
            throw new UsageException("Run options must be supplied as name/value pairs.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var name = arguments[index];
            if (name is not ("--platform" or "--screen-reader" or "--system-name"
                or "--observer" or "--build-label" or "--package" or "--evidence-dir"))
            {
                throw new UsageException($"Unknown option {name}.");
            }

            if (!values.TryAdd(name, arguments[index + 1]))
            {
                throw new UsageException($"Option {name} was supplied more than once.");
            }
        }

        return values;
    }

    private static T ParseEnum<T>(string value, string name)
        where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new UsageException($"{name} has an unsupported value.");
        }

        return parsed;
    }

    private static string RequireIdentifier(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        var value = Required(values, name);
        if (!EvidenceSanitizer.IsValidIdentifier(value))
        {
            throw new UsageException(
                $"{name} must contain 3-64 letters, digits, periods, underscores, or hyphens and begin with a letter or digit.");
        }

        return value;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"{name} is required.");
        }

        return value;
    }
}

internal sealed class UsageException(string message) : Exception(message)
{
}

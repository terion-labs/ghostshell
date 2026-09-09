using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImageMagick;

namespace Asura.DesignQa;

internal enum DesignQaBaselineMode
{
    None,
    Verify,
    Approve,
}

internal static class DesignQaBaseline
{
    private const int CurrentFormat = 2;
    private const int FingerprintWidth = 96;
    private const int FingerprintHeight = 60;
    private const double MaximumMeanChannelDifference = 0.25;
    private const int MaximumStronglyChangedSamples = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Process(
        DesignQaBaselineMode mode,
        string captureDirectory,
        string baselinePath,
        IReadOnlyCollection<string> requestedRoutes)
    {
        var actual = CreateManifest(captureDirectory, requestedRoutes);
        if (mode == DesignQaBaselineMode.Approve)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(
                baselinePath,
                JsonSerializer.Serialize(actual, JsonOptions) + Environment.NewLine);
            Console.WriteLine(
                $"APPROVED {actual.Captures.Count} design QA references -> {baselinePath}");
            return;
        }

        if (!File.Exists(baselinePath))
        {
            throw new InvalidOperationException(
                $"The design QA baseline does not exist: {baselinePath}. "
                + "Review the captures, then run --approve-baseline explicitly.");
        }

        var expected = JsonSerializer.Deserialize<DesignQaManifest>(
            File.ReadAllText(baselinePath),
            JsonOptions)
            ?? throw new InvalidOperationException(
                $"The design QA baseline is empty: {baselinePath}.");
        if (expected.Format != CurrentFormat)
        {
            throw new InvalidOperationException(
                $"Unsupported design QA baseline format {expected.Format}; expected {CurrentFormat}.");
        }

        var differences = Compare(expected.Captures, actual.Captures);
        if (differences.Count > 0)
        {
            throw new InvalidOperationException(
                "Design QA coherence gate failed. The implementation no longer matches the "
                + "approved route, viewport, interaction, and appearance references:\n- "
                + string.Join("\n- ", differences)
                + "\nInspect the PNGs in "
                + captureDirectory
                + ". Approve only an intentional, reviewed change with --approve-baseline.");
        }

        Console.WriteLine(
            $"PASS design QA coherence: {actual.Captures.Count} captures match approved references.");
    }

    private static DesignQaManifest CreateManifest(
        string captureDirectory,
        IReadOnlyCollection<string> requestedRoutes)
    {
        var captures = new List<DesignQaCapture>(requestedRoutes.Count);
        foreach (var route in requestedRoutes.Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(captureDirectory, route + ".png");
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"The required design QA capture was not produced: {route}.");
            }

            using var image = new MagickImage(path);
            if (image.Width == 0 || image.Height == 0)
            {
                throw new InvalidOperationException(
                    $"The design QA capture has no drawable area: {route}.");
            }

            if (image.Histogram().Count < 8)
            {
                throw new InvalidOperationException(
                    $"The design QA capture is blank or effectively blank: {route}. "
                    + "Verify that its extracted presentation resource was materialized.");
            }

            var pixels = image.ToByteArray(MagickFormat.Rgba);
            captures.Add(new DesignQaCapture(
                route,
                checked((int)image.Width),
                checked((int)image.Height),
                Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant(),
                CreateFingerprint(image)));
        }

        return new DesignQaManifest(
            CurrentFormat,
            "Synthetic, offline, non-live Asura presentation fixtures",
            captures);
    }

    private static List<string> Compare(
        IReadOnlyCollection<DesignQaCapture> expected,
        IReadOnlyCollection<DesignQaCapture> actual)
    {
        var differences = new List<string>();
        var expectedByRoute = expected.ToDictionary(item => item.Route, StringComparer.Ordinal);
        var actualByRoute = actual.ToDictionary(item => item.Route, StringComparer.Ordinal);

        foreach (var missing in expectedByRoute.Keys.Except(actualByRoute.Keys, StringComparer.Ordinal))
        {
            differences.Add($"missing capture: {missing}");
        }

        foreach (var added in actualByRoute.Keys.Except(expectedByRoute.Keys, StringComparer.Ordinal))
        {
            differences.Add($"unapproved capture: {added}");
        }

        foreach (var route in expectedByRoute.Keys.Intersect(actualByRoute.Keys, StringComparer.Ordinal))
        {
            var reference = expectedByRoute[route];
            var implementation = actualByRoute[route];
            if (reference.Width != implementation.Width || reference.Height != implementation.Height)
            {
                differences.Add(
                    $"{route}: viewport changed from {reference.Width}x{reference.Height} "
                    + $"to {implementation.Width}x{implementation.Height}");
            }
            else if (!string.Equals(reference.Sha256, implementation.Sha256, StringComparison.Ordinal)
                && !PerceptuallyMatches(reference.Fingerprint, implementation.Fingerprint))
            {
                differences.Add($"{route}: pixels differ from the approved reference");
            }
        }

        return differences;
    }

    private static string CreateFingerprint(MagickImage image)
    {
        using var sample = image.Clone();
        sample.Resize(new MagickGeometry(FingerprintWidth, FingerprintHeight)
        {
            IgnoreAspectRatio = true,
        });
        return Convert.ToBase64String(sample.ToByteArray(MagickFormat.Rgb));
    }

    private static bool PerceptuallyMatches(string expectedValue, string actualValue)
    {
        var expected = Convert.FromBase64String(expectedValue);
        var actual = Convert.FromBase64String(actualValue);
        if (expected.Length != actual.Length || expected.Length == 0)
        {
            return false;
        }

        long totalDifference = 0;
        var stronglyChangedSamples = 0;
        for (var index = 0; index < expected.Length; index += 3)
        {
            var red = Math.Abs(expected[index] - actual[index]);
            var green = Math.Abs(expected[index + 1] - actual[index + 1]);
            var blue = Math.Abs(expected[index + 2] - actual[index + 2]);
            totalDifference += red + green + blue;
            if ((red + green + blue) / 3d > 20)
            {
                stronglyChangedSamples++;
            }
        }

        var meanChannelDifference = totalDifference / (double)expected.Length;
        return meanChannelDifference <= MaximumMeanChannelDifference
            && stronglyChangedSamples <= MaximumStronglyChangedSamples;
    }
}

internal sealed record DesignQaManifest(
    int Format,
    string Fixture,
    IReadOnlyList<DesignQaCapture> Captures);

internal sealed record DesignQaCapture(
    string Route,
    int Width,
    int Height,
    string Sha256,
    string Fingerprint);

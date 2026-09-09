using System.Xml;
using System.Xml.Linq;

namespace Asura.SecurityCampaign;

internal static class TrxEvidenceReader
{
    private const int MaximumTrxFiles = 128;
    private const long MaximumTrxBytes = 64L * 1024 * 1024;

    public static IReadOnlyList<CaseEvidence> Read(
        string resultDirectory,
        IReadOnlyList<CampaignCaseDefinition> definitions)
    {
        var root = Path.GetFullPath(resultDirectory);
        var files = Directory.EnumerateFiles(root, "*.trx", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Take(MaximumTrxFiles + 1)
            .ToArray();
        if (files.Length == 0 || files.Length > MaximumTrxFiles)
        {
            throw new InvalidDataException("The campaign requires between 1 and 128 TRX files.");
        }

        var results = files.SelectMany(ReadResults).ToArray();
        var evidence = new List<CaseEvidence>(definitions.Count);
        foreach (var definition in definitions)
        {
            var matches = results
                .Where(result => result.Name.Contains(definition.TestNameContains, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException($"Campaign case {definition.Id} has {matches.Length} matching TRX results.");
            }

            var match = matches[0];
            if (!string.Equals(match.Outcome, "Passed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Campaign case {definition.Id} did not pass.");
            }

            evidence.Add(new CaseEvidence(
                definition.Id,
                match.Name,
                CampaignFiles.Sha256File(match.Path, MaximumTrxBytes),
                "pass"));
        }

        return [.. evidence.OrderBy(static item => item.Id, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<TrxResult> ReadResults(string path)
    {
        _ = CampaignFiles.ReadFile(path, MaximumTrxBytes);
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaximumTrxBytes,
            XmlResolver = null,
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        return [.. document.Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "UnitTestResult", StringComparison.Ordinal))
            .Select(element => new TrxResult(
                (string?)element.Attribute("testName") ?? string.Empty,
                (string?)element.Attribute("outcome") ?? string.Empty,
                path))
            ];
    }

    private sealed record TrxResult(string Name, string Outcome, string Path);
}

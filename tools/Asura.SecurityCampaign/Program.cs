using System.Text.Json;

namespace Asura.SecurityCampaign;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Run(args);
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void Run(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException(Usage());
        }

        var command = args[0];
        var options = Options.Parse(args[1..]);
        switch (command)
        {
            case "validate-definition":
                ValidateDefinition(options);
                break;
            case "list-test-projects":
                ListTestProjects(options);
                break;
            case "assemble-source-evidence":
                AssembleSourceEvidence(options);
                break;
            case "assemble-dependency-evidence":
                AssembleDependencyEvidence(options);
                break;
            case "seal-release-source":
                SealReleaseSource(options);
                break;
            case "verify-release-source":
                VerifyReleaseSource(options);
                break;
            case "assemble-release-evidence":
                AssembleReleaseEvidence(options);
                break;
            case "validate-local-release-inputs":
                ValidateLocalReleaseInputs(options);
                break;
            case "validate-evidence":
                ValidateEvidence(options);
                break;
            case "validate-release-evidence":
                ValidateReleaseEvidence(options);
                break;
            default:
                throw new ArgumentException($"Unknown command {command}.\n{Usage()}");
        }
    }

    private static void ValidateDefinition(Options options)
    {
        options.RequireOnly("repository", "registry", "receipt-schema");
        var repository = options.Require("repository");
        var registry = options.Require("registry");
        _ = CampaignFiles.ReadFile(options.Require("receipt-schema"), 1024 * 1024);
        var definition = CampaignDefinitionValidator.Validate(repository, registry);
        Console.WriteLine($"Validated {definition.Cases.Count} security campaign cases.");
    }

    private static void ListTestProjects(Options options)
    {
        options.RequireOnly("repository", "registry");
        var definition = CampaignDefinitionValidator.Validate(
            options.Require("repository"),
            options.Require("registry"));
        foreach (var project in definition.Cases
                     .Select(static item => item.TestProject)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            Console.WriteLine(project);
        }
    }

    private static void AssembleSourceEvidence(Options options)
    {
        options.RequireOnly("repository", "registry", "receipt-schema", "test-results", "output");
        var receipt = CampaignAssembler.CreateSourceReceipt(
            options.Require("repository"),
            options.Require("registry"),
            options.Require("receipt-schema"),
            options.Require("test-results"));
        CampaignAssembler.ValidateReceipt(receipt);
        CampaignFiles.WriteReceipt(options.Require("output"), receipt);
        Console.WriteLine("Wrote source-only security campaign evidence with overall notEvaluated.");
    }

    private static void AssembleDependencyEvidence(Options options)
    {
        options.RequireOnly("source-commit", "nuget", "maven", "output");
        var sourceCommit = options.Require("source-commit");
        RequireLowerHex(sourceCommit, 40, "source commit");
        var output = Path.GetFullPath(options.Require("output"));
        if (File.Exists(output) || Directory.Exists(output))
        {
            throw new IOException("Dependency evidence output must not exist.");
        }

        Directory.CreateDirectory(output);
        try
        {
            RequireCleanNugetAudit(options.Require("nuget"));
            RequireCleanMavenAudit(options.Require("maven"));
            var inputs = new[]
            {
                CopyDependencyInput("nuget-audit", options.Require("nuget"), output, "nuget-audit.json"),
                CopyDependencyInput("maven-audit", options.Require("maven"), output, "maven-audit.json"),
            };
            var evidence = new DependencyEvidenceDocument(
                1,
                "asura-dependency-security-evidence-v1",
                sourceCommit,
                "pass",
                inputs,
                0,
                0);
            File.WriteAllBytes(
                Path.Combine(output, "evidence.json"),
                JsonSerializer.SerializeToUtf8Bytes(evidence, CampaignFiles.StrictJson));
        }
        catch
        {
            Directory.Delete(output, recursive: true);
            throw;
        }
    }

    private static void SealReleaseSource(Options options)
    {
        options.RequireOnly(
            "repository",
            "source-root",
            "source-commit",
            "source-tree",
            "tag",
            "output");
        var verification = ReleaseSourceSeal.Create(
            options.Require("repository"),
            options.Require("source-root"),
            options.Require("source-commit"),
            options.Require("source-tree"),
            options.Require("tag"),
            options.Require("output"));
        Console.WriteLine(
            $"Sealed {verification.Seal.Files.Count} tagged source files with manifest {verification.Seal.ManifestSha256}.");
    }

    private static void VerifyReleaseSource(Options options)
    {
        options.RequireOnlyWithOptional(
            ["source-root", "source-seal", "source-commit", "source-tree", "tag"],
            ["build-identity-output"]);
        var verification = ReleaseSourceSeal.Verify(
            options.Require("source-root"),
            options.Require("source-seal"),
            options.Require("source-commit"),
            options.Require("source-tree"),
            options.Require("tag"),
            options.Optional("build-identity-output"));
        Console.WriteLine(
            $"Verified sealed release source manifest {verification.ObservedManifestSha256}.");
    }

    private static void RequireCleanNugetAudit(string path)
    {
        using var document = JsonDocument.Parse(CampaignFiles.ReadFile(path, 64 * 1024 * 1024));
        if (ContainsProperty(document.RootElement, "vulnerabilities"))
        {
            throw new InvalidDataException("NuGet dependency evidence contains vulnerable packages.");
        }
    }

    private static void RequireCleanMavenAudit(string path)
    {
        using var document = JsonDocument.Parse(CampaignFiles.ReadFile(path, 64 * 1024 * 1024));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("matches", out var matches)
            || matches.ValueKind != JsonValueKind.Array
            || matches.GetArrayLength() != 0
            || !document.RootElement.TryGetProperty("descriptor", out var descriptor)
            || descriptor.ValueKind != JsonValueKind.Object
            || !descriptor.TryGetProperty("name", out var name)
            || !string.Equals(name.GetString(), "grype", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Maven dependency evidence is missing Grype identity or contains advisories.");
        }
    }

    private static bool ContainsProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal)
                    || ContainsProperty(property.Value, name))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsProperty(item, name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static FileEvidence CopyDependencyInput(string kind, string source, string output, string name)
    {
        var bytes = CampaignFiles.ReadFile(source, 64 * 1024 * 1024);
        var destination = Path.Combine(output, name);
        File.WriteAllBytes(destination, bytes);
        return new FileEvidence(kind, name, bytes.LongLength, CampaignFiles.Sha256(bytes));
    }

    private static void AssembleReleaseEvidence(Options options)
    {
        var inputs = ReleaseInputsFrom(options, finalOption: "output");
        var receipt = CampaignAssembler.CreateReleaseReceipt(inputs);
        CampaignAssembler.ValidateReceipt(receipt);
        CampaignFiles.WriteReceipt(options.Require("output"), receipt);
        Console.WriteLine("Wrote passing macOS arm64 release-candidate evidence.");
    }

    private static void ValidateLocalReleaseInputs(Options options)
    {
        var inputs = ReleaseInputsFrom(options, finalOption: null);
        CampaignAssembler.ValidateLocalReleaseInputs(inputs);
        Console.WriteLine("Validated local signed and notarized macOS arm64 release inputs.");
    }

    private static void ValidateEvidence(Options options)
    {
        options.RequireOnly(
            "evidence",
            "repository",
            "registry",
            "receipt-schema",
            "test-results");
        var receipt = CampaignFiles.ReadReceipt(options.Require("evidence"));
        CampaignAssembler.ValidateReceipt(receipt);
        if (!string.Equals(receipt.EvidenceClass, "source-only", StringComparison.Ordinal))
        {
            throw new InvalidDataException("This command accepts only source-only evidence and exact source inputs.");
        }

        var expected = CampaignAssembler.CreateSourceReceipt(
            options.Require("repository"),
            options.Require("registry"),
            options.Require("receipt-schema"),
            options.Require("test-results"));
        if (!ReceiptsEqual(receipt, expected))
        {
            throw new InvalidDataException("The source-only receipt does not match recomputed source and TRX evidence.");
        }

        Console.WriteLine("Validated source-only evidence with overall notEvaluated.");
    }

    private static void ValidateReleaseEvidence(Options options)
    {
        var inputs = ReleaseInputsFrom(options, finalOption: "evidence");
        var actual = CampaignFiles.ReadReceipt(options.Require("evidence"));
        CampaignAssembler.ValidateReceipt(actual);
        var expected = CampaignAssembler.CreateReleaseReceipt(inputs);
        if (!ReceiptsEqual(actual, expected))
        {
            throw new InvalidDataException("The release receipt does not match recomputed candidate evidence.");
        }

        Console.WriteLine("Validated exact macOS arm64 release-candidate evidence.");
    }

    private static bool ReceiptsEqual(CampaignReceipt left, CampaignReceipt right) =>
        JsonSerializer.SerializeToUtf8Bytes(left, CampaignFiles.StrictJson)
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, CampaignFiles.StrictJson));

    private static ReleaseInputs ReleaseInputsFrom(Options options, string? finalOption)
    {
        List<string> names =
        [
            "repository", "source-commit", "source-tree", "tag", "run-id", "run-attempt",
            "source-seal", "build-identity", "archive", "package", "test-results",
            "dependency-evidence", "notarization-evidence",
        ];
        if (finalOption is not null)
        {
            names.Add(finalOption);
        }
        options.RequireOnly([.. names]);
        return new ReleaseInputs(
            options.Require("repository"),
            options.Require("source-commit"),
            options.Require("source-tree"),
            options.Require("tag"),
            options.Require("run-id"),
            options.Require("run-attempt"),
            options.Require("source-seal"),
            options.Require("build-identity"),
            options.Require("archive"),
            options.Require("package"),
            options.Require("test-results"),
            options.Require("dependency-evidence"),
            options.Require("notarization-evidence"));
    }

    private static void RequireLowerHex(string value, int length, string name)
    {
        if (value.Length != length || !value.All(static character => char.IsAsciiHexDigit(character) && !char.IsUpper(character)))
        {
            throw new ArgumentException($"The {name} must be {length} lowercase hexadecimal characters.");
        }
    }

    private static string Usage() =>
        "Commands: validate-definition, list-test-projects, assemble-source-evidence, "
        + "assemble-dependency-evidence, seal-release-source, verify-release-source, "
        + "assemble-release-evidence, validate-local-release-inputs, validate-evidence, "
        + "validate-release-evidence. "
        + "Every argument uses --name value.";

    private sealed class Options
    {
        private readonly Dictionary<string, string> _values;

        private Options(Dictionary<string, string> values) => _values = values;

        public static Options Parse(IReadOnlyList<string> args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Count; index += 2)
            {
                if (index + 1 >= args.Count || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Every command argument must use --name value.");
                }

                var name = args[index][2..];
                if (string.IsNullOrWhiteSpace(name) || !values.TryAdd(name, args[index + 1]))
                {
                    throw new ArgumentException($"Command option {name} is invalid or duplicated.");
                }
            }

            return new Options(values);
        }

        public string Require(string name) =>
            _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"Missing --{name}.");

        public string? Optional(string name) =>
            _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;

        public void RequireOnly(params string[] names)
        {
            var allowed = names.ToHashSet(StringComparer.Ordinal);
            var unknown = _values.Keys.Where(name => !allowed.Contains(name)).Order(StringComparer.Ordinal).ToArray();
            if (unknown.Length != 0)
            {
                throw new ArgumentException("Unknown options: " + string.Join(", ", unknown));
            }

            foreach (var name in names)
            {
                _ = Require(name);
            }
        }

        public void RequireOnlyWithOptional(
            IReadOnlyList<string> required,
            IReadOnlyList<string> optional)
        {
            var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
            var unknown = _values.Keys
                .Where(name => !allowed.Contains(name))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (unknown.Length != 0)
            {
                throw new ArgumentException("Unknown options: " + string.Join(", ", unknown));
            }

            foreach (var name in required)
            {
                _ = Require(name);
            }
        }
    }
}

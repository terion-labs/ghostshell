using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.SecurityCampaign;

internal static class CampaignDefinitionValidator
{
    private static readonly HashSet<string> ActionExceptions =
        new([BuiltInAgentTools.TerminalWait, BuiltInAgentTools.BrowserWait], StringComparer.Ordinal);

    public static CampaignDefinition Validate(string repository, string registryPath)
    {
        var root = RequireDirectory(repository);
        var definition = CampaignFiles.ReadJson<CampaignDefinition>(registryPath);
        if (definition.SchemaVersion != 1
            || !string.Equals(definition.Format, "asura-security-campaign-cases-v1", StringComparison.Ordinal)
            || !string.Equals(definition.ReleaseScope, "macos-arm64", StringComparison.Ordinal)
            || !definition.DeferredPlatforms.SequenceEqual(["windows", "linux"], StringComparer.Ordinal)
            || !definition.ActionExceptions.ToHashSet(StringComparer.Ordinal).SetEquals(ActionExceptions))
        {
            throw new InvalidDataException("The campaign definition has an invalid fixed release scope.");
        }

        var byId = new Dictionary<string, CampaignCaseDefinition>(StringComparer.Ordinal);
        foreach (var item in definition.Cases)
        {
            ValidateCaseId(item.Id);
            if (!byId.TryAdd(item.Id, item))
            {
                throw new InvalidDataException($"Campaign case {item.Id} is duplicated.");
            }

            RequireRelativePath(root, item.TestProject);
            var source = RequireRelativePath(root, item.TestSource);
            if (!File.ReadAllText(source, Encoding.UTF8).Contains(
                    $"SecurityCampaignCase\", \"{item.Id}\"",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Campaign case {item.Id} has no exact source marker.");
            }

            if (!item.TestNameContains.Contains(item.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Campaign case {item.Id} must bind its stable ID into the TRX name.");
            }
        }

        var expectedActions = BuiltInAgentTools.Catalog.Tools
            .Where(static tool => tool.Risk != AgentActionRisk.Observation)
            .Select(static tool => tool.Name)
            .Where(name => !ActionExceptions.Contains(name))
            .Select(static name => "authority." + name)
            .ToHashSet(StringComparer.Ordinal);
        var registeredActions = definition.Cases
            .Where(static item => string.Equals(item.Kind, "authority", StringComparison.Ordinal))
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!registeredActions.SetEquals(expectedActions))
        {
            var missing = string.Join(", ", expectedActions.Except(registeredActions, StringComparer.Ordinal).Order(StringComparer.Ordinal));
            var extra = string.Join(", ", registeredActions.Except(expectedActions, StringComparer.Ordinal).Order(StringComparer.Ordinal));
            throw new InvalidDataException($"Action campaign coverage drifted. Missing: [{missing}]. Extra: [{extra}].");
        }

        return definition;
    }

    public static string CatalogDigest()
    {
        var text = string.Join(
            '\n',
            BuiltInAgentTools.Catalog.Tools
                .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                .Select(static tool => $"{tool.Name}|{tool.Risk}|{tool.Capability}|{tool.MaximumExecutionLifetime.Ticks}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string RequireDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(fullPath);
        }

        return fullPath;
    }

    private static string RequireRelativePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split(['/', '\\']).Any(static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Campaign source paths must be canonical repository-relative paths.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(fullPath)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException(
                $"Campaign source path is missing or linked: {relativePath}",
                fullPath);
        }

        return fullPath;
    }

    internal static void ValidateCaseId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 160
            || value.Any(static character => character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9') and not '.' and not '-' and not '_'))
        {
            throw new InvalidDataException("Campaign IDs must be bounded lowercase identifiers.");
        }
    }
}

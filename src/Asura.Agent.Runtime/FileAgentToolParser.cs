using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class FileAgentToolParser
{
    public static FileAgentIntentResult Parse(AgentToolProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        return ParseProperties(proposal.ToolName, properties, panelId: null);
    }

    /// <summary>
    /// Parses a proposal for one exact file panel. Panel and session identity
    /// remain host-owned and therefore cannot appear in provider arguments.
    /// </summary>
    public static FileAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextPanel panel,
        FileSessionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        if (properties.ContainsKey("panel_id"))
        {
            return Invalid(
                "An exact file tool does not accept a panel ID.");
        }

        return FileAgentToolSet.Supports(panel, metadata, proposal.ToolName)
            ? ParseProperties(
                proposal.ToolName,
                properties,
                panel.PanelId)
            : UnavailableTool();
    }

    /// <summary>
    /// Parses a proposal against a freshly resolved broad scope. An explicit
    /// enumerated panel ID is required even when one file panel is eligible.
    /// </summary>
    public static FileAgentIntentResult Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        var activePanels = FileAgentToolSet.ActiveFilePanels(panels, metadata);
        if (activePanels.Length == 0)
        {
            return UnavailableTool();
        }

        if (!properties.TryGetValue("panel_id", out var panelIdElement)
            || panelIdElement.ValueKind != JsonValueKind.String
            || !TryGetString(panelIdElement, out var panelId))
        {
            return Invalid(
                "A broad file tool requires one exact panel_id.");
        }

        var selected = activePanels.FirstOrDefault(binding =>
            string.Equals(
                binding.Panel.PanelId.Value,
                panelId,
                StringComparison.Ordinal));
        if (selected is null
            || !FileAgentToolSet.Supports(
                selected.Panel,
                selected.Metadata,
                proposal.ToolName))
        {
            return Invalid(
                "The selected panel_id is not available for this file tool.");
        }

        properties.Remove("panel_id");
        return ParseProperties(
            proposal.ToolName,
            properties,
            selected.Panel.PanelId);
    }

    private static FileAgentIntentResult ParseProperties(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (string.Equals(toolName, BuiltInAgentTools.FilesTransfers, StringComparison.Ordinal))
        {
            return properties.Count == 0
                ? new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.Transfers(),
                    panelId)
                : Invalid("File transfer observation accepts no arguments.");
        }

        if (string.Equals(toolName, BuiltInAgentTools.FilesSearch, StringComparison.Ordinal))
        {
            return ParseSearch(properties, panelId);
        }

        if (string.Equals(toolName, BuiltInAgentTools.FilesDelete, StringComparison.Ordinal))
        {
            return ParseDelete(properties, panelId);
        }

        if (string.Equals(toolName, BuiltInAgentTools.FilesMove, StringComparison.Ordinal))
        {
            return ParseMove(properties, panelId);
        }

        if (string.Equals(toolName, GovernedFileToolNames.CreateText, StringComparison.Ordinal))
        {
            return ParseText(properties, panelId, requireReference: false);
        }

        if (string.Equals(toolName, GovernedFileToolNames.ReplaceText, StringComparison.Ordinal))
        {
            return ParseText(properties, panelId, requireReference: true);
        }

        if (string.Equals(toolName, GovernedFileToolNames.Copy, StringComparison.Ordinal))
        {
            return ParseCopy(properties, panelId);
        }

        if (properties.Count != 1
            || !properties.TryGetValue(
                "path_segments",
                out var pathElement)
            || !TryReadPath(pathElement, out var path))
        {
            return Invalid(
                "File tools require only a bounded path_segments array "
                + "relative to the trusted panel root.");
        }

        return toolName switch
        {
            BuiltInAgentTools.FilesList =>
                new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.List(path),
                    panelId),
            BuiltInAgentTools.FilesStat =>
                new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.Stat(path),
                    panelId),
            BuiltInAgentTools.FilesRead when path.Length > 0 =>
                new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.Read(path),
                    panelId),
            BuiltInAgentTools.FilesRead =>
                Invalid("File read requires a non-root file path."),
            BuiltInAgentTools.FilesAccessRead =>
                new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.AccessRead(path),
                    panelId),
            BuiltInAgentTools.FilesCreateDirectory when path.Length > 0 =>
                new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.CreateDirectory(path),
                    panelId),
            BuiltInAgentTools.FilesCreateDirectory =>
                Invalid("Directory creation requires a non-root file path."),
            _ => UnknownTool(),
        };
    }

    private static FileAgentIntentResult ParseDelete(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count is < 2 or > 3
            || !properties.TryGetValue("path_segments", out var pathElement)
            || !TryReadPath(pathElement, out var path)
            || path.Length == 0
            || !TryReadReference(properties, out var entryReference))
        {
            return Invalid("File deletion requires a non-root path_segments array.");
        }

        var recursive = false;
        if (properties.TryGetValue("recursive", out var recursiveElement))
        {
            if (recursiveElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Invalid("File deletion recursive must be a boolean.");
            }

            recursive = recursiveElement.GetBoolean();
        }

        if (properties.Keys.Any(name => name is not ("path_segments" or "recursive" or "entry_ref")))
        {
            return Invalid("File deletion contains an unknown field.");
        }

        return new FileAgentIntentResult.Parsed(
            new FileAgentIntent.Delete(path, entryReference, recursive),
            panelId);
    }

    private static FileAgentIntentResult ParseMove(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 3
            || !properties.TryGetValue(
                "source_path_segments",
                out var sourceElement)
            || !TryReadPath(sourceElement, out var source)
            || source.Length == 0
            || !TryReadReference(properties, out var entryReference)
            || !properties.TryGetValue(
                "destination_path_segments",
                out var destinationElement)
            || !TryReadPath(destinationElement, out var destination)
            || destination.Length == 0
            || source.SequenceEqual(destination))
        {
            return Invalid(
                "File move requires distinct non-root source_path_segments "
                + "and destination_path_segments arrays.");
        }

        return new FileAgentIntentResult.Parsed(
            new FileAgentIntent.Move(source, entryReference, destination),
            panelId);
    }

    private static FileAgentIntentResult ParseText(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId,
        bool requireReference)
    {
        var expectedCount = requireReference ? 3 : 2;
        if (properties.Count != expectedCount
            || !properties.TryGetValue("path_segments", out var pathElement)
            || !TryReadPath(pathElement, out var path)
            || path.Length == 0
            || !properties.TryGetValue("content", out var contentElement)
            || contentElement.ValueKind != JsonValueKind.String
            || !TryGetString(contentElement, out var content)
            || !IsSafeText(content))
        {
            return Invalid("Text mutation requires a non-root path and bounded UTF-8 content.");
        }

        if (!requireReference)
        {
            return new FileAgentIntentResult.Parsed(
                new FileAgentIntent.CreateText(path, content),
                panelId);
        }

        if (!TryReadReference(properties, out var entryReference))
        {
            return Invalid("Text replacement requires an opaque entry_ref from files.stat.");
        }

        return new FileAgentIntentResult.Parsed(
            new FileAgentIntent.ReplaceText(path, entryReference, content),
            panelId);
    }

    private static FileAgentIntentResult ParseCopy(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 3
            || !properties.TryGetValue("source_path_segments", out var sourceElement)
            || !TryReadPath(sourceElement, out var source)
            || source.Length == 0
            || !properties.TryGetValue("destination_path_segments", out var destinationElement)
            || !TryReadPath(destinationElement, out var destination)
            || destination.Length == 0
            || source.SequenceEqual(destination)
            || !TryReadReference(properties, out var entryReference))
        {
            return Invalid(
                "File copy requires distinct source and destination paths and an opaque entry_ref.");
        }

        return new FileAgentIntentResult.Parsed(
            new FileAgentIntent.Copy(source, entryReference, destination),
            panelId);
    }

    private static bool TryReadReference(
        IReadOnlyDictionary<string, JsonElement> properties,
        out AgentFileEntryReference entryReference)
    {
        entryReference = default;
        if (!properties.TryGetValue("entry_ref", out var element)
            || element.ValueKind != JsonValueKind.String
            || !TryGetString(element, out var value))
        {
            return false;
        }

        try
        {
            entryReference = new AgentFileEntryReference(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSafeText(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > FileAgentToolSet.MaximumTextBytes)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(value[index])
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[++index]))
            {
                return false;
            }
        }

        return value.EnumerateRunes().All(rune =>
            Rune.GetUnicodeCategory(rune) is not (
                UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            && (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Control
                || rune.Value is '\t' or '\n' or '\r'));
    }

    private static FileAgentIntentResult ParseSearch(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 4
            || !properties.TryGetValue("path_segments", out var pathElement)
            || !TryReadPath(pathElement, out var path)
            || !properties.TryGetValue("query", out var queryElement)
            || queryElement.ValueKind != JsonValueKind.String
            || !TryGetString(queryElement, out var query)
            || !IsSafeQuery(query)
            || !properties.TryGetValue("scope", out var scopeElement)
            || scopeElement.ValueKind != JsonValueKind.String
            || !TryGetString(scopeElement, out var scopeText)
            || !TryReadSearchScope(scopeText, out var scope)
            || !properties.TryGetValue("max_results", out var maximumElement)
            || maximumElement.ValueKind != JsonValueKind.Number
            || !maximumElement.TryGetInt32(out var maximumResults)
            || maximumResults is < 1 or > FileAgentToolSet.MaximumSearchResults)
        {
            return Invalid(
                "File search requires bounded path_segments, query, scope, "
                + "and max_results fields only.");
        }

        return new FileAgentIntentResult.Parsed(
            new FileAgentIntent.Search(
                path,
                query,
                scope,
                maximumResults),
            panelId);
    }

    private static bool TryReadSearchScope(
        string value,
        out FilePanelDiscoveryScope scope)
    {
        scope = value switch
        {
            "current_directory" => FilePanelDiscoveryScope.CurrentDirectory,
            "subtree" => FilePanelDiscoveryScope.Subtree,
            _ => (FilePanelDiscoveryScope)(-1),
        };
        return Enum.IsDefined(scope);
    }

    private static bool IsSafeQuery(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > FileAgentToolSet.MaximumSearchQueryBytes)
        {
            return false;
        }

        try
        {
            if (Encoding.UTF8.GetByteCount(value)
                > FileAgentToolSet.MaximumSearchQueryBytes)
            {
                return false;
            }
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(value[index])
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }

            index++;
        }

        return value.EnumerateRunes().All(rune =>
            Rune.GetUnicodeCategory(rune) is not (
                UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator));
    }

    private static bool TryReadPath(
        JsonElement element,
        out ImmutableArray<FilePanelPathSegment> path)
    {
        path = [];
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<FilePanelPathSegment>();
        var totalBytes = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (builder.Count == FileAgentToolSet.MaximumPathSegments
                || item.ValueKind != JsonValueKind.String
                || !TryGetString(item, out var value)
                || !IsSafePathSegment(value))
            {
                return false;
            }

            totalBytes = checked(
                totalBytes
                + Encoding.UTF8.GetByteCount(value)
                + (builder.Count == 0 ? 0 : 1));
            if (totalBytes > FileAgentToolSet.MaximumRelativePathBytes)
            {
                return false;
            }

            builder.Add(new FilePanelPathSegment(value));
        }

        path = builder.ToImmutable();
        return true;
    }

    private static bool IsSafePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(value)
                > FileAgentToolSet.MaximumPathSegmentBytes)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsSurrogate(character))
            {
                if (!char.IsHighSurrogate(character)
                    || index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
        }

        return value.EnumerateRunes().All(rune =>
            Rune.GetUnicodeCategory(rune) is not (
                UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator));
    }

    private static bool TryReadProperties(
        AgentToolProposal proposal,
        out Dictionary<string, JsonElement> properties,
        out FileAgentIntentResult rejection)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            rejection = Invalid("Tool arguments must be an object.");
            return false;
        }

        foreach (var property in proposal.Arguments.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                properties.Clear();
                rejection = Invalid(
                    "Tool arguments cannot contain duplicate fields.");
                return false;
            }
        }

        rejection = null!;
        return true;
    }

    private static bool TryGetString(
        JsonElement element,
        out string value)
    {
        value = string.Empty;
        try
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsKnownTool(string toolName) =>
        toolName is
            BuiltInAgentTools.FilesList
            or BuiltInAgentTools.FilesSearch
            or BuiltInAgentTools.FilesStat
            or BuiltInAgentTools.FilesRead
            or BuiltInAgentTools.FilesAccessRead
            or BuiltInAgentTools.FilesTransfers
            or BuiltInAgentTools.FilesCreateDirectory
            or GovernedFileToolNames.CreateText
            or GovernedFileToolNames.ReplaceText
            or GovernedFileToolNames.Copy
            or BuiltInAgentTools.FilesMove
            or BuiltInAgentTools.FilesDelete;

    private static FileAgentIntentResult UnknownTool() =>
        new FileAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a tool that is not available to this run.");

    private static FileAgentIntentResult UnavailableTool() =>
        new FileAgentIntentResult.Rejected(
            "tool_not_available",
            "The file tool is not available in the freshly resolved scope.");

    private static FileAgentIntentResult Invalid(string message) =>
        new FileAgentIntentResult.Rejected(
            "invalid_tool_arguments",
            message);
}

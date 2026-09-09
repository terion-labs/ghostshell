using System.Text;
using System.Text.Json;

namespace Asura.Agent;

public enum AgentToolResultStatus
{
    Succeeded,
    Failed,
}

public enum AgentToolResultValueKind
{
    Text,
    Json,
}

public sealed class AgentToolResultValue
{
    public const int MaximumContentBytes = 1024 * 1024;

    private const int MaximumJsonDepth = 128;
    private const int MaximumJsonNodes = 64 * 1024;

    private AgentToolResultValue(AgentToolResultValueKind kind, string content)
    {
        Kind = kind;
        Content = content;
    }

    public AgentToolResultValueKind Kind { get; }

    public string Content { get; }

    public bool ContainsUntrustedContent => true;

    public static AgentToolResultValue FromText(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Encoding.UTF8.GetByteCount(content) > MaximumContentBytes)
        {
            throw new ArgumentException(
                "The tool-result text exceeds its byte limit.",
                nameof(content));
        }

        return new AgentToolResultValue(AgentToolResultValueKind.Text, content);
    }

    public static AgentToolResultValue FromJson(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumContentBytes)
        {
            throw new ArgumentException(
                "The tool-result JSON exceeds its byte limit.",
                nameof(utf8Json));
        }

        try
        {
            using var document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowDuplicateProperties = false,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            var remainingNodes = MaximumJsonNodes;
            ValidateJson(document.RootElement, ref remainingNodes, nameof(utf8Json));
            return new AgentToolResultValue(
                AgentToolResultValueKind.Json,
                document.RootElement.GetRawText());
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The tool-result value is not valid bounded JSON.",
                nameof(utf8Json),
                exception);
        }
    }

    private static void ValidateJson(
        JsonElement element,
        ref int remainingNodes,
        string parameterName)
    {
        if (--remainingNodes < 0)
        {
            throw new ArgumentException(
                "The tool-result JSON exceeds its node limit.",
                parameterName);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                ValidateJson(property.Value, ref remainingNodes, parameterName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateJson(item, ref remainingNodes, parameterName);
            }
        }
    }
}

public sealed class AgentToolResult
{
    private const int MaximumStableCodeLength = 128;

    public AgentToolResult(
        AgentToolProposal proposal,
        AgentToolResultStatus status,
        string stableCode,
        AgentToolResultValue value)
        : this(
            proposal?.Id ?? throw new ArgumentNullException(nameof(proposal)),
            proposal.Generation,
            proposal.ProviderCallId,
            status,
            stableCode,
            value)
    {
    }

    internal AgentToolResult(
        string proposalId,
        long generation,
        string providerCallId,
        AgentToolResultStatus status,
        string stableCode,
        AgentToolResultValue value)
    {
        AgentToolDefinition.ValidateIdentifier(
            proposalId,
            nameof(proposalId),
            512);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        AgentToolDefinition.ValidateIdentifier(
            providerCallId,
            nameof(providerCallId),
            256);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        AgentToolDefinition.ValidateIdentifier(
            stableCode,
            nameof(stableCode),
            MaximumStableCodeLength);
        ProposalId = proposalId;
        Generation = generation;
        ProviderCallId = providerCallId;
        Status = status;
        StableCode = stableCode;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string ProposalId { get; }

    public long Generation { get; }

    public string ProviderCallId { get; }

    public AgentToolResultStatus Status { get; }

    public string StableCode { get; }

    public AgentToolResultValue Value { get; }

    public bool ContainsUntrustedContent => true;
}

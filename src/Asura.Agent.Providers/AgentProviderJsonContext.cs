using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asura.Agent.Providers;

internal sealed record OpenAiSyntheticMessage(
    string Type,
    string Id,
    string Role,
    string Status,
    OpenAiSyntheticContent[] Content);

internal sealed record OpenAiSyntheticContent(
    string Type,
    string Text,
    JsonElement[] Annotations);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AiProviderOAuthSession))]
[JsonSerializable(typeof(OpenAiSyntheticMessage))]
internal sealed partial class AgentProviderJsonContext : JsonSerializerContext;

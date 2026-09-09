using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Reads historical local preference rows without relaxing definition imports.
/// Missing capabilities stay Off, and historical implicit maintenance routes
/// retain the row's primary provider/model rather than today's defaults.
/// </summary>
internal static class StoredAgentPolicyJson
{
    public static AgentPolicy? Read(string json)
    {
        AgentPolicy? policy;
        try
        {
            policy = DefinitionJson.DeserializeAgentPolicy(json);
        }
        catch (JsonException)
        {
            var legacy = JsonSerializer.Deserialize(json, LegacyStoredAgentPolicyJsonContext.Default.LegacyStoredAgentPolicy);
            if (legacy is null)
            {
                return null;
            }

            policy = new AgentPolicy(legacy.Provider, legacy.Model, legacy.Permissions)
            {
                CompactionModel = legacy.CompactionModel ?? new(legacy.Provider, legacy.Model),
                TitleModel = legacy.TitleModel ?? new(legacy.Provider, legacy.Model),
                SystemPrompt = legacy.SystemPrompt,
            };
        }

        if (policy?.Permissions is not { Count: > 0 } permissions
            || permissions.Any(pair => !Enum.IsDefined(pair.Key)
                || !Enum.IsDefined(pair.Value) || pair.Value == AgentPermission.Yolo))
        {
            return null;
        }

        return policy with
        {
            Permissions = AgentPolicy.Capabilities.ToImmutableDictionary(
                capability => capability,
                capability => permissions.GetValueOrDefault(capability, AgentPermission.Off)),
        };
    }
}

internal sealed record LegacyStoredAgentPolicy(
    string Provider,
    string Model,
    ImmutableDictionary<AgentCapability, AgentPermission> Permissions)
{
    public AgentModelSelection? CompactionModel { get; init; }

    public AgentModelSelection? TitleModel { get; init; }

    public string? SystemPrompt { get; init; }

    // The old serializer included this derived presentation property.
    public string? EffectiveSummary { get; init; }
}

[JsonSourceGenerationOptions(
    AllowDuplicateProperties = false,
    AllowTrailingCommas = false,
    MaxDepth = 64,
    PropertyNameCaseInsensitive = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LegacyStoredAgentPolicy))]
internal sealed partial class LegacyStoredAgentPolicyJsonContext : JsonSerializerContext;

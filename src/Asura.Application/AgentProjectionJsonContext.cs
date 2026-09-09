using System.Text.Json.Serialization;

namespace Asura.Application;

/// <summary>
/// Static JSON contracts used to enforce bounded agent-facing projections.
/// Projection code may shape generated metadata, but may never fall back to
/// discovering a runtime CLR shape.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentDatabaseReadResult.State), TypeInfoPropertyName = "DatabaseState")]
[JsonSerializable(typeof(AgentDatabaseReadResult.Objects))]
[JsonSerializable(typeof(AgentDatabaseReadResult.ObjectDescription))]
[JsonSerializable(typeof(AgentDatabaseReadResult.Table))]
[JsonSerializable(typeof(AgentDatabaseReadResult.Schema))]
[JsonSerializable(typeof(AgentDatabaseReadResult.RedisKeys))]
[JsonSerializable(typeof(AgentDatabaseReadResult.RedisValue))]
[JsonSerializable(typeof(AgentDatabaseReadResult.RedisSearch))]
[JsonSerializable(typeof(AgentDatabaseReadResult.RedisIndexes))]
[JsonSerializable(typeof(AgentDockerReadResult.State), TypeInfoPropertyName = "DockerState")]
[JsonSerializable(typeof(AgentDockerReadResult.Inspection))]
[JsonSerializable(typeof(AgentDockerReadResult.Logs))]
[JsonSerializable(typeof(AgentDockerReadResult.Files))]
[JsonSerializable(typeof(AgentDockerReadResult.FileStat))]
[JsonSerializable(typeof(AgentDockerReadResult.FileText))]
internal sealed partial class AgentProjectionJsonContext : JsonSerializerContext;

using System.Text.Json.Serialization;
using Asura.Application;

namespace Asura.Databases;

/// <summary>Static contracts for bounded provider-to-application projections.</summary>
[JsonSerializable(typeof(RedisServerFacts))]
[JsonSerializable(typeof(RedisKeyPage))]
[JsonSerializable(typeof(RedisKeyValueSnapshot))]
[JsonSerializable(typeof(RedisSearchResult))]
[JsonSerializable(typeof(RedisSearchIndexPage))]
[JsonSerializable(typeof(DatabaseObjectPage))]
[JsonSerializable(typeof(DatabaseObjectSnapshot))]
[JsonSerializable(typeof(DatabaseTablePage))]
[JsonSerializable(typeof(DatabaseTableSnapshot))]
[JsonSerializable(typeof(DatabaseSchemaGraphSnapshot))]
internal sealed partial class DatabaseProjectionJsonContext : JsonSerializerContext;

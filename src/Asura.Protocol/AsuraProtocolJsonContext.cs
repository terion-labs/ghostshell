using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asura.Protocol;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProtocolRequestEnvelope<ProtocolPing>))]
[JsonSerializable(typeof(ProtocolResponseEnvelope<ProtocolPong>))]
[JsonSerializable(typeof(ProtocolRequestEnvelope<JsonElement>))]
[JsonSerializable(typeof(ProtocolResponseEnvelope<JsonElement>))]
[JsonSerializable(typeof(ProtocolSessionEventEnvelope))]
public sealed partial class AsuraProtocolJsonContext : JsonSerializerContext;

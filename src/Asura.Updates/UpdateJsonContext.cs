using System.Text.Json.Serialization;

namespace Asura.Updates;

[JsonSerializable(typeof(DistributionManifest))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;

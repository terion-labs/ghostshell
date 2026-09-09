using System.Text.Json.Serialization;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>Static JSON contracts shared by platform infrastructure.</summary>
[JsonSerializable(typeof(SecretMetadata))]
internal sealed partial class InfrastructureJsonContext : JsonSerializerContext;

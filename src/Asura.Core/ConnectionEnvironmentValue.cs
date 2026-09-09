using System.Text.Json.Serialization;

namespace Asura.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ConnectionEnvironmentValue.PlainText), "plain-text")]
[JsonDerivedType(typeof(ConnectionEnvironmentValue.Secret), "secret")]
public abstract record ConnectionEnvironmentValue
{
    private ConnectionEnvironmentValue()
    {
    }

    public sealed record PlainText : ConnectionEnvironmentValue
    {
        [JsonConstructor]
        public PlainText(string value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public string Value { get; }
    }

    public sealed record Secret : ConnectionEnvironmentValue
    {
        [JsonConstructor]
        public Secret(SecretRef reference)
        {
            RuntimeId.Require(reference.Value, nameof(reference));
            Reference = reference;
        }

        public SecretRef Reference { get; }
    }
}

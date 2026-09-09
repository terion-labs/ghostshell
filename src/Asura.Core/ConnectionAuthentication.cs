using System.Text.Json.Serialization;

namespace Asura.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ConnectionAuthentication.None), "none")]
[JsonDerivedType(typeof(ConnectionAuthentication.SshAgent), "ssh-agent")]
[JsonDerivedType(typeof(ConnectionAuthentication.Password), "password")]
[JsonDerivedType(typeof(ConnectionAuthentication.PrivateKey), "private-key")]
public abstract record ConnectionAuthentication
{
    private ConnectionAuthentication()
    {
    }

    public sealed record None : ConnectionAuthentication;

    public sealed record SshAgent : ConnectionAuthentication;

    public sealed record Password : ConnectionAuthentication
    {
        [JsonConstructor]
        public Password(SecretRef passwordSecret)
        {
            RuntimeId.Require(passwordSecret.Value, nameof(passwordSecret));
            PasswordSecret = passwordSecret;
        }

        public SecretRef PasswordSecret { get; }
    }

    public sealed record PrivateKey : ConnectionAuthentication
    {
        [JsonConstructor]
        public PrivateKey(SecretRef privateKeySecret, SecretRef? passphraseSecret = null)
        {
            RuntimeId.Require(privateKeySecret.Value, nameof(privateKeySecret));
            if (passphraseSecret is { } value)
            {
                RuntimeId.Require(value.Value, nameof(passphraseSecret));
            }

            PrivateKeySecret = privateKeySecret;
            PassphraseSecret = passphraseSecret;
        }

        public SecretRef PrivateKeySecret { get; }

        public SecretRef? PassphraseSecret { get; }
    }
}

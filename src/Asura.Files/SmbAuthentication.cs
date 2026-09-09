using Asura.Core;

namespace Asura.Files;

/// <summary>
/// SMB authentication policy stored in a provider profile. Password values remain opaque vault
/// references and are resolved only while opening a network session.
/// </summary>
public abstract record SmbAuthentication
{
    private SmbAuthentication()
    {
    }

    public sealed record Guest : SmbAuthentication
    {
        public override string ToString() => "SMB guest authentication";
    }

    public sealed record Password : SmbAuthentication
    {
        public Password(string domain, string username, SecretRef passwordSecret)
        {
            ArgumentNullException.ThrowIfNull(domain);
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            if (string.IsNullOrWhiteSpace(passwordSecret.Value))
            {
                throw new ArgumentException(
                    "An SMB password must reference a stored credential.",
                    nameof(passwordSecret));
            }

            if (domain.Length > 256 || domain.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "An SMB domain must contain at most 256 printable characters.",
                    nameof(domain));
            }

            if (username.Length > 256 || username.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "An SMB username must contain at most 256 printable characters.",
                    nameof(username));
            }

            Domain = domain;
            Username = username;
            PasswordSecret = passwordSecret;
        }

        public string Domain { get; }

        public string Username { get; }

        public SecretRef PasswordSecret { get; }

        public override string ToString() => "SMB password authentication [opaque credential]";
    }
}

using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

internal sealed record McpServerCredentialBindingDescriptor(
    McpServerCredentialBindingKind Kind,
    string Name,
    SecretRef Reference);

/// <summary>
/// Centralizes the durable-definition relationships to opaque secret
/// references. Presentation and mutation owners use the same dependency rules.
/// </summary>
internal static class SecretDefinitionReferences
{
    public static bool Uses(ConnectionProfile connection, SecretRef reference) =>
        (connection.Authentication switch
        {
            ConnectionAuthentication.Password password =>
                password.PasswordSecret == reference,
            ConnectionAuthentication.PrivateKey privateKey =>
                privateKey.PrivateKeySecret == reference
                || privateKey.PassphraseSecret == reference,
            _ => false,
        })
        || connection.Startup.Environment.Any(variable =>
            variable.Value is ConnectionEnvironmentValue.Secret secret
            && secret.Reference == reference);

    public static bool Uses(FileProviderProfile profile, SecretRef reference) =>
        profile.Configuration switch
        {
            FileProviderConfiguration.S3 value =>
                value.CredentialsSecret == reference,
            FileProviderConfiguration.Ftp value =>
                value.PasswordSecret == reference,
            FileProviderConfiguration.Smb value =>
                value.PasswordSecret == reference,
            FileProviderConfiguration.WebDav value =>
                value.PasswordSecret == reference,
            _ => false,
        };

    public static bool Uses(AiProviderProfile profile, SecretRef reference) =>
        profile.Authentication is AiProviderAuthentication.ApiKey apiKey
        && apiKey.Secret == reference;

    public static bool Uses(McpServerProfile profile, SecretRef reference) =>
        EnumerateMcpServerCredentialBindings(profile).Any(binding =>
            binding.Reference == reference);

    public static bool Uses(BrowserProfileDefinition profile, SecretRef reference) =>
        profile.Authentication?.PasswordSecret == reference;

    public static IEnumerable<McpServerCredentialBindingDescriptor>
        EnumerateMcpServerCredentialBindings(McpServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        switch (profile.Transport)
        {
            case McpServerTransport.Stdio stdio:
                foreach (var binding in stdio.Environment)
                {
                    yield return new McpServerCredentialBindingDescriptor(
                        McpServerCredentialBindingKind.EnvironmentVariable,
                        binding.Name,
                        binding.Reference);
                }

                break;
            case McpServerTransport.StreamableHttp http:
                foreach (var header in http.Headers)
                {
                    yield return new McpServerCredentialBindingDescriptor(
                        McpServerCredentialBindingKind.HttpHeader,
                        header.Name,
                        header.Reference);
                }

                break;
            default:
                throw new InvalidOperationException(
                    "The MCP server transport is unavailable.");
        }
    }
}

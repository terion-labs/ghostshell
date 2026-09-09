using System.Security.Cryptography;
using System.Text;

namespace Asura.SecurityCampaign.Tests;

internal static class SecurityCampaignCanaries
{
    public const string ApplicationManaged =
        "campaign-application-managed-4d10f0c2";
    public const string VaultResolved =
        "campaign-vault-resolved-8a71ce35";

    public static IReadOnlyList<string> Values { get; } =
        [ApplicationManaged, VaultResolved];

    public static string Joined { get; } =
        ApplicationManaged + ":" + VaultResolved;

    public static string Digest { get; } =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Joined)));
}

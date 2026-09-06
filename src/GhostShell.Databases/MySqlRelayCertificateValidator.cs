using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GhostShell.Databases;

/// <summary>
/// MySqlConnector 2.4 only invokes custom validation in Required/Preferred mode
/// without SslCa. This callback supplies VerifyFull's hostname, chain and online
/// revocation checks against the logical server while TCP targets the relay.
/// SNI remains the relay address because the provider has no TargetHost hook.
/// </summary>
internal sealed class MySqlRelayCertificateValidator(string hostname, string caFile)
{
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? remoteChain, SslPolicyErrors errors)
    {
        _ = sender;
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            return false;
        }

        var roots = new X509Certificate2Collection();
        try
        {
            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            if (!leaf.MatchesHostname(hostname))
            {
                return false;
            }

            // SslCa supplements system trust in the provider; it is not a
            // certificate pin and must not disable ordinary chain validation.
            if (!string.IsNullOrWhiteSpace(caFile))
            {
                roots.ImportFromPemFile(caFile);
                if (roots.Count == 0)
                {
                    return false;
                }
            }

            return BuildChain(leaf, remoteChain, roots: null)
                || (roots.Count > 0 && BuildChain(leaf, remoteChain, roots));
        }
        catch (Exception exception) when (exception is
            CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        finally
        {
            foreach (var root in roots)
            {
                root.Dispose();
            }
        }
    }

    private static bool BuildChain(X509Certificate2 leaf, X509Chain? remoteChain, X509Certificate2Collection? roots)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (remoteChain is not null)
        {
            foreach (var element in remoteChain.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }
        if (roots is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.ExtraStore.AddRange(roots);
        }
        return chain.Build(leaf);
    }
}

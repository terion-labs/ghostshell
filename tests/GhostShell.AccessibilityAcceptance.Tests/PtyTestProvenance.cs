using System.Security.Cryptography;

namespace GhostShell.AccessibilityAcceptance;

internal static class PtyTestProvenance
{
    public static object Catalog => new
    {
        sourceCommit = new string('a', 40),
        sourceSha256 = new string('b', 64),
        patchSha256 = new string('c', 64),
        boundarySha256 = new string('d', 64),
    };

    public static object Create(string executableDirectory, string licenseDirectory)
    {
        var library = Path.Combine(executableDirectory, "libghostshell_pty.dylib");
        var license = Path.Combine(licenseDirectory, "PORTA-PTY-LICENSE");
        File.WriteAllBytes(library, [5, 6, 7, 8]);
        File.WriteAllText(license, "MIT PTY fixture");
        return new
        {
            sourceCommit = new string('a', 40),
            sourceSha256 = new string('b', 64),
            patchSha256 = new string('c', 64),
            boundarySha256 = new string('d', 64),
            abi = 1,
            artifact = new
            {
                path = "libghostshell_pty.dylib",
                bytes = new FileInfo(library).Length,
                sha256 = Hash(library),
                signatureRemovedSha256 = Hash(library)
            },
            license = new { path = "PORTA-PTY-LICENSE", bytes = new FileInfo(license).Length, sha256 = Hash(license) },
        };
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}

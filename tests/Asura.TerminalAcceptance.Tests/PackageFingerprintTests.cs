using System.Text.Json;

namespace Asura.TerminalAcceptance.Tests;

public sealed class PackageFingerprintTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateTempSubdirectory(
        "asura-terminal-acceptance-tests-").FullName;

    [Fact]
    public void Fingerprint_identifies_backend_and_hashes_the_complete_package()
    {
        var executable = Path.Combine(_temporaryDirectory, "Asura");
        File.WriteAllText(executable, "executable-v1");
        File.WriteAllText(Path.Combine(_temporaryDirectory, "support.dll"), "support-v1");
        WriteDependencyManifest();
        WriteNativeTerminalPackage(TargetPlatform.LinuxX11);

        var first = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-20260723-1");

        Assert.Equal("Asura", first.Build.PackageExecutable);
        Assert.Equal(5, first.Build.PackageFileCount);
        Assert.Equal(64, first.Build.ExecutableSha256.Length);
        Assert.Contains("libghostty-vt 0.1.0-dev", first.Backend.Renderer, StringComparison.Ordinal);
        Assert.Contains("Avalonia managed renderer", first.Backend.Renderer, StringComparison.Ordinal);
        Assert.Contains("Porta.Pty 1.0.7", first.Backend.PtyAdapter, StringComparison.Ordinal);
        Assert.Contains("Linux Unix PTY", first.Backend.PtySubstrate, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(_temporaryDirectory, "support.dll"), "support-v2");
        var second = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-20260723-1");

        Assert.NotEqual(first.Build.PackageManifestSha256, second.Build.PackageManifestSha256, StringComparer.Ordinal);
        Assert.Equal(first.Build.ExecutableSha256, second.Build.ExecutableSha256);
    }

    [Fact]
    public void Fingerprint_fails_when_backend_dependencies_cannot_be_proven()
    {
        File.WriteAllText(Path.Combine(_temporaryDirectory, "Asura"), "executable");
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, "Asura.deps.json"),
            "{\"libraries\":{}}\n");

        var exception = Assert.Throws<InvalidDataException>(() => PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-20260723-1"));

        Assert.Contains("Porta.Pty", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_fails_when_native_terminal_provenance_cannot_be_proven()
    {
        File.WriteAllText(Path.Combine(_temporaryDirectory, "Asura"), "executable");
        WriteDependencyManifest();
        File.WriteAllText(Path.Combine(_temporaryDirectory, "libghostty-vt.so"), "native");

        var exception = Assert.Throws<FileNotFoundException>(() => PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-20260723-1"));

        Assert.Contains("native-terminal-components.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_rejects_ambiguous_backend_versions()
    {
        File.WriteAllText(Path.Combine(_temporaryDirectory, "Asura"), "executable");
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, "Asura.deps.json"),
            "{\"libraries\":{\"Porta.Pty/1.0.7\":{},\"Porta.Pty/2.0.0\":{}}}\n");
        WriteNativeTerminalPackage(TargetPlatform.LinuxX11);

        var exception = Assert.Throws<InvalidDataException>(() => PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-20260723-1"));

        Assert.Contains("more than one Porta.Pty", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_temporaryDirectory, recursive: true);

    private void WriteDependencyManifest()
    {
        var manifest = new
        {
            libraries = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Asura/1.0.0"] = new { },
                ["Porta.Pty/1.0.7"] = new { },
            },
        };
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, "Asura.deps.json"),
            JsonSerializer.Serialize(manifest));
    }

    private void WriteNativeTerminalPackage(TargetPlatform platform)
    {
        var libraryName = platform == TargetPlatform.Windows
            ? "ghostty-vt.dll"
            : "libghostty-vt.so";
        File.WriteAllText(Path.Combine(_temporaryDirectory, libraryName), "native");
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, "native-terminal-components.json"),
            JsonSerializer.Serialize(new
            {
                component = new
                {
                    identity = "libghostty-vt/0.1.0-dev",
                },
            }));
    }
}

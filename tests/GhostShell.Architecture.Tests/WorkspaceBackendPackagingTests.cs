using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceBackendPackagingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ghostshell-backend-package-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("arm64")]
    [InlineData("x64")]
    public async Task BackendArchiveIsDeterministicAndContainsAnIntegrityManifest(string architecture)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var payload = CreatePayload(architecture);
        Task<string> Package(params string[] arguments) => RunForArchitectureAsync(architecture, arguments);
        var sourceDigest = await Package("source-digest", _directory);
        var distribution = Path.Combine(_directory, "distribution");
        _ = await Package("build", _directory, distribution, payload, sourceDigest, "10.0.111");
        _ = await Package("verify", _directory, distribution);
        var archive = Path.Combine(distribution, $"GhostShell-workspace-backend-{architecture}.tar.gz");
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(distribution, "build-receipt.json")));
        Assert.Equal($"linux-{architecture}", receipt.RootElement.GetProperty("rid").GetString());
        var first = await File.ReadAllBytesAsync(archive);
        File.Delete(Path.Combine(payload, "MANIFEST.sha256"));
        File.SetLastWriteTimeUtc(Path.Combine(payload, "GhostShell.Backend"), DateTime.UtcNow.AddDays(-30));
        _ = await Package("build", _directory, distribution, payload, sourceDigest, "10.0.111");
        Assert.Equal(first, await File.ReadAllBytesAsync(archive));
        using var file = File.OpenRead(archive);
        using var compressed = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(compressed);
        string? manifest = null;
        while (reader.GetNextEntry() is { } entry)
        {
            Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
            if (string.Equals(entry.Name, "MANIFEST.sha256", StringComparison.Ordinal))
            {
                using var text = new StreamReader(entry.DataStream!);
                manifest = await text.ReadToEndAsync();
            }
        }
        Assert.NotNull(manifest);
        Assert.Contains("  GhostShell.Backend\n", manifest, StringComparison.Ordinal);
        Assert.Contains("  GhostShell.Backend.runtimeconfig.json\n", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("MANIFEST.sha256", manifest, StringComparison.Ordinal);
        await File.AppendAllTextAsync(Path.Combine(_directory, "src", "input.cs"), "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Package("verify", _directory, distribution));
    }

    [Theory]
    [InlineData("Avalonia.Base.dll")]
    [InlineData("libcef.so")]
    [InlineData("GhostShell.Desktop.dll")]
    [InlineData("wrong-runtime.dylib")]
    [InlineData("bad\\path")]
    public async Task RejectsUiLibrariesAndUnsafeArchivePaths(string filename)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var payload = CreatePayload();
        await File.WriteAllTextAsync(Path.Combine(payload, filename), "unexpected payload");
        var sourceDigest = await RunAsync("source-digest", _directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("build", _directory,
            Path.Combine(_directory, "distribution"), payload, sourceDigest, "10.0.111"));
    }

    [Fact]
    public async Task RejectsWrongArchitectureAndRuntime()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var payload = CreatePayload();
        var sourceDigest = await RunAsync("source-digest", _directory);
        var executable = Path.Combine(payload, "GhostShell.Backend");
        var correctExecutable = await File.ReadAllBytesAsync(executable);
        await File.WriteAllTextAsync(executable, "not Linux ARM64");
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("build", _directory,
            Path.Combine(_directory, "distribution"), payload, sourceDigest, "10.0.111"));
        await File.WriteAllBytesAsync(executable, correctExecutable);
        await File.WriteAllTextAsync(Path.Combine(payload, "GhostShell.Backend.runtimeconfig.json"),
            """{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.1"}]}}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("build", _directory,
            Path.Combine(_directory, "distribution"), payload, sourceDigest, "10.0.111"));
    }

    [Fact]
    public async Task ReleaseStagingRejectsBytesChangedBetweenValidationAndCopy()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Directory.CreateDirectory(_directory);
        const string fixture = """
            import importlib.util, pathlib, sys
            spec = importlib.util.spec_from_file_location('sidecars', sys.argv[1])
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            source = pathlib.Path(sys.argv[2]) / 'source.tar.gz'
            destination = source.with_name('staged.tar.gz')
            source.write_bytes(b'original archive')
            pin = {'sha256': module.digest(source), 'size': source.stat().st_size}
            module.copy_pinned_archive(source, destination, pin)
            original_copy = module.shutil.copyfile
            def changed_copy(source_path, destination_path):
                original_copy(source_path, destination_path)
                pathlib.Path(destination_path).write_bytes(b'changed archive!')
            module.shutil.copyfile = changed_copy
            module.copy_pinned_archive(source, destination, pin)
            """;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunPythonAsync("-B", "-c", fixture,
            Path.Combine(FindRepositoryRoot(), "scripts", "package-networking-downloads.py"), _directory));
        Assert.Contains("Staged sidecar differs from signed app pin", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicationRequiresSeparateApprovalOfUnchangedLinuxEvidence()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] names = ["src/GhostShell.Backend/packages.linux-arm64.lock.json", "licenses/workspace-backend-managed-components.json", "licenses/SMBLIBRARY-SOURCE.json"];
        foreach (var name in names)
        {
            var path = Path.Combine(_directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "test evidence");
            inputs[name] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
        var record = Path.Combine(_directory, "licenses", "workspace-backend-release-legal.json");
        await File.WriteAllTextAsync(record, File.ReadAllText(Path.Combine(FindRepositoryRoot(), "licenses", "workspace-backend-release-legal.json")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("verify-release-clearance", _directory));
        // Synthetic approval applies only to disposable fixture bytes, not the
        // repository record or a real distribution decision.
        await File.WriteAllTextAsync(record, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            format = "ghostshell-workspace-backend-release-legal-v1",
            platform = "linux-arm64",
            runtime = "10.0.11",
            legalClearance = true,
            releaseBlockers = Array.Empty<string>(),
            review = new { status = "accepted-by-project-owner", basis = "test only", reviewedBy = "test fixture", reviewedAtUtc = "2026-09-08T00:00:00Z" },
            reviewedInputs = inputs,
        }));
        _ = await RunAsync("verify-release-clearance", _directory);
        await File.AppendAllTextAsync(Path.Combine(_directory, names[0]), "changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("verify-release-clearance", _directory));
    }

    [Fact]
    public async Task RejectsLinkedPayloadFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var payload = CreatePayload();
        File.CreateSymbolicLink(Path.Combine(payload, "linked.dll"), Path.Combine(_directory, "src", "input.cs"));
        var sourceDigest = await RunAsync("source-digest", _directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("build", _directory,
            Path.Combine(_directory, "distribution"), payload, sourceDigest, "10.0.111"));
    }

    [Fact]
    public void DesktopShipsOnlyTheDescriptorAndReleasePublishesBackendSeparately()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "GhostShell.Desktop", "GhostShell.Desktop.csproj"));
        var backends = project.Descendants("Content").Where(item =>
            ((string?)item.Attribute("Include"))?.Contains("GhostShellWorkspaceBackendDirectory", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(2, backends.Length);
        foreach (var architecture in new[] { "arm64", "x64" })
        {
            var backend = Assert.Single(backends, item => (string?)item.Attribute("Link") == $"runtimes/linux-{architecture}/workspace-backend/backend-assets.json");
            Assert.Equal(architecture is "arm64" ? "$(GhostShellWorkspaceBackendDirectory)/backend-assets.json"
                : "$(GhostShellWorkspaceBackendDirectory)/x64/backend-assets.json", (string?)backend.Attribute("Include"));
        }
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "repository-gate.yml"));
        Assert.Contains("./scripts/build-workspace-backend.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("/distribution/GhostShell-workspace-backend-arm64.tar.gz", workflow, StringComparison.Ordinal);
        Assert.Contains("GHOSTSHELL_BACKEND_ARCH=x64 ./scripts/build-workspace-backend.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("/distribution/GhostShell-workspace-backend-x64.tar.gz", workflow, StringComparison.Ordinal);
    }

    private string CreatePayload(string architecture = "arm64")
    {
        Directory.CreateDirectory(Path.Combine(_directory, "src"));
        File.WriteAllText(Path.Combine(_directory, "src", "input.cs"), "source");
        var payload = Path.Combine(_directory, "payload");
        Directory.CreateDirectory(payload);
        byte[] header = [0x7f, 0x45, 0x4c, 0x46, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xb7, 0];
        if (architecture is "x64") { header[18] = 0x3e; }
        File.WriteAllBytes(Path.Combine(payload, "GhostShell.Backend"), header);
        File.WriteAllText(Path.Combine(payload, "GhostShell.Backend.runtimeconfig.json"),
            """{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.11"}]}}""");
        return payload;
    }

    private static Task<string> RunAsync(params string[] arguments) =>
        RunPythonAsync([Path.Combine(FindRepositoryRoot(), "scripts", "package-workspace-backend.py"), .. arguments]);

    private static Task<string> RunForArchitectureAsync(string architecture, params string[] arguments) =>
        RunPythonAsync(["-c", "import os,runpy,sys; os.environ['GHOSTSHELL_BACKEND_ARCH']=sys.argv.pop(1); sys.argv=sys.argv[1:]; runpy.run_path(sys.argv[0],run_name='__main__')",
            architecture, Path.Combine(FindRepositoryRoot(), "scripts", "package-workspace-backend.py"), .. arguments]);

    private static async Task<string> RunPythonAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start backend packager.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var result = await output;
        var failure = await error;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(failure);
        }
        return result.Trim();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Packaging;

namespace Asura.AccessibilityAcceptance;

public sealed class CefRuntimePackageProvenanceTests : IDisposable
{
    private const string ArchiveSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PatchSetSha256 =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SourceSnapshotSha256 =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"asura-cef-package-tests-{Guid.NewGuid():N}");

    public CefRuntimePackageProvenanceTests() =>
        Directory.CreateDirectory(_temporaryDirectory);

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public void Receipt_validates_the_complete_supported_runtime_closure(string rid)
    {
        var fixture = CreateRuntime(rid);

        var inspection = CefRuntimeReceipt.Validate(
            fixture.RuntimeRoot,
            fixture.CatalogPath,
            rid);

        Assert.Equal(rid, inspection.Rid);
        Assert.Equal("150.0.9+test", inspection.Catalog.CefVersion);
        Assert.Equal(ArchiveSha256, inspection.ArchiveSha256);
        Assert.Equal(PatchSetSha256, inspection.PatchSetSha256);
        Assert.Equal(SourceSnapshotSha256, inspection.SourceSnapshotSha256);
        Assert.NotEmpty(inspection.Files);
        using var receipt = JsonDocument.Parse(inspection.ReceiptContent);
        Assert.Equal(
            SourceSnapshotSha256,
            receipt.RootElement.GetProperty("bindingSourceSnapshotSha256")
                .GetString());
    }

    [Fact]
    public void Reviewed_catalog_pins_the_vendored_binding_patch_manifest()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = CefRuntimeCatalog.Read(Path.Combine(
            repositoryRoot,
            "licenses",
            "cef-runtime-components.json"));

        Assert.Equal(
            "7751a0b76cbabaf1fa81ef2b71b694a44c87f77e",
            catalog.BindingCommit);
        Assert.Equal("0.8.0-asura.11", catalog.BindingVersion);
        Assert.Equal(
            Hash(Path.Combine(
                repositoryRoot,
                "vendor",
                "exclr8cef",
                "ASURA-PATCHSET.sha256")),
            catalog.BindingPatchSetSha256);
        Assert.Equal(
            Hash(Path.Combine(
                repositoryRoot,
                "vendor",
                "exclr8cef",
                "ASURA-SOURCE-SNAPSHOT.sha256")),
            catalog.BindingSourceSnapshotSha256);
        Assert.Equal(
            Hash(Path.Combine(repositoryRoot, "vendor", "exclr8cef", "LICENSE")),
            catalog.BindingLicenseSha256);
    }

    [Fact]
    public void Receipt_rejects_a_runtime_file_changed_after_staging()
    {
        var fixture = CreateRuntime("linux-x64");
        File.AppendAllText(
            Path.Combine(fixture.RuntimeRoot, "resources.pak"),
            "tampered");

        var exception = Assert.Throws<InvalidDataException>(() =>
            CefRuntimeReceipt.Validate(
                fixture.RuntimeRoot,
                fixture.CatalogPath,
                "linux-x64"));

        Assert.Contains("does not match its receipt", exception.Message);
    }

    [Fact]
    public void Receipt_rejects_a_missing_mac_helper_variant()
    {
        var fixture = CreateRuntime("osx-arm64", createReceipt: false);
        Directory.Delete(
            Path.Combine(
                fixture.RuntimeRoot,
                "Asura Helper (GPU).app"),
            recursive: true);

        var exception = Assert.Throws<InvalidDataException>(() =>
            CreateReceipt(fixture, "osx-arm64"));

        Assert.Contains("Asura Helper (GPU)", exception.Message);
    }

    [Fact]
    public void Receipt_rejects_an_unreviewed_mac_helper_variant()
    {
        var fixture = CreateRuntime("osx-arm64", createReceipt: false);
        Write(
            fixture.RuntimeRoot,
            "Asura Helper (Other).app/Contents/MacOS/Asura Helper (Other)",
            "unexpected helper");

        var exception = Assert.Throws<InvalidDataException>(() =>
            CreateReceipt(fixture, "osx-arm64"));

        Assert.Contains("exactly the five reviewed helper bundles", exception.Message);
    }

    [Fact]
    public void Receipt_rejects_a_snapshot_for_the_wrong_mac_architecture()
    {
        var fixture = CreateRuntime("osx-arm64", createReceipt: false);
        var frameworkResources = Path.Combine(
            fixture.RuntimeRoot,
            "Chromium Embedded Framework.framework",
            "Resources");
        File.Move(
            Path.Combine(frameworkResources, "v8_context_snapshot.arm64.bin"),
            Path.Combine(frameworkResources, "v8_context_snapshot.x86_64.bin"));

        var exception = Assert.Throws<InvalidDataException>(() =>
            CreateReceipt(fixture, "osx-arm64"));

        Assert.Contains("v8_context_snapshot.arm64.bin", exception.Message);
    }

    [Fact]
    public void Receipt_rejects_a_native_binary_for_the_wrong_architecture()
    {
        var fixture = CreateRuntime("linux-arm64", createReceipt: false);
        WriteElf(
            Path.Combine(fixture.RuntimeRoot, "libexclr8cef.so"),
            arm64: false);

        var exception = Assert.Throws<InvalidDataException>(() =>
            CreateReceipt(fixture, "linux-arm64"));

        Assert.Contains("wrong binary architecture", exception.Message);
    }

    [Fact]
    public void Mac_bundle_plan_places_framework_helpers_and_evidence_correctly()
    {
        var fixture = CreateRuntime("osx-arm64");
        var contents = Path.Combine(_temporaryDirectory, "Contents");
        Directory.CreateDirectory(contents);

        var plan = CefMacOsBundlePlan.Create(
            fixture.RuntimeRoot,
            fixture.CatalogPath,
            "osx-arm64");
        plan.CopyTo(contents);

        Assert.True(File.Exists(Path.Combine(
            contents,
            "Frameworks",
            "Chromium Embedded Framework.framework",
            "Chromium Embedded Framework")));
        Assert.True(File.Exists(Path.Combine(
            contents,
            "Frameworks",
            "Asura Helper (Renderer).app",
            "Contents",
            "MacOS",
            "Asura Helper (Renderer)")));
        Assert.True(File.Exists(Path.Combine(
            contents,
            "Frameworks",
            "libexclr8cef.dylib")));
        Assert.True(File.Exists(Path.Combine(
            contents,
            "MacOS",
            "libexclr8cef.dylib")));
        var spdxPath = Path.Combine(
            contents,
            "Resources",
            "Licenses",
            "CEF-SBOM.spdx.json");
        Assert.True(File.Exists(spdxPath));
        using var spdx = JsonDocument.Parse(File.ReadAllBytes(spdxPath));
        var packages = spdx.RootElement.GetProperty("packages").EnumerateArray()
            .ToArray();
        Assert.Equal(2, packages.Length);
        Assert.Equal(
            "0.8.0-asura.11",
            packages[1].GetProperty("versionInfo").GetString());
        Assert.All(
            packages,
            package => Assert.Equal(
                "NOASSERTION",
                package.GetProperty("copyrightText").GetString()));
        Assert.Equal(
            3,
            spdx.RootElement.GetProperty("relationships").GetArrayLength());
        Assert.Equal(
            plan.FileCount,
            Directory.EnumerateFiles(
                contents,
                "*",
                SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void Receipt_rejects_a_helper_with_mismatched_bundle_identity()
    {
        var fixture = CreateRuntime("osx-arm64", createReceipt: false);
        var plist = Path.Combine(
            fixture.RuntimeRoot,
            "Asura Helper.app",
            "Contents",
            "Info.plist");
        File.WriteAllText(
            plist,
            HelperPropertyList("Asura Helper", "app.wrong.helper"));

        var exception = Assert.Throws<InvalidDataException>(() =>
            CreateReceipt(fixture, "osx-arm64"));

        Assert.Contains("mismatched bundle identity", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private RuntimeFixture CreateRuntime(string rid, bool createReceipt = true)
    {
        var root = Path.Combine(
            _temporaryDirectory,
            $"{rid}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Write(root, "CEF-LICENSE.txt", "test CEF license");
        Write(root, "CEF-CREDITS.html", "<html>test credits</html>");
        Write(root, "EXCLR8CEF-LICENSE.txt", "test binding license");

        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            CreateMacPayload(root, rid);
        }
        else if (string.Equals(rid, "win-x64", StringComparison.Ordinal))
        {
            CreateFlatPayload(root, rid, windows: true);
        }
        else
        {
            CreateFlatPayload(root, rid, windows: false);
        }

        var catalog = WriteCatalog(root);
        var fixture = new RuntimeFixture(root, catalog);
        if (createReceipt)
        {
            CreateReceipt(fixture, rid);
        }

        return fixture;
    }

    private static void CreateFlatPayload(string root, string rid, bool windows)
    {
        string[] files = windows
            ?
            [
                "exclr8cef.dll",
                "libcef.dll",
                "chrome_elf.dll",
                "d3dcompiler_47.dll",
                "dxcompiler.dll",
                "dxil.dll",
                "libEGL.dll",
                "libGLESv2.dll",
                "vk_swiftshader.dll",
                "vk_swiftshader_icd.json",
                "vulkan-1.dll",
                "icudtl.dat",
                "resources.pak",
                "chrome_100_percent.pak",
                "chrome_200_percent.pak",
                "v8_context_snapshot.bin",
                "locales/en-US.pak",
            ]
            :
            [
                "libexclr8cef.so",
                "libcef.so",
                "libEGL.so",
                "libGLESv2.so",
                "libvk_swiftshader.so",
                "libvulkan.so.1",
                "vk_swiftshader_icd.json",
                "chrome-sandbox",
                "icudtl.dat",
                "resources.pak",
                "chrome_100_percent.pak",
                "chrome_200_percent.pak",
                "v8_context_snapshot.bin",
                "locales/en-US.pak",
            ];
        foreach (var path in files)
        {
            Write(root, path, $"runtime file {path}");
            var fullPath = Path.Combine(root, path);
            if (windows && path.EndsWith(".dll", StringComparison.Ordinal))
            {
                WritePeX64(fullPath);
            }
            else if (!windows
                     && (path.EndsWith(".so", StringComparison.Ordinal)
                         || path.EndsWith(".so.1", StringComparison.Ordinal)
                         || string.Equals(path, "chrome-sandbox", StringComparison.Ordinal)))
            {
                WriteElf(fullPath, arm64: string.Equals(rid, "linux-arm64", StringComparison.Ordinal));
            }
        }

        if (!windows)
        {
            MakeExecutable(Path.Combine(root, "chrome-sandbox"));
        }
    }

    private static void CreateMacPayload(string root, string rid)
    {
        const string framework = "Chromium Embedded Framework.framework";
        var architecture = string.Equals(rid, "osx-arm64", StringComparison.Ordinal) ? "arm64" : "x86_64";
        Write(root, "libexclr8cef.dylib", "binding shim");
        Write(root, $"{framework}/Chromium Embedded Framework", "cef binary");
        Write(root, $"{framework}/Libraries/libEGL.dylib", "egl");
        Write(root, $"{framework}/Libraries/libGLESv2.dylib", "gles");
        Write(root, $"{framework}/Libraries/libcef_sandbox.dylib", "sandbox");
        Write(root, $"{framework}/Libraries/libvk_swiftshader.dylib", "vulkan");
        Write(root, $"{framework}/Libraries/vk_swiftshader_icd.json", "{}");
        WriteMachO(Path.Combine(root, "libexclr8cef.dylib"), architecture);
        WriteMachO(
            Path.Combine(root, framework, "Chromium Embedded Framework"),
            architecture);
        WriteMachO(
            Path.Combine(root, framework, "Libraries", "libEGL.dylib"),
            architecture);
        WriteMachO(
            Path.Combine(root, framework, "Libraries", "libGLESv2.dylib"),
            architecture);
        WriteMachO(
            Path.Combine(root, framework, "Libraries", "libcef_sandbox.dylib"),
            architecture);
        WriteMachO(
            Path.Combine(root, framework, "Libraries", "libvk_swiftshader.dylib"),
            architecture);
        MakeExecutable(Path.Combine(root, "libexclr8cef.dylib"));
        MakeExecutable(Path.Combine(root, framework, "Chromium Embedded Framework"));
        MakeExecutable(Path.Combine(root, framework, "Libraries", "libEGL.dylib"));
        MakeExecutable(Path.Combine(root, framework, "Libraries", "libGLESv2.dylib"));
        MakeExecutable(Path.Combine(root, framework, "Libraries", "libcef_sandbox.dylib"));
        MakeExecutable(Path.Combine(root, framework, "Libraries", "libvk_swiftshader.dylib"));
        Write(root, $"{framework}/Resources/Info.plist", "framework plist");
        Write(root, $"{framework}/Resources/chrome_100_percent.pak", "chrome pak");
        Write(root, $"{framework}/Resources/chrome_200_percent.pak", "chrome hidpi pak");
        Write(root, $"{framework}/Resources/gpu_shader_cache.bin", "gpu cache");
        Write(root, $"{framework}/Resources/icudtl.dat", "icu");
        Write(root, $"{framework}/Resources/resources.pak", "resources");
        Write(root, $"{framework}/Resources/en.lproj/locale.pak", "locale");
        Write(
            root,
            $"{framework}/Resources/v8_context_snapshot.{architecture}.bin",
            "snapshot");

        var suffixes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [string.Empty] = string.Empty,
            [" (Alerts)"] = ".alerts",
            [" (GPU)"] = ".gpu",
            [" (Plugin)"] = ".plugin",
            [" (Renderer)"] = ".renderer",
        };
        foreach (var (suffix, identifierSuffix) in suffixes)
        {
            var name = $"Asura Helper{suffix}";
            var bundle = $"{name}.app/Contents";
            Write(root, $"{bundle}/MacOS/{name}", $"helper {name}");
            var executable = Path.Combine(root, $"{bundle}/MacOS/{name}");
            WriteMachO(executable, architecture);
            MakeExecutable(executable);
            Write(
                root,
                $"{bundle}/Info.plist",
                HelperPropertyList(
                    name,
                    $"sh.asura.helper{identifierSuffix}"));
        }
    }

    private string WriteCatalog(string runtimeRoot)
    {
        var path = Path.Combine(
            _temporaryDirectory,
            $"catalog-{Guid.NewGuid():N}.json");
        var distributions = new[]
        {
            ("linux-arm64", "linuxarm64", new string('1', 40)),
            ("linux-x64", "linux64", new string('2', 40)),
            ("osx-arm64", "macosarm64", new string('3', 40)),
            ("osx-x64", "macosx64", new string('4', 40)),
            ("win-x64", "windows64", new string('5', 40)),
        }.Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rid"] = item.Item1,
            ["platform"] = item.Item2,
            ["archiveSha1"] = item.Item3,
            ["archiveSha256"] = ArchiveSha256,
            ["cefLicenseSha256"] = Hash(Path.Combine(
                runtimeRoot,
                "CEF-LICENSE.txt")),
            ["cefCreditsSha256"] = Hash(Path.Combine(
                runtimeRoot,
                "CEF-CREDITS.html")),
        });
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["schemaVersion"] = 1,
                    ["documentCreatedUtc"] = "2026-08-08T00:00:00Z",
                    ["cefVersion"] = "150.0.9+test",
                    ["bindingRepository"] = "https://example.test/exclr8cef",
                    ["bindingCommit"] = new string('c', 40),
                    ["bindingVersion"] = "0.8.0-asura.11",
                    ["bindingPatchSetSha256"] = PatchSetSha256,
                    ["bindingSourceSnapshotSha256"] = SourceSnapshotSha256,
                    ["bindingLicenseSha256"] = Hash(Path.Combine(
                        runtimeRoot,
                        "EXCLR8CEF-LICENSE.txt")),
                    ["releaseBlockers"] = new[] { "Test release review remains open." },
                    ["distributions"] = distributions,
                },
                new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static void CreateReceipt(RuntimeFixture fixture, string rid) =>
        CefRuntimeReceipt.Create(
            fixture.RuntimeRoot,
            fixture.CatalogPath,
            rid,
            rid switch
            {
                "linux-arm64" => new string('1', 40),
                "linux-x64" => new string('2', 40),
                "osx-arm64" => new string('3', 40),
                "osx-x64" => new string('4', 40),
                "win-x64" => new string('5', 40),
                _ => throw new ArgumentOutOfRangeException(nameof(rid)),
            },
            ArchiveSha256,
            PatchSetSha256,
            SourceSnapshotSha256,
            Path.Combine(fixture.RuntimeRoot, CefRuntimeReceipt.FileName));

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute);
    }

    private static void WriteMachO(string path, string architecture)
    {
        var bytes = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xfeedfacf);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(4),
string.Equals(architecture, "arm64", StringComparison.Ordinal) ? 0x0100000c : 0x01000007);
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteElf(string path, bool arm64)
    {
        var bytes = new byte[64];
        bytes[0] = 0x7f;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 2;
        bytes[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(18),
            arm64 ? (ushort)183 : (ushort)62);
        File.WriteAllBytes(path, bytes);
    }

    private static void WritePeX64(string path)
    {
        var bytes = new byte[128];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664);
        File.WriteAllBytes(path, bytes);
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            "The Asura repository root was not found.");
    }

    private static string HelperPropertyList(string name, string identifier) =>
        $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
          <dict>
            <key>CFBundleDisplayName</key><string>{{name}}</string>
            <key>CFBundleExecutable</key><string>{{name}}</string>
            <key>CFBundleIdentifier</key><string>{{identifier}}</string>
            <key>CFBundleName</key><string>{{name}}</string>
            <key>CFBundlePackageType</key><string>APPL</string>
          </dict>
        </plist>
        """;

    private sealed record RuntimeFixture(
        string RuntimeRoot,
        string CatalogPath);
}

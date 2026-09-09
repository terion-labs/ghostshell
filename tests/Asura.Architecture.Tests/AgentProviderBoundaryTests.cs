using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Asura.Architecture.Tests;

public sealed class AgentProviderBoundaryTests
{
    private const string ProviderAssemblyFileName = "Asura.Agent.Providers.dll";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProviderProjectReferencesOnlyAgentApplicationAndCore()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.Agent.Providers",
            "Asura.Agent.Providers.csproj"));
        var projectReferences = project
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => value is not null)
            .Cast<string>()
            .Select(value => Path.GetFileName(
                value.Replace('\\', Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Asura.Agent.csproj",
                "Asura.Application.csproj",
                "Asura.Core.csproj",
            },
            projectReferences);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.DoesNotContain(
            project.Descendants(),
            element => element.Name.LocalName is
                "COMReference" or
                "FrameworkReference" or
                "NativeReference" or
                "PackageDownload" or
                "Reference");
    }

    [Fact]
    public void ProviderSourceContainsNoTerminalFilesystemProcessOrNativeAuthority()
    {
        var sourceRoot = Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.Agent.Providers");
        var banned = new[]
        {
            "Asura.SessionHost",
            "Asura.Terminal",
            "ISessionHostClient",
            "TerminalSendKeysRequest",
            "TerminalWriteRequest",
            "System.Diagnostics.Process",
            "ProcessStartInfo",
            "Process.Start",
            "System.IO.Directory",
            "System.IO.File",
            "System.IO.Pipes",
            "Directory.CreateDirectory",
            "Directory.Delete",
            "Directory.Enumerate",
            "Directory.Get",
            "File.Append",
            "File.Copy",
            "File.Create",
            "File.Delete",
            "File.Move",
            "File.Open",
            "File.Read",
            "File.Replace",
            "File.Write",
            "FileStream",
            "FileSystemWatcher",
            "Microsoft.Win32",
            "System.Reflection.Emit",
            "System.Runtime.InteropServices",
            "System.Runtime.Loader",
            "AssemblyLoadContext",
            "DllImport",
            "LibraryImport",
            "NativeLibrary",
            "UnmanagedCallersOnly",
        };

        foreach (var file in Directory
                     .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !HasPathSegment(path, "bin")
                         && !HasPathSegment(path, "obj")))
        {
            var source = File.ReadAllText(file);
            Assert.All(
                banned,
                value => Assert.DoesNotContain(value, source, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CompiledProviderAssemblyHasNoTerminalFilesystemProcessOrNativeAuthority()
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, ProviderAssemblyFileName);
        Assert.True(
            File.Exists(assemblyPath),
            $"{ProviderAssemblyFileName} must be copied from the provider project reference.");

        using var stream = File.OpenRead(assemblyPath);
        using var portableExecutable = new PEReader(stream);
        var headers = portableExecutable.PEHeaders;
        Assert.NotNull(headers.CorHeader);
        Assert.True(headers.CorHeader.Flags.HasFlag(CorFlags.ILOnly));
        Assert.Equal(0, headers.CorHeader.EntryPointTokenOrRelativeVirtualAddress);

        var metadata = portableExecutable.GetMetadataReader();
        var asuraReferences = metadata.AssemblyReferences
            .Select(handle => metadata.GetAssemblyReference(handle))
            .Select(reference => metadata.GetString(reference.Name))
            .Where(name => name.StartsWith("Asura.", StringComparison.Ordinal))
            .ToArray();
        Assert.All(
            asuraReferences,
            name => Assert.Contains(
                name,
                [
                    "Asura.Agent",
                    "Asura.Application",
                    "Asura.Core",
                ], StringComparer.Ordinal));

        var forbiddenAssemblyReferences = new HashSet<string>(
            [
                "Asura.Desktop",
                "Asura.Files",
                "Asura.Infrastructure",
                "Asura.Protocol",
                "Asura.SessionHost",
                "Asura.Terminal",
                "Microsoft.Win32.Registry",
                "System.Diagnostics.Process",
                "System.IO.FileSystem",
                "System.IO.FileSystem.Watcher",
            ],
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            metadata.AssemblyReferences
                .Select(handle => metadata.GetAssemblyReference(handle))
                .Select(reference => metadata.GetString(reference.Name)),
            forbiddenAssemblyReferences.Contains);

        var forbiddenExactTypes = new HashSet<string>(
            [
                "Microsoft.Win32.Registry",
                "System.Diagnostics.Process",
                "System.Diagnostics.ProcessStartInfo",
                "System.IO.Directory",
                "System.IO.DirectoryInfo",
                "System.IO.DriveInfo",
                "System.IO.File",
                "System.IO.FileInfo",
                "System.IO.FileStream",
                "System.IO.FileSystemInfo",
                "System.IO.FileSystemWatcher",
                "System.IO.RandomAccess",
                "System.Reflection.Emit.AssemblyBuilder",
                "System.Runtime.InteropServices.DllImportAttribute",
                "System.Runtime.InteropServices.LibraryImportAttribute",
                "System.Runtime.InteropServices.Marshal",
                "System.Runtime.InteropServices.NativeLibrary",
                "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute",
                "System.Runtime.Loader.AssemblyLoadContext",
            ],
            StringComparer.Ordinal);
        var referencedTypes = metadata.TypeReferences
            .Select(handle => metadata.GetTypeReference(handle))
            .Select(reference =>
                $"{metadata.GetString(reference.Namespace)}.{metadata.GetString(reference.Name)}")
            .ToArray();

        Assert.DoesNotContain(
            referencedTypes,
            typeName => forbiddenExactTypes.Contains(typeName)
                || typeName.StartsWith("Microsoft.Win32.Registry", StringComparison.Ordinal)
                || typeName.StartsWith("System.IO.Pipes.", StringComparison.Ordinal)
                || IsTerminalOrSessionHostType(typeName));
        Assert.DoesNotContain(
            metadata.MethodDefinitions
                .Select(handle => metadata.GetMethodDefinition(handle)),
            definition => definition.Attributes.HasFlag(MethodAttributes.PinvokeImpl));
    }

    private static bool IsTerminalOrSessionHostType(string typeName)
    {
        if (typeName.StartsWith("Asura.SessionHost.", StringComparison.Ordinal)
            || typeName.StartsWith("Asura.Terminal.", StringComparison.Ordinal))
        {
            return true;
        }

        if (!typeName.StartsWith("Asura.Application.", StringComparison.Ordinal))
        {
            return false;
        }

        var name = typeName["Asura.Application.".Length..];
        return name.Contains("Terminal", StringComparison.Ordinal)
            || name.Contains("SessionHost", StringComparison.Ordinal);
    }

    private static bool HasPathSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar).Contains(segment, StringComparer.Ordinal);

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

        throw new DirectoryNotFoundException("Unable to locate the Asura repository root.");
    }
}

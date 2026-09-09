using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Asura.Agent;
using Asura.Agent.Runtime;

namespace Asura.Architecture.Tests;

public sealed class AgentRuntimeBoundaryTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void NativeAgentProjectDependsOnlyOnCoreAndTheBcl()
    {
        var projectPath = Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.Agent",
            "Asura.Agent.csproj");
        var project = XDocument.Load(projectPath);
        Assert.Equal("Microsoft.NET.Sdk", (string?)project.Root?.Attribute("Sdk"));
        Assert.Equal("net10.0", project.Descendants("TargetFramework").Single().Value);
        Assert.All(
            project.Descendants("OutputType"),
            element => Assert.Equal("Library", element.Value));
        var projectReference = Assert.Single(project.Descendants("ProjectReference"));
        Assert.EndsWith(
            "Asura.Core.csproj",
            (string?)projectReference.Attribute("Include"),
            StringComparison.Ordinal);
        var forbiddenElements = new HashSet<string>(
            [
                "PackageReference",
                "FrameworkReference",
                "Reference",
                "COMReference",
                "NativeReference",
                "PackageDownload",
                "Analyzer",
                "UsingTask",
                "Import",
                "Exec",
                "Target",
            ],
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            project.Descendants(),
            element => forbiddenElements.Contains(element.Name.LocalName));
        Assert.DoesNotContain(
            project.Descendants("Compile"),
            element => element.Attribute("Link") is not null
                || element.Attribute("Include") is not null);
    }

    [Fact]
    public void NativeAgentSourceHasNoExecutionOrVendorRuntimeBackdoor()
    {
        var sourceRoot = Path.Combine(RepositoryRoot, "src", "Asura.Agent");
        var banned = new[]
        {
            "System.Diagnostics",
            "ProcessStartInfo",
            "Process.Start",
            "ISessionHostClient",
            "TerminalWriteRequest",
            "TerminalSendKeysRequest",
            "InputLease",
            "Asura.SessionHost",
            "Asura.Terminal",
            "Asura.Files",
            "Asura.Infrastructure",
            "ISecretVault",
            "SecretMaterial",
            "HttpClient",
            "System.Net.",
            "System.IO.File",
            "System.IO.Pipes",
            "System.Linq.Expressions",
            "System.Reflection.Emit",
            "AssemblyLoadContext",
            "NativeLibrary",
            "DllImport",
            "LibraryImport",
            "Microsoft.Win32",
            "Console.",
            "Environment.",
            "Node.js",
            "package.json",
            "Func<",
            "Action<",
        };

        foreach (var file in Directory
                     .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj")))
        {
            var source = File.ReadAllText(file);
            Assert.All(
                banned,
                value => Assert.DoesNotContain(value, source, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NativeAgentProjectContainsNoExecutableOrNodeAssets()
    {
        var sourceRoot = Path.Combine(RepositoryRoot, "src", "Asura.Agent");
        var forbiddenExtensions = new HashSet<string>(
            [
                ".a",
                ".bash",
                ".bat",
                ".cmd",
                ".cjs",
                ".dll",
                ".dylib",
                ".exe",
                ".jar",
                ".js",
                ".lib",
                ".mjs",
                ".node",
                ".pl",
                ".ps1",
                ".py",
                ".rb",
                ".sh",
                ".so",
                ".ts",
                ".tsx",
                ".wasm",
                ".zsh",
            ],
            StringComparer.OrdinalIgnoreCase);
        var forbidden = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"))
            .Where(path =>
                forbiddenExtensions.Contains(Path.GetExtension(path))
                || string.Equals(Path.GetFileName(path), "package.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(forbidden);
        Assert.DoesNotContain(
            XDocument.Load(Path.Combine(sourceRoot, "Asura.Agent.csproj"))
                .Descendants()
                .Select(element => (string?)element.Attribute("Include"))
                .Where(value => value is not null)
                .Cast<string>(),
            value => value.Contains("node", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompiledNativeAgentAssemblyHasNoAmbientExecutionCapability()
    {
        var assemblyPath = typeof(NativeAgentSession).Assembly.Location;
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
        Assert.Equal(["Asura.Core"], asuraReferences);

        var forbiddenExactTypes = new HashSet<string>(
            [
                "System.Console",
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
                "System.IO.StreamReader",
                "System.IO.StreamWriter",
                "System.Reflection.Emit.AssemblyBuilder",
                "System.Runtime.InteropServices.DllImportAttribute",
                "System.Runtime.InteropServices.GCHandle",
                "System.Runtime.InteropServices.LibraryImportAttribute",
                "System.Runtime.InteropServices.Marshal",
                "System.Runtime.InteropServices.NativeLibrary",
                "System.Runtime.Loader.AssemblyLoadContext",
                "System.Net.FtpWebRequest",
                "System.Net.HttpWebRequest",
                "System.Net.WebClient",
                "System.Net.WebRequest",
                "Microsoft.Win32.Registry",
            ],
            StringComparer.Ordinal);
        var forbiddenNamespacePrefixes = new[]
        {
            "Microsoft.Win32.",
            "System.IO.",
            "System.Linq.Expressions.",
            "System.Net.",
            "System.Reflection.Emit.",
        };
        var referencedTypes = metadata.TypeReferences
            .Select(handle => metadata.GetTypeReference(handle))
            .Select(reference =>
                $"{metadata.GetString(reference.Namespace)}.{metadata.GetString(reference.Name)}")
            .ToArray();
        Assert.DoesNotContain(
            referencedTypes,
            typeName => forbiddenExactTypes.Contains(typeName)
                || forbiddenNamespacePrefixes.Any(
                    prefix => typeName.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.All(
            referencedTypes.Where(
                typeName => typeName.StartsWith("Asura.Core.", StringComparison.Ordinal)),
            typeName => Assert.Contains(
                typeName,
                [
                    "Asura.Core.AgentImageAttachment",
                    "Asura.Core.AgentReasoningEffort",
                    "Asura.Core.AgentRunId",
                    "Asura.Core.AgentServiceTier",
                    "Asura.Core.AgentSessionCheckpoint",
                    "Asura.Core.AiProviderKind",
                    "Asura.Core.AiProviderProfileId",
                    "Asura.Core.AiProviderProtocol",
                    "Asura.Core.LiteralSecretValidator",
                ], StringComparer.Ordinal));
        var environmentMembers = metadata.MemberReferences
            .Select(handle => metadata.GetMemberReference(handle))
            .Where(reference => reference.Parent.Kind == HandleKind.TypeReference)
            .Where(reference =>
            {
                var declaringType = metadata.GetTypeReference(
                    (TypeReferenceHandle)reference.Parent);
                return string.Equals(metadata.GetString(declaringType.Namespace), "System"
, StringComparison.Ordinal) && string.Equals(metadata.GetString(declaringType.Name), "Environment", StringComparison.Ordinal);
            })
            .Select(reference => metadata.GetString(reference.Name));
        Assert.All(
            environmentMembers,
            memberName => Assert.Equal("get_CurrentManagedThreadId", memberName));

        Assert.DoesNotContain(
            metadata.MethodDefinitions
                .Select(handle => metadata.GetMethodDefinition(handle)),
            definition => definition.Attributes.HasFlag(MethodAttributes.PinvokeImpl));
    }

    [Fact]
    public void PublicAgentBoundaryExposesOnlyProviderAndCompactorSeams()
    {
        var assembly = typeof(NativeAgentSession).Assembly;
        Assert.Equal(
            [
                typeof(IAgentConversationCompactor),
                typeof(IAgentProvider),
            ],
            assembly.GetExportedTypes()
                .Where(type => type.IsInterface)
                .OrderBy(type => type.FullName, StringComparer.Ordinal));
        Assert.DoesNotContain(
            assembly.GetExportedTypes(),
            type => typeof(Delegate).IsAssignableFrom(type));

        Assert.Empty(typeof(AgentProviderRequest).GetConstructors());
        Assert.True(typeof(NativeAgentSession).IsSealed);
        var sessionConstructor = Assert.Single(typeof(NativeAgentSession).GetConstructors());
        Assert.DoesNotContain(
            sessionConstructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(TimeProvider));
        Assert.Equal(
            [
                nameof(NativeAgentSession.Cancel),
                nameof(NativeAgentSession.CaptureCheckpoint),
                nameof(NativeAgentSession.CaptureInterruptedCheckpoint),
                nameof(NativeAgentSession.CaptureInterruptedCheckpoint),
                nameof(NativeAgentSession.CaptureInterruptedCheckpoint),
                nameof(NativeAgentSession.CompactAsync),
                nameof(NativeAgentSession.CompactAsync),
                nameof(NativeAgentSession.DescribeConversation),
                nameof(NativeAgentSession.EstimateContextUsage),
                nameof(NativeAgentSession.RunTurnAsync),
                nameof(NativeAgentSession.RunTurnAsync),
                nameof(NativeAgentSession.RunTurnAsync),
                nameof(NativeAgentSession.Snapshot),
                nameof(NativeAgentSession.Steer),
                nameof(NativeAgentSession.SubmitToolResultsAsync),
                nameof(NativeAgentSession.TryRebaseSystemPrompt),
                nameof(NativeAgentSession.TrySetConversationRoute),
                nameof(NativeAgentSession.TrySetConversationTitle),
                nameof(NativeAgentSession.WatchAsync),
            ],
            typeof(NativeAgentSession)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.DoesNotContain(
            typeof(NativeAgentSession).GetFields(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance),
            field => IsCapabilityBearingField(field.FieldType));
    }

    [Fact]
    public void GovernedAgentRuntimeKeepsProviderAdaptersAndPlatformAuthorityOutside()
    {
        var projectPath = Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.Agent.Runtime",
            "Asura.Agent.Runtime.csproj");
        var project = XDocument.Load(projectPath);
        Assert.Equal("Microsoft.NET.Sdk", (string?)project.Root?.Attribute("Sdk"));
        Assert.Equal("net10.0", project.Descendants("TargetFramework").Single().Value);
        Assert.Equal(
            [
                "Asura.Agent.csproj",
                "Asura.Application.csproj",
                "Asura.Core.csproj",
            ],
            project.Descendants("ProjectReference")
                .Select(reference => ((string?)reference.Attribute("Include"))!
                    .Replace('\\', '/'))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Empty(project.Descendants("PackageReference"));

        var assemblyReferences = typeof(GovernedAgentRuntime).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith(
                "Asura.",
                StringComparison.Ordinal) == true)
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            [
                "Asura.Agent",
                "Asura.Application",
                "Asura.Core",
            ],
            assemblyReferences, StringComparer.Ordinal);

        var sourceRoot = Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.Agent.Runtime");
        var forbidden = new[]
        {
            "Asura.Agent.Providers",
            "Asura.Desktop",
            "Asura.Infrastructure",
            "Asura.SessionHost",
            "Asura.Terminal",
            "Avalonia",
            "HttpClient",
            "ISecretVault",
            "SecretMaterial",
            "System.Diagnostics.Process",
            "System.IO.File",
            "System.IO.Directory",
            "NativeLibrary",
            "DllImport",
            "LibraryImport",
            "IServiceProvider",
            "Node.js",
            "package.json",
        };
        foreach (var file in Directory
                     .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path =>
                         !HasPathSegment(path, "bin")
                         && !HasPathSegment(path, "obj")))
        {
            var source = File.ReadAllText(file);
            Assert.All(
                forbidden,
                value => Assert.DoesNotContain(
                    value,
                    source,
                    StringComparison.Ordinal));
        }

        Assert.Equal(
            [
                typeof(IAgentProviderBinding),
                typeof(IAgentProviderResolver),
            ],
            typeof(GovernedAgentRuntime).Assembly
                .GetExportedTypes()
                .Where(type => type.IsInterface)
                .OrderBy(type => type.FullName, StringComparer.Ordinal));
        Assert.DoesNotContain(
            typeof(GovernedAgentRuntime).Assembly.GetExportedTypes(),
            type => typeof(Delegate).IsAssignableFrom(type));
    }

    [Fact]
    public void ToolProposalsContainClosedImmutableDataOnly()
    {
        var proposalType = typeof(AgentToolProposal);
        Assert.True(proposalType.IsSealed);
        Assert.Empty(proposalType.GetConstructors());
        Assert.Empty(proposalType.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(proposalType.GetEvents(BindingFlags.Public | BindingFlags.Instance));
        var properties = proposalType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                nameof(AgentToolProposal.Arguments),
                nameof(AgentToolProposal.ContainsUntrustedContent),
                nameof(AgentToolProposal.Generation),
                nameof(AgentToolProposal.Id),
                nameof(AgentToolProposal.ProviderCallId),
                nameof(AgentToolProposal.ProviderName),
                nameof(AgentToolProposal.ToolName),
            ],
            properties.Select(property => property.Name), StringComparer.Ordinal);
        Assert.All(properties, property => Assert.Null(property.SetMethod));
        Assert.All(
            properties,
            property => Assert.Contains(
                property.PropertyType,
                new[]
                {
                    typeof(bool),
                    typeof(System.Text.Json.JsonElement),
                    typeof(long),
                    typeof(string),
                }));
        Assert.All(
            proposalType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => Assert.True(method.IsSpecialName));
    }

    private static bool IsCapabilityBearingField(Type type) =>
        typeof(Delegate).IsAssignableFrom(type)
        || typeof(IAgentProvider).IsAssignableFrom(type)
        || typeof(IAgentConversationCompactor).IsAssignableFrom(type)
        || typeof(IServiceProvider).IsAssignableFrom(type)
        || typeof(Stream).IsAssignableFrom(type)
        || typeof(SafeHandle).IsAssignableFrom(type)
        || type.IsPointer;

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

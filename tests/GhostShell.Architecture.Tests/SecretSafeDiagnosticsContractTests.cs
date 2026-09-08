using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace GhostShell.Architecture.Tests;

public sealed class SecretSafeDiagnosticsContractTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public void FirstPartyNormalLogsUseOnlyAuditedClosedSinks()
    {
        string[] violations =
        [
            .. ProductAssemblyPaths()
                .SelectMany(path => CompiledDiagnosticSinkRule.FindViolations(path)),
        ];

        Assert.True(
            violations.Length == 0,
            "Direct or raw diagnostic sinks:\n" + string.Join('\n', violations));
    }

    [Theory]
    [InlineData(0, "unexpected")]
    [InlineData(1, "io")]
    [InlineData(2, "access-denied")]
    public void Profile_rejection_projection_does_not_retain_caller_paths_or_exception_text(
        int errorIndex, string expectedKind)
    {
        const string canary = "/private/profile-secret-canary/throwaway-profile.txt";
        Exception[] errors =
        [
            new ArgumentException(canary),
            new IOException(canary),
            new UnauthorizedAccessException(canary),
        ];
        var projection = GhostShell.Application.SecretSafeDiagnosticProjection.FromException(
            "desktop.profile-selection.rejected", errors[errorIndex], new string('a', 32));

        Assert.DoesNotContain(canary, projection, StringComparison.Ordinal);
        Assert.Equal("[ghostshell:diagnostic] code=desktop.profile-selection.rejected correlation="
            + new string('a', 32) + " type=" + expectedKind, projection);
    }

    [Theory]
    [InlineData(nameof(AdversarialDiagnosticFixtures.ExceptionAlias))]
    [InlineData(nameof(AdversarialDiagnosticFixtures.MessageAlias))]
    [InlineData(nameof(AdversarialDiagnosticFixtures.ToStringAlias))]
    [InlineData(nameof(AdversarialDiagnosticFixtures.Interpolation))]
    [InlineData(nameof(AdversarialDiagnosticFixtures.WriteLineAsync))]
    public void CompiledSinkRuleRejectsAdversarialArgumentShapes(string methodName)
    {
        var assemblyPath = typeof(SecretSafeDiagnosticsContractTests).Assembly.Location;
        var violations = CompiledDiagnosticSinkRule.FindViolations(
            assemblyPath,
            typeof(AdversarialDiagnosticFixtures).FullName,
            methodName);

        Assert.NotEmpty(violations);
    }

    [Fact]
    public void ExactCentralSinkImplementationIsAllowed()
    {
        var assemblyPath = typeof(GhostShell.Application.SecretSafeDiagnosticProjection)
            .Assembly.Location;
        var violations = CompiledDiagnosticSinkRule.FindViolations(
            assemblyPath,
            typeof(GhostShell.Application.SecretSafeDiagnosticProjection).FullName);

        Assert.Empty(violations);
        Assert.NotEmpty(CompiledDiagnosticSinkRule.FindAuditedSinkCalls(assemblyPath));
    }

    private static IReadOnlyList<string> ProductAssemblyPaths()
    {
        var projects = Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "src"),
            "*.csproj",
            SearchOption.AllDirectories);
        return
        [
            .. projects
                .Select(path =>
                {
                    var project = XDocument.Load(path);
                    var assemblyName = project
                        .Descendants("AssemblyName")
                        .Select(element => element.Value)
                        .FirstOrDefault()
                        ?? Path.GetFileNameWithoutExtension(path);
                    return Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
                })
                .Select(path =>
                {
                    Assert.True(File.Exists(path), $"First-party assembly was not available: {path}");
                    return path;
                }),
        ];
    }
}

internal static class AdversarialDiagnosticFixtures
{
    public static void ExceptionAlias(Exception caught)
    {
        var leaked = caught;
        Console.Error.WriteLine(leaked);
    }

    public static void MessageAlias(Exception caught)
    {
        var leaked = caught.Message;
        Console.Error.WriteLine(leaked);
    }

    public static void ToStringAlias(Exception caught)
    {
        var leaked = caught.ToString();
        Console.Error.WriteLine(leaked);
    }

    public static void Interpolation(Exception caught) =>
        Console.Error.WriteLine($"failure: {caught}");

    public static Task WriteLineAsync(Exception caught)
    {
        var leaked = caught.Message;
        return Console.Error.WriteLineAsync(leaked);
    }
}

internal static class CompiledDiagnosticSinkRule
{
    private const string ProjectionAssembly = "GhostShell.Application";
    private const string ProjectionType =
        "GhostShell.Application.SecretSafeDiagnosticProjection";
    private const string CefAssembly = "GhostShell.Browser";
    private const string CefType = "GhostShell.Browser.CefBrowserView";
    private const string DesktopAssembly = "GhostShell";
    private const string DesktopType = "GhostShell.Desktop.Program";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>>
        AuditedProjectionMethods =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["Console.Error"] = new HashSet<string>(StringComparer.Ordinal)
                {
                    "WriteStandardError",
                    "WriteStandardErrorAsync",
                    "WriteTraceAndStandardError",
                },
                ["Trace"] = new HashSet<string>(StringComparer.Ordinal)
                {
                    "WriteBrowserConsoleTrace",
                    "WriteTrace",
                    "WriteTraceAndStandardError",
                },
            };

    private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

    static CompiledDiagnosticSinkRule()
    {
        foreach (var field in typeof(OpCodes).GetFields(
                     BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            var value = unchecked((ushort)opCode.Value);
            if (value < 0x100)
            {
                OneByteOpCodes[value] = opCode;
            }
            else if ((value & 0xff00) == 0xfe00)
            {
                TwoByteOpCodes[value & 0xff] = opCode;
            }
        }
    }

    public static IReadOnlyList<string> FindViolations(
        string assemblyPath,
        string? includedType = null,
        string? includedMethod = null) =>
        [
            .. FindSinkCalls(assemblyPath, includedType, includedMethod)
                .Where(call => !call.IsAudited)
                .Select(call => call.Description),
        ];

    public static IReadOnlyList<string> FindAuditedSinkCalls(string assemblyPath) =>
        [
            .. FindSinkCalls(assemblyPath, null, null)
                .Where(call => call.IsAudited)
                .Select(call => call.Description),
        ];

    private static IReadOnlyList<DiagnosticSinkCall> FindSinkCalls(
        string assemblyPath,
        string? includedType,
        string? includedMethod)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();
        var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
        var calls = new List<DiagnosticSinkCall>();

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var methodName = reader.GetString(method.Name);
            var typeName = TypeName(reader, method.GetDeclaringType());
            if (includedType is not null
                && (!string.Equals(typeName, includedType, StringComparison.Ordinal)
                    || includedMethod is not null
                    && !string.Equals(methodName, includedMethod, StringComparison.Ordinal)))
            {
                continue;
            }

            var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
            foreach (var target in CalledMethods(body.GetILReader(), reader))
            {
                var sink = ClassifySink(target);
                if (sink is null)
                {
                    continue;
                }

                var caller = new MethodIdentity(assemblyName, typeName, methodName);
                calls.Add(new DiagnosticSinkCall(
                    IsAudited(caller, sink),
                    $"{assemblyName}:{typeName}.{methodName} -> {target.TypeName}.{target.MethodName} ({sink})"));
            }
        }

        return calls;
    }

    private static IEnumerable<MethodIdentity> CalledMethods(
        BlobReader il,
        MetadataReader reader)
    {
        while (il.RemainingBytes > 0)
        {
            var first = il.ReadByte();
            var opCode = first == 0xfe
                ? TwoByteOpCodes[il.ReadByte()]
                : OneByteOpCodes[first];
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var target = ResolveMethod(reader, MetadataTokens.EntityHandle(il.ReadInt32()));
                if (target is not null)
                {
                    yield return target.Value;
                }

                continue;
            }

            SkipOperand(ref il, opCode.OperandType);
        }
    }

    private static MethodIdentity? ResolveMethod(
        MetadataReader reader,
        EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }

        if (handle.Kind == HandleKind.MemberReference)
        {
            var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
            var typeName = reference.Parent.Kind switch
            {
                HandleKind.TypeReference => TypeName(
                    reader,
                    (TypeReferenceHandle)reference.Parent),
                HandleKind.TypeDefinition => TypeName(
                    reader,
                    (TypeDefinitionHandle)reference.Parent),
                _ => string.Empty,
            };
            return new MethodIdentity(
                string.Empty,
                typeName,
                reader.GetString(reference.Name));
        }

        if (handle.Kind != HandleKind.MethodDefinition)
        {
            return null;
        }

        var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
        return new MethodIdentity(
            string.Empty,
            TypeName(reader, definition.GetDeclaringType()),
            reader.GetString(definition.Name));
    }

    private static string? ClassifySink(MethodIdentity target)
    {
        if (target.TypeName == "System.Console"
            && target.MethodName == "get_Error")
        {
            return "Console.Error";
        }

        if (target.TypeName == "System.Diagnostics.Trace")
        {
            return "Trace";
        }

        if (target.MethodName == "LogToTrace")
        {
            return "Avalonia.LogToTrace";
        }

        if (target.MethodName is "add_ConsoleMessage" or "remove_ConsoleMessage")
        {
            return "CEF.ConsoleMessage";
        }

        return target.MethodName == "set_DiagnosticsLogHandler"
            ? "Dock.DiagnosticsLogHandler"
            : null;
    }

    private static bool IsAudited(MethodIdentity caller, string sink)
    {
        if (sink is "Console.Error" or "Trace")
        {
            return caller.AssemblyName == ProjectionAssembly
                && caller.TypeName == ProjectionType
                && AuditedProjectionMethods[sink].Contains(caller.MethodName);
        }

        if (sink == "CEF.ConsoleMessage")
        {
            return caller.AssemblyName == CefAssembly
                && caller.TypeName == CefType
                && caller.MethodName is "Subscribe" or "Unsubscribe";
        }

        return sink == "Dock.DiagnosticsLogHandler"
            && caller.AssemblyName == DesktopAssembly
            && caller.TypeName == DesktopType
            && caller.MethodName == "ConfigureDockDiagnostics";
    }

    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        return JoinTypeName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    private static string TypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        return JoinTypeName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    private static string JoinTypeName(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;

    private static void SkipOperand(ref BlobReader il, OperandType operandType)
    {
        if (operandType == OperandType.InlineSwitch)
        {
            var branchCount = il.ReadInt32();
            il.Offset += branchCount * sizeof(int);
            return;
        }

        il.Offset += operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField
                or OperandType.InlineI or OperandType.InlineSig
                or OperandType.InlineString or OperandType.InlineTok
                or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            _ => throw new InvalidDataException($"Unsupported IL operand type {operandType}."),
        };
    }

    private readonly record struct MethodIdentity(
        string AssemblyName,
        string TypeName,
        string MethodName);

    private readonly record struct DiagnosticSinkCall(
        bool IsAudited,
        string Description);
}

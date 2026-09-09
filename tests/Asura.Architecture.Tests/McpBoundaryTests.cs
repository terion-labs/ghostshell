using System.Runtime.CompilerServices;
using Asura.Mcp;

namespace Asura.Architecture.Tests;

public sealed class McpBoundaryTests
{
    [Fact]
    public void McpAssemblyExportsOnlyTheGovernedSessionHost()
    {
        var assembly = typeof(AgentMcpSessionHost).Assembly;
        var exportedTypes = assembly
            .GetTypes()
            .Where(type => type.IsVisible)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([typeof(AgentMcpSessionHost)], exportedTypes);
        Assert.DoesNotContain(
            exportedTypes,
            type => type.IsNestedPublic);
        Assert.DoesNotContain(
            exportedTypes,
            type => type.IsDefined(
                typeof(CompilerGeneratedAttribute),
                inherit: false));
    }
}

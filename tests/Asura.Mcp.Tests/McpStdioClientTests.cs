using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Asura.Mcp.Tests;

public sealed class McpStdioClientTests
{
    [Fact]
    public void PublicApi_ExposesOnlyTheGovernedSessionHost()
    {
        var launch = new McpStdioServerLaunch(
            Path.GetFullPath("secret-server"),
            ["--token", "secret-argument"],
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SECRET_TOKEN"] = "secret-value",
            });

        Assert.Equal("MCP stdio server launch", launch.ToString());
        Assert.Equal(
            Path.GetDirectoryName(launch.Executable),
            launch.WorkingDirectory);
        var exportedTypes = typeof(AgentMcpSessionHost).Assembly
            .GetExportedTypes();
        Assert.Equal([typeof(AgentMcpSessionHost)], exportedTypes);

        var sdkTypes = exportedTypes
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(member => member switch
            {
                MethodInfo method => method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType),
                PropertyInfo property => [property.PropertyType],
                FieldInfo field => [field.FieldType],
                _ => [],
            })
            .Where(type => type.Namespace?.StartsWith(
                "ModelContextProtocol",
                StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Empty(sdkTypes);
    }

    [Fact]
    public async Task ConnectListAndCall_UsesPinnedClosedProtocol()
    {
        await using var client = await ConnectAsync("server-request");

        Assert.Equal(McpProtocol.Version, "2025-11-25");
        Assert.Equal("asura-test", client.ServerInfo.Name);
        Assert.True(client.ServerInfo.ToolsListChanged);

        var tools = await client.ListToolsAsync();
        Assert.True(tools.IsSuccess);
        var tool = Assert.Single(tools.Value!);
        Assert.Equal("control", tool.Name);

        using var arguments = JsonDocument.Parse("""{"value":"hello"}""");
        var call = await client.CallToolAsync(
            "control",
            arguments.RootElement,
            CancellationToken.None);

        Assert.True(call.IsSuccess);
        Assert.False(call.Value!.IsError);
        Assert.Equal("text", Assert.Single(call.Value.Content).Type);
    }

    [Fact]
    public async Task ChildEnvironment_DoesNotInheritAmbientValues()
    {
        var ambientName = "ASURA_MCP_AMBIENT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(ambientName, "must-not-cross-boundary");
        try
        {
            await using var client = await ConnectAsync(
                "environment",
                [ambientName],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ASURA_ALLOWED"] = "resolved-value",
                });
            Assert.True((await client.ListToolsAsync()).IsSuccess);

            var call = await client.CallToolAsync("control");

            Assert.True(call.IsSuccess);
            var structured = call.Value!.StructuredContent!.Value;
            Assert.Equal(JsonValueKind.Null, structured.GetProperty("inherited").ValueKind);
            Assert.Equal("resolved-value", structured.GetProperty("allowed").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ambientName, null);
        }
    }

    [Fact]
    public async Task Arguments_ArePassedWithoutShellParsing()
    {
        const string expected = "space $HOME ; \"quoted\"";
        await using var client = await ConnectAsync("arguments", [expected]);
        Assert.True((await client.ListToolsAsync()).IsSuccess);

        var call = await client.CallToolAsync("control");

        Assert.True(call.IsSuccess);
        Assert.Equal(
            expected,
            call.Value!.StructuredContent!.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task OversizedWireMessage_ClosesClientWithTypedError()
    {
        await using var client = await ConnectAsync(
            "oversized-message",
            options: new McpSessionOptions
            {
                MaxMessageBytes = 1024,
                MaxToolSchemaBytes = 512,
                MaxToolArgumentsBytes = 512,
                MaxToolResultBytes = 512,
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            });

        var result = await client.ListToolsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(McpErrorCode.MessageTooLarge, result.Error!.Code);
    }

    [Fact]
    public async Task DuplicateJsonProperty_IsRejectedAtWireBoundary()
    {
        await using var client = await ConnectAsync("duplicate-property");

        var result = await client.ListToolsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(McpErrorCode.InvalidMessage, result.Error!.Code);
    }

    [Fact]
    public async Task IncomingControlMessageFlood_ClosesClientWithTypedLimitError()
    {
        await using var client = await ConnectAsync(
            "control-message-flood",
            options: new McpSessionOptions
            {
                MaxControlMessagesPerResponse = 4,
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await client.ListToolsAsync(deadline.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(McpErrorCode.LimitExceeded, result.Error!.Code);
    }

    [Fact]
    public async Task IncomingControlMessageBudget_ResetsForEachOperation()
    {
        await using var client = await ConnectAsync(
            "normal",
            options: new McpSessionOptions
            {
                MaxControlMessagesPerResponse = 1,
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            });

        Assert.True((await client.ListToolsAsync()).IsSuccess);
        Assert.True((await client.CallToolAsync("control")).IsSuccess);
    }

    [Fact]
    public async Task OversizedToolSchema_IsRejectedWithoutCachingCatalog()
    {
        await using var client = await ConnectAsync(
            "oversized-schema",
            options: new McpSessionOptions
            {
                MaxToolSchemaBytes = 128,
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            });

        var result = await client.ListToolsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(McpErrorCode.InvalidResult, result.Error!.Code);
        Assert.True(client.IsToolCatalogStale);
    }

    [Theory]
    [InlineData("oversized-title")]
    [InlineData("oversized-description")]
    public async Task OversizedUnusedToolMetadata_IsRejectedWithoutRetention(
        string mode)
    {
        await using var client = await ConnectAsync(mode);

        var result = await client.ListToolsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(McpErrorCode.InvalidResult, result.Error!.Code);
        Assert.True(client.IsToolCatalogStale);
    }

    [Fact]
    public async Task OversizedServerInstructions_AreRejectedDuringInitialization()
    {
        var result = await ConnectResultAsync("oversized-instructions");

        Assert.False(result.IsSuccess);
        Assert.Equal(McpErrorCode.InvalidResult, result.Error!.Code);
    }

    [Fact]
    public async Task OversizedToolResult_IsRejectedAfterBoundedDeserialization()
    {
        await using var client = await ConnectAsync(
            "oversized-result",
            options: new McpSessionOptions
            {
                MaxMessageBytes = 4096,
                MaxToolSchemaBytes = 1024,
                MaxToolArgumentsBytes = 1024,
                MaxToolResultBytes = 512,
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            });
        Assert.True((await client.ListToolsAsync()).IsSuccess);

        var result = await client.CallToolAsync("control");

        Assert.False(result.IsSuccess);
        Assert.Equal(McpErrorCode.InvalidResult, result.Error!.Code);
        Assert.True(result.Error.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCount_IsBounded()
    {
        await using var client = await ConnectAsync(
            "many-tools",
            options: new McpSessionOptions
            {
                MaxTools = 1,
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            });

        var result = await client.ListToolsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(McpErrorCode.LimitExceeded, result.Error!.Code);
    }

    [Fact]
    public async Task OversizedArguments_AreRejectedBeforeDispatch()
    {
        await using var client = await ConnectAsync(
            "normal",
            options: new McpSessionOptions
            {
                MaxToolArgumentsBytes = 128,
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            });
        Assert.True((await client.ListToolsAsync()).IsSuccess);
        using var arguments = JsonDocument.Parse(
            JsonSerializer.Serialize(new { value = new string('a', 256) }));

        var result = await client.CallToolAsync("control", arguments.RootElement);

        Assert.False(result.IsSuccess);
        Assert.Equal(McpErrorCode.InvalidArguments, result.Error!.Code);
        Assert.False(result.Error.OutcomeUncertain);
        Assert.True((await client.ListToolsAsync()).IsSuccess);
    }

    [Fact]
    public async Task Stderr_IsDrainedAsBoundedShapeMetadataOnly()
    {
        await using var client = await ConnectAsync(
            "stderr",
            options: new McpSessionOptions
            {
                MaxStderrBytes = 64,
                MaxStderrLines = 1,
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            });
        Assert.True((await client.ListToolsAsync()).IsSuccess);

        var diagnostics = await WaitForDiagnosticsAsync(client);

        Assert.Equal(64, diagnostics.ObservedByteCount);
        Assert.Equal(1, diagnostics.ObservedLineCount);
        Assert.True(diagnostics.WasTruncated);
        Assert.False(diagnostics.ReadFailed);
        Assert.DoesNotContain(
            "LEAK-ME-NOT",
            diagnostics.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledDispatchedCall_HasUnknownOutcomeAndKillsProcess()
    {
        var client = await ConnectAsync(
            "normal",
            options: new McpSessionOptions
            {
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            });
        await using (client)
        {
            Assert.True((await client.ListToolsAsync()).IsSuccess);
            var pidCall = await client.CallToolAsync("control");
            var pid = pidCall.Value!.StructuredContent!.Value
                .GetProperty("pid")
                .GetInt32();

            using var arguments = JsonDocument.Parse("""{"hang":true}""");
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(100));
            var cancelled = await client.CallToolAsync(
                "control",
                arguments.RootElement,
                cancellation.Token);

            Assert.False(cancelled.IsSuccess);
            Assert.Equal(McpErrorCode.Cancelled, cancelled.Error!.Code);
            Assert.True(cancelled.Error.OutcomeUncertain);
            await AssertProcessExitedAsync(pid);
        }
    }

    [Fact]
    public async Task ToolMustComeFromFreshCatalog()
    {
        await using var client = await ConnectAsync("normal");

        var beforeList = await client.CallToolAsync("control");
        Assert.Equal(McpErrorCode.ToolCatalogStale, beforeList.Error!.Code);

        Assert.True((await client.ListToolsAsync()).IsSuccess);
        var unknown = await client.CallToolAsync("not-listed");
        Assert.Equal(McpErrorCode.ToolNotListed, unknown.Error!.Code);
    }

    private static async Task<McpClientSession> ConnectAsync(
        string mode,
        string[]? hostArguments = null,
        IReadOnlyDictionary<string, string>? environment = null,
        McpSessionOptions? options = null)
    {
        var result = await ConnectResultAsync(
            mode,
            hostArguments,
            environment,
            options);

        Assert.True(
            result.IsSuccess,
            result.Error is null
                ? "MCP connection failed."
                : $"{result.Error.Code}: {result.Error.Message}");
        return result.Value!;
    }

    private static async Task<McpResult<McpClientSession>> ConnectResultAsync(
        string mode,
        string[]? hostArguments = null,
        IReadOnlyDictionary<string, string>? environment = null,
        McpSessionOptions? options = null)
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var dotnetPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The .NET host path is unavailable.");
        var childEnvironment = new Dictionary<string, string>(
            environment ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        childEnvironment.TryAdd(
            "DOTNET_ROOT",
            Path.GetDirectoryName(dotnetPath)
                ?? throw new InvalidOperationException("The .NET host directory is unavailable."));

        var arguments = new List<string>
        {
            assemblyPath,
            "--mcp-test-host",
            mode,
        };
        arguments.AddRange(hostArguments ?? []);

        return await McpClientSession.ConnectStdioAsync(
            new McpStdioServerLaunch(
                dotnetPath,
                arguments,
                Path.GetDirectoryName(assemblyPath),
                childEnvironment),
            new McpClientInfo("asura-tests", "1.0.0"),
            options ?? new McpSessionOptions
            {
                ShutdownGracePeriod = TimeSpan.FromMilliseconds(50),
            },
            CancellationToken.None);
    }

    private static async Task<McpStderrDiagnostics> WaitForDiagnosticsAsync(
        McpClientSession client)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var diagnostics = client.StandardErrorDiagnostics;
            if (diagnostics.WasTruncated)
            {
                return diagnostics;
            }

            await Task.Delay(10);
        }

        return client.StandardErrorDiagnostics;
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"MCP test host process {processId} is still running.");
    }
}

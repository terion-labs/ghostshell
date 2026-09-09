using System.Collections.ObjectModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdkMcpClient = ModelContextProtocol.Client.McpClient;

namespace Asura.Mcp;

/// <summary>
/// A closed-capability MCP protocol session over a bounded client transport.
/// </summary>
internal sealed class McpClientSession : IAsyncDisposable
{
    private const int MaximumServerInstructionsBytes = 4 * 1024;
    private const int MaximumToolTitleBytes = 1024;
    private const int MaximumToolDescriptionBytes = 4 * 1024;

    private readonly SdkMcpClient _client;
    private readonly IMcpClientTransportBoundary _transport;
    private readonly McpSessionOptions _options;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly IAsyncDisposable _toolChangeRegistration;
    private IReadOnlyDictionary<string, McpTool> _toolCatalog =
        new ReadOnlyDictionary<string, McpTool>(
            new Dictionary<string, McpTool>(StringComparer.Ordinal));
    private readonly object _catalogGate = new();
    private int _catalogGeneration;
    private int _catalogVersion = -1;
    private int _disposed;

    private McpClientSession(
        SdkMcpClient client,
        IMcpClientTransportBoundary transport,
        McpSessionOptions options,
        McpServerInfo serverInfo)
    {
        _client = client;
        _transport = transport;
        _options = options;
        ServerInfo = serverInfo;
        _toolChangeRegistration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                lock (_catalogGate)
                {
                    _catalogGeneration++;
                }

                return ValueTask.CompletedTask;
            });
    }

    public McpServerInfo ServerInfo { get; }

    public bool IsToolCatalogStale =>
        Volatile.Read(ref _catalogVersion) != Volatile.Read(ref _catalogGeneration);

    public bool CleanupUncertain => _transport.CleanupUncertain;

    public McpStderrDiagnostics StandardErrorDiagnostics => _transport.Diagnostics;

    public static Task<McpResult<McpClientSession>> ConnectStdioAsync(
        McpStdioServerLaunch launch,
        McpClientInfo clientInfo,
        McpSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        options ??= new McpSessionOptions();
        options.Validate();
        return ConnectAsync(
            new BoundedStdioClientTransport(launch, options),
            clientInfo,
            options,
            cancellationToken);
    }

    internal static async Task<McpResult<McpClientSession>> ConnectAsync(
        IMcpClientTransportBoundary transport,
        McpClientInfo clientInfo,
        McpSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(clientInfo);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        SdkMcpClient? client = null;
        try
        {
            client = await SdkMcpClient.CreateAsync(
                    transport,
                    new McpClientOptions
                    {
                        ProtocolVersion = McpProtocol.Version,
                        ClientInfo = new Implementation
                        {
                            Name = clientInfo.Name,
                            Version = clientInfo.Version,
                        },
                        // No roots, sampling, elicitation, tasks, extensions, or
                        // experimental features are advertised in this first slice.
                        Capabilities = new ClientCapabilities(),
                        InitializationTimeout = options.InitializationTimeout,
                    },
                    loggerFactory: null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(
                    client.NegotiatedProtocolVersion,
                    McpProtocol.Version,
                    StringComparison.Ordinal))
            {
                await DisposeConnectionAsync(client, transport).ConfigureAwait(false);
                return Failure<McpClientSession>(
                    McpErrorCode.UnsupportedProtocolVersion,
                    "The MCP server did not negotiate the required protocol version.");
            }

            if (client.ServerCapabilities.Tools is not { } toolsCapability)
            {
                await DisposeConnectionAsync(client, transport).ConfigureAwait(false);
                return Failure<McpClientSession>(
                    McpErrorCode.MissingToolsCapability,
                    "The MCP server does not advertise the tools capability.");
            }

            if (!TryCreateServerInfo(
                    client,
                    toolsCapability,
                    out var serverInfo))
            {
                await DisposeConnectionAsync(client, transport).ConfigureAwait(false);
                return Failure<McpClientSession>(
                    McpErrorCode.InvalidResult,
                    "The MCP server returned invalid or oversized initialization metadata.");
            }

            return McpResult<McpClientSession>.Success(
                new McpClientSession(client, transport, options, serverInfo!));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeConnectionAsync(client, transport).ConfigureAwait(false);

            return Failure<McpClientSession>(
                McpErrorCode.Cancelled,
                "The MCP connection was cancelled and its transport was closed.",
                cleanupUncertain: transport.CleanupUncertain);
        }
        catch (Exception exception)
        {
            await DisposeConnectionAsync(client, transport).ConfigureAwait(false);

            var error = MapException(exception) with
            {
                CleanupUncertain = transport.CleanupUncertain,
            };
            return McpResult<McpClientSession>.Failure(error);
        }
    }

    public async Task<McpResult<IReadOnlyList<McpTool>>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        var entered = false;
        try
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Failure<IReadOnlyList<McpTool>>(
                    McpErrorCode.Disposed,
                    "The MCP client is closed.");
            }

            Volatile.Write(ref _catalogVersion, -1);
            var tools = new List<McpTool>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            var startingGeneration = Volatile.Read(ref _catalogGeneration);
            string? cursor = null;
            for (var pageNumber = 0; pageNumber < _options.MaxToolListPages; pageNumber++)
            {
                _transport.ResetIncomingMessageBudget();
                var page = await _client.ListToolsAsync(
                        new ListToolsRequestParams { Cursor = cursor },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (page.Tools is null)
                {
                    return Failure<IReadOnlyList<McpTool>>(
                        McpErrorCode.InvalidResult,
                        "The MCP server returned a tool page without a tools collection.");
                }

                foreach (var sdkTool in page.Tools)
                {
                    if (tools.Count >= _options.MaxTools)
                    {
                        return Failure<IReadOnlyList<McpTool>>(
                            McpErrorCode.LimitExceeded,
                            "The MCP tool catalog exceeds its configured tool limit.");
                    }

                    if (!TryConvertTool(sdkTool, _options, out var tool)
                        || !names.Add(tool!.Name))
                    {
                        return Failure<IReadOnlyList<McpTool>>(
                            McpErrorCode.InvalidResult,
                            "The MCP server returned an invalid or duplicate tool descriptor.");
                    }

                    tools.Add(tool);
                }

                cursor = page.NextCursor;
                if (cursor is null)
                {
                    var catalog = new Dictionary<string, McpTool>(
                        tools.Count,
                        StringComparer.Ordinal);
                    foreach (var tool in tools)
                    {
                        catalog.Add(tool.Name, tool);
                    }

                    lock (_catalogGate)
                    {
                        _toolCatalog = new ReadOnlyDictionary<string, McpTool>(catalog);
                        if (_catalogGeneration == startingGeneration)
                        {
                            Volatile.Write(ref _catalogVersion, startingGeneration);
                        }
                    }

                    return McpResult<IReadOnlyList<McpTool>>.Success(
                        Array.AsReadOnly(tools.ToArray()));
                }

                if (!McpText.IsBounded(cursor, _options.MaxMessageBytes / 2)
                    || !cursors.Add(cursor))
                {
                    return Failure<IReadOnlyList<McpTool>>(
                        McpErrorCode.InvalidResult,
                        "The MCP server returned an invalid pagination cursor.");
                }
            }

            return Failure<IReadOnlyList<McpTool>>(
                McpErrorCode.LimitExceeded,
                "The MCP tool catalog exceeds its configured page limit.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            return Failure<IReadOnlyList<McpTool>>(
                McpErrorCode.Cancelled,
                "MCP tool discovery was cancelled and the transport was closed.",
                cleanupUncertain: _transport.CleanupUncertain);
        }
        catch (Exception exception)
        {
            var error = MapException(exception);
            if (ShouldClose(error))
            {
                await DisposeAsync().ConfigureAwait(false);
                error = error with { CleanupUncertain = _transport.CleanupUncertain };
            }

            return McpResult<IReadOnlyList<McpTool>>.Failure(error);
        }
        finally
        {
            if (entered)
            {
                _operationLock.Release();
            }
        }
    }

    public async Task<McpResult<McpToolCallResult>> CallToolAsync(
        string toolName,
        JsonElement? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidToolName(toolName))
        {
            return Failure<McpToolCallResult>(
                McpErrorCode.InvalidArguments,
                "The MCP tool name is invalid.");
        }

        IDictionary<string, JsonElement>? sdkArguments = null;
        if (arguments is { } value)
        {
            if (!McpJsonBudget.TryValidate(
                    value,
                    _options.MaxToolArgumentsBytes,
                    _options.MaxJsonDepth,
                    _options.MaxJsonNodes,
                    requireObject: true,
                    out _))
            {
                return Failure<McpToolCallResult>(
                    McpErrorCode.InvalidArguments,
                    "The MCP tool arguments are invalid or exceed their configured limits.");
            }

            sdkArguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                sdkArguments.Add(property.Name, property.Value.Clone());
            }
        }

        var entered = false;
        var dispatched = false;
        try
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Failure<McpToolCallResult>(
                    McpErrorCode.Disposed,
                    "The MCP client is closed.");
            }

            if (IsToolCatalogStale)
            {
                return Failure<McpToolCallResult>(
                    McpErrorCode.ToolCatalogStale,
                    "Refresh the MCP tool catalog before invoking a tool.");
            }

            if (!_toolCatalog.ContainsKey(toolName))
            {
                return Failure<McpToolCallResult>(
                    McpErrorCode.ToolNotListed,
                    "The requested MCP tool is not present in the current catalog.");
            }

            _transport.ResetIncomingMessageBudget();
            dispatched = true;
            var sdkResult = await _client.CallToolAsync(
                    new CallToolRequestParams
                    {
                        Name = toolName,
                        Arguments = sdkArguments,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!TryConvertCallResult(sdkResult, _options, out var result))
            {
                await DisposeAsync().ConfigureAwait(false);
                return Failure<McpToolCallResult>(
                    McpErrorCode.InvalidResult,
                    "The MCP server returned an invalid or oversized tool result.",
                    cleanupUncertain: _transport.CleanupUncertain,
                    outcomeUncertain: true);
            }

            return McpResult<McpToolCallResult>.Success(result!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (dispatched)
            {
                await DisposeAsync().ConfigureAwait(false);
            }

            return Failure<McpToolCallResult>(
                McpErrorCode.Cancelled,
                dispatched
                    ? "The MCP tool call was cancelled; its remote outcome is unknown and the transport was closed."
                    : "The MCP tool call was cancelled before dispatch.",
                cleanupUncertain: dispatched && _transport.CleanupUncertain,
                outcomeUncertain: dispatched);
        }
        catch (Exception exception)
        {
            var error = MapException(exception);
            error = error with { OutcomeUncertain = dispatched };
            if (dispatched || ShouldClose(error))
            {
                await DisposeAsync().ConfigureAwait(false);
                error = error with { CleanupUncertain = _transport.CleanupUncertain };
            }

            return McpResult<McpToolCallResult>.Failure(error);
        }
        finally
        {
            if (entered)
            {
                _operationLock.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await DisposeIgnoringErrorsAsync(_toolChangeRegistration).ConfigureAwait(false);
            await DisposeIgnoringErrorsAsync(_client).ConfigureAwait(false);
            await DisposeIgnoringErrorsAsync(_transport).ConfigureAwait(false);
        }
        finally
        {
            _disposeLock.Release();
        }
    }

    private static bool TryCreateServerInfo(
        SdkMcpClient client,
        ToolsCapability toolsCapability,
        out McpServerInfo? serverInfo)
    {
        var info = client.ServerInfo;
        var instructions = client.ServerInstructions;
        if (!McpText.IsBounded(info.Name, 128)
            || !McpText.IsBounded(info.Version, 128)
            || string.IsNullOrWhiteSpace(info.Name)
            || string.IsNullOrWhiteSpace(info.Version)
            || !McpText.IsBounded(
                instructions,
                MaximumServerInstructionsBytes))
        {
            serverInfo = null;
            return false;
        }

        serverInfo = new(
            info.Name,
            info.Version,
            toolsCapability.ListChanged == true);
        return true;
    }

    private static bool TryConvertTool(
        Tool sdkTool,
        McpSessionOptions options,
        out McpTool? tool)
    {
        tool = null;
        if (!IsValidToolName(sdkTool.Name)
            || !McpText.IsBounded(
                sdkTool.Title,
                MaximumToolTitleBytes)
            || !McpText.IsBounded(
                sdkTool.Description,
                MaximumToolDescriptionBytes)
            || !McpJsonBudget.TryValidate(
                sdkTool.InputSchema,
                options.MaxToolSchemaBytes,
                options.MaxJsonDepth,
                options.MaxJsonNodes,
                requireObject: true,
                out _))
        {
            return false;
        }

        tool = new(
            sdkTool.Name,
            sdkTool.InputSchema.Clone());
        return true;
    }

    private static bool TryConvertCallResult(
        CallToolResult sdkResult,
        McpSessionOptions options,
        out McpToolCallResult? result)
    {
        result = null;
        if (sdkResult.Content is null)
        {
            return false;
        }

        byte[] serialized;
        try
        {
            serialized = JsonSerializer.SerializeToUtf8Bytes(
                sdkResult,
                McpSdkJson.TypeInfo<CallToolResult>());
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return false;
        }

        if (serialized.Length > options.MaxToolResultBytes
            || sdkResult.Content.Count > options.MaxToolContentItems
            || !McpJsonBudget.TryValidateDocument(
                serialized,
                options.MaxJsonDepth,
                options.MaxJsonNodes,
                out var resultDocument))
        {
            return false;
        }

        using (var validatedResult = resultDocument!)
        {
            // Task-augmented calls are intentionally outside this closed first slice.
            if (validatedResult.RootElement.TryGetProperty("task", out _))
            {
                return false;
            }
        }

        if (sdkResult.StructuredContent is { } structured
            && !McpJsonBudget.TryValidate(
                structured,
                options.MaxToolResultBytes,
                options.MaxJsonDepth,
                options.MaxJsonNodes,
                requireObject: true,
                out _))
        {
            return false;
        }

        var content = new McpToolCallContent[sdkResult.Content.Count];
        for (var index = 0; index < sdkResult.Content.Count; index++)
        {
            var block = sdkResult.Content[index];
            JsonElement blockJson;
            try
            {
                blockJson = JsonSerializer.SerializeToElement(
                    block,
                    McpSdkJson.TypeInfo<ContentBlock>());
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return false;
            }

            if (!McpText.IsBounded(block.Type, 64)
                || !McpJsonBudget.TryValidate(
                    blockJson,
                    options.MaxToolResultBytes,
                    options.MaxJsonDepth,
                    options.MaxJsonNodes,
                    requireObject: true,
                    out _))
            {
                return false;
            }

            content[index] = new(block.Type, blockJson.Clone());
        }

        result = new(
            Array.AsReadOnly(content),
            sdkResult.StructuredContent?.Clone(),
            sdkResult.IsError == true);
        return true;
    }

    private static bool IsValidToolName(string? name)
    {
        if (string.IsNullOrEmpty(name)
            || name.Length > 128)
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!(character is >= 'a' and <= 'z')
                && !(character is >= 'A' and <= 'Z')
                && !(character is >= '0' and <= '9')
                && character is not '_'
                && character is not '-'
                && character is not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static McpError MapException(Exception exception)
    {
        if (FindTransportFailure(exception) is { } transportFailure)
        {
            return new(transportFailure.Code, SafeMessage(transportFailure.Code));
        }

        return exception switch
        {
            McpException mcpException when mcpException.Message.StartsWith(
                "Server protocol version mismatch.",
                StringComparison.Ordinal) => new(
                    McpErrorCode.UnsupportedProtocolVersion,
                    "The MCP server did not negotiate the required protocol version."),
            McpProtocolException protocolException => new(
                McpErrorCode.RemoteError,
                "The MCP server rejected the request.",
                (int)protocolException.ErrorCode),
            McpException => new(
                McpErrorCode.RemoteError,
                "The MCP server rejected the request."),
            TimeoutException => new(
                McpErrorCode.TransportFailed,
                "The MCP server did not initialize before the configured timeout."),
            ClientTransportClosedException => new(
                McpErrorCode.TransportClosed,
                "The MCP transport closed unexpectedly."),
            IOException => new(
                McpErrorCode.TransportFailed,
                "The MCP transport failed."),
            ObjectDisposedException => new(
                McpErrorCode.Disposed,
                "The MCP client is closed."),
            _ => new(
                McpErrorCode.TransportFailed,
                "The MCP operation failed."),
        };
    }

    private static McpTransportFailureException? FindTransportFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is McpTransportFailureException failure)
            {
                return failure;
            }

            if (current is ClientTransportClosedException closed
                && closed.Details.Exception is { } detailsException)
            {
                var nested = FindTransportFailure(detailsException);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string SafeMessage(McpErrorCode code) => code switch
    {
        McpErrorCode.LaunchFailed => "The MCP server process could not be started.",
        McpErrorCode.ProcessExited => "The MCP server process exited unexpectedly.",
        McpErrorCode.MessageTooLarge => "An MCP message exceeded its configured byte limit.",
        McpErrorCode.LimitExceeded => "The MCP server exceeded a configured transport limit.",
        McpErrorCode.InvalidMessage => "The MCP server emitted an invalid message.",
        McpErrorCode.TransportClosed => "The MCP transport closed unexpectedly.",
        _ => "The MCP transport failed.",
    };

    private static bool ShouldClose(McpError error) =>
        error.Code is McpErrorCode.TransportClosed
            or McpErrorCode.TransportFailed
            or McpErrorCode.ProcessExited
            or McpErrorCode.MessageTooLarge
            or McpErrorCode.LimitExceeded
            or McpErrorCode.InvalidMessage;

    private static McpResult<T> Failure<T>(
        McpErrorCode code,
        string message,
        bool cleanupUncertain = false,
        bool outcomeUncertain = false) =>
        McpResult<T>.Failure(
            new McpError(
                code,
                message,
                CleanupUncertain: cleanupUncertain,
                OutcomeUncertain: outcomeUncertain));

    private static async Task DisposeIgnoringErrorsAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task DisposeConnectionAsync(
        IAsyncDisposable? client,
        IMcpClientTransportBoundary transport)
    {
        if (client is not null)
        {
            await DisposeIgnoringErrorsAsync(client).ConfigureAwait(false);
        }

        await DisposeIgnoringErrorsAsync(transport).ConfigureAwait(false);
    }
}

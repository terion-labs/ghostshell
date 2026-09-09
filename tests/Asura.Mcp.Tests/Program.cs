using System.Text.Json;

namespace Asura.Mcp.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] arguments)
    {
        if (arguments is not ["--mcp-test-host", var mode, .. var hostArguments])
        {
            return 0;
        }

        await RunServerAsync(mode, hostArguments).ConfigureAwait(false);
        return 0;
    }

    private static async Task RunServerAsync(string mode, string[] hostArguments)
    {
        if (string.Equals(mode, "lifecycle-marker"
, StringComparison.Ordinal) && hostArguments is [var startedPath, _, _])
        {
            await File.WriteAllTextAsync(
                    startedPath,
                    Environment.ProcessId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }

        string? delayedListId = null;
        var unsupportedRequestAnswered = !string.Equals(mode, "server-request", StringComparison.Ordinal);
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            if (root.TryGetProperty("method", out var methodProperty))
            {
                var method = methodProperty.GetString();
                if (string.Equals(method, "initialize", StringComparison.Ordinal))
                {
                    await WriteAsync(new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").Clone(),
                        result = new
                        {
                            protocolVersion = "2025-11-25",
                            capabilities = new
                            {
                                tools = new { listChanged = true },
                            },
                            serverInfo = new { name = "asura-test", version = "1.0.0" },
                            instructions = string.Equals(mode, "oversized-instructions"
, StringComparison.Ordinal) ? new string('i', 5 * 1024)
                                : null,
                        },
                    }).ConfigureAwait(false);
                }
                else if (string.Equals(method, "notifications/initialized", StringComparison.Ordinal))
                {
                    if (string.Equals(mode, "server-request", StringComparison.Ordinal))
                    {
                        await WriteAsync(new
                        {
                            jsonrpc = "2.0",
                            id = "unsupported-server-request",
                            method = "roots/list",
                            @params = new { },
                        }).ConfigureAwait(false);
                    }

                    if (string.Equals(mode, "stderr", StringComparison.Ordinal))
                    {
                        await Console.Error.WriteAsync(
                                "LEAK-ME-NOT:" + new string('s', 2048) + "\n")
                            .ConfigureAwait(false);
                        await Console.Error.FlushAsync().ConfigureAwait(false);
                    }
                }
                else if (string.Equals(method, "tools/list", StringComparison.Ordinal))
                {
                    var id = root.GetProperty("id").GetRawText();
                    if (string.Equals(mode, "control-message-flood", StringComparison.Ordinal))
                    {
                        for (var index = 0; index < 64; index++)
                        {
                            await WriteAsync(new
                            {
                                jsonrpc = "2.0",
                                method = "notifications/tools/list_changed",
                            }).ConfigureAwait(false);
                        }
                    }
                    else if (string.Equals(mode, "hang-list", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    else if (!unsupportedRequestAnswered)
                    {
                        delayedListId = id;
                    }
                    else
                    {
                        await WriteToolListAsync(mode, id).ConfigureAwait(false);
                    }
                }
                else if (string.Equals(method, "tools/call", StringComparison.Ordinal))
                {
                    await WriteToolCallAsync(
                            mode,
                            hostArguments,
                            root.GetProperty("id").GetRawText(),
                            root.GetProperty("params"))
                        .ConfigureAwait(false);
                }
            }
            else if (string.Equals(mode, "server-request"
, StringComparison.Ordinal) && root.TryGetProperty("id", out var responseId)
                && string.Equals(responseId.GetString(), "unsupported-server-request"
, StringComparison.Ordinal) && root.TryGetProperty("error", out var error)
                && error.GetProperty("code").GetInt32() == -32601)
            {
                unsupportedRequestAnswered = true;
                if (delayedListId is not null)
                {
                    await WriteToolListAsync(mode, delayedListId).ConfigureAwait(false);
                    delayedListId = null;
                }
            }
        }

        if (string.Equals(mode, "lifecycle-marker"
, StringComparison.Ordinal) && hostArguments is [_, var closedPath, _])
        {
            await File.WriteAllTextAsync(
                    closedPath,
                    "closed")
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteToolListAsync(string mode, string rawId)
    {
        using var idDocument = JsonDocument.Parse(rawId);
        var id = idDocument.RootElement.Clone();
        if (string.Equals(mode, "oversized-message", StringComparison.Ordinal))
        {
            await WriteAsync(new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "echo",
                            description = new string('x', 2048),
                            inputSchema = new { type = "object" },
                        },
                    },
                },
            }).ConfigureAwait(false);
            return;
        }

        if (string.Equals(mode, "duplicate-property", StringComparison.Ordinal))
        {
            await WriteRawAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":" + rawId
                    + ",\"result\":{\"tools\":[],\"tools\":[]}}")
                .ConfigureAwait(false);
            return;
        }

        var schema = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
        };
        if (string.Equals(mode, "oversized-schema", StringComparison.Ordinal))
        {
            schema.Add("description", new string('x', 512));
        }
        else if (string.Equals(mode, "aggregate-schema-limit", StringComparison.Ordinal))
        {
            schema.Add(
                "properties",
                new
                {
                    value = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            new string('s', 58 * 1024),
                        },
                    },
                });
        }

        var tools = new List<object>
        {
            new
            {
                name = "control",
                title = string.Equals(mode, "oversized-title"
, StringComparison.Ordinal) ? new string('t', 2 * 1024)
                    : "Control",
                description = string.Equals(mode, "oversized-description"
, StringComparison.Ordinal) ? new string('d', 5 * 1024)
                    : "Test tool",
                inputSchema = schema,
            },
        };
        if (string.Equals(mode, "many-tools", StringComparison.Ordinal))
        {
            tools.Add(new
            {
                name = "second",
                title = "Second",
                description = "Second test tool",
                inputSchema = schema,
            });
        }
        else if (string.Equals(mode, "many-unselected-tools", StringComparison.Ordinal))
        {
            for (var index = 1; index < 65; index++)
            {
                tools.Add(new
                {
                    name = $"unselected-{index}",
                    title = $"Unselected {index}",
                    description = "Unselected test tool",
                    inputSchema = schema,
                });
            }
        }
        else if (string.Equals(mode, "aggregate-schema-limit", StringComparison.Ordinal))
        {
            for (var index = 1; index < 10; index++)
            {
                tools.Add(new
                {
                    name = $"schema-{index}",
                    title = $"Schema {index}",
                    description = "Large valid schema",
                    inputSchema = schema,
                });
            }
        }
        else if (string.Equals(mode, "secret-tool-name", StringComparison.Ordinal))
        {
            tools.Add(new
            {
                name = Environment.GetEnvironmentVariable(
                    "ASURA_REFLECTED_TOOL"),
                title = "Reflected",
                description = "Server-controlled identifier",
                inputSchema = schema,
            });
        }

        await WriteAsync(new
        {
            jsonrpc = "2.0",
            id,
            result = new { tools },
        }).ConfigureAwait(false);
    }

    private static async Task WriteToolCallAsync(
        string mode,
        string[] hostArguments,
        string rawId,
        JsonElement requestParams)
    {
        if (string.Equals(mode, "call-marker", StringComparison.Ordinal) && hostArguments is [var markerPath])
        {
            await File.WriteAllTextAsync(
                    markerPath,
                    "tool-called")
                .ConfigureAwait(false);
        }
        else if (string.Equals(mode, "lifecycle-marker"
, StringComparison.Ordinal) && hostArguments is [_, _, var calledPath])
        {
            await File.WriteAllTextAsync(
                    calledPath,
                    "tool-called")
                .ConfigureAwait(false);
        }

        if (requestParams.TryGetProperty("arguments", out var arguments)
            && arguments.TryGetProperty("hang", out var hang)
            && hang.ValueKind == JsonValueKind.True)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return;
        }

        object structured = mode switch
        {
            "environment" => new
            {
                inherited = Environment.GetEnvironmentVariable(hostArguments[0]),
                allowed = Environment.GetEnvironmentVariable("ASURA_ALLOWED"),
            },
            "arguments" => new { value = hostArguments[0] },
            _ => new { pid = Environment.ProcessId },
        };

        using var idDocument = JsonDocument.Parse(rawId);
        await WriteAsync(new
        {
            jsonrpc = "2.0",
            id = idDocument.RootElement.Clone(),
            result = new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = string.Equals(mode, "oversized-result"
, StringComparison.Ordinal) ? new string('r', 2048)
                            : "ok",
                    },
                },
                structuredContent = structured,
                isError = false,
            },
        }).ConfigureAwait(false);
    }

    private static Task WriteAsync<T>(T value) =>
        WriteRawAsync(JsonSerializer.Serialize(value));

    private static async Task WriteRawAsync(string json)
    {
        await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

internal static class DurableDefinitionFixtures
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(allowIntegerValues: false),
        },
    };

    public static LayoutDefinition Layout(
        string id = "layout-one",
        string name = "Layout One",
        string slotId = "main") =>
        new(
            new LayoutId(id),
            LayoutDefinition.CurrentSchemaVersion,
            name,
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId(slotId),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(160, 100)),
            ]);

    public static LayoutDefinition TwoSlotLayout(
        string id = "layout-one",
        string name = "Layout One") =>
        new(
            new LayoutId(id),
            LayoutDefinition.CurrentSchemaVersion,
            name,
            new LayoutGrid(2, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("left"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(160, 100)),
                new LayoutSlotDefinition(
                    new LayoutSlotId("right"),
                    new LayoutGridBounds(1, 0, 1, 1),
                    new LayoutMinimumSize(160, 100)),
            ]);

    public static ScreenDefinition Screen(
        string id = "screen-one",
        string name = "Screen One",
        string layoutId = "layout-one",
        string slotId = "main") =>
        new(
            new ScreenId(id),
            ScreenDefinition.CurrentSchemaVersion,
            name,
            description: null,
            new LayoutId(layoutId),
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("panel-one"),
                    new LayoutSlotId(slotId),
                    ScreenPanelKind.Terminal,
                    Title: null,
                    ConnectionId: null,
                    PanelStartupBehavior.None),
            ]);

    public static AiProviderProfile AiProvider(
        string id,
        string name,
        int order) =>
        new(
            new AiProviderProfileId(id),
            AiProviderProfile.CurrentSchemaVersion,
            name,
            AiProviderKind.OpenAiCompatible,
            new Uri("http://localhost:11434/v1/"),
            new AiProviderAuthentication.None(),
            "local-model",
            order);

    public static McpServerProfile McpServer(
        string id = "mcp-server",
        string name = "MCP Server",
        SecretRef? environmentSecret = null) =>
        new(
            new McpServerProfileId(id),
            McpServerProfile.CurrentSchemaVersion,
            name,
            new McpServerTransport.Stdio(
                "/usr/local/bin/mcp-server",
                ["--stdio"],
                "/srv/mcp",
                environmentSecret is { } reference
                    ? [new McpServerEnvironmentVariable("MCP_TOKEN", reference)]
                    : []),
            ["status.read"]);

    public static PortableDefinitionDocument Document<TDefinition>(TDefinition definition)
        where TDefinition : IDurableDefinition =>
        new(
            definition.Key.Kind,
            definition.Key.Value,
            definition.SchemaVersion,
            definition.Name,
            JsonSerializer.Serialize(definition, JsonOptions));

}

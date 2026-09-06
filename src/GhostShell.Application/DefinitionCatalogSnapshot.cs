using GhostShell.Core;

namespace GhostShell.Application;

public sealed record DefinitionCatalogSnapshot(
    IReadOnlyList<StoredDefinition<ConnectionProfile>> Connections,
    IReadOnlyList<StoredDefinition<LayoutDefinition>> Layouts,
    IReadOnlyList<StoredDefinition<ScreenDefinition>> Screens,
    IReadOnlyList<StoredDefinition<WorkspaceDefinition>> Workspaces,
    IReadOnlyList<StoredDefinition<ThemePreference>> Themes,
    IReadOnlyList<StoredDefinition<TerminalProfile>> TerminalProfiles,
    IReadOnlyList<StoredDefinition<KeymapProfile>> Keymaps,
    IReadOnlyList<StoredDefinition<FileProviderProfile>> FileProviderProfiles,
    IReadOnlyList<StoredDefinition<QuickTerminalSettings>> QuickTerminalSettings)
{
    public static DefinitionCatalogSnapshot Empty { get; } = new([], [], [], [], [], [], [], [], []);

    public IReadOnlyList<StoredDefinition<AiProviderProfile>> AiProviderProfiles { get; init; } = [];

    public IReadOnlyList<StoredDefinition<McpServerProfile>> McpServerProfiles { get; init; } = [];

    public IReadOnlyList<StoredDefinition<DatabaseConnectionProfile>> DatabaseConnections { get; init; } = [];

    public IReadOnlyList<StoredDefinition<BrowserProfileDefinition>> BrowserProfiles { get; init; } = [];

    public IReadOnlyList<StoredDefinition<NetworkConnectionProfile>> NetworkConnections { get; init; } = [];

    public IReadOnlyList<StoredDefinition<ApplicationNetworkSettings>> ApplicationNetworkSettings { get; init; } = [];
}

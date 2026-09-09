namespace Asura.Core;

/// <summary>
/// Stable provider-protocol tokens for agent capabilities. These values are
/// intentionally independent of CLR enum names so routine code renames cannot
/// silently change the provider contract.
/// </summary>
public static class AgentCapabilityProtocol
{
    public const string TerminalRead = "terminal_read";
    public const string RunCommands = "run_commands";
    public const string EditFiles = "edit_files";
    public const string ReadFiles = "read_files";
    public const string Search = "search";
    public const string Git = "git";
    public const string WebFetch = "web_fetch";
    public const string Docker = "docker";
    public const string DestructiveTerminalActions =
        "destructive_terminal_actions";
    public const string BrowserNavigation = "browser_navigation";
    public const string BrowserData = "browser_data";
    public const string ProcessControl = "process_control";
    public const string McpTools = "mcp_tools";
    public const string SecretUse = "secret_use";
    public const string BrowserInteraction = "browser_interaction";
    public const string BrowserScripting = "browser_scripting";
    public const string BrowserDiagnostics = "browser_diagnostics";
    public const string DatabaseRead = "database_read";
    public const string DatabaseWrite = "database_write";
    public const string DockerData = "docker_data";
    public const string SystemData = "system_data";
    public const string ProcessData = "process_data";
    public const string ArtifactTransfer = "artifact_transfer";
    public const string WorkspaceLayout = "workspace_layout";
    public const string GitData = "git_data";

    public static string GetToken(AgentCapability capability) =>
        capability switch
        {
            AgentCapability.TerminalRead => TerminalRead,
            AgentCapability.RunCommands => RunCommands,
            AgentCapability.EditFiles => EditFiles,
            AgentCapability.ReadFiles => ReadFiles,
            AgentCapability.Search => Search,
            AgentCapability.Git => Git,
            AgentCapability.WebFetch => WebFetch,
            AgentCapability.Docker => Docker,
            AgentCapability.DestructiveTerminalActions =>
                DestructiveTerminalActions,
            AgentCapability.BrowserNavigation => BrowserNavigation,
            AgentCapability.BrowserData => BrowserData,
            AgentCapability.ProcessControl => ProcessControl,
            AgentCapability.McpTools => McpTools,
            AgentCapability.SecretUse => SecretUse,
            AgentCapability.BrowserInteraction => BrowserInteraction,
            AgentCapability.BrowserScripting => BrowserScripting,
            AgentCapability.BrowserDiagnostics => BrowserDiagnostics,
            AgentCapability.DatabaseRead => DatabaseRead,
            AgentCapability.DatabaseWrite => DatabaseWrite,
            AgentCapability.DockerData => DockerData,
            AgentCapability.SystemData => SystemData,
            AgentCapability.ProcessData => ProcessData,
            AgentCapability.ArtifactTransfer => ArtifactTransfer,
            AgentCapability.WorkspaceLayout => WorkspaceLayout,
            AgentCapability.GitData => GitData,
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };

    public static bool TryParseToken(
        string? token,
        out AgentCapability capability)
    {
        switch (token)
        {
            case TerminalRead:
                capability = AgentCapability.TerminalRead;
                return true;
            case RunCommands:
                capability = AgentCapability.RunCommands;
                return true;
            case EditFiles:
                capability = AgentCapability.EditFiles;
                return true;
            case ReadFiles:
                capability = AgentCapability.ReadFiles;
                return true;
            case Search:
                capability = AgentCapability.Search;
                return true;
            case Git:
                capability = AgentCapability.Git;
                return true;
            case WebFetch:
                capability = AgentCapability.WebFetch;
                return true;
            case Docker:
                capability = AgentCapability.Docker;
                return true;
            case DestructiveTerminalActions:
                capability = AgentCapability.DestructiveTerminalActions;
                return true;
            case BrowserNavigation:
                capability = AgentCapability.BrowserNavigation;
                return true;
            case BrowserData:
                capability = AgentCapability.BrowserData;
                return true;
            case ProcessControl:
                capability = AgentCapability.ProcessControl;
                return true;
            case McpTools:
                capability = AgentCapability.McpTools;
                return true;
            case SecretUse:
                capability = AgentCapability.SecretUse;
                return true;
            case BrowserInteraction:
                capability = AgentCapability.BrowserInteraction;
                return true;
            case BrowserScripting:
                capability = AgentCapability.BrowserScripting;
                return true;
            case BrowserDiagnostics:
                capability = AgentCapability.BrowserDiagnostics;
                return true;
            case DatabaseRead:
                capability = AgentCapability.DatabaseRead;
                return true;
            case DatabaseWrite:
                capability = AgentCapability.DatabaseWrite;
                return true;
            case DockerData:
                capability = AgentCapability.DockerData;
                return true;
            case SystemData:
                capability = AgentCapability.SystemData;
                return true;
            case ProcessData:
                capability = AgentCapability.ProcessData;
                return true;
            case ArtifactTransfer:
                capability = AgentCapability.ArtifactTransfer;
                return true;
            case WorkspaceLayout:
                capability = AgentCapability.WorkspaceLayout;
                return true;
            case GitData:
                capability = AgentCapability.GitData;
                return true;
            default:
                capability = default;
                return false;
        }
    }
}

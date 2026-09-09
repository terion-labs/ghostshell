using Asura.Core;

namespace Asura.App.ViewModels;

internal static class AgentPolicyPresentation
{
    public static string CapabilityName(AgentCapability capability) =>
        capability switch
        {
            AgentCapability.TerminalRead => "Terminal read",
            AgentCapability.RunCommands => "Run commands",
            AgentCapability.EditFiles => "Edit files",
            AgentCapability.ReadFiles => "Read files",
            AgentCapability.Search => "Search",
            AgentCapability.GitData => "Git data",
            AgentCapability.Git => "Git",
            AgentCapability.WebFetch => "Web search and fetch",
            AgentCapability.Docker => "Docker",
            AgentCapability.DestructiveTerminalActions => "Destructive terminal actions",
            AgentCapability.BrowserNavigation => "Browser navigation",
            AgentCapability.BrowserData => "Browser data",
            AgentCapability.ProcessControl => "Process control",
            AgentCapability.McpTools => "MCP tools",
            AgentCapability.SecretUse => "Secret use",
            AgentCapability.BrowserInteraction => "Browser interaction",
            AgentCapability.BrowserScripting => "Browser scripting",
            AgentCapability.BrowserDiagnostics => "Browser diagnostics",
            AgentCapability.DatabaseRead => "Database read",
            AgentCapability.DatabaseWrite => "Database write",
            AgentCapability.DockerData => "Docker data",
            AgentCapability.SystemData => "System data",
            AgentCapability.ProcessData => "Process data",
            AgentCapability.ArtifactTransfer => "Artifact transfer",
            AgentCapability.WorkspaceLayout => "Workspace layout",
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };

    public static string PermissionName(AgentPermission permission) =>
        permission == AgentPermission.Yolo
            ? "Full access"
            : permission.ToString();

    public static string CapabilityDescription(AgentCapability capability) =>
        capability switch
        {
            AgentCapability.TerminalRead => "Read visible terminal output.",
            AgentCapability.RunCommands => "Send commands to terminal sessions.",
            AgentCapability.EditFiles => "Create, modify, and delete files.",
            AgentCapability.ReadFiles => "Read files and list directories.",
            AgentCapability.Search => "Search file contents and paths.",
            AgentCapability.GitData => "Inspect Git repositories without changing them.",
            AgentCapability.Git => "Change Git repositories.",
            AgentCapability.WebFetch => "Search and fetch internet resources.",
            AgentCapability.Docker => "Manage Docker workload lifecycle.",
            AgentCapability.DestructiveTerminalActions =>
                "Run terminal actions classified as destructive.",
            AgentCapability.BrowserNavigation => "Navigate browser panels.",
            AgentCapability.BrowserData => "Read browser page state.",
            AgentCapability.BrowserInteraction => "Interact with page controls.",
            AgentCapability.ProcessControl =>
                "Control local processes when mutation tools are available.",
            AgentCapability.McpTools => "Use tools exposed by configured MCP servers.",
            AgentCapability.SecretUse => "Resolve approved secret references for tools.",
            AgentCapability.BrowserScripting =>
                "Evaluate JavaScript in an exact browser document.",
            AgentCapability.BrowserDiagnostics =>
                "Use browser console, network, and approved DevTools diagnostics.",
            AgentCapability.DatabaseRead =>
                "Read bounded relational database and Redis data.",
            AgentCapability.DatabaseWrite =>
                "Modify relational database and Redis data.",
            AgentCapability.DockerData =>
                "Inspect Docker workloads without lifecycle control.",
            AgentCapability.SystemData =>
                "Read aggregate local system statistics.",
            AgentCapability.ProcessData =>
                "Read bounded local process information.",
            AgentCapability.ArtifactTransfer =>
                "Transfer approved browser and cross-panel artifacts.",
            AgentCapability.WorkspaceLayout =>
                "Create, split, and close tabs and panels in this workspace.",
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };
}

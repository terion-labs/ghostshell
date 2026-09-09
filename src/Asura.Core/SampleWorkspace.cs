namespace Asura.Core;

public static class SampleWorkspace
{
    public static WorkspaceSnapshot Create()
    {
        ConnectionProfile[] connections =
        [
            CreateSshConnection(
                new ConnectionId("production-api"),
                "production-api",
                "10.0.1.42",
                "deploy",
                ["production", "us-east"]),
            CreateSshConnection(
                new ConnectionId("staging-web"),
                "staging-web",
                "staging.ember.dev",
                "dev",
                ["staging"]),
            CreateSshConnection(
                new ConnectionId("postgres-primary"),
                "postgres-primary",
                "db.internal",
                "admin",
                ["database", "production"]),
        ];

        var mainPanel = new TerminalPanel(
            new PanelId("api-server"),
            "deploy@api-server-01 · ~/app · main",
            connections[0].Id,
            new PanelBounds(0, 0, 8, 8),
            CreateMainCommandBlocks(),
            true);

        var logPanel = new TerminalPanel(
            new PanelId("db-logs"),
            "deploy@db-01 · logs",
            connections[2].Id,
            new PanelBounds(8, 0, 4, 4),
            [
                new CommandBlock(
                    new CommandBlockId("tail-db"),
                    CommandActor.User,
                    "~/logs",
                    "tail -f postgres.log",
                    "14:22:04  WARN  slow query: 1284ms\n14:22:08  INFO  checkpoint complete",
                    CommandStatus.Running,
                    TimeSpan.FromSeconds(28)),
            ],
            false);

        var webPanel = new TerminalPanel(
            new PanelId("local-web"),
            "local · ~/dev/asura",
            connections[1].Id,
            new PanelBounds(8, 4, 4, 4),
            [
                new CommandBlock(
                    new CommandBlockId("vite"),
                    CommandActor.User,
                    "~/dev/asura",
                    "npm run dev",
                    "VITE ready in 412ms\n➜ Local: http://localhost:5173",
                    CommandStatus.Succeeded,
                    TimeSpan.FromMilliseconds(412)),
            ],
            false);

        WorkspaceTab[] tabs =
        [
            new(new TabId("api-server"), "api-server", WorkspaceTabKind.Terminal, [mainPanel], false),
            new(new TabId("deploy-dashboard"), "Deploy Dashboard", WorkspaceTabKind.Screen, [mainPanel, logPanel, webPanel], true),
            new(new TabId("staging"), "staging", WorkspaceTabKind.Terminal, [webPanel], false),
        ];

        return new WorkspaceSnapshot(
            new WorkspaceId("production"),
            "Production",
            "#FF5C33",
            connections,
            tabs,
            AgentPolicy.Default);
    }

    private static ConnectionProfile CreateSshConnection(
        ConnectionId id,
        string name,
        string host,
        string username,
        IReadOnlyList<string> tags) =>
        new(
            id,
            ConnectionProfile.CurrentSchemaVersion,
            name,
            new ConnectionEndpoint.Ssh(host, username: username),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.EnabledEvery(TimeSpan.FromSeconds(30)),
            SshHostKeyPolicy.Strict,
            tags);

    private static CommandBlock[] CreateMainCommandBlocks() =>
    [
        new(
            new CommandBlockId("pull"),
            CommandActor.User,
            "~/app",
            "git pull origin main",
            "remote: Enumerating objects: 42, done.\nFast-forward · 6 files changed, 214 insertions(+), 38 deletions(-)",
            CommandStatus.Succeeded,
            TimeSpan.FromMilliseconds(800)),
        new(
            new CommandBlockId("build"),
            CommandActor.User,
            "~/app",
            "pnpm build",
            "✓ Compiled successfully\nRoute (app)          Size     First Load JS\n/                    1.2 kB   96.4 kB\n/dashboard           18.7 kB  142 kB",
            CommandStatus.Succeeded,
            TimeSpan.FromSeconds(12.4)),
        new(
            new CommandBlockId("deploy"),
            CommandActor.Agent,
            "~/app",
            "pnpm deploy --prod",
            "Deploying to production…\nError: Missing env var DATABASE_URL\n    at loadConfig (config.ts:44)",
            CommandStatus.Failed,
            TimeSpan.FromSeconds(3.1)),
    ];
}

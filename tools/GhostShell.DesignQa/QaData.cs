using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.DesignQa;

/// <summary>
/// Deterministic presentation fixture that mirrors the density of the Pencil
/// reference frames. This is harness-only sample content: it is never written
/// to a store and never presented as the user's real connections or sessions.
/// </summary>
internal static class QaData
{
    public static readonly DateTimeOffset Now =
        new(2026, 7, 26, 9, 30, 0, TimeSpan.Zero);

    private static StoredDefinition<T> Stored<T>(T value)
        where T : IDurableDefinition =>
        new(value, 1, Now, Now);

    private static ConnectionProfile Connection(
        string id,
        string name,
        ConnectionEndpoint endpoint,
        SshHostKeyPolicy hostKeyPolicy,
        params string[] tags) =>
        new(
            new ConnectionId(id),
            ConnectionProfile.CurrentSchemaVersion,
            name,
            endpoint,
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            hostKeyPolicy,
            tags);

    public static IReadOnlyList<StoredDefinition<ConnectionProfile>> Connections { get; } =
    [
        Stored(Connection(
            "production-api",
            "production-api",
            new ConnectionEndpoint.Ssh("10.0.1.42", 22, "deploy"),
            SshHostKeyPolicy.AcceptNew,
            "production",
            "us-east")),
        Stored(Connection(
            "staging-web",
            "staging-web",
            new ConnectionEndpoint.Ssh("staging.ember.dev", 22, "dev"),
            SshHostKeyPolicy.AcceptNew,
            "staging")),
        Stored(Connection(
            "postgres-primary",
            "postgres-primary",
            new ConnectionEndpoint.Ssh("db.internal", 22, "admin"),
            SshHostKeyPolicy.AcceptNew,
            "database",
            "production")),
        Stored(Connection(
            "local-dev",
            "local-dev",
            new ConnectionEndpoint.Local("/bin/zsh"),
            SshHostKeyPolicy.NotApplicable,
            "local")),
        Stored(Connection(
            "redis-cache",
            "redis-cache",
            new ConnectionEndpoint.Docker("redis-cache", "ops"),
            SshHostKeyPolicy.NotApplicable,
            "cache",
            "staging")),
        Stored(Connection(
            "bastion-eu",
            "bastion-eu",
            new ConnectionEndpoint.Ssh("bastion.eu-west", 22, "jump"),
            SshHostKeyPolicy.AcceptNew,
            "production",
            "eu-west")),
        Stored(Connection(
            "ci-runner",
            "ci-runner",
            new ConnectionEndpoint.Wsl("Ubuntu", "runner"),
            SshHostKeyPolicy.NotApplicable,
            "ci")),
        Stored(Connection(
            "edge-proxy",
            "edge-proxy",
            new ConnectionEndpoint.Docker("edge-proxy", "admin"),
            SshHostKeyPolicy.NotApplicable,
            "edge",
            "production")),
    ];

    private static readonly LayoutSlotId SlotA = new("slot-a");
    private static readonly LayoutSlotId SlotB = new("slot-b");
    private static readonly LayoutSlotId SlotC = new("slot-c");
    private static readonly LayoutSlotId SlotD = new("slot-d");

    private static LayoutDefinition Layout(
        string id,
        string name,
        params LayoutSlotDefinition[] slots) =>
        new(new LayoutId(id), LayoutDefinition.CurrentSchemaVersion, name, new LayoutGrid(12, 8), slots);

    private static LayoutSlotDefinition Slot(
        LayoutSlotId id, int column, int row, int columnSpan, int rowSpan) =>
        new(id, new LayoutGridBounds(column, row, columnSpan, rowSpan), new LayoutMinimumSize(160, 90));

    public static IReadOnlyList<StoredDefinition<LayoutDefinition>> Layouts { get; } =
    [
        Stored(Layout(
            "split-three",
            "Split · three panels",
            Slot(SlotA, 0, 0, 6, 8),
            Slot(SlotB, 6, 0, 6, 4),
            Slot(SlotC, 6, 4, 6, 4))),
        Stored(Layout(
            "grid-four",
            "Grid · four panels",
            Slot(SlotA, 0, 0, 6, 4),
            Slot(SlotB, 6, 0, 6, 4),
            Slot(SlotC, 0, 4, 6, 4),
            Slot(SlotD, 6, 4, 6, 4))),
        Stored(Layout(
            "stacked-three",
            "Stacked · three panels",
            Slot(SlotA, 0, 0, 12, 4),
            Slot(SlotB, 0, 4, 6, 4),
            Slot(SlotC, 6, 4, 6, 4))),
        Stored(Layout(
            "split-two",
            "Split · two panels",
            Slot(SlotA, 0, 0, 6, 8),
            Slot(SlotB, 6, 0, 6, 8))),
    ];

    private static ScreenPanelDefinition Panel(
        string id, LayoutSlotId slot, string title, string connection) =>
        new(
            new ScreenPanelId(id),
            slot,
            ScreenPanelKind.Terminal,
            title,
            new ConnectionId(connection),
            PanelStartupBehavior.None);

    public static IReadOnlyList<StoredDefinition<ScreenDefinition>> Screens { get; } =
    [
        Stored(new ScreenDefinition(
            new ScreenId("deploy-dashboard"),
            ScreenDefinition.CurrentSchemaVersion,
            "Deploy Dashboard",
            "Release watch for the production API and staging web tier.",
            new LayoutId("split-three"),
            [
                Panel("deploy-a", SlotA, "prod-api", "production-api"),
                Panel("deploy-b", SlotB, "staging-web", "staging-web"),
                Panel("deploy-c", SlotC, "logs", "production-api"),
            ],
            ["production"])),
        Stored(new ScreenDefinition(
            new ScreenId("full-stack-dev"),
            ScreenDefinition.CurrentSchemaVersion,
            "Full-stack Dev",
            "Local shell, database, and cache side by side.",
            new LayoutId("grid-four"),
            [
                Panel("dev-a", SlotA, "local-dev", "local-dev"),
                Panel("dev-b", SlotB, "postgres-primary", "postgres-primary"),
                Panel("dev-c", SlotC, "redis-cache", "redis-cache"),
                Panel("dev-d", SlotD, "edge-proxy", "edge-proxy"),
            ],
            ["local"])),
        Stored(new ScreenDefinition(
            new ScreenId("log-monitor"),
            ScreenDefinition.CurrentSchemaVersion,
            "Log Monitor",
            "Edge and CI log tails.",
            new LayoutId("stacked-three"),
            [
                Panel("log-a", SlotA, "edge-proxy", "edge-proxy"),
                Panel("log-b", SlotB, "ci-runner", "ci-runner"),
                Panel("log-c", SlotC, "bastion-eu", "bastion-eu"),
            ],
            ["observability"])),
        Stored(new ScreenDefinition(
            new ScreenId("infra-overview"),
            ScreenDefinition.CurrentSchemaVersion,
            "Infra Overview",
            "Bastion and cache overview.",
            new LayoutId("split-two"),
            [
                Panel("infra-a", SlotA, "bastion-eu", "bastion-eu"),
                Panel("infra-b", SlotB, "redis-cache", "redis-cache"),
            ],
            ["production"])),
    ];

    // Declared before the workspaces that reference them: static initializers
    // run in textual order, and a policy built from an unset ID throws.
    private static readonly NetworkConnectionId CorporateAnyConnectId = new("corporate-anyconnect");

    private static readonly NetworkConnectionId CorpProxyId = new("corp-proxy");

    private static readonly NetworkConnectionId HomelabTailnetId = new("homelab-tailnet");

    private static readonly NetworkConnectionId PersonalWireGuardId = new("personal-wireguard");

    private static readonly NetworkConnectionId ProtonOpenVpnId = new("proton-openvpn");

    public static IReadOnlyList<StoredDefinition<WorkspaceDefinition>> Workspaces { get; } =
    [
        Stored(new WorkspaceDefinition(
            new WorkspaceId("operations"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            "Production release watch",
            "#B8793A",
            [
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("ops-deploy"),
                    new ScreenId("deploy-dashboard"),
                    "Deploy Dashboard"),
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("ops-logs"),
                    new ScreenId("log-monitor"),
                    "Log Monitor"),
                // One of each kind, because the editor's rows are meant to make
                // the difference between them visible and a fixture of all one
                // kind cannot show that.
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("ops-api"),
                    new ConnectionId("production-api"),
                    null),
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("ops-release-notes"),
                    "Release Notes",
                    new LayoutId("split-two"),
                    [
                        Panel("ops-notes-a", SlotA, "Notes", "production-api"),
                        Panel("ops-notes-b", SlotB, "Checklist", "staging-web"),
                    ]),
            ],
            icon: "rocket")),
        Stored(new WorkspaceDefinition(
            new WorkspaceId("data"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Data",
            "Warehouse and reporting",
            accent: null,
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("data-postgres"),
                    new ConnectionId("postgres-primary"),
                    null),
            ],
            icon: "database",
            // A colour with no accent: the workspace is marked without retinting
            // the shell, which is the pairing the two fields exist to allow.
            color: "#5B8FD1")),
        Stored(new WorkspaceDefinition(
            new WorkspaceId("development"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Development",
            "Local stack and services",
            "#5FA97A",
            [
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("dev-stack"),
                    new ScreenId("full-stack-dev"),
                    "Full-stack Dev"),
            ],
            icon: "code")),
        // Isolated, with host folders mounted and its own network route, so
        // the editor's isolation and networking groups render populated
        // rather than as two toggles that are off.
        Stored(new WorkspaceDefinition(
            new WorkspaceId("field"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Field",
            "Customer site work over the corporate VPN",
            "#7A6BD1",
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("field-bastion"),
                    new ConnectionId("bastion-eu"),
                    null),
            ],
            icon: "shield",
            isIsolated: true,
            isolationMounts:
            [
                new WorkspaceIsolationMountDefinition("/Users/me/projects/field", "/workspace", false),
                new WorkspaceIsolationMountDefinition("/Users/me/.ssh", "/home/me/.ssh", true),
            ],
            networkOverride: new NetworkPolicy(
                [CorporateAnyConnectId, CorpProxyId],
                CorporateAnyConnectId,
                isEnabled: true,
                killSwitchEnabled: true))),
    ];

    /// <summary>
    /// Every connection kind the list distinguishes, named the way the feature
    /// is meant to be used: the corporate VPN for client work, the homelab's
    /// tailnet, a personal WireGuard tunnel, a commercial OpenVPN, and a proxy
    /// with an endpoint and an account.
    /// </summary>
    public static IReadOnlyList<StoredDefinition<NetworkConnectionProfile>> NetworkConnections { get; } =
    [
        Stored(new NetworkConnectionProfile(
            CorporateAnyConnectId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Corporate AnyConnect",
            new NetworkConnectionConfiguration.AnyConnect(
                new Uri("https://vpn.corp.example"),
                "terion",
                new SecretRef("qa-corporate-anyconnect-password"),
                "employees"))),
        Stored(new NetworkConnectionProfile(
            CorpProxyId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Corp proxy",
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Socks5,
                "proxy.corp.example",
                1080,
                "terion",
                new SecretRef("qa-corp-proxy-password")))),
        Stored(new NetworkConnectionProfile(
            HomelabTailnetId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Homelab tailnet",
            new NetworkConnectionConfiguration.Tailscale("homelab-gateway"))),
        Stored(new NetworkConnectionProfile(
            PersonalWireGuardId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Personal WireGuard",
            new NetworkConnectionConfiguration.WireGuard(new SecretRef("qa-personal-wireguard-config")))),
        Stored(new NetworkConnectionProfile(
            ProtonOpenVpnId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Proton OpenVPN",
            new NetworkConnectionConfiguration.OpenVpn(
                new SecretRef("qa-proton-openvpn-profile"),
                "proton-user",
                new SecretRef("qa-proton-openvpn-password")))),
    ];

    /// <summary>The application default: the personal WireGuard tunnel, kill switch on.</summary>
    public static StoredDefinition<ApplicationNetworkSettings> ApplicationNetworkSettings { get; } =
        Stored(new ApplicationNetworkSettings(
            GhostShell.Core.ApplicationNetworkSettings.DefaultId,
            GhostShell.Core.ApplicationNetworkSettings.CurrentSchemaVersion,
            "Application networking",
            new NetworkPolicy(
                [CorporateAnyConnectId, CorpProxyId, HomelabTailnetId, PersonalWireGuardId, ProtonOpenVpnId],
                PersonalWireGuardId,
                isEnabled: true,
                killSwitchEnabled: true)));

    /// <summary>
    /// The same built-in terminal profile the catalog seeds on first run, so the
    /// terminal and keybinding routes render with the shipped defaults rather
    /// than empty controls.
    /// </summary>
    public static IReadOnlyList<StoredDefinition<TerminalProfile>> TerminalProfiles { get; } =
    [
        Stored(new TerminalProfile(
            new TerminalProfileId("builtin.terminal.default"),
            "Default terminal",
            "JetBrains Mono",
            14,
            1.4,
            TerminalCursorStyle.Block,
            cursorBlink: true,
            100_000,
            TerminalPalette.GhostShellDark,
            BuiltInKeymaps.MacOsTerminalId)),
    ];

    public static IReadOnlyList<StoredDefinition<KeymapProfile>> Keymaps { get; } =
        [.. BuiltInKeymaps.All.Select(Stored)];

    /// <summary>
    /// The capture set exercises a side-docked tab strip so the vertical layout
    /// is reviewable, not only the default top strip.
    /// </summary>
    public static ThemePreference SideTabTheme { get; } = new(
        ThemePreference.Default.Id,
        ThemePreference.Default.Name,
        AppearanceMode.System,
        PlatformProfile.Automatic,
        AccentPreference.FollowHost,
        tabStripPlacement: TabStripPlacement.Left);

    public static IReadOnlyList<StoredDefinition<FileProviderProfile>> FileProviderProfiles { get; } =
    [
        Stored(new FileProviderProfile(
            new FileProviderProfileId("release-artifacts"),
            FileProviderProfile.CurrentSchemaVersion,
            "release-artifacts",
            new FileProviderConfiguration.S3("release-artifacts", "eu-central-1"))),
        Stored(new FileProviderProfile(
            new FileProviderProfileId("staging-uploads"),
            FileProviderProfile.CurrentSchemaVersion,
            "staging-uploads",
            new FileProviderConfiguration.Sftp(
                new ConnectionId("staging-web"),
                "/var/uploads"))),
    ];

    public static IReadOnlyList<StoredDefinition<DatabaseConnectionProfile>> DatabaseConnections { get; } =
    [
        Stored(new DatabaseConnectionProfile(
            new DatabaseConnectionProfileId("core-warehouse"),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "core-warehouse",
            "postgres",
            "Host=warehouse.internal;Port=5432;Database=events;Username=reader",
            passwordSecret: new SecretRef("qa-database-password"),
            tunnelConnectionId: new ConnectionId("bastion-eu"))),
        Stored(new DatabaseConnectionProfile(
            new DatabaseConnectionProfileId("local-metrics"),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "local-metrics",
            "sqlite",
            "/Users/terion/metrics.db")),
    ];


    /// <summary>A delimited sample for the table capture, quoting and all.</summary>
    public const string SampleCsv = """
service,region,status,deployed_at,note
billing-api,eu-central-1,healthy,2026-08-02T21:14:09Z,"rolled forward, no downtime"
billing-api,us-east-1,healthy,2026-08-02T21:12:44Z,
checkout-web,eu-central-1,rolled-back,2026-08-02T19:03:18Z,"failed health check twice"
ledger-worker,eu-central-1,healthy,2026-08-02T17:40:51Z,
search-index,ap-south-1,degraded,2026-08-02T16:22:03Z,"replica lag above threshold"
""";

    /// <summary>A C# sample for the syntax-highlight capture.</summary>
    public const string SampleCSharp = """
using System.Text;

namespace GhostShell.Core;

/// <summary>How a terminal session begins.</summary>
public sealed record ConnectionStartup
{
    public ConnectionStartup(string? directory = null, string? command = null)
    {
        Directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        Command = command;
    }

    public string? Directory { get; }

    public string? Command { get; }

    public override string ToString()
    {
        var builder = new StringBuilder("startup");
        if (Directory is { } directory)
        {
            builder.Append($" in {directory}");
        }

        return builder.ToString(); // 2 + 2 == 4
    }
}
""";

    /// <summary>
    /// A C# sample with lines far wider than a narrow preview, so wrapping is
    /// exercised rather than assumed.
    /// </summary>
    public const string LongLinedCSharp = """
public static TerminalLaunchRequest Plan(ConnectionProfile profile, ConnectionStartup startup) =>
    new(profile.Endpoint, profile.Credential, startup.Directory, startup.Environment, startup.Command);

// A comment long enough that no reasonable panel width can hold it on one line, which is the point.
""";

    /// <summary>
    /// Markdown whose fenced block is long enough to expose a fence sized from
    /// a guessed line height rather than from what the editor laid out.
    /// </summary>
    public const string MarkdownWithLongFence = """
# Control theory

A PID controller turns the error between target and actual warm instances into a
pool adjustment:

```python
class PoolController:
    def __init__(self, kp, ki, kd, minimum, maximum):
        self.kp = kp
        self.ki = ki
        self.kd = kd
        self.minimum = minimum
        self.maximum = maximum
        self.integral = 0.0
        self.prev_error_filtered = 0.0

    def step(self, target, actual, dt):
        error = target - actual
        self.integral += error * dt
        derivative = (error - self.prev_error_filtered) / dt
        output = self.kp * error + self.ki * self.integral + self.kd * derivative
        clamped = max(self.minimum, min(self.maximum, output))
        self.prev_error_filtered = error
        return int(clamped)
```

Start with a PI controller and add the derivative term only if oscillation is a
problem.
""";


    /// <summary>A Markdown sample covering every block the renderer handles.</summary>
    public const string SampleMarkdown = """
        # Connection profiles

        Profiles store **secret references**, never secret values. A profile is
        *durable*: it survives restarts, and `ConnectionStartup` decides what
        happens when a session opens. See [the design notes](https://example.com).

        ## What a profile carries

        - An endpoint — local, SSH, Docker, or WSL
        - An authentication choice, as an opaque reference
        - An optional startup directory and ~~command line~~ command

        1. Validate the profile
        2. Resolve credentials
        3. Build the launch plan

        > A terminal claims its stored credential through a one-use helper when
        > it starts, never before.

        ```csharp
        var startup = new ConnectionStartup(
            directory: "/srv/app",
            command: "npm run dev");
        ```

        | Field | Meaning |
        | --- | --- |
        | Directory | Where the shell begins |
        | Command | Typed once the shell is ready |

        ---

        Trailing prose, so the last block is not a rule.
        """;

    public static DefinitionCatalogSnapshot Snapshot { get; } = new(
        Connections,
        Layouts,
        Screens,
        Workspaces,
        [Stored(SideTabTheme)],
        TerminalProfiles,
        Keymaps,
        FileProviderProfiles,
        [Stored(QuickTerminalSettings.Default)])
    {
        DatabaseConnections = DatabaseConnections,
        NetworkConnections = NetworkConnections,
        ApplicationNetworkSettings = [ApplicationNetworkSettings],
        BrowserProfiles =
        [
            Stored(BuiltInBrowserProfiles.Default),
            Stored(new BrowserProfileDefinition(
                new BrowserProfileId("qa.browser.shared"),
                BrowserProfileDefinition.CurrentSchemaVersion,
                "Operations",
                BrowserProfilePersistence.DurableMetadata,
                BrowserProfilePrivacyPolicy.Strict)),
            Stored(new BrowserProfileDefinition(
                new BrowserProfileId("qa.browser.private"),
                BrowserProfileDefinition.CurrentSchemaVersion,
                "Private sign-in",
                BrowserProfilePersistence.PrivateSession,
                BrowserProfilePrivacyPolicy.Strict)),
        ],
    };
}

/// <summary>A startup gate that is locked, for the lock-screen capture.</summary>
internal sealed class QaLockedProtection : GhostShell.Application.IStartupProtection
{
    public bool IsEnabled => true;

    public bool IsLocked => true;

    public TimeSpan? LockTimeout => TimeSpan.FromMinutes(5);

    public int RetryDelaySeconds => 0;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public ValueTask<string?> EnableAsync(string pin, CancellationToken cancellationToken) =>
        new((string?)null);

    public ValueTask<string?> DisableAsync(string pin, CancellationToken cancellationToken) =>
        new((string?)null);

    public ValueTask<bool> TryUnlockAsync(string pin, CancellationToken cancellationToken) =>
        new(false);

    public void Lock()
    {
    }

    public void UnlockAuthenticated()
    {
    }

    public ValueTask SetLockTimeoutAsync(TimeSpan? timeout, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask<string?> SealEncryptionKeysAsync(
        string pin,
        CancellationToken cancellationToken) =>
        new((string?)null);
}

/// <summary>Biometrics that exist but never verify, for the capture.</summary>
internal sealed class QaBiometrics : GhostShell.Application.IBiometricAuthenticator
{
    public bool IsAvailable => true;

    public string MethodName => "Touch ID";

    public Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

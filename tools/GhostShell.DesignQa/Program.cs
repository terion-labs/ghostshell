using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.App.Views;
using GhostShell.App.Views.Components;
using GhostShell.Application;
using GhostShell.Application.Previews;
using GhostShell.Core;

namespace GhostShell.DesignQa;

/// <summary>
/// Presentation-only capture harness. It renders the product's real
/// <see cref="MainWindow"/>, styles, and view models against a deterministic
/// in-memory fixture and writes one PNG per route. Rendering happens in-process
/// through <see cref="RenderTargetBitmap"/>, so it needs no screen-recording
/// permission and never captures unrelated windows.
/// </summary>
internal static class Program
{
    private const string FixtureTimeZone = "Europe/Kyiv";

    public static string OutputDirectory { get; private set; } = string.Empty;

    /// <summary>
    /// A real SQLite database written beside the captures, so the database
    /// preview is exercised against an actual file rather than a mock.
    /// </summary>
    public static string SqliteProbePath { get; private set; } = string.Empty;

    /// <summary>A real TIFF, so the image decoder is exercised on a real file.</summary>
    public static string TiffProbePath { get; private set; } = string.Empty;

    /// <summary>A real two-page PDF, so the renderer is exercised on a document.</summary>
    public static string PdfProbePath { get; private set; } = string.Empty;

    /// <summary>
    /// A JPEG far larger than the bounded preview read, so the whole-file image
    /// path is exercised on a photograph rather than a thumbnail.
    /// </summary>
    public static string JpegProbePath { get; private set; } = string.Empty;

    public static string[] RequestedRoutes { get; private set; } = [];

    public static bool IsWebsiteExport { get; private set; }

    public static bool IsTerminalFontVerification { get; private set; }

    public static DesignQaBaselineMode BaselineMode { get; private set; }

    public static string BaselinePath { get; private set; } = string.Empty;

    private static readonly string[] GateRoutes =
    [
        "workspace",
        "workspace-minimum",
        "workspace-agent",
        "workspace-file-viewer",
        "workspace-browser",
        "workspace-statistics",
        "workspace-process-monitor",
        "workspace-docker",
        "workspace-git",
        "workspace-database",
        "workspace-redis",
        "settings-appearance",
        "settings-workspaces",
        "settings-networking",
        "settings-networking-editor",
        "settings-workspace-editor-isolated",
        "settings-workspace-editor-networking",
        "settings-terminal",
        "settings-quick-terminal",
        "settings-keybindings",
        "settings-files",
        "settings-browser",
        "settings-agent",
        "settings-mcp",
        "settings-secrets",
        "settings-diagnostics",
        "settings-about",
        "overlay-command-palette",
        "overlay-new-panel",
        "overlay-layout-designer",
        "settings-appearance-focused",
        "design-system",
        "design-system-light",
        "design-system-high-contrast",
        "design-system-scale-200",
        "design-system-scale-250",
    ];

    [STAThread]
    public static void Main(string[] args)
    {
        // File metadata is intentionally presented in the reader's local
        // time. Pin that environment input before any view model formats a
        // timestamp so captures mean the same thing on developer and hosted
        // machines.
        Environment.SetEnvironmentVariable("TZ", FixtureTimeZone);
        TimeZoneInfo.ClearCachedData();

        if (args is ["--compare", var sourcePath, var implementationPath, var outputPath])
        {
            WriteSideBySideComparison(sourcePath, implementationPath, outputPath);
            return;
        }

        if (args is ["--verify-terminal-font"])
        {
            IsTerminalFontVerification = true;
            BuildAvaloniaApp().SetupWithoutStarting();
            VerifyTerminalFont();
            return;
        }

        if (args.FirstOrDefault() is "--gate" or "--approve-baseline")
        {
            BaselineMode = string.Equals(
                args[0],
                "--gate",
                StringComparison.Ordinal)
                ? DesignQaBaselineMode.Verify
                : DesignQaBaselineMode.Approve;
            OutputDirectory = Path.GetFullPath(
                args.ElementAtOrDefault(1)
                ?? Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "artifacts",
                    "design-qa",
                    "gate"));
            BaselinePath = Path.GetFullPath(
                args.ElementAtOrDefault(2)
                ?? Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "tools",
                    "GhostShell.DesignQa",
                    "design-qa-baseline.json"));
            RequestedRoutes = GateRoutes;
        }

        else if (string.Equals(args.FirstOrDefault(), "--website", StringComparison.Ordinal))
        {
            IsWebsiteExport = true;
            OutputDirectory = Path.GetFullPath(
                args.ElementAtOrDefault(1)
                ?? Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "artifacts",
                    "design-qa",
                    "website"));
            RequestedRoutes = [.. args.Skip(2)];
        }
        else
        {
            OutputDirectory = Path.GetFullPath(
                args.FirstOrDefault()
                ?? Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "artifacts",
                    "design-qa",
                    "current"));
            RequestedRoutes = [.. args.Skip(1)];
        }

        SqliteProbePath = Path.Combine(OutputDirectory, "probe.sqlite");
        TiffProbePath = Path.Combine(OutputDirectory, "probe.tiff");
        PdfProbePath = Path.Combine(OutputDirectory, "probe.pdf");
        JpegProbePath = Path.Combine(OutputDirectory, "probe.jpg");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static void WriteSideBySideComparison(
        string sourcePath,
        string implementationPath,
        string outputPath)
    {
        const uint columnWidth = 920;
        const uint gutter = 8;
        using var source = new ImageMagick.MagickImage(sourcePath);
        using var implementation = new ImageMagick.MagickImage(implementationPath);
        source.Resize(new ImageMagick.MagickGeometry(columnWidth, 0));
        implementation.Resize(new ImageMagick.MagickGeometry(columnWidth, 0));
        var height = Math.Max(source.Height, implementation.Height);
        using var comparison = new ImageMagick.MagickImage(
            new ImageMagick.MagickColor("#111111"),
            (columnWidth * 2) + gutter,
            height);
        comparison.Composite(
            source,
            0,
            (int)(height - source.Height) / 2,
            ImageMagick.CompositeOperator.Over);
        comparison.Composite(
            implementation,
            (int)(columnWidth + gutter),
            (int)(height - implementation.Height) / 2,
            ImageMagick.CompositeOperator.Over);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        comparison.Format = ImageMagick.MagickFormat.Png;
        comparison.Write(outputPath);
        Console.WriteLine(
            $"COMPARE {sourcePath} + {implementationPath} -> {outputPath} "
            + $"({comparison.Width}x{comparison.Height})");
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<QaApplication>()
            // Headless with real Skia drawing: every capture already renders
            // offscreen through RenderTargetBitmap, so the on-screen windows
            // only ever existed to pump layout — and stole focus doing it.
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
        if (!IsWebsiteExport)
        {
            builder = builder.WithInterFont();
        }

        // Website artwork is generated on macOS and must inherit the same native
        // default UI font as the real app. WithInterFont replaces that default;
        // matching density tokens then still look roomier because Inter's text
        // metrics are wider and taller than the system UI face.
        return builder
            .ConfigureFonts(fontManager =>
                fontManager.AddFontCollection(new GhostShellTerminalFontCollection()))
            .LogToTrace();
    }

    private static void VerifyTerminalFont()
    {
        var faces = new[]
        {
            (File: "JetBrainsMono-Regular.ttf", Style: FontStyle.Normal, Weight: FontWeight.Normal),
            (File: "JetBrainsMono-Bold.ttf", Style: FontStyle.Normal, Weight: FontWeight.Bold),
            (File: "JetBrainsMono-Italic.ttf", Style: FontStyle.Italic, Weight: FontWeight.Normal),
            (File: "JetBrainsMono-BoldItalic.ttf", Style: FontStyle.Italic, Weight: FontWeight.Bold),
        };

        foreach (var face in faces)
        {
            var assetUri = new Uri(
                $"avares://GhostShell.App/Assets/Fonts/JetBrainsMono/{face.File}",
                UriKind.Absolute);
            using var asset = AssetLoader.Open(assetUri);
            if (asset.Length == 0)
            {
                throw new InvalidOperationException($"Embedded terminal font is empty: {face.File}.");
            }

            var typeface = new Typeface(
                GhostShellTerminalFontCollection.Family,
                face.Style,
                face.Weight);
            if (!FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface)
                || !string.Equals(glyphTypeface.FamilyName, GhostShellTerminalFontCollection.FamilyName
, StringComparison.Ordinal) || glyphTypeface.Style != face.Style
                || glyphTypeface.Weight != face.Weight
                || glyphTypeface.Stretch != FontStretch.Normal
                || glyphTypeface.FontSimulations != FontSimulations.None
                || !glyphTypeface.Metrics.IsFixedPitch)
            {
                throw new InvalidOperationException(
                    $"Embedded terminal font did not resolve to its exact fixed-pitch face: {face.File}.");
            }
        }

        Console.WriteLine(
            $"Verified embedded {GhostShellTerminalFontCollection.FamilyName}: "
            + "regular, bold, italic, and bold italic are fixed-pitch native faces.");
    }
}

internal sealed record RouteCapture(
    string Name,
    Action<MainWindowViewModel> Apply,
    string? FocusFirst = null,
    int Width = 1440,
    int Height = 900,
    ThemePreference? Theme = null,
    string? ClickFirst = null,
    Action<MainWindow>? PrepareCapture = null,
    Func<MainWindowViewModel, Window>? Dialog = null,
    bool HighContrast = false);

internal sealed class QaApplication : Avalonia.Application
{
    private static readonly QaAiProfileRuntime AgentProfiles = new();

    private static readonly QaOfflineAgentRuntime AgentRuntime = new();

    private static readonly EmptyFileClients Files = new();

    private static readonly QaDockerEngineClient Docker = new();
    private static readonly QaGitRepositoryClient Git = new();

    private static readonly RouteCapture[] Routes =
    [
        new("settings-security", vm => vm.ShowSettings(SettingsPage.Secrets)),
        new("settings-appearance", vm => vm.ShowSettings(SettingsPage.Appearance)),
        new("settings-workspaces", vm => vm.ShowSettings(SettingsPage.Workspaces)),
        new("settings-networking", vm => vm.ShowSettings(SettingsPage.Networking)),
        // The connection editor is a dialog, opened on the saved proxy so the
        // account and credential slots render filled rather than blank.
        new(
            "settings-networking-editor",
            vm => vm.ShowSettings(SettingsPage.Networking),
            Dialog: OpenNetworkConnectionEditor),
        new("settings-terminal", vm => vm.ShowSettings(SettingsPage.Terminal)),
        new("settings-quick-terminal", vm => vm.ShowSettings(SettingsPage.QuickTerminal)),
        new("settings-keybindings", vm => vm.ShowSettings(SettingsPage.Keybindings)),
        new("settings-files", vm => vm.ShowSettings(SettingsPage.Files)),
        new("settings-browser", vm => vm.ShowSettings(SettingsPage.Browser)),
        new("settings-agent", vm => vm.ShowSettings(SettingsPage.Agent)),
        new("settings-mcp", vm => vm.ShowSettings(SettingsPage.Mcp)),
        new("settings-secrets", vm => vm.ShowSettings(SettingsPage.Secrets)),
        new("settings-diagnostics", vm => vm.ShowSettings(SettingsPage.Diagnostics)),
        new("settings-about", vm => vm.ShowSettings(SettingsPage.About)),
        new("overlay-command-palette", vm =>
        {
            vm.ShowWorkspace();
            vm.ShowOverlay(ShellOverlay.CommandPalette);
        }),
        new("overlay-new-panel", vm =>
        {
            vm.ShowWorkspace();
            vm.ShowOverlay(ShellOverlay.NewPanel);
        }),
        // Opened on a real layout: an empty designer hides every defect the
        // populated one would show.
        new("overlay-layout-designer", vm =>
        {
            vm.ShowWorkspace();
            vm.BeginEditLayout(new LayoutId("grid-four"));
        }),
        new("workspace", vm => vm.ShowWorkspace()),
        new("workspace-minimum", vm => vm.ShowWorkspace(), Width: 1080, Height: 680),
        // Every mark at once: a panel that asked, the tab and workspace that
        // inherit it, and the rail showing which workspaces are running.
        new(
            "workspace-attention",
            vm =>
            {
                vm.ShowWorkspace();
                MarkBackgroundTabForAttention(vm);
            }),
        // The rail tile opened for closing, in the rail itself: the expansion
        // has to overflow the sidebar and draw over the canvas, which a probe
        // window cannot show.
        new(
            "workspace-rail-close",
            vm =>
            {
                vm.ShowWorkspace();
                MarkBackgroundTabForAttention(vm);
            },
            PrepareCapture: ShowRailTileCloseAction),
        new(
            "workspace-drag-ghost",
            vm => vm.ShowWorkspace(),
            PrepareCapture: ShowSampleDragGhost),
        new(
            "workspace-transfers",
            vm =>
            {
                vm.ShowWorkspace();
                Files.PublishSampleTransfer();
            },
            ClickFirst: "Open transfer manager"),
        // The agent panel's conversation layout is otherwise unreviewable: the
        // harness has no provider and reaches no endpoint, so every other route
        // can only render the panel's empty state.
        new("workspace-agent", vm =>
        {
            vm.ShowWorkspace();
            // Floating is the default, so the panel is summoned the way a
            // person summons it — this capture reviews the flyout state.
            vm.ToggleAgentPanel();
            MarkTerminalPanelForAgentActivity(vm);
            AgentProfiles.PublishSampleProfile();
            AgentRuntime.PublishSampleConversation();
        }),
        new("workspace-agent-reasoning", vm =>
        {
            vm.ShowWorkspace();
            vm.ToggleAgentPanel();
            AgentProfiles.PublishSampleProfile();
            AgentRuntime.PublishSampleReasoningConversation();
        }, PrepareCapture: ShowReasoningStart),
        // The same panel pinned: the layout holds a slot for it and the canvas
        // moves aside instead of being covered.
        new("workspace-agent-docked", vm =>
        {
            vm.ShowWorkspace();
            _ = vm.ToggleAgentPanelPinAsync(CancellationToken.None);
            MarkTerminalPanelForAgentActivity(vm);
            AgentProfiles.PublishSampleProfile();
            AgentRuntime.PublishSampleConversation();
        }),
        new("workspace-agent-failed", vm =>
        {
            vm.ShowWorkspace();
            _ = vm.ToggleAgentPanelPinAsync(CancellationToken.None);
            AgentProfiles.PublishSampleProfile();
            AgentRuntime.PublishSampleFailure();
        }),
        // The one governance decision the panel ever asks. It was the panel's
        // least reviewed surface for exactly that reason.
        new("workspace-agent-capability", vm =>
        {
            vm.ShowWorkspace();
            vm.ToggleAgentPanel();
            AgentProfiles.PublishSampleProfile();
            AgentRuntime.PublishSampleCapabilityRequest();
        }),
        // The local utility panels against deterministic data. These are full
        // workspace routes rather than component probes so the website receives
        // the tab strip, rail, panel chrome, and product content together.
        new("workspace-file-viewer", vm =>
        {
            vm.ShowWorkspace();
            AddSampleFilePanel(vm);
        }, PrepareCapture: SelectFileViewerPreview),
        new("workspace-statistics", vm =>
        {
            vm.ShowWorkspace();
            AddSampleStatisticsPanel(vm);
        }),
        new("workspace-process-monitor", vm =>
        {
            vm.ShowWorkspace();
            AddSampleProcessMonitorPanel(vm);
        }),
        new("workspace-browser", vm =>
        {
            vm.ShowWorkspace();
            AddSampleBrowserPanel(vm);
        }),
        // The Docker browser with realistic engine data: navigation, selected
        // container, lifecycle affordances, inspection rows, and live metrics.
        new("workspace-docker", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDockerPanel(vm);
        }),
        new("workspace-docker-stats", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDockerPanel(vm).SelectDetail(DockerPanelDetail.Stats);
        }),
        new("workspace-docker-logs", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDockerPanel(vm).SelectDetail(DockerPanelDetail.Logs);
        }),
        new("workspace-docker-log-search", vm =>
        {
            vm.ShowWorkspace();
            var panel = AddSampleDockerPanel(vm);
            panel.SelectDetail(DockerPanelDetail.Logs);
            panel.LogSearchText = "warn";
            panel.LogSearchContext = 2;
            panel.SearchLogsAsync().GetAwaiter().GetResult();
        }),
        new("workspace-docker-json", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDockerPanel(vm).SelectDetail(DockerPanelDetail.Json);
        }),
        new("workspace-docker-shell", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDockerPanel(vm).SelectDetail(DockerPanelDetail.Shell);
        }),
        new("workspace-docker-files", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDockerPanel(vm).SelectDetail(DockerPanelDetail.Files);
        }, PrepareCapture: SelectDockerReadmePreview),
        new("workspace-docker-images", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDockerPanel(vm).SelectSection(DockerPanelSection.Images);
        }),
        new("workspace-docker-volumes", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDockerPanel(vm).SelectSection(DockerPanelSection.Volumes);
        }),
        new("workspace-docker-networks", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDockerPanel(vm).SelectSection(DockerPanelSection.Networks);
        }),
        new("workspace-docker-narrow", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDockerPanel(vm).SelectSection(DockerPanelSection.Containers);
        }, Width: 1080),
        // The Git panel over a stub repository: working set, staged set, the
        // commit composer, and the structured diff with semantic line bands.
        new("workspace-git", vm =>
        {
            vm.ShowWorkspace();
            AddSampleGitPanel(vm);
        }),
        new("workspace-git-history", vm =>
        {
            vm.ShowWorkspace();
            // The stub client answers synchronously, so the history detail
            // and its diff are already loaded when the capture happens.
            var panel = AddSampleGitPanel(vm);
            panel.Section = GitPanelSection.AllCommits;
            panel.DetailTab = GitCommitDetailTab.Changes;
        }),
        new("workspace-git-commit-tab", vm =>
        {
            vm.ShowWorkspace();
            var panel = AddSampleGitPanel(vm);
            panel.Section = GitPanelSection.AllCommits;
            panel.DetailTab = GitCommitDetailTab.Commit;
        }),
        new("workspace-git-file-tree", vm =>
        {
            vm.ShowWorkspace();
            var panel = AddSampleGitPanel(vm);
            panel.Section = GitPanelSection.AllCommits;
            panel.DetailTab = GitCommitDetailTab.FileTree;
            panel.SelectedTreeNode = panel.CommitTreeRoots
                .FirstOrDefault(node => !node.IsDirectory);
        }),
        new("workspace-git-narrow", vm =>
        {
            vm.ShowWorkspace();
            AddSampleGitPanel(vm);
        }, Width: 1080),
        new("workspace-git-open", vm =>
        {
            vm.ShowWorkspace();
            AddSampleGitPanel(vm, repositoryPath: null);
        }),
        new(
            "git-repository-picker",
            vm =>
            {
                vm.ShowWorkspace();
                AddSampleGitPanel(vm, repositoryPath: null);
            },
            Dialog: vm =>
            {
                var panel = AddSampleGitPanel(vm, repositoryPath: null);
                return new GitRepositoryPickerDialog(panel.CreateRepositoryPicker());
            }),
        // The database viewer with a connected stub: table list, query editor,
        // and a populated result grid. Last workspace route, because the added
        // panel stays in the shared fixture.
        new("workspace-database", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDatabasePanel(vm);
        }),
        // The Redis panel's three perspectives, each against a connected stub:
        // key browser with a value selected, a search index with results, and a
        // subscription that has already received messages.
        new("workspace-redis", vm =>
        {
            vm.ShowWorkspace();
            AddSampleRedisPanel(vm, RedisWorkspacePerspective.Browser);
        }),
        // The create-key sheet is only reachable through its toolbar action, so
        // it needs a route of its own or it is never looked at again.
        new("workspace-redis-new-key", vm =>
        {
            vm.ShowWorkspace();
            AddSampleRedisPanel(vm, RedisWorkspacePerspective.Browser);
        }, PrepareCapture: OpenRedisNewKeySheet),
        // A document is edited as a document: the highlighted editor is only
        // reachable on a JSON key, so it gets a route of its own.
        new("workspace-redis-json", vm =>
        {
            vm.ShowWorkspace();
            AddSampleRedisPanel(vm, RedisWorkspacePerspective.Browser, selectedKeyType: "json");
        }),
        new("workspace-redis-search", vm =>
        {
            vm.ShowWorkspace();
            AddSampleRedisPanel(vm, RedisWorkspacePerspective.Search);
        }),
        new("workspace-redis-pubsub", vm =>
        {
            vm.ShowWorkspace();
            AddSampleRedisPanel(vm, RedisWorkspacePerspective.PubSub);
        }),
        new("settings-workspace-editor", vm =>
        {
            vm.ShowSettings(SettingsPage.Workspaces);
            vm.BeginEditWorkspace(new WorkspaceId("operations"));
        }, Height: 1200),
        // The isolated workspace: host mounts, the runtime image, and a
        // network policy of its own, which the plain editor never shows.
        new("settings-workspace-editor-isolated", vm =>
        {
            vm.ShowSettings(SettingsPage.Workspaces);
            vm.BeginEditWorkspace(new WorkspaceId("field"));
        }, Height: 1500),
        // The same editor scrolled to its end: the workspace's own network
        // policy and the connections it offers sit below the fold.
        new(
            "settings-workspace-editor-networking",
            vm =>
            {
                vm.ShowSettings(SettingsPage.Workspaces);
                vm.BeginEditWorkspace(new WorkspaceId("field"));
            },
            Height: 1500,
            PrepareCapture: ScrollWorkspaceEditorToEnd),
        // The two interactions a still capture cannot otherwise reach. Both
        // drive the real controls — the flyout is opened as a click opens it,
        // and the reorder is a genuine press-move-release on the drag handle —
        // so these captures are evidence that they work, not that they render.
        new(
            "settings-workspace-editor-add",
            vm =>
            {
                vm.ShowSettings(SettingsPage.Workspaces);
                vm.BeginEditWorkspace(new WorkspaceId("operations"));
            },
            Height: 1200,
            Dialog: OpenAddTabDialog),
        // The same dialog with the workspace-only screen chosen, because the
        // name and layout it then asks for are the part a static list cannot
        // show.
        new(
            "settings-workspace-editor-add-new-screen",
            vm =>
            {
                vm.ShowSettings(SettingsPage.Workspaces);
                vm.BeginEditWorkspace(new WorkspaceId("operations"));
            },
            Height: 1200,
            Dialog: viewModel =>
            {
                var dialog = OpenAddTabDialog(viewModel);
                dialog.FindControl<ListBox>("SourceList")!.SelectedIndex = 0;
                return dialog;
            }),
        new(
            "settings-workspace-editor-reorder",
            vm =>
            {
                vm.ShowSettings(SettingsPage.Workspaces);
                vm.BeginEditWorkspace(new WorkspaceId("operations"));
            },
            Height: 1200,
            PrepareCapture: DragLastTabToTop),
        new(
            "settings-workspace-editor-switch",
            vm =>
            {
                vm.ShowSettings(SettingsPage.Workspaces);
                vm.BeginEditWorkspace(new WorkspaceId("operations"));
                // Dirty on purpose: the rail has to carry an edit in progress
                // with it, or it is a list you cannot use.
                vm.WorkspaceEditor!.Name = "Operations (renamed)";
            },
            Height: 1200,
            PrepareCapture: SelectDataInTheRail),
        // Keyboard focus has its own visuals; capturing it keeps the focus ring
        // reviewable instead of only reachable by hand.
        new("settings-appearance-focused", vm => vm.ShowSettings(SettingsPage.Appearance), FocusFirst: "Leave settings"),
        // The whole settings-apply-immediately loop, end to end: the click
        // commits the theme, the catalog change re-publishes the resources, and
        // the capture must visibly densify against plain settings-appearance.
        new(
            "settings-appearance-density-compact",
            vm => vm.ShowSettings(SettingsPage.Appearance),
            ClickFirst: "Compact density"),
        // Long settings pages are also captured whole, so a section below the
        // fold is reviewable without scrolling by hand.
        new("settings-appearance-full", vm => vm.ShowSettings(SettingsPage.Appearance), Height: 2100),
        new("settings-terminal-full", vm => vm.ShowSettings(SettingsPage.Terminal), Height: 1500),
        // The corner and density settings are only worth having if they visibly
        // reshape the interface. Capturing both extremes makes a regression that
        // silently disconnects them show up as two identical images.
        new(
            "appearance-corners-tight",
            vm => vm.ShowWorkspace(),
            Theme: AppearanceExtreme(InterfaceDensity.Compact)),
        new(
            "appearance-corners-round",
            vm => vm.ShowWorkspace(),
            Theme: AppearanceExtreme(InterfaceDensity.Comfortable)),
        // The side-docked strip is its own box model — margins, padding, row
        // metrics all orientation-owned — and it shipped broken twice because
        // nothing rendered it headlessly.
        new(
            "workspace-tabs-side",
            vm => vm.ShowWorkspace(),
            Theme: new ThemePreference(
                ThemePreference.Default.Id,
                ThemePreference.Default.Name,
                AppearanceMode.Dark,
                PlatformProfile.Automatic,
                AccentPreference.FollowHost,
                tabStripPlacement: TabStripPlacement.Left)),
        // The component gallery at every accessibility-relevant appearance
        // variant. These routes use the product appearance mapper rather than
        // maintaining a harness-only palette or font scale.
        new(
            "design-system-high-contrast",
            vm => vm.ShowWorkspace(),
            Dialog: static _ => new DesignSystemGalleryWindow(),
            HighContrast: true),
        new(
            "design-system-scale-200",
            vm => vm.ShowWorkspace(),
            Theme: AppearanceScale(2),
            Dialog: static _ => new DesignSystemGalleryWindow()),
        new(
            "design-system-scale-250",
            vm => vm.ShowWorkspace(),
            Theme: AppearanceScale(2.5),
            Dialog: static _ => new DesignSystemGalleryWindow()),
    ];

    /// <summary>
    /// A theme that differs from the default only in corner radius and density,
    /// so a comparison between the two captures isolates those two settings.
    /// </summary>
    private static ThemePreference AppearanceExtreme(
        InterfaceDensity density) =>
        new(
            ThemePreference.Default.Id,
            ThemePreference.Default.Name,
            AppearanceMode.Dark,
            PlatformProfile.Automatic,
            AccentPreference.FollowHost,
            density: density);

    private static ThemePreference AppearanceScale(double scale) =>
        new(
            ThemePreference.Default.Id,
            ThemePreference.Default.Name,
            AppearanceMode.Dark,
            PlatformProfile.Automatic,
            AccentPreference.FollowHost,
            textScaleOverride: scale);

    private static void ShowSampleDragGhost(MainWindow window)
    {
        var payloadType = typeof(MainWindow).Assembly.GetType(
            "GhostShell.App.Views.Components.DragGhostPayload",
            throwOnError: true)!;
        var constructor = AssertSingle(payloadType.GetConstructors());
        var symbolType = constructor.GetParameters()[0].ParameterType;
        var payload = constructor.Invoke(
        [
            Enum.Parse(symbolType, "Document"),
            "Archive.zip",
            "Copy from dev.terion.pro",
        ]);
        var show = typeof(MainWindow).GetMethod(
            "ShowDragGhost",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The main window no longer exposes its internal drag-ghost presentation seam.");
        _ = show.Invoke(window, [payload, new Point(760, 420)]);
    }

    private static void ShowReasoningStart(MainWindow window)
    {
        var transcript = window.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .SingleOrDefault(candidate => string.Equals(candidate.Name, "AgentChatTranscript", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The reasoning capture requires the agent transcript.");
        transcript.Offset = new Vector(0, 0);
    }

    /// <summary>
    /// The dialog the tab list opens, built from the same options the editor
    /// hands it.
    /// </summary>
    private static void ScrollWorkspaceEditorToEnd(MainWindow window)
    {
        var editor = window.GetVisualDescendants()
            .OfType<WorkspaceEditorView>()
            .First();
        // The detail column's own scroller, by name: the rail's list scrolls
        // too, and it comes first in the tree.
        editor.FindControl<ScrollViewer>("DetailScroll")!.ScrollToEnd();
    }

    private static Window OpenNetworkConnectionEditor(MainWindowViewModel viewModel)
    {
        var settings = viewModel.NetworkSettings;
        var proxy = settings.Profiles.Single(profile =>
            string.Equals(profile.Name, "Corp proxy", StringComparison.Ordinal));
        // The draft is created before the vault is consulted, so the dialog
        // has an editor to show as soon as this returns.
        _ = settings.BeginEditProfileAsync(proxy, CancellationToken.None).AsTask();
        return new NetworkConnectionEditorDialog(settings);
    }

    private static Window OpenAddTabDialog(MainWindowViewModel viewModel)
    {
        var editor = viewModel.WorkspaceEditor
            ?? throw new InvalidOperationException(
                "The route wanted the workspace editor's add-tab dialog with no editor open.");
        return new AddWorkspaceTabDialog(
            editor.ConnectionOptions,
            editor.ScreenOptions,
            editor.LayoutOptions);
    }

    /// <summary>
    /// Drags the last tab row to the top with real pointer events, so the
    /// capture shows the result of the reorder rather than a list that merely
    /// draws a handle.
    /// </summary>
    private static void DragLastTabToTop(MainWindow window)
    {
        var handles = window.GetVisualDescendants()
            .OfType<Control>()
            .Where(control => string.Equals(
                AutomationProperties.GetName(control),
                "Reorder this tab",
                StringComparison.Ordinal))
            .ToArray();
        if (handles.Length < 2)
        {
            throw new InvalidOperationException(
                $"The workspace editor offered {handles.Length} drag handles; the reorder "
                + "capture needs at least two rows to move between.");
        }

        var from = CenterInWindow(handles[^1], window);
        var to = CenterInWindow(handles[0], window);
        window.MouseDown(from, MouseButton.Left);
        // Two moves: the first crosses the intervening rows, the second lands on
        // the target. A single jump would still work, but a drag that only ever
        // arrives is not the gesture a hand makes.
        window.MouseMove(new Point(from.X, (from.Y + to.Y) / 2));
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Switches the editor to another workspace through the rail's own selection
    /// path, so the capture shows what happens to the edit that was in progress.
    /// </summary>
    private static void SelectDataInTheRail(MainWindow window)
    {
        var rail = window.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(list => string.Equals(list.Name, "PeerList", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The workspace editor has no rail.");
        rail.SelectedItem = rail.Items
            .OfType<WorkspaceRailItemViewModel>()
            .FirstOrDefault(peer => peer.Id == new WorkspaceId("data"))
            ?? throw new InvalidOperationException("The rail does not list the Data workspace.");
        Dispatcher.UIThread.RunJobs();
    }

    private static Point CenterInWindow(Control control, MainWindow window) =>
        control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2),
            window)
        ?? throw new InvalidOperationException(
            "A control in the workspace editor is not positioned relative to the window.");

    private static T NamedControl<T>(MainWindow window, string automationName)
        where T : Control =>
        window.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(candidate => string.Equals(
                AutomationProperties.GetName(candidate),
                automationName,
                StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"The route wanted '{automationName}', which is not in the tree.");

    /// <summary>
    /// Marks a tab other than the one in front, which is the case the marks
    /// exist for — a notification you can see the result of is not one you
    /// needed telling about.
    /// </summary>
    private static void MarkBackgroundTabForAttention(MainWindowViewModel viewModel)
    {
        var workspace = viewModel.RuntimeWorkspace
            ?? throw new InvalidOperationException(
                "The attention route needs a runtime workspace.");
        // Two marks, both real: the panel on screen was marked while the window
        // was in the background (a case the notification centre has a test
        // for), and a tab behind it is holding one of its own. One capture then
        // shows the panel header, the tab strip, and the rail together.
        var visiblePanel = workspace.Tabs
            .SelectMany(candidate => candidate.Panels)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The attention route needs a panel to mark.");
        visiblePanel.HasAttention = true;
        foreach (var candidate in workspace.Tabs)
        {
            candidate.HasAttention = candidate.Panels.Contains(visiblePanel)
                || !candidate.IsActive;
        }

        workspace.HasAttention = true;
        foreach (var item in viewModel.Workspaces)
        {
            item.HasAttention = true;
            item.IsOpen = true;
            item.IsInFront = item == viewModel.Workspaces[0];
        }
    }

    /// <summary>
    /// Opens the close action on the first rail tile that offers one.
    /// </summary>
    private static void ShowRailTileCloseAction(MainWindow window)
    {
        var tile = window.GetVisualDescendants()
            .OfType<GhostShell.App.Controls.WorkspaceRailTile>()
            .FirstOrDefault(candidate => candidate.CanClose)
            ?? throw new InvalidOperationException(
                "No rail tile offers a close action, so there is nothing to capture.");
        ForcePointerOver(tile);
        // And the close itself, because a pointer resting on it is where the
        // tile has to still look like one block rather than two.
        window.ApplyTemplate();
        tile.ApplyTemplate();
        if (tile.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => string.Equals(button.Name, "PART_Close", StringComparison.Ordinal)) is { } close)
        {
            ForcePointerOver(close);
        }
    }

    private static async Task ResetWebsiteRouteStateAsync(
        MainWindowViewModel viewModel,
        MainWindow window)
    {
        window.HideDragGhost();
        viewModel.IsAgentPanelVisible = false;
        if (viewModel.IsAgentPanelDocked)
        {
            await viewModel.ToggleAgentPanelPinAsync(CancellationToken.None);
            viewModel.IsAgentPanelVisible = false;
        }

        if (window.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(control => string.Equals(
                    control.Name,
                    "FileTransferManager",
                    StringComparison.Ordinal)) is { } transferManager)
        {
            transferManager.IsVisible = false;
        }

        foreach (var element in window.GetVisualDescendants().OfType<StyledElement>())
        {
            var pseudoClasses = typeof(StyledElement).GetProperty(
                    "PseudoClasses",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(element) as IPseudoClasses;
            pseudoClasses?.Remove(":pointerover");
        }

        if (viewModel.RuntimeWorkspace is { } workspace)
        {
            foreach (var sampleTab in workspace.Tabs
                         .Where(tab => tab.Id.Value is
                             "qa-tab-browser" or "qa-tab-database" or "qa-tab-docker" or "qa-tab-file-viewer"
                             or "qa-tab-git" or "qa-tab-process-monitor" or "qa-tab-statistics"
                             || tab.Id.Value.StartsWith("qa-tab-redis-", StringComparison.Ordinal))
                         .ToArray())
            {
                sampleTab.DisposePanels();
                workspace.Tabs.Remove(sampleTab);
            }

            if (workspace.Tabs.FirstOrDefault() is { } activeTab)
            {
                foreach (var tab in workspace.Tabs)
                {
                    tab.IsActive = ReferenceEquals(tab, activeTab);
                    tab.HasAttention = false;
                    foreach (var panel in tab.Panels)
                    {
                        panel.HasAttention = false;
                    }
                }

                typeof(RuntimeWorkspaceViewModel)
                    .GetProperty(nameof(RuntimeWorkspaceViewModel.ActiveTab))!
                    .GetSetMethod(nonPublic: true)!
                    .Invoke(workspace, [activeTab]);
            }

            workspace.HasAttention = false;
        }

        for (var index = 0; index < viewModel.Workspaces.Count; index++)
        {
            var item = viewModel.Workspaces[index];
            item.HasAttention = false;
            item.IsOpen = index == 0;
            item.IsInFront = index == 0;
        }
    }

    private static void ClearWorkspaceAgentActivity(MainWindowViewModel viewModel)
    {
        if (viewModel.RuntimeWorkspace is { } workspace)
        {
            foreach (var tab in workspace.Tabs)
            {
                foreach (var panel in tab.Panels)
                {
                    panel.SetAgentActivity(null);
                }

                tab.SetAgentActivity(null);
            }

            workspace.HasAgentActivity = false;
        }

        foreach (var workspaceItem in viewModel.Workspaces)
        {
            workspaceItem.HasAgentActivity = false;
        }
    }

    private static void MarkTerminalPanelForAgentActivity(MainWindowViewModel viewModel)
    {
        if (viewModel.RuntimeWorkspace is not { } workspace
            || workspace.ActiveTab is not { } tab
            || tab.Panels.FirstOrDefault(panel => panel.Kind == PanelKind.Terminal) is not { } terminal)
        {
            throw new InvalidOperationException(
                "The agent terminal capture needs an active terminal panel.");
        }

        terminal.SetAgentActivity("AI agent working in this panel");
        tab.SetAgentActivity(terminal.AgentActivity);
        workspace.HasAgentActivity = true;
        if (viewModel.Workspaces.FirstOrDefault(item => item.IsInFront) is { } workspaceItem)
        {
            workspaceItem.HasAgentActivity = true;
        }
    }

    private static void ForcePointerOver(StyledElement element)
    {
        var pseudoClasses = typeof(StyledElement).GetProperty(
                "PseudoClasses",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(element) as IPseudoClasses
            ?? throw new InvalidOperationException(
                "Avalonia no longer exposes the protected pseudo-class collection used by QA.");

        pseudoClasses.Add(":pointerover");
    }

    private static T AssertSingle<T>(IReadOnlyList<T> values) =>
        values.Count == 1
            ? values[0]
            : throw new InvalidOperationException(
                $"Expected one value but found {values.Count}.");

    /// <summary>
    /// The rail's own tile in each of its resting states: idle, running in the
    /// background, in front, and asking to be noticed.
    ///
    /// The real component rather than a replica of it. A hand-built copy could
    /// only ever show what the copy does — and the states are the whole point,
    /// because the rail's job is to be readable at a glance.
    ///
    /// The opened state is deliberately not here. It overflows its own tile,
    /// and a bare window sized to the resting tiles renders it as an empty
    /// block; `workspace-rail-close` captures it inside the real rail, which is
    /// the only place its overflow means anything anyway.
    /// </summary>
    private static Window CreateRailTileProbe()
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(8),
            Spacing = 8,
        };
        foreach (var (accent, symbol, running, current, attention, closable, hovered) in
                 new (string, FluentIcons.Common.Symbol, bool, bool, bool, bool, bool)[]
                 {
                     ("#C77828", FluentIcons.Common.Symbol.Window, false, false, false, false, false),
                     ("#3FB950", FluentIcons.Common.Symbol.Code, true, false, false, true, false),
                     ("#4A90D9", FluentIcons.Common.Symbol.Database, true, true, false, true, false),
                     ("#C4322B", FluentIcons.Common.Symbol.Rocket, true, false, true, true, false),
                 })
        {
            var tile = new GhostShell.App.Controls.WorkspaceRailTile
            {
                Accent = accent,
                Icon = symbol,
                IsRunning = running,
                IsCurrent = current,
                HasAttention = attention,
                CanClose = closable,
            };
            if (hovered)
            {
                ForcePointerOver(tile);
            }

            stack.Children.Add(tile);
        }

        return new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            Content = stack,
        };
    }

    /// <summary>
    /// The unified editor with all three families available, matching the shell's
    /// launcher composition. An existing terminal connection pins the editor to
    /// the terminal family, like editing from the launcher does.
    /// </summary>
    private static UnifiedConnectionEditorViewModel CreateQaConnectionEditor(
        bool existingTerminal = false,
        SavedConnectionFamily initialFamily = SavedConnectionFamily.Terminal)
    {
        var terminal = existingTerminal
            ? new ConnectionEditorViewModel(
                new UnusedConnectionRuntime(),
                QaData.Connections[0].Value,
                QaData.Connections[0].Revision)
            : new ConnectionEditorViewModel(new UnusedConnectionRuntime());
        var connections = QaData.Connections.Select(item => item.Value).ToArray();
        return new UnifiedConnectionEditorViewModel(
            terminal,
            new FileProviderProfileEditorViewModel(
                new QaFileProviderRuntime(),
                connections,
                []),
            new DatabaseConnectionEditorViewModel(
                new QaDatabasePanelClient(),
                connections),
            lockedFamily: existingTerminal ? SavedConnectionFamily.Terminal : null,
            initialFamily: initialFamily);
    }

    /// <summary>
    /// The unified editor on the Git · SSH type: SSH endpoint fields plus the
    /// repository path that the saved connection opens into.
    /// </summary>
    private static UnifiedConnectionEditorViewModel CreateQaGitConnectionEditor()
    {
        var terminal = new ConnectionEditorViewModel(
            new UnusedConnectionRuntime(),
            gitClient: new QaGitRepositoryClient(),
            savedConnections: [.. QaData.Connections.Select(item => item.Value)]);
        var editor = new UnifiedConnectionEditorViewModel(
            terminal,
            files: null,
            database: null,
            lockedFamily: SavedConnectionFamily.Terminal);
        editor.SelectedType = editor.TypeOptions.Single(option =>
            string.Equals(option.DisplayName, "Git · SSH", StringComparison.Ordinal));
        editor.Name = "GhostSHELL repo";
        editor.Terminal.Host = "bastion.example";
        editor.Terminal.Username = "ops";
        editor.Terminal.RepositoryPath = "/srv/ghostshell";
        return editor;
    }

    /// <summary>
    /// Modal editors and confirmations are their own windows, so they are
    /// captured directly rather than through a shell route.
    /// </summary>
    private static readonly (string Name, Func<Window> Create, ThemePreference? Theme)[] Dialogs =
    [
        // The workspaces-rail tiles at Retina density: every icon the rail can
        // draw, so glyph centering is measurable at the scale users run at.
        ("rail-tiles-2x", CreateRailTileProbe, null),
        // The lock screen as a person meets it: the veil, the PIN box, and a
        // refusal message, over a stub gate that is locked and metering.
        ("shell-locked", () => new Window
        {
            Width = 900,
            Height = 620,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new GhostShell.App.Views.Components.ShellLockView
            {
                DataContext = new GhostShell.App.ViewModels.ApplicationSecurityEditorViewModel(
                    encryption: null,
                    protection: new QaLockedProtection(),
                    biometrics: new QaBiometrics()),
            },
        }, null),
        // The statistics panel at a split-panel width, so the stat-card wrap is
        // reviewable at the size that used to clip every value.
        ("stats-narrow", () => new Window
        {
            Width = 330,
            Height = 940,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new GhostShell.App.Views.RuntimePanels.StatisticsRuntimePanelView
            {
                DataContext = new QaStatisticsPreview(),
            },
        }, null),
        // Space must reach the panel even though a ListBox treats it as a
        // selection key. The route raises a real tunneled key event through a
        // real control tree and captures what the panel did with it.
        ("preview-space-shortcut", CreateSpaceShortcutProbe, null),
        // A real PDF rendered by PDFium and drawn by the panel, with the page
        // indicator the preview shows.
        ("pdf-preview", CreatePdfProbeWindow, null),
        // Markdown rendered as native controls, so headings, emphasis, lists,
        // quotes, tables, and a highlighted fence are reviewable together.
        ("markdown-preview", () => new Window
        {
            Width = 560,
            Height = 720,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Child = new GhostShell.App.Views.Components.MarkdownPreviewView
                {
                    Text = QaData.SampleMarkdown,
                },
            },
        }, null),
        // A TIFF decoded through the image decoder and drawn by the panel, so
        // the format the drawing stack cannot open is proven end to end.
        // A large JPEG read from disk exactly as the panel reads one: scaled
        // down while decoding, never from the bounded preview head.
        ("image-preview-photo", () =>
        {
            using var file = File.OpenRead(Program.JpegProbePath);
            var bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(file, 2400);
            return new Window
            {
                Width = 420,
                Height = 320,
                CanResize = false,
                ShowInTaskbar = false,
                Content = new Border
                {
                    Classes = { "FloatingSidebar" },
                    Child = new Image
                    {
                        Margin = new Thickness(12),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Stretch = Stretch.Uniform,
                        StretchDirection = StretchDirection.DownOnly,
                        Source = bitmap,
                    },
                },
            };
        }, null),
        // The picture viewer as the panel shows it: fitted on open, with the
        // zoom and rotation controls to hand.
        ("image-preview-zoom", () => CreateZoomableImageProbe(_ => { }), null),
        // And after three zoom steps and a quarter turn clockwise, so the
        // transform is proven by what it draws rather than by its arithmetic.
        ("image-preview-zoomed", () => CreateZoomableImageProbe(view =>
        {
            view.RotateRight();
            view.ZoomIn();
            view.ZoomIn();
            view.ZoomIn();
        }), null),
        ("image-preview-fit", () =>
        {
            var decoder = new GhostShell.Previews.MagickImagePreviewDecoder();
            var decoded = decoder
                .DecodeAsync(GhostShell.Application.FilePreviewContent.FromLocalFile(Program.TiffProbePath), 8_000_000, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult()
                ?? throw new InvalidOperationException("The TIFF probe did not decode.");
            using var stream = new MemoryStream(decoded.PngBytes.ToArray(), writable: false);
            // A zone narrower than the image: the picture must fit inside it
            // rather than overflow and demand scrolling.
            return new Window
            {
                Width = 220,
                Height = 260,
                CanResize = false,
                ShowInTaskbar = false,
                Content = new Border
                {
                    Classes = { "FloatingSidebar" },
                    Child = new Image
                    {
                        Margin = new Thickness(12),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Stretch = Stretch.Uniform,
                        StretchDirection = StretchDirection.DownOnly,
                        Source = new Avalonia.Media.Imaging.Bitmap(stream),
                    },
                },
            };
        }, null),
        ("image-preview-tiff", () =>
        {
            var decoder = new GhostShell.Previews.MagickImagePreviewDecoder();
            var decoded = decoder
                .DecodeAsync(GhostShell.Application.FilePreviewContent.FromLocalFile(Program.TiffProbePath), 8_000_000, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult()
                ?? throw new InvalidOperationException("The TIFF probe did not decode.");
            using var stream = new MemoryStream(decoded.PngBytes.ToArray(), writable: false);
            return new Window
            {
                Width = 420,
                Height = 320,
                CanResize = false,
                ShowInTaskbar = false,
                Content = new Border
                {
                    Classes = { "FloatingSidebar" },
                    Padding = new Thickness(12),
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"{decoded.FormatName} · {decoded.Width}×{decoded.Height}",
                            },
                            new Image
                            {
                                Source = new Avalonia.Media.Imaging.Bitmap(stream),
                                Stretch = Stretch.Uniform,
                                Height = 220,
                            },
                        },
                    },
                },
            };
        }, null),
        // The database viewer opened on a real SQLite file through the shared
        // workspace component — the same one the file preview embeds.
        ("database-preview", () =>
        {
            var viewer = new DatabaseRuntimePanelViewModel(
                PanelInstanceId.New(),
                "probe.db",
                new GhostShell.Databases.DatabasePanelClient(),
                "sqlite",
                Program.SqliteProbePath);
            _ = viewer.ConnectAsync();
            return new Window
            {
                Width = 820,
                Height = 460,
                CanResize = false,
                ShowInTaskbar = false,
                Content = new Border
                {
                    Classes = { "FloatingSidebar" },
                    Child = new GhostShell.App.Views.Components.DatabaseWorkspaceView
                    {
                        DataContext = viewer,
                    },
                },
            };
        }, null),
        // And narrow, where the objects list folds into a picker beside the
        // statement editor rather than squeezing the results.
        ("database-preview-narrow", () =>
        {
            var viewer = new DatabaseRuntimePanelViewModel(
                PanelInstanceId.New(),
                "probe.db",
                new GhostShell.Databases.DatabasePanelClient(),
                "sqlite",
                Program.SqliteProbePath);
            _ = viewer.ConnectAsync();
            return new Window
            {
                Width = 340,
                Height = 460,
                CanResize = false,
                ShowInTaskbar = false,
                Content = new Border
                {
                    Classes = { "FloatingSidebar" },
                    Child = new GhostShell.App.Views.Components.DatabaseWorkspaceView
                    {
                        DataContext = viewer,
                    },
                },
            };
        }, null),
        // The real database grid context menu, opened over a real SQLite value
        // and rendered at Retina density so its item rhythm and nested-menu
        // affordances can be compared to the supplied reference without a
        // desktop screenshot permission.
        ("database-context-menu-2x", CreateDatabaseContextMenuProbe, null),
        // The same real database grid in the three states fixed by the latest
        // interaction pass: a compact Quick Look, an explicit descending
        // server sort, and a context menu opened after running an edited raw
        // query whose complete base-table provenance keeps mutations safe.
        ("database-quick-look-compact-2x", CreateDatabaseQuickLookCompactProbe, null),
        // Editing a prose-sized cell grows the expanded editor beside it; the
        // probe proves the popup opens on the real edit path and holds focus.
        ("database-cell-expand-editor-2x", CreateDatabaseCellExpandProbe, null),
        // A row-inspector field mid-edit: draft box open, apply/revert where
        // the type label sits, the neighbours still read-only.
        ("database-inspector-edit-2x", CreateDatabaseInspectorEditProbe, null),
        ("database-sort-descending-2x", CreateDatabaseSortDescendingProbe, null),
        ("database-raw-query-context-menu-2x", CreateDatabaseRawQueryContextMenuProbe, null),
        ("database-copy-insert-2x", CreateDatabaseCopyInsertProbe, null),
        ("database-pagination-count-2x", CreateDatabasePaginationCountProbe, null),
        // The file preview's syntax highlighting over a C# sample, so token
        // colouring is reviewable rather than assumed from the grammar name.
        // A source file in a narrow panel: long lines wrap rather than run off
        // the side of a preview nobody can scroll sideways.
        // The panel resized after its first layout, which is what the file
        // panel does as the splitter and window settle. A fence measured once
        // keeps the height of a width it no longer has.
        ("markdown-preview-resized", () =>
        {
            var view = new GhostShell.App.Views.Components.MarkdownPreviewView
            {
                Text = QaData.MarkdownWithLongFence,
            };
            var host = new Border
            {
                Classes = { "FloatingSidebar" },
                Width = 700,
                Child = view,
            };
            var narrowed = false;
            view.LayoutUpdated += (_, _) =>
            {
                if (narrowed || view.Bounds.Width < 1)
                {
                    return;
                }

                narrowed = true;
                host.Width = 430;
            };
            return new Window
            {
                Width = 720,
                Height = 700,
                CanResize = false,
                ShowInTaskbar = false,
                Content = host,
            };
        }, null),
        // The real file panel over a real local provider: what a reader sees,
        // including the switches the claiming previewer offers.
        // A format the shell cannot show: named and given a symbol, with the
        // bytes one switch away rather than dumped unasked.
        ("file-preview-binary", () => CreateFilePanelProbe("libghost.dylib"), null),
        ("file-preview-binary-hex", () => CreateFilePanelProbe("libghost.dylib", "hex"), null),
        ("file-preview-markdown", () => CreateFilePanelProbe("notes.md"), null),
        // The same file read as its source: the two readings are the identical
        // string, which is exactly why switching between them once failed.
        ("file-preview-markdown-raw", () => CreateFilePanelProbe("notes.md", "raw"), null),
        ("file-preview-csv", () => CreateFilePanelProbe("deployments.csv"), null),
        ("file-preview-archive", () => CreateFilePanelProbe("release.zip"), null),
        ("file-preview-json", () => CreateFilePanelProbe("settings.json"), null),
        ("file-toolbar-narrow", () => CreateFilePanelProbe("notes.md", width: 560), null),
        // A right-click on a file, and one on the space below the last of them.
        // The two menus are different lists — what can be done to what is
        // picked out, and what can be done to the folder holding it — and both
        // are drawn from what the connection says it can do.
        // The two shapes the permissions dialog takes. Neither is a mock of the
        // other: a filesystem has nine bits and no named accounts, an object
        // store has named accounts and no group.
        ("file-permissions-posix", () => new FileAccessControlDialog(
            new FileAccessControlEditorViewModel(
                "deploy.sh",
                "staging-web",
                new FilePanelAccessControl(
                    QaFileLocation("deploy.sh"),
                    new FilePanelPosixMode(0b111_101_101)),
                canEdit: true)), null),
        ("file-permissions-acl", () => new FileAccessControlDialog(
            new FileAccessControlEditorViewModel(
                "2020-12-18T212401+0000_start.png",
                "artifacts-bucket",
                new FilePanelAccessControl(
                    QaFileLocation("2020-12-18T212401+0000_start.png"),
                    grants:
                    [
                        new FilePanelAccessGrant(
                            new FilePanelGrantee(FilePanelGranteeKind.Everyone),
                            FilePanelAccessRight.Read),
                        new FilePanelAccessGrant(
                            new FilePanelGrantee(
                                FilePanelGranteeKind.Owner,
                                "8a1f…c0d4",
                                "Owner"),
                            FilePanelAccessRight.FullControl),
                        new FilePanelAccessGrant(
                            new FilePanelGrantee(FilePanelGranteeKind.User, "p3179430"),
                            FilePanelAccessRight.FullControl),
                    ]),
                canEdit: true)), null),
        ("file-context-menu-entry", () => CreateFileContextMenuProbe(onEntry: true), null),
        ("file-context-menu-folder", () => CreateFileContextMenuProbe(onEntry: false), null),
        // The overflow menu, which is the whole action list. Narrow, because
        // that is where it stops being a convenience and becomes the only way
        // to reach any of it.
        ("file-actions-menu", () =>
        {
            var window = CreateFilePanelProbe("notes.md", width: 560);
            window.Opened += (_, _) =>
            {
                var button = window.GetVisualDescendants()
                    .OfType<Button>()
                    .First(candidate => string.Equals(
                        AutomationProperties.GetName(candidate),
                        "Open more file actions",
                        StringComparison.Ordinal));
                button.Flyout?.ShowAt(button);
            };
            return window;
        }, null),
        // A closed preview, and then the view built again over the same panel:
        // floating it, adding one beside it, any change to the arrangement.
        // The listing has to have taken the whole width back. A gap on the
        // right is the column the closing zeroed and the rebuilt view read
        // from the markup again.
        ("file-preview-closed-relayout", () =>
        {
            var window = CreateFilePanelProbe("notes.md");
            var view = (GhostShell.App.Views.RuntimePanels.FileRuntimePanelView)
                window.Content!;
            var panel = (FileRuntimePanelViewModel)view.DataContext!;
            panel.IsPreviewVisible = false;
            window.Content = new GhostShell.App.Views.RuntimePanels.FileRuntimePanelView
            {
                DataContext = panel,
            };
            return window;
        }, null),
        // Responsiveness, measured the way a person feels it: a beat posted at
        // input priority over and over, and the longest gap between two beats
        // while a preview lands. That gap is how long the panel could not have
        // answered a click.
        ("preview-responsiveness", () =>
        {
            var window = CreateFilePanelProbe("libghost.dylib");
            var panel = (FileRuntimePanelViewModel)
                ((GhostShell.App.Views.RuntimePanels.FileRuntimePanelView)window.Content!)
                .DataContext!;
            var measured = false;
            window.Opened += (_, _) =>
            {
                if (measured)
                {
                    return;
                }

                measured = true;
                Settle(panel.PreviewSelectedAsync());

                foreach (var (label, act) in new (string, Action)[]
                         {
                             ("show-bytes", () => panel.PreviewToggles.Single().IsOn = true),
                             ("hide-bytes", () => panel.PreviewToggles.Single().IsOn = false),
                         })
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var last = 0L;
                    var longest = 0L;
                    var beating = true;
                    void Beat()
                    {
                        var now = watch.ElapsedMilliseconds;
                        longest = Math.Max(longest, now - last);
                        last = now;
                        // Bounded: a beat that reposts itself for ever is work
                        // the pump can never drain.
                        if (beating && now < 4_000)
                        {
                            Dispatcher.UIThread.Post(Beat, DispatcherPriority.Input);
                        }
                    }

                    Dispatcher.UIThread.Post(Beat, DispatcherPriority.Input);
                    act();
                    Settle(panel.PreviewPresentation);
                    beating = false;
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Console.WriteLine($"TIMING {label} longest-unanswered={longest}ms");
                }
            };
            return window;
        }, null),
        // The same dump both ways, drawn for real: a text view has to measure
        // every line before it can show the first, a list only what is on
        // screen. This is the difference a headless timing never showed.
        ("preview-timing-hex-shapes", () =>
        {
            var size = int.TryParse(Environment.GetEnvironmentVariable("QA_HEX_BYTES"),
System.Globalization.CultureInfo.InvariantCulture, out var requested) ? requested
                : 64 * 1024;
            var bytes = Enumerable.Range(0, size)
                .Select(value => (byte)(value % 256))
                .ToArray();
            var window = new Window { Width = 700,
                Height = 500,
                CanResize = false,
                ShowInTaskbar = false,
            // The window is up and warm before either is timed, so the numbers
            // are the cost of showing the dump and nothing else.
            Content = new Border() };
            window.Show();
            window.UpdateLayout();
            using (var warm = new RenderTargetBitmap(new PixelSize(700, 500), new Vector(96, 96)))
            {
                warm.Render(window);
            }

            var text = GhostShell.Application.Previews.PreviewText.Hex(bytes, false);
            _ = text.Length;
            var rows = GhostShell.Application.Previews.PreviewText.HexRows(bytes, false);

            var editorWatch = System.Diagnostics.Stopwatch.StartNew();
            var editor = new GhostShell.App.Views.Components.CodePreviewView
            {
                FileName = "payload.bin",
                WordWrap = false,
                Text = text,
            };
            window.Content = editor;
            window.UpdateLayout();
            using (var bitmap = new RenderTargetBitmap(new PixelSize(700, 500), new Vector(96, 96)))
            {
                bitmap.Render(window);
            }

            Console.WriteLine(
                $"TIMING hex-in-text-view {editorWatch.ElapsedMilliseconds}ms "
                + $"({rows.Rows.Count} rows)");

            var listWatch = System.Diagnostics.Stopwatch.StartNew();
            var list = new GhostShell.App.Views.Components.HexPreviewView
            {
                DataContext = rows,
            };
            window.Content = list;
            window.UpdateLayout();
            using (var bitmap = new RenderTargetBitmap(new PixelSize(700, 500), new Vector(96, 96)))
            {
                bitmap.Render(window);
            }

            Console.WriteLine(
                $"TIMING hex-in-list {listWatch.ElapsedMilliseconds}ms "
                + $"({rows.Rows.Count} rows)");
            return window;
        }, null),
        // The hex reading of a real-sized binary, timed the same way.
        ("preview-timing-hex", () =>
        {
            var window = CreateFilePanelProbe("libghost.dylib");
            var panel = (FileRuntimePanelViewModel)
                ((GhostShell.App.Views.RuntimePanels.FileRuntimePanelView)window.Content!)
                .DataContext!;
            var timed = false;
            window.Opened += (_, _) =>
            {
                if (timed)
                {
                    return;
                }

                timed = true;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var preview = panel.PreviewSelectedAsync();
                while (!preview.IsCompleted && watch.ElapsedMilliseconds < 10_000)
                {
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
                    Thread.Sleep(1);
                }

                window.UpdateLayout();
                Console.WriteLine($"TIMING select-binary blocking={watch.ElapsedMilliseconds}ms");
                Dispatcher.UIThread.RunJobs();

                for (var round = 0; round < 3; round++)
                {
                    watch.Restart();
                    panel.PreviewToggles.Single().IsOn = true;
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
                    window.UpdateLayout();
                    var blockingOn = watch.ElapsedMilliseconds;
                    watch.Restart();
                    Settle(panel.PreviewPresentation);
                    var prepared = watch.ElapsedMilliseconds;
                    // The part that lands on the UI thread whatever we do off
                    // it: handing the text to the view and laying it out.
                    watch.Restart();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    using (var bitmap = new RenderTargetBitmap(
                               new PixelSize(900, 620),
                               new Vector(96, 96)))
                    {
                        bitmap.Render(window);
                    }

                    Console.WriteLine(
                        $"TIMING toggle-to-hex switch={blockingOn}ms "
                        + $"prepared={prepared}ms applied-on-ui={watch.ElapsedMilliseconds}ms");

                    watch.Restart();
                    panel.PreviewToggles.Single().IsOn = false;
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
                    window.UpdateLayout();
                    Console.WriteLine($"TIMING toggle-from-hex blocking={watch.ElapsedMilliseconds}ms");
                    Dispatcher.UIThread.RunJobs();
                }
            };
            return window;
        }, null),
        // Not a picture: a stopwatch. Renders the preview path the way the panel
        // does and prints where the time goes.
        ("preview-timing", () =>
        {
            var text = File.ReadAllText(
                Path.Combine(Program.OutputDirectory, "preview-samples", "notes.md"));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var options = new TextMateSharp.Grammars.RegistryOptions(
                TextMateSharp.Grammars.ThemeName.DarkPlus);
            Console.WriteLine($"TIMING registry-options {watch.ElapsedMilliseconds}ms");

            watch.Restart();
            var second = new TextMateSharp.Grammars.RegistryOptions(
                TextMateSharp.Grammars.ThemeName.DarkPlus);
            Console.WriteLine($"TIMING registry-options-again {watch.ElapsedMilliseconds}ms");
            _ = second;

            watch.Restart();
            var editor = new AvaloniaEdit.TextEditor();
            var installation = AvaloniaEdit.TextMate.TextMate.InstallTextMate(editor, options);
            Console.WriteLine($"TIMING install-textmate {watch.ElapsedMilliseconds}ms");

            watch.Restart();
            installation.SetGrammar(options.GetScopeByLanguageId(
                options.GetLanguageByExtension(".md")!.Id));
            Console.WriteLine($"TIMING set-grammar-markdown {watch.ElapsedMilliseconds}ms");

            watch.Restart();
            editor.Document.Text = text;
            Console.WriteLine($"TIMING set-document {watch.ElapsedMilliseconds}ms");

            watch.Restart();
            var blocks = GhostShell.App.MarkdownPreviewDocument.Parse(text);
            Console.WriteLine(
                $"TIMING markdown-parse {watch.ElapsedMilliseconds}ms ({blocks.Length} blocks)");

            watch.Restart();
            var view = new GhostShell.App.Views.Components.MarkdownPreviewView { Text = text };
            Console.WriteLine($"TIMING markdown-view-construct {watch.ElapsedMilliseconds}ms");

            return new Window
            {
                Width = 400,
                Height = 200,
                CanResize = false,
                ShowInTaskbar = false,
                Content = view,
            };
        }, null),
        // The whole path, timed as a person experiences it: select the file,
        // then flip the switch, on the real panel.
        ("preview-timing-panel", () =>
        {
            var window = CreateFilePanelProbe("notes.md");
            var panel = (FileRuntimePanelViewModel)
                ((GhostShell.App.Views.RuntimePanels.FileRuntimePanelView)window.Content!)
                .DataContext!;
            var timed = false;
            window.Opened += (_, _) =>
            {
                if (timed)
                {
                    return;
                }

                timed = true;
                // Split deliberately: what the reader waits for before the file
                // appears, and what arrives afterwards without holding them up.
                // Drained at Input priority first: that is everything the
                // reader waits on before the file is on screen. Whatever the
                // panel deferred below that priority runs afterwards, with
                // input processed in between.
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var preview = panel.PreviewSelectedAsync();
                while (!preview.IsCompleted && watch.ElapsedMilliseconds < 10_000)
                {
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
                    Thread.Sleep(1);
                }

                Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
                window.UpdateLayout();
                var blocking = watch.ElapsedMilliseconds;

                watch.Restart();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Console.WriteLine(
                    $"TIMING select-markdown blocking={blocking}ms "
                    + $"deferred-afterwards={watch.ElapsedMilliseconds}ms");

                // Drawn, not just laid out: syntax colouring happens while the
                // text view renders, so a timing that never paints misses it.
                void Draw(string label, long elapsed)
                {
                    var paint = System.Diagnostics.Stopwatch.StartNew();
                    using var bitmap = new RenderTargetBitmap(
                        new PixelSize(900, 620),
                        new Vector(96, 96));
                    bitmap.Render(window);
                    Console.WriteLine(
                        $"TIMING {label} {elapsed}ms + paint {paint.ElapsedMilliseconds}ms");
                }

                for (var round = 0; round < 3; round++)
                {
                    watch.Restart();
                    panel.PreviewToggles.Single().IsOn = true;
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Draw("toggle-to-raw", watch.ElapsedMilliseconds);

                    watch.Restart();
                    panel.PreviewToggles.Single().IsOn = false;
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Draw("toggle-to-markdown", watch.ElapsedMilliseconds);
                }
            };
            return window;
        }, null),
        // A hex dump must not wrap: the rows are a fixed-width grid, and a
        // folded row stops lining up with the ones above it.
        ("hex-preview", () => new Window
        {
            Width = 560,
            Height = 260,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Padding = new Thickness(12),
                Child = new GhostShell.App.Views.Components.CodePreviewView
                {
                    FileName = "payload.bin",
                    WordWrap = false,
                    Text = GhostShell.Application.Previews.PreviewText.Hex(
                        Enumerable.Range(0, 96).Select(value => (byte)value).ToArray(),
                        providerTruncated: false),
                },
            },
        }, null),
        // A delimited file as the table it describes.
        ("csv-preview", () => new Window
        {
            Width = 620,
            Height = 320,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Padding = new Thickness(8),
                Child = new GhostShell.App.Views.Components.PreviewTableView
                {
                    DataContext = new PreviewTableViewModel(
                        (TablePreviewRendering)new FilePreviewCatalog().Create(
                            new FilePreviewSource(
                                "deployments.csv",
                                FilePanelPreviewKind.Text,
                                "text/plain",
                                System.Text.Encoding.UTF8.GetBytes(QaData.SampleCsv),
                                IsTruncated: false)).Rendering),
                },
            },
        }, null),
        // An archive listed rather than unpacked.
        ("archive-preview", () => new Window
        {
            Width = 460,
            Height = 380,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Padding = new Thickness(8),
                Child = new GhostShell.App.Views.Components.PreviewTreeView
                {
                    DataContext = CreateArchiveListing(),
                },
            },
        }, null),
        ("code-preview-wrap", () => new Window
        {
            Width = 380,
            Height = 300,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Padding = new Thickness(12),
                Child = new GhostShell.App.Views.Components.CodePreviewView
                {
                    FileName = "LaunchPlanner.cs",
                    Text = QaData.LongLinedCSharp,
                },
            },
        }, null),
        // A long fenced block inside Markdown: the fence is exactly as tall as
        // its code, with no dead space under it and nothing scrolled away.
        ("markdown-preview-long-fence", () => new Window
        {
            Width = 560,
            Height = 640,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Child = new GhostShell.App.Views.Components.MarkdownPreviewView
                {
                    Text = QaData.MarkdownWithLongFence,
                },
            },
        }, null),
        ("code-preview", () => new Window
        {
            Width = 720,
            Height = 520,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Padding = new Thickness(12),
                Child = new GhostShell.App.Views.Components.CodePreviewView
                {
                    FileName = "ConnectionStartup.cs",
                    Text = QaData.SampleCSharp,
                },
            },
        }, null),
        // The process monitor at both widths: wide proves the name column trims
        // instead of overlapping CPU, narrow proves the Started column folds.
        ("procs-wide", () => new Window
        {
            Width = 900,
            Height = 620,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new GhostShell.App.Views.RuntimePanels.ProcessMonitorRuntimePanelView
            {
                DataContext = new QaProcessMonitorPreview(),
            },
        }, null),
        ("procs-narrow", () => new Window
        {
            Width = 430,
            Height = 620,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new GhostShell.App.Views.RuntimePanels.ProcessMonitorRuntimePanelView
            {
                DataContext = new QaProcessMonitorPreview(),
            },
        }, null),
        // And wide, where the two-column cap keeps the cards sharing the full
        // width instead of packing at minimum width in a corner.
        ("stats-wide", () => new Window
        {
            Width = 1100,
            Height = 720,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new GhostShell.App.Views.RuntimePanels.StatisticsRuntimePanelView
            {
                DataContext = new QaStatisticsPreview(),
            },
        }, null),
        // The same chooser the placeholder panel embeds, at a split-panel width,
        // so the adaptive tile grid is reviewable at the size that used to crush
        // its labels to one letter.
        ("chooser-narrow", () => new Window
        {
            Width = 480,
            Height = 820,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Padding = new Thickness(16),
                Child = new GhostShell.App.Views.Components.LauncherView
                {
                    DataContext = CreateViewModel(),
                },
            },
        }, null),
        ("dialog-connection-editor", () => new ConnectionEditorDialog(
            CreateQaConnectionEditor()), null),
        ("dialog-connection-editor-existing", () => new ConnectionEditorDialog(
            CreateQaConnectionEditor(existingTerminal: true)), null),
        ("dialog-connection-editor-files", () => new ConnectionEditorDialog(
            CreateQaConnectionEditor(
                initialFamily: SavedConnectionFamily.Files)), null),
        ("dialog-connection-editor-database", () => new ConnectionEditorDialog(
            CreateQaConnectionEditor(
                initialFamily: SavedConnectionFamily.Database)), null),
        ("dialog-connection-editor-git", () => new ConnectionEditorDialog(
            CreateQaGitConnectionEditor()), null),
        // The same editor with a saved SSH connection chosen: the endpoint
        // fields fold behind the one-line summary.
        ("dialog-connection-editor-git-linked", () =>
        {
            var editor = CreateQaGitConnectionEditor();
            editor.Terminal.SelectedSavedSshSource = editor.Terminal.SavedSshSources[1];
            return new ConnectionEditorDialog(editor);
        }, null),
        ("dialog-ai-provider-editor", () => new AiProviderProfileEditorDialog(
            new AiProviderProfileEditorViewModel(new QaAiProfileRuntime(), [])), null),
        // The chrome glyphs at cell size and at proof size, side by side: if a
        // glyph is whole at 64 and cut at 12, the clip is measurement, not art.
        ("icon-probe", static () =>
        {
            var symbols = new[]
            {
                FluentIcons.Common.Symbol.WindowMultiple,
                FluentIcons.Common.Symbol.SplitVertical,
                FluentIcons.Common.Symbol.SplitHorizontal,
                FluentIcons.Common.Symbol.Dismiss,
            };
            var grid = new StackPanel { Spacing = 24, Margin = new Thickness(24) };
            foreach (var size in new[] { 12d, 24d, 64d })
            {
                var row = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 24,
                };
                foreach (var symbol in symbols)
                {
                    row.Children.Add(new Border
                    {
                        BorderBrush = Avalonia.Media.Brushes.Gray,
                        BorderThickness = new Thickness(1),
                        Child = new FluentIcons.Avalonia.SymbolIcon
                        {
                            Symbol = symbol,
                            FontSize = size,
                        },
                    });
                }

                grid.Children.Add(row);
            }

            return new Window
            {
                Width = 560,
                Height = 420,
                CanResize = false,
                ShowInTaskbar = false,
                Content = grid,
            };
        }, null),
        ("dialog-mcp-server-editor", () => new McpServerProfileEditorDialog(
            new McpServerProfileEditorViewModel()), null),
        ("dialog-saved-screen-editor", () => new SavedScreenEditorDialog(
            new SavedScreenEditorViewModel(
                QaData.Screens[0].Value,
                QaData.Screens[0].Revision,
                [.. QaData.Connections.Select(item => item.Value)],
                [],
                [.. QaData.Layouts.Select(item => item.Value)]),
            // The harness never persists; capture is presentation only.
            static (_, _) => throw new NotSupportedException(
                "The design QA harness does not save definitions.")), null),
        // The design system itself, so a changed radius, gap, or tone shows up as a
        // diff in one image rather than as drift discovered later in a screenshot.
        ("design-system", static () => new DesignSystemGalleryWindow(), null),
        ("dialog-definition-delete", () => Confirmations.DefinitionDelete(
            "connection",
            QaData.Connections[0].Value.Name), null),
        // The same gallery at the two density extremes. The spacing scale, the
        // radii, and the control metrics all derive from the settings, so if any
        // of them stops doing so these two become the same image — which is the
        // only way to notice that a token quietly went back to being a literal.
        ("design-system-compact",
            static () => new DesignSystemGalleryWindow(),
            AppearanceExtreme(InterfaceDensity.Compact)),
        ("design-system-comfortable",
            static () => new DesignSystemGalleryWindow(),
            AppearanceExtreme(InterfaceDensity.Comfortable)),
        // The light appearance, which otherwise only exists on user machines.
        ("design-system-light",
            static () => new DesignSystemGalleryWindow(),
            new ThemePreference(
                ThemePreference.Default.Id,
                ThemePreference.Default.Name,
                AppearanceMode.Light,
                PlatformProfile.Automatic,
                AccentPreference.FollowHost)),
    ];

    public override void Initialize()
    {
        // Load the product's compiled resources and styles rather than a
        // harness copy that could drift from the shipped application.
        var productApplication = new GhostShell.App.App();
        productApplication.Initialize();
        _productApplication = productApplication;

        // The shipped app publishes its ShellFontSize*/metric resources from the
        // appearance pipeline at composition time. The harness has no composition
        // root, so it runs that same private mapping here; without it every
        // DynamicResource size silently falls back and the capture misreports
        // the real typography.
        ApplyProductAppearanceResources(productApplication, ThemePreference.Default);

        ((IResourceProvider)productApplication.Resources).RemoveOwner(productApplication);
        Resources = productApplication.Resources;
        while (productApplication.Styles.Count > 0)
        {
            var style = productApplication.Styles[0];
            productApplication.Styles.RemoveAt(0);
            Styles.Add(style);
        }

        RequestedThemeVariant = ThemeVariant.Dark;
    }

    private static GhostShell.App.App? _productApplication;

    /// <summary>
    /// Re-publishes the application resources for <paramref name="theme"/>. The
    /// resource dictionary is shared with this harness application, so the live
    /// window picks the new metrics up exactly as it does in the product.
    /// </summary>
    private static void ApplyTheme(
        ThemePreference theme,
        bool highContrast = false)
    {
        if (_productApplication is { } productApplication)
        {
            var mapped = ApplyProductAppearanceResources(
                productApplication,
                theme,
                highContrast);
            // The theme dictionaries key off the requested variant, exactly as
            // the product's ApplyAppearance switches it; without this a light
            // theme capture would mix light published tokens with dark
            // dictionary fallbacks.
            if (Current is { } current
                && mapped?.GetType().GetProperty("ThemeVariant")?.GetValue(mapped)
                    is ThemeVariant variant)
            {
                current.RequestedThemeVariant = variant;
            }
        }
    }

    private static object? ApplyProductAppearanceResources(
        GhostShell.App.App productApplication,
        ThemePreference theme,
        bool highContrast = false)
    {
        // The reference frames were drawn with #FF8400 as the example host accent.
        // Supplying it here keeps captures directly comparable; the product's own
        // bronze fallback still applies whenever a host reports no accent.
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            new RgbColor(0xFF, 0x84, 0x00),
            highContrast: highContrast,
            supportsAdvancedMaterials: true);
        var effectiveTheme = theme.Resolve(host);

        var appAssembly = typeof(GhostShell.App.App).Assembly;
        var mapper = appAssembly.GetType(
                "GhostShell.App.EffectiveAppearanceResourceMapper",
                throwOnError: true)!;
        var mapped = mapper
            .GetMethod("Map", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [effectiveTheme]);

        typeof(GhostShell.App.App)
            .GetMethod(
                "ApplyApplicationResources",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(productApplication, [mapped]);
        return mapped;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (Program.IsTerminalFontVerification)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            throw new InvalidOperationException("A desktop lifetime is required.");
        }

        var viewModel = CreateViewModel();
        var window = new MainWindow
        {
            DataContext = viewModel,
            Title = "GhostSHELL · design QA",
            Width = 1440,
            Height = 900,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(40, 40),
        };
        window.DataTemplates.Add(
            new FuncDataTemplate<WebsiteMonitoringRuntimePanelViewModel>(
                static (panel, _) => new WebsiteMonitoringRuntimePanelView
                {
                    DataContext = panel,
                }));
        if (Program.IsWebsiteExport)
        {
            WebsiteScreenshotExport.PrepareWindow(window);
        }

        desktop.MainWindow = window;
        window.Opened += async (_, _) => await CaptureAllAsync(desktop, window, viewModel);
        window.Show();

        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var catalog = Program.IsWebsiteExport
            ? new QaDefinitionCatalog(
                WebsiteScreenshotExport.NormalizeSnapshot(QaData.Snapshot),
                WebsiteScreenshotExport.NormalizeTheme)
            : new QaDefinitionCatalog(QaData.Snapshot);
        // The product republishes the appearance whenever the catalog changes;
        // mirroring that here makes "settings apply immediately" capturable.
        catalog.Changed += (_, _) =>
        {
            if (catalog.SavedTheme is { } savedTheme)
            {
                ApplyTheme(Program.IsWebsiteExport
                    ? WebsiteScreenshotExport.NormalizeTheme(savedTheme)
                    : savedTheme);
            }
        };
        var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<ISessionHostClient, UnusedProxy>(),
            catalog,
            new UnusedConnectionRuntime(),
            new MemoryOnlySecretVault(),
            Files,
            Files,
            new TerminalStartupCommandDispatcher(new MemoryOnlyAuditStore(), TimeProvider.System),
            uiThreadDispatcher: new ImmediateUiDispatcher(),
            recentSessionHistory: new GhostShell.App.RecentSessionHistory(
                new QaRecentSessionStore(),
                new QaTimeProvider()),
            fileProviderRuntime: new QaFileProviderRuntime(),
            timeProvider: new QaTimeProvider(),
            aiProviderRuntime: AgentProfiles,
            agentChatRuntime: AgentRuntime,
            databasePanelClient: new QaDatabasePanelClient(),
            dockerEngineClient: Docker,
            gitRepositoryClient: Git);

        // Real connection pills, so the row that carries them is reviewable
        // rather than rendering empty.
        var workspace = new RuntimeWorkspaceViewModel(
            new WorkspaceInstanceId("qa-workspace"),
            "Operations",
            "Bronze",
            [.. viewModel.Connections.Take(2)]);
        // Real tabs so the tab strip is reviewable at every placement.
        foreach (var (id, title, source) in new[]
                 {
                     ("qa-tab-api", "production-api", "production-api"),
                     ("qa-tab-web", "staging-web", "staging-web"),
                     ("qa-tab-db", "postgres-primary", "postgres-primary"),
                 })
        {
            workspace.Tabs.Add(new RuntimeTabViewModel(new TabInstanceId(id), title, source));
        }

        // Real panels, so the panel card and its header are actually rendered. The
        // harness cannot run a PTY, but the chrome around one is Avalonia's and is
        // exactly where the rounded corner is either clipped or not.
        RuntimePanelViewModel[] panels = Program.IsWebsiteExport
            ?
            [
                new WebsiteDummyRuntimePanelViewModel(
                    new PanelInstanceId("qa-panel-terminal"),
                    PanelKind.Terminal,
                    "production-api",
                    "LOCAL",
                    WebsiteDummyPanelContent.Terminal),
                new WebsiteDummyRuntimePanelViewModel(
                    new PanelInstanceId("qa-panel-browser"),
                    PanelKind.Browser,
                    "Browser",
                    "BROWSER",
                    WebsiteDummyPanelContent.Browser),
            ]
            :
            [
                new UnavailableRuntimePanelViewModel(
                    new PanelInstanceId("qa-panel-terminal"),
                    PanelKind.Terminal,
                    "production-api",
                    "LOCAL",
                    "This harness renders panel chrome without a live session."),
                new UnavailableRuntimePanelViewModel(
                    new PanelInstanceId("qa-panel-browser"),
                    PanelKind.Browser,
                    "Browser",
                    "BROWSER",
                    "This harness renders panel chrome without a live session."),
            ];
        foreach (var panel in panels)
        {
            workspace.Tabs[0].AddPanel(panel);
        }

        _ = workspace.Tabs[0].ActivatePanel(panels[0].Id);
        workspace.Tabs[0].NotifyPanelLayoutChanged();
        workspace.Tabs[0].IsActive = true;
        // The active tab is what the canvas presents, and its setter is internal.
        typeof(RuntimeWorkspaceViewModel)
            .GetProperty(nameof(RuntimeWorkspaceViewModel.ActiveTab))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(workspace, [workspace.Tabs[0]]);

        // The canvas presents one control per open workspace, so a workspace
        // that is in front without being in the open set has nowhere to draw.
        // The product puts it there when it activates one; this harness assigns
        // the property directly, so it has to say the same thing itself.
        var openWorkspaces = (System.Collections.ObjectModel.ObservableCollection<RuntimeWorkspaceViewModel>)
            typeof(MainWindowViewModel)
                .GetField("_openWorkspaces", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel)!;
        openWorkspaces.Add(workspace);
        workspace.GetType()
            .GetProperty("IsCanvasShown")!
            .GetSetMethod(nonPublic: true)!
            .Invoke(workspace, [true]);

        typeof(MainWindowViewModel)
            .GetProperty(nameof(MainWindowViewModel.RuntimeWorkspace))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(viewModel, [workspace]);

        return viewModel;
    }

    private static void AddSampleRedisPanel(
        MainWindowViewModel viewModel,
        RedisWorkspacePerspective perspective,
        string? selectedKeyType = null)
    {
        if (viewModel.RuntimeWorkspace is not { } workspace)
        {
            return;
        }

        // The fixture is shared across routes, so the previous perspective's
        // tab goes before this one arrives; three "cache" tabs in a capture is
        // the harness talking about itself.
        foreach (var stale in workspace.Tabs
                     .Where(candidate => candidate.Panels.Any(panel => panel is RedisRuntimePanelViewModel))
                     .ToArray())
        {
            workspace.Tabs.Remove(stale);
        }

        var tab = new RuntimeTabViewModel(
            new TabInstanceId($"qa-tab-redis-{perspective}".ToLowerInvariant()),
            "cache",
            "Local");
        var panel = new RedisRuntimePanelViewModel(
            new PanelInstanceId($"qa-panel-redis-{perspective}".ToLowerInvariant()),
            "Redis",
            new QaRedisPanelSessionFactory(),
            new QaRedisConnectionCatalog(),
            connectionString: "cache.internal:6379",
            // The key rows count their TTLs down, so a capture only diffs
            // against the last one if its clock stands still.
            timeProvider: new QaTimeProvider());
        // The stub answers synchronously, so the capture shows a connected
        // server rather than the panel's own empty state.
        panel.Initialization.GetAwaiter().GetResult();
        panel.Perspective = perspective;

        switch (perspective)
        {
            case RedisWorkspacePerspective.Browser:
                panel.SelectedKey = selectedKeyType is null
                    ? panel.Keys.FirstOrDefault()
                    : panel.Keys.FirstOrDefault(key => string.Equals(key.Type, selectedKeyType, StringComparison.Ordinal));
                // Pointing at an entry is what the entry-level action acts on,
                // so the capture shows it live rather than disabled.
                panel.SelectedValueEntry = panel.ValueEntries.FirstOrDefault();
                break;
            case RedisWorkspacePerspective.Search:
                panel.RefreshIndexesCommand.Execute(null);
                panel.SearchIndex = "idx:catalog";
                panel.SearchCommand.Execute(null);
                break;
            case RedisWorkspacePerspective.PubSub:
                panel.SubscriptionName = "orders.*";
                panel.SubscriptionKind = RedisSubscriptionKind.Pattern;
                panel.SubscribeCommand.Execute(null);
                break;
        }

        tab.AddPanel(panel);
        _ = tab.ActivatePanel(panel.Id);
        tab.NotifyPanelLayoutChanged();
        workspace.Tabs.Add(tab);

        foreach (var candidate in workspace.Tabs)
        {
            candidate.IsActive = ReferenceEquals(candidate, tab);
        }

        typeof(RuntimeWorkspaceViewModel)
            .GetProperty(nameof(RuntimeWorkspaceViewModel.ActiveTab))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(workspace, [tab]);
    }

    private static void AddSampleDatabasePanel(MainWindowViewModel viewModel)
    {
        if (viewModel.RuntimeWorkspace is not { } workspace)
        {
            return;
        }

        var tab = workspace.Tabs.FirstOrDefault(candidate =>
            candidate.Panels.Any(panel => panel is DatabaseRuntimePanelViewModel));
        if (tab is null)
        {
            tab = new RuntimeTabViewModel(
                new TabInstanceId("qa-tab-database"),
                "deployments-db",
                "Local");
            // A server engine, so the capture exercises the database selector
            // and the full session line in the status bar.
            var panel = new DatabaseRuntimePanelViewModel(
                new PanelInstanceId("qa-panel-database"),
                "Database",
                new QaDatabasePanelClient(),
                driverId: "postgres",
                connectionString: "Host=db.internal;Port=5432;Database=app;Username=ops");
            tab.AddPanel(panel);
            _ = tab.ActivatePanel(panel.Id);
            // The stub completes synchronously, so the capture shows real rows,
            // and a selected row exercises the field inspector.
            _ = panel.PreviewTableAsync(panel.Tables[0]);
            panel.SelectRow(panel.ResultRows[2]);
            tab.NotifyPanelLayoutChanged();
            workspace.Tabs.Add(tab);
        }

        foreach (var candidate in workspace.Tabs)
        {
            candidate.IsActive = ReferenceEquals(candidate, tab);
        }

        typeof(RuntimeWorkspaceViewModel)
            .GetProperty(nameof(RuntimeWorkspaceViewModel.ActiveTab))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(workspace, [tab]);
    }

    private static void AddSampleFilePanel(MainWindowViewModel viewModel)
    {
        if (viewModel.RuntimeWorkspace is not { } workspace)
        {
            throw new InvalidOperationException(
                "The File Viewer route needs a runtime workspace.");
        }

        var tab = new RuntimeTabViewModel(
            new TabInstanceId("qa-tab-file-viewer"),
            "File Viewer",
            "Local");
        var panel = CreateSampleFilePanel();
        tab.AddPanel(panel);
        _ = tab.ActivatePanel(panel.Id);
        tab.NotifyPanelLayoutChanged();
        workspace.Tabs.Add(tab);

        ActivateTab(workspace, tab);
    }

    private static void AddSampleBrowserPanel(MainWindowViewModel viewModel)
    {
        if (viewModel.RuntimeWorkspace is not { } workspace)
        {
            throw new InvalidOperationException(
                "The Browser route needs a runtime workspace.");
        }

        var tab = new RuntimeTabViewModel(
            new TabInstanceId("qa-tab-browser"),
            "Browser",
            "Local");
        RuntimePanelViewModel panel = Program.IsWebsiteExport
            ? new WebsiteDummyRuntimePanelViewModel(
                new PanelInstanceId("qa-panel-browser-full"),
                PanelKind.Browser,
                "Browser",
                "Browser",
                WebsiteDummyPanelContent.Browser)
            : new UnavailableRuntimePanelViewModel(
                new PanelInstanceId("qa-panel-browser-full"),
                PanelKind.Browser,
                "Browser",
                "Browser",
                "This harness renders the browser panel without a live session.");
        tab.AddPanel(panel);
        _ = tab.ActivatePanel(panel.Id);
        tab.NotifyPanelLayoutChanged();
        workspace.Tabs.Add(tab);
        ActivateTab(workspace, tab);
    }

    private static void AddSampleStatisticsPanel(MainWindowViewModel viewModel)
    {
        if (viewModel.RuntimeWorkspace is not { } workspace)
        {
            throw new InvalidOperationException(
                "The Statistics route needs a runtime workspace.");
        }

        var tab = new RuntimeTabViewModel(
            new TabInstanceId("qa-tab-statistics"),
            "Statistics",
            "Local");
        var panel = new WebsiteMonitoringRuntimePanelViewModel(
            new PanelInstanceId("qa-panel-statistics"),
            PanelKind.Statistics,
            "Statistics",
            "Statistics");
        tab.AddPanel(panel);
        _ = tab.ActivatePanel(panel.Id);
        tab.NotifyPanelLayoutChanged();
        workspace.Tabs.Add(tab);
        ActivateTab(workspace, tab);
    }

    private static void AddSampleProcessMonitorPanel(MainWindowViewModel viewModel)
    {
        if (viewModel.RuntimeWorkspace is not { } workspace)
        {
            throw new InvalidOperationException(
                "The Process Monitor route needs a runtime workspace.");
        }

        var tab = new RuntimeTabViewModel(
            new TabInstanceId("qa-tab-process-monitor"),
            "Process Monitor",
            "Local");
        var panel = new WebsiteMonitoringRuntimePanelViewModel(
            new PanelInstanceId("qa-panel-process-monitor"),
            PanelKind.ProcessMonitor,
            "Process Monitor",
            "Process monitor");
        tab.AddPanel(panel);
        _ = tab.ActivatePanel(panel.Id);
        tab.NotifyPanelLayoutChanged();
        workspace.Tabs.Add(tab);
        ActivateTab(workspace, tab);
    }

    private static void ActivateTab(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab)
    {
        foreach (var candidate in workspace.Tabs)
        {
            candidate.IsActive = ReferenceEquals(candidate, tab);
        }

        typeof(RuntimeWorkspaceViewModel)
            .GetProperty(nameof(RuntimeWorkspaceViewModel.ActiveTab))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(workspace, [tab]);
    }

    private static DockerRuntimePanelViewModel AddSampleDockerPanel(
        MainWindowViewModel viewModel)
    {
        if (viewModel.RuntimeWorkspace is not { } workspace)
        {
            throw new InvalidOperationException(
                "The Docker route needs a runtime workspace.");
        }

        var tab = workspace.Tabs.FirstOrDefault(candidate =>
            candidate.Panels.Any(panel => panel is DockerRuntimePanelViewModel));
        if (tab is null)
        {
            tab = new RuntimeTabViewModel(
                new TabInstanceId("qa-tab-docker"),
                "Docker",
                "Local");
            var panel = new DockerRuntimePanelViewModel(
                new PanelInstanceId("qa-panel-docker"),
                "Docker",
                Docker,
                BuiltInConnections.Local);
            tab.AddPanel(panel);
            _ = tab.ActivatePanel(panel.Id);
            tab.NotifyPanelLayoutChanged();
            workspace.Tabs.Add(tab);
        }

        foreach (var candidate in workspace.Tabs)
        {
            candidate.IsActive = ReferenceEquals(candidate, tab);
        }

        typeof(RuntimeWorkspaceViewModel)
            .GetProperty(nameof(RuntimeWorkspaceViewModel.ActiveTab))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(workspace, [tab]);

        return tab.ActivePanel as DockerRuntimePanelViewModel
            ?? throw new InvalidOperationException(
                "The Docker route did not activate its Docker panel.");
    }

    private static GitRuntimePanelViewModel AddSampleGitPanel(
        MainWindowViewModel viewModel,
        string? repositoryPath = "/Users/qa/projects/ghostshell")
    {
        if (viewModel.RuntimeWorkspace is not { } workspace)
        {
            throw new InvalidOperationException(
                "The Git route needs a runtime workspace.");
        }

        var tab = workspace.Tabs.FirstOrDefault(candidate =>
            candidate.Panels.Any(panel => panel is GitRuntimePanelViewModel));
        if (tab is null)
        {
            tab = new RuntimeTabViewModel(
                new TabInstanceId("qa-tab-git"),
                "Git",
                "Local");

            // The stub client resolves synchronously, so the constructor's
            // initial open completes before the capture.
            var panel = new GitRuntimePanelViewModel(
                new PanelInstanceId("qa-panel-git"),
                "Git",
                Git,
                BuiltInConnections.Local,
                repositoryPath);
            tab.AddPanel(panel);
            _ = tab.ActivatePanel(panel.Id);
            tab.NotifyPanelLayoutChanged();
            workspace.Tabs.Add(tab);
        }

        foreach (var candidate in workspace.Tabs)
        {
            candidate.IsActive = ReferenceEquals(candidate, tab);
        }

        typeof(RuntimeWorkspaceViewModel)
            .GetProperty(nameof(RuntimeWorkspaceViewModel.ActiveTab))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(workspace, [tab]);

        return tab.ActivePanel as GitRuntimePanelViewModel
            ?? throw new InvalidOperationException(
                "The Git route did not activate its Git panel.");
    }

    private static void OpenRedisNewKeySheet(MainWindow window)
    {
        // A collection is the sheet's widest shape — rows, a remove per row and
        // the action that adds one — so that is what the capture shows.
        if (window.DataContext is MainWindowViewModel { RuntimeWorkspace: { } workspace }
            && workspace.Tabs
                .SelectMany(tab => tab.Panels)
                .OfType<RedisRuntimePanelViewModel>()
                .FirstOrDefault() is { } panel)
        {
            panel.NewKeyType = "hash";
            panel.NewKeyEntries[0].Field = "email";
            panel.NewKeyEntries[0].Value = "ops@ghostshell.dev";
            panel.AddNewKeyEntry();
            panel.BeginCreateKey();
        }
    }

    private static void SelectDockerReadmePreview(MainWindow window)
    {
        var files = window.GetVisualDescendants()
            .OfType<GhostShell.App.Views.RuntimePanels.FileRuntimePanelView>()
            .FirstOrDefault(view => view.IsEmbedded)
            ?.DataContext as FileRuntimePanelViewModel;
        if (files?.Entries.FirstOrDefault(entry => string.Equals(entry.Name, "README.md", StringComparison.Ordinal)) is { } readme)
        {
            files.SelectedEntry = readme;
            _ = files.PreviewSelectedAsync();
        }
    }

    private static void SelectFileViewerPreview(MainWindow window)
    {
        var panel = (window.DataContext as MainWindowViewModel)?
            .RuntimeWorkspace?
            .Tabs
            .SelectMany(tab => tab.Panels)
            .OfType<FileRuntimePanelViewModel>()
            .FirstOrDefault();
        if (panel?.Entries.FirstOrDefault(entry => string.Equals(
                entry.Name,
                "notes.md",
                StringComparison.Ordinal)) is not { } notes)
        {
            throw new InvalidOperationException(
                "The File Viewer route could not select its sample document.");
        }

        panel.SelectedEntry = notes;
        Settle(panel.PreviewSelectedAsync());
    }

    /// <summary>
    /// Shows a modal editor off-screen, lets it settle, then renders it at its
    /// own arranged size so the capture reflects the dialog's real geometry.
    /// </summary>
    /// <summary>
    /// Writes a small real SQLite database by hand — the harness references no
    /// SQLite package of its own, and the file format's header plus a couple of
    /// tables is exactly what the viewer needs to be exercised honestly.
    /// </summary>
    private static void WriteSqliteProbe()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Program.SqliteProbePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS deployments;
            DROP TABLE IF EXISTS environments;
            CREATE TABLE deployments (
                id INTEGER PRIMARY KEY,
                service TEXT,
                region TEXT,
                status TEXT,
                deployed_at TEXT);
            INSERT INTO deployments VALUES
                (184, 'billing-api', 'eu-central-1', 'healthy', '2026-08-02T21:14:09Z'),
                (183, 'billing-api', 'us-east-1', 'healthy', '2026-08-02T21:12:44Z'),
                (182, 'checkout-web', 'eu-central-1', 'rolled-back', '2026-08-02T19:03:18Z'),
                (181, 'ledger-worker', 'eu-central-1', 'healthy', '2026-08-02T17:40:51Z');
            CREATE TABLE environments (name TEXT, tier TEXT);
            INSERT INTO environments VALUES ('production', 'tier-1'), ('staging', 'tier-2');
            CREATE VIEW IF NOT EXISTS recent_failures AS
                SELECT * FROM deployments WHERE status <> 'healthy';
            """;
        command.ExecuteNonQuery();
    }

    private static Window CreateDatabaseContextMenuProbe()
    {
        var viewer = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "probe.db",
            new GhostShell.Databases.DatabasePanelClient(),
            "sqlite",
            Program.SqliteProbePath);
        var view = new GhostShell.App.Views.Components.DatabaseWorkspaceView
        {
            DataContext = viewer,
        };
        var prepared = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            Width = 920,
            Height = 620,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Child = view,
            },
            Tag = prepared.Task,
        };
        window.Opened += async (_, _) =>
        {
            try
            {
                await viewer.ConnectAsync();
                var table = viewer.Tables.Single(item =>
                    string.Equals(
                        item.Descriptor.Name,
                        "deployments",
                        StringComparison.Ordinal));
                await viewer.PreviewTableAsync(table);
                window.UpdateLayout();

                var grid = window.GetVisualDescendants()
                    .OfType<DataGrid>()
                    .Single(control => string.Equals(
                        AutomationProperties.GetName(control),
                        "Database rows",
                        StringComparison.Ordinal));
                var row = viewer.ResultRows[0];
                var column = grid.Columns.Single(candidate =>
                    candidate.Tag is DatabaseResultColumnViewModel descriptor
                    && string.Equals(descriptor.Name, "service", StringComparison.Ordinal));
                grid.SelectedItem = row;
                grid.CurrentColumn = column;
                grid.ScrollIntoView(row, column);
                window.UpdateLayout();
                var cell = row.Cells[column.DisplayIndex];
                var cellContainer = grid.GetVisualDescendants()
                    .OfType<DataGridCell>()
                    .FirstOrDefault(candidate => candidate
                        .GetVisualDescendants()
                        .Any(descendant => ReferenceEquals(descendant.DataContext, cell)))
                    ?? throw new InvalidOperationException(
                        "The database context-menu probe did not realize its target cell.");
                cellContainer.RaiseEvent(new ContextRequestedEventArgs
                {
                    RoutedEvent = InputElement.ContextRequestedEvent,
                });
                window.UpdateLayout();
                if (grid.ContextMenu?.IsOpen != true)
                {
                    throw new InvalidOperationException(
                        "The database context-menu probe did not open the real grid menu.");
                }

                // The off-screen QA platform deliberately has no native save
                // picker, while a desktop app window does. Normalize that one
                // platform capability so the reference comparison captures the
                // ordinary enabled desktop state; export behavior is exercised
                // separately through the injected headless storage fixture.
                var export = grid.ContextMenu.Items
                    .OfType<MenuItem>()
                    .Single(item => string.Equals(
                        AutomationProperties.GetName(item),
                        "Export the current database page",
                        StringComparison.Ordinal));
                export.IsEnabled = true;

                prepared.SetResult();
            }
            catch (Exception exception)
            {
                prepared.SetException(exception);
            }
        };
        window.Closed += (_, _) => viewer.Dispose();
        return window;
    }

    private static Window CreateDatabaseInspectorEditProbe() =>
        CreatePreparedDatabaseProbe(
            width: 720,
            height: 560,
            async (window, viewer, _) =>
            {
                await PreviewQaDeploymentTableAsync(viewer);
                viewer.SelectRow(viewer.ResultRows[0]);
                var field = viewer.SelectedRowFields.Single(candidate =>
                    string.Equals(candidate.Name, "service", StringComparison.Ordinal));
                field.BeginEdit();
                field.Draft = "checkout-web-eu — renamed through the inspector "
                    + "draft, applied with Cmd+Enter.";
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                if (!field.IsEditing)
                {
                    throw new InvalidOperationException(
                        "The inspector field did not enter its editing state.");
                }
            });

    private static Window CreateDatabaseCellExpandProbe() =>
        CreatePreparedDatabaseProbe(
            width: 720,
            height: 560,
            async (window, viewer, _) =>
            {
                await PreviewQaDeploymentTableAsync(viewer);
                var (grid, _, _) = SelectDatabaseProbeCell(window, viewer, "service");
                grid.BeginEdit();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(80);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                // Focus lands inside the AvaloniaEdit text area; the expanded
                // editor is the CodeEditBox ancestor wearing the name.
                var focused = TopLevel.GetTopLevel(grid)?.FocusManager?.GetFocusedElement()
                    as Visual;
                var editor = (focused?.GetVisualAncestors()
                    .OfType<GhostShell.App.Views.Components.CodeEditBox>()
                    .FirstOrDefault(candidate => string.Equals(
                        AutomationProperties.GetName(candidate),
                        "Expanded cell editor",
                        StringComparison.Ordinal))) ?? throw new InvalidOperationException(
                        "Editing a text cell did not open and focus the expanded editor.");
                editor.Text = "How automakers are responding to the 25% car tariffs "
                    + "so far — a deliberately long value that wraps across the "
                    + "expanded editor's five-or-so lines.";
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            });

    private static Window CreateDatabaseQuickLookCompactProbe() =>
        CreatePreparedDatabaseProbe(
            width: 720,
            height: 560,
            async (window, viewer, _) =>
            {
                await PreviewQaDeploymentTableAsync(viewer);
                var (grid, row, column) = SelectDatabaseProbeCell(
                    window,
                    viewer,
                    "service");
                viewer.SetSelectedCellText(
                    column.DisplayIndex,
                    "How automakers are responding to the 25% car tariffs so far — "
                    + "a deliberately long value that wraps inside the compact editor.");
                OpenDatabaseProbeContextMenu(window, grid);
                var quickLook = grid.ContextMenu!.Items
                    .OfType<MenuItem>()
                    .Single(candidate => string.Equals(
                        AutomationProperties.GetName(candidate),
                        "Open the active database cell in Quick Look",
                        StringComparison.Ordinal));
                grid.ContextMenu.Close();
                window.UpdateLayout();
                quickLook.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(80);
                Dispatcher.UIThread.RunJobs();

                if (TopLevel.GetTopLevel(grid)?.FocusManager?.GetFocusedElement() is not TextBox editor
                    || !string.Equals(
                        AutomationProperties.GetName(editor),
                        "Quick Look value for service",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The compact database probe did not open the real Quick Look editor.");
                }

                var popupHost = editor.GetVisualAncestors().Last();
                var presenter = popupHost.GetVisualDescendants()
                    .OfType<FlyoutPresenter>()
                    .Single();
                var dialog = popupHost.GetVisualDescendants()
                    .OfType<Grid>()
                    .Single(candidate => string.Equals(
                        AutomationProperties.GetName(candidate),
                        "Database cell Quick Look dialog",
                        StringComparison.Ordinal));
                var apply = presenter.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(candidate => string.Equals(
                        AutomationProperties.GetName(candidate),
                        "Apply the Quick Look database cell value",
                        StringComparison.Ordinal));
                var cancel = presenter.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(candidate => string.Equals(
                        AutomationProperties.GetName(candidate),
                        "Close the database cell Quick Look",
                        StringComparison.Ordinal));
                if (!apply.IsEffectivelyVisible
                    || !cancel.IsEffectivelyVisible
                    || dialog.Bounds.Width > grid.Bounds.Width
                    || dialog.Bounds.Height > grid.Bounds.Height)
                {
                    throw new InvalidOperationException(
                        "The compact Quick Look escaped the grid viewport or hid an action.");
                }
            });

    private static Window CreateDatabaseSortDescendingProbe() =>
        CreatePreparedDatabaseProbe(
            width: 920,
            height: 620,
            async (window, viewer, _) =>
            {
                await PreviewQaDeploymentTableAsync(viewer);
                await viewer.ToggleTableSortAsync("service");
                await viewer.ToggleTableSortAsync("service");
                window.UpdateLayout();

                var grid = DatabaseProbeGrid(window);
                var descendingHeader = grid.GetVisualDescendants()
                    .OfType<Control>()
                    .SingleOrDefault(candidate => string.Equals(
                        AutomationProperties.GetName(candidate),
                        "Sort database column service, descending",
                        StringComparison.Ordinal));
                if (descendingHeader is null
                    || viewer.ResultColumns.Single(column => string.Equals(column.Name, "service", StringComparison.Ordinal))
                        .SortDescending is not true)
                {
                    throw new InvalidOperationException(
                        "The database sort probe did not render the descending header state.");
                }
            });

    private static Window CreateDatabaseRawQueryContextMenuProbe() =>
        CreatePreparedDatabaseProbe(
            width: 920,
            height: 620,
            async (window, viewer, _) =>
            {
                await PreviewQaDeploymentTableAsync(viewer);
                viewer.QueryText = """
                    SELECT id, service, region, status, deployed_at
                    FROM deployments
                    WHERE id >= 179
                    ORDER BY id DESC;
                    """;
                await viewer.RunQueryAsync();
                if (!viewer.CanMutateRows)
                {
                    throw new InvalidOperationException(
                        "The exact-provenance raw-query probe unexpectedly became read-only.");
                }

                var (grid, _, _) = SelectDatabaseProbeCell(window, viewer, "service");
                OpenDatabaseProbeContextMenu(window, grid);
                var mutationItems = new[]
                {
                    "Paste a database cell value from the clipboard",
                    "Add a database row from the context menu",
                    "Duplicate the selected database row",
                    "Set the active database cell value",
                    "Delete the selected database row from the context menu",
                };
                foreach (var automationName in mutationItems)
                {
                    var item = grid.ContextMenu!.Items
                        .OfType<MenuItem>()
                        .Single(candidate => string.Equals(
                            AutomationProperties.GetName(candidate),
                            automationName,
                            StringComparison.Ordinal));
                    if (!item.IsVisible || !item.IsEnabled)
                    {
                        throw new InvalidOperationException(
                            $"Raw-query action '{automationName}' was hidden or disabled.");
                    }
                }

                var export = grid.ContextMenu!.Items
                    .OfType<MenuItem>()
                    .Single(item => string.Equals(
                        AutomationProperties.GetName(item),
                        "Export the current database page",
                        StringComparison.Ordinal));
                export.IsEnabled = true;
            });

    private static Window CreateDatabaseCopyInsertProbe() =>
        CreatePreparedDatabaseProbe(
            width: 920,
            height: 620,
            async (_, viewer, _) =>
            {
                await PreviewQaDeploymentTableAsync(viewer);
                viewer.SelectRow(viewer.ResultRows[0]);
                if (!viewer.CanCopySelectedRowAsInsert)
                {
                    throw new InvalidOperationException(
                        "The row inspector did not enable provider-aware INSERT copy.");
                }
            });

    private static Window CreateDatabasePaginationCountProbe() =>
        CreatePreparedDatabaseProbe(
            width: 920,
            height: 620,
            async (window, viewer, _) =>
            {
                await PreviewQaDeploymentTableAsync(viewer);
                window.UpdateLayout();
                var pageLimit = window.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Single(control => string.Equals(
                        AutomationProperties.GetName(control),
                        "Database page row limit",
                        StringComparison.Ordinal));
                var total = window.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(control => string.Equals(
                        AutomationProperties.GetName(control),
                        "Total matching database rows",
                        StringComparison.Ordinal));
                var expectedTotal = viewer.TotalRowsText;
                if (!string.Equals(pageLimit.Text, "200"
, StringComparison.Ordinal) || !string.Equals(total.Text, expectedTotal
, StringComparison.Ordinal) || viewer.TotalRows <= 0)
                {
                    throw new InvalidOperationException(
                        $"The pager rendered '{pageLimit.Text} / {total.Text}' "
                        + $"instead of '200 / {expectedTotal}'.");
                }
            });

    private static Window CreatePreparedDatabaseProbe(
        double width,
        double height,
        Func<Window, DatabaseRuntimePanelViewModel,
            GhostShell.App.Views.Components.DatabaseWorkspaceView, Task> prepare)
    {
        var viewer = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "deployments.db",
            new QaDatabasePanelClient(),
            "sqlite",
            "Data Source=:memory:");
        var view = new GhostShell.App.Views.Components.DatabaseWorkspaceView
        {
            DataContext = viewer,
        };
        var prepared = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            Width = width,
            Height = height,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Child = view,
            },
            Tag = prepared.Task,
        };
        window.Opened += async (_, _) =>
        {
            try
            {
                await viewer.ConnectAsync();
                await prepare(window, viewer, view);
                window.UpdateLayout();
                prepared.SetResult();
            }
            catch (Exception exception)
            {
                prepared.SetException(exception);
            }
        };
        window.Closed += (_, _) => viewer.Dispose();
        return window;
    }

    private static async Task PreviewQaDeploymentTableAsync(
        DatabaseRuntimePanelViewModel viewer)
    {
        var table = viewer.Tables.Single(item => string.Equals(
            item.Descriptor.Name,
            "deployments",
            StringComparison.Ordinal));
        await viewer.PreviewTableAsync(table);
    }

    private static DataGrid DatabaseProbeGrid(Window window) =>
        window.GetVisualDescendants()
            .OfType<DataGrid>()
            .Single(control => string.Equals(
                AutomationProperties.GetName(control),
                "Database rows",
                StringComparison.Ordinal));

    private static (DataGrid Grid, DatabaseResultRowViewModel Row, DataGridColumn Column)
        SelectDatabaseProbeCell(
            Window window,
            DatabaseRuntimePanelViewModel viewer,
            string columnName)
    {
        window.UpdateLayout();
        var grid = DatabaseProbeGrid(window);
        var row = viewer.ResultRows[0];
        var column = grid.Columns.Single(candidate =>
            candidate.Tag is DatabaseResultColumnViewModel descriptor
            && string.Equals(descriptor.Name, columnName, StringComparison.Ordinal));
        grid.SelectedItem = row;
        grid.CurrentColumn = column;
        grid.ScrollIntoView(row, column);
        Dispatcher.UIThread.RunJobs();
        if (!ReferenceEquals(viewer.SelectedRow, row))
        {
            viewer.SelectRow(row);
        }

        window.UpdateLayout();
        return (grid, row, column);
    }

    private static void OpenDatabaseProbeContextMenu(Window window, DataGrid grid)
    {
        grid.RaiseEvent(new ContextRequestedEventArgs
        {
            RoutedEvent = InputElement.ContextRequestedEvent,
        });
        window.UpdateLayout();
        if (grid.ContextMenu?.IsOpen != true)
        {
            throw new InvalidOperationException(
                "The database probe did not open the real grid context menu.");
        }
    }

    /// <summary>
    /// The zoomable picture viewer over the JPEG probe. The adjustment runs on
    /// the first real layout pass — before the view has bounds there is no
    /// "fitted" to zoom relative to.
    /// </summary>
    private static Window CreateZoomableImageProbe(
        Action<GhostShell.App.Views.Components.ZoomableImageView> adjust)
    {
        using var file = File.OpenRead(Program.JpegProbePath);
        var view = new GhostShell.App.Views.Components.ZoomableImageView
        {
            Margin = new Thickness(12),
            Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(file, 2400),
        };

        var adjusted = false;
        view.LayoutUpdated += (_, _) =>
        {
            if (adjusted || view.Bounds.Width < 1)
            {
                return;
            }

            adjusted = true;
            adjust(view);
        };

        return new Window
        {
            Width = 460,
            Height = 340,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border { Classes = { "FloatingSidebar" }, Child = view },
        };
    }

    /// <summary>
    /// A real zip, written beside the captures and listed through the shipped
    /// reader — so the tree shown is one an archive actually produced.
    /// </summary>
    private static PreviewTreeViewModel CreateArchiveListing()
    {
        var path = Path.Combine(Program.OutputDirectory, "listing-probe.zip");
        using (var file = File.Create(path))
        using (var archive = new System.IO.Compression.ZipArchive(
                   file,
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var entry in new (string Name, string Content)[]
                     {
                         ("README.md", "# Release"),
                         ("bin/ghostshell", new string('x', 4096)),
                         ("share/icons/app.png", new string('x', 12_288)),
                         ("share/locale/en.json", "{}"),
                     })
            {
                using var stream = archive.CreateEntry(entry.Name).Open();
                using var writer = new StreamWriter(stream);
                writer.Write(entry.Content);
            }
        }

        var entries = new GhostShell.Previews.ArchiveTableOfContents()
            .ReadAsync(
                GhostShell.Application.FilePreviewContent.FromLocalFile(path),
                Path.GetFileName(path),
                500,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult()
            ?? throw new InvalidOperationException("The archive probe did not list.");
        return new PreviewTreeViewModel(
            PreviewTreeBuilder.FromPaths(entries),
            $"{entries.Count} files, "
                + ByteSize.Format(entries.Sum(entry => entry.Size ?? 0))
                + " unpacked");
    }

    /// <summary>
    /// Runs the dispatcher until a task the panel started on this thread has
    /// finished, so a capture can be set up from inside a route factory.
    /// </summary>
    private static void Settle(Task task)
    {
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(5);
        while (!task.IsCompleted && waited < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(step);
            waited += step;
        }

        Dispatcher.UIThread.RunJobs();
        if (task is { IsFaulted: true, Exception: { } failure })
        {
            throw failure;
        }
    }

    /// <summary>
    /// The shipped file panel, pointed at a directory of real sample files and
    /// asked to preview one of them. Nothing here is a mock: the provider reads
    /// the disk, the previewers claim by name, and the archive is listed by the
    /// same reader the product uses.
    /// </summary>
    private static FileRuntimePanelViewModel CreateSampleFilePanel()
    {
        var root = Path.Combine(Program.OutputDirectory, "preview-samples");
        Directory.CreateDirectory(root);
        // The real file when it is there: a synthetic sample proves nothing
        // about a document that behaves badly.
        var realNotes = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AGENTS.md");
        File.WriteAllText(
            Path.Combine(root, "notes.md"),
            File.Exists(realNotes) && string.Equals(Environment.GetEnvironmentVariable("QA_REAL_MARKDOWN"), "1"
, StringComparison.Ordinal) ? File.ReadAllText(realNotes)
                : QaData.MarkdownWithLongFence);
        File.WriteAllText(Path.Combine(root, "deployments.csv"), QaData.SampleCsv);
        File.WriteAllBytes(
            Path.Combine(root, "libghost.dylib"),
            [
                0xCF, 0xFA, 0xED, 0xFE,
                .. Enumerable.Range(0, 512 * 1024).Select(value => (byte)(value % 256)),
            ]);
        File.WriteAllText(
            Path.Combine(root, "settings.json"),
            """{"telemetry":{"enabled":false,"endpoint":"https://example.test"},"panels":[1,2,3]}""");
        var archivePath = Path.Combine(root, "release.zip");
        if (!File.Exists(archivePath))
        {
            using var file = File.Create(archivePath);
            using var archive = new System.IO.Compression.ZipArchive(
                file,
                System.IO.Compression.ZipArchiveMode.Create);
            foreach (var entry in new (string Name, string Content)[]
                     {
                         ("README.md", "# Release"),
                         ("bin/ghostshell", new string('x', 4096)),
                         ("share/icons/app.png", new string('x', 12_288)),
                         ("share/locale/en.json", "{}"),
                     })
            {
                using var stream = archive.CreateEntry(entry.Name).Open();
                using var writer = new StreamWriter(stream);
                writer.Write(entry.Content);
            }
        }

        var sampleTimestamp = new DateTime(2026, 8, 20, 18, 15, 0, DateTimeKind.Utc);
        foreach (var samplePath in Directory.EnumerateFiles(root))
        {
            File.SetLastWriteTimeUtc(samplePath, sampleTimestamp);
        }

        var provider = GhostShell.Files.LocalFileProvider.CreateForCurrentPlatform(
            new GhostShell.Files.LocalFileProviderOptions(
                new GhostShell.Files.FileProviderProfileId("qa.files"),
                new GhostShell.Files.FileAuthority("local"),
                root));
        var client = new GhostShell.Files.FilePanelClient(
        [
            new GhostShell.Files.FileProviderRegistration(
                "Samples",
                FileProviderFamily.Posix,
                provider,
                new GhostShell.Files.FileLocation(
                    provider.ProfileId,
                    provider.Authority,
                    GhostShell.Files.FilePath.Root)),
        ]);

        var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "File Viewer",
            client,
            archiveReader: new GhostShell.Previews.ArchiveTableOfContents());
        // Pumped rather than blocked on: the panel finishes its work on this
        // very thread, so waiting on it here would wait forever.
        Settle(panel.Initialization);
        return panel;
    }

    private static Window CreateFilePanelProbe(
        string fileName,
        string? toggleId = null,
        double width = 900)
    {
        var panel = CreateSampleFilePanel();

        var view = new GhostShell.App.Views.RuntimePanels.FileRuntimePanelView
        {
            DataContext = panel,
        };

        // Selected after the view is bound and laid out, which is the order the
        // running app does it in. Selecting first hides a whole class of bug:
        // a binding that is only ever read once still looks right if the value
        // was already there when it was read.
        var selected = false;
        view.LayoutUpdated += (_, _) =>
        {
            if (selected || view.Bounds.Width < 1)
            {
                return;
            }

            selected = true;
            panel.SelectedEntry = panel.Entries.First(entry => string.Equals(entry.Name, fileName, StringComparison.Ordinal));
            Settle(panel.PreviewSelectedAsync());
            if (toggleId is not null)
            {
                panel.PreviewToggles.Single(toggle => string.Equals(toggle.Id, toggleId, StringComparison.Ordinal)).IsOn = true;
            }
        };

        return new Window
        {
            Width = width,
            Height = 620,
            CanResize = false,
            ShowInTaskbar = false,
            Content = view,
        };
    }

    /// <summary>
    /// The listing right-clicked for real: a right button pressed and released
    /// over a row, or over the space below the last one. Nothing here reaches
    /// into the view to open a menu — which menu opens is the panel's answer to
    /// where the press landed, and that is the part worth capturing.
    /// </summary>
    private static FilePanelLocation QaFileLocation(string name) => new(
        "qa.files",
        "local",
        new FilePanelAddress.Hierarchical(
            FilePanelPath.Root.Append(new FilePanelPathSegment(name))));

    private static Window CreateFileContextMenuProbe(bool onEntry)
    {
        var window = CreateFilePanelProbe("notes.md", width: 560);
        window.Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                var view = (GhostShell.App.Views.RuntimePanels.FileRuntimePanelView)
                    window.Content!;
                var target = onEntry
                    ? (Control)view.GetVisualDescendants()
                        .OfType<ListBoxItem>()
                        .First(item => item.DataContext is FileEntryViewModel
                        {
                            Name: "notes.md",
                        })
                    : view.GetVisualDescendants()
                        .OfType<ListBox>()
                        .First(list => list.IsVisible);
                // The space below the last row for the folder menu, the middle
                // of the row for the other.
                var inside = onEntry
                    ? new Point(target.Bounds.Width / 2, target.Bounds.Height / 2)
                    : new Point(target.Bounds.Width / 2, target.Bounds.Height - 40);
                var point = target.TranslatePoint(inside, window) ?? new Point(20, 20);
                Avalonia.Headless.HeadlessWindowExtensions.MouseDown(
                    window,
                    point,
                    MouseButton.Right);
                Avalonia.Headless.HeadlessWindowExtensions.MouseUp(
                    window,
                    point,
                    MouseButton.Right);
            },
            DispatcherPriority.Background);
        return window;
    }

    /// <summary>
    /// A file panel whose selected remote file is waiting to be downloaded,
    /// with Space raised on the file list exactly as the platform would deliver
    /// it. If the shortcut regresses to bubbling, the ListBox consumes the key
    /// and the capture still shows the waiting state.
    /// </summary>
    private static Window CreateSpaceShortcutProbe()
    {
        var list = new ListBox
        {
            ItemsSource = new[] { "payload.bin" },
            SelectedIndex = 0,
            Height = 90,
        };
        var status = new TextBlock
        {
            Margin = new Thickness(12),
            Text = "waiting: space not delivered",
        };
        var panel = new Border
        {
            Classes = { "FloatingSidebar" },
            Child = new StackPanel { Children = { list, status } },
        };
        panel.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.Key == Key.Space && !e.Handled)
                {
                    e.Handled = true;
                    status.Text = "space reached the panel before the list";
                }
            },
            RoutingStrategies.Tunnel);

        var window = new Window
        {
            Width = 460,
            Height = 190,
            CanResize = false,
            ShowInTaskbar = false,
            Content = panel,
        };
        window.Opened += (_, _) =>
        {
            list.Focus();
            list.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Space,
            });
        };
        return window;
    }

    /// <summary>
    /// Writes a small real TIFF: uncompressed RGB with a gradient, enough for
    /// the decoder to be exercised on an actual file of a format the drawing
    /// stack cannot open.
    /// </summary>
    private static void WriteTiffProbe()
    {
        using var image = new ImageMagick.MagickImage(
            new ImageMagick.MagickColor("#1A1A1A"),
            240,
            180);
        using var gradient = new ImageMagick.MagickImage(
            "gradient:#FF8400-#77D797",
            new ImageMagick.MagickReadSettings { Width = 240, Height = 180 });
        image.Composite(gradient, ImageMagick.CompositeOperator.Over);
        image.Format = ImageMagick.MagickFormat.Tiff;
        image.Write(Program.TiffProbePath);
    }

    /// <summary>
    /// Writes a real two-page PDF with visible text on each page, built by hand
    /// so the probe is exactly a PDF and nothing else.
    /// </summary>
    private static void WritePdfProbe()
    {
        const string firstPage =
            "BT /F1 26 Tf 60 700 Td (GhostSHELL preview) Tj ET\n"
            + "BT /F1 13 Tf 60 660 Td (Page one of two) Tj ET";
        const string secondPage = "BT /F1 26 Tf 60 700 Td (Second page) Tj ET";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var builder = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(builder.Length);
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        foreach (var (number, content) in new[] { (6, firstPage), (7, secondPage) })
        {
            offsets.Add(builder.Length);
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"{number} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        var startXref = builder.Length;
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF\n");
        File.WriteAllText(Program.PdfProbePath, builder.ToString());
    }

    /// <summary>
    /// The PDF probe rendered by PDFium, behind the same platform guard the
    /// composition uses: the route table is static, so the check has to live
    /// where the engine is actually touched.
    /// </summary>
    private static Window CreatePdfProbeWindow()
    {
        // The positive form is the shape the platform analyzer understands.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            return RenderPdfProbeWindow();
        }

        throw new PlatformNotSupportedException(
            "PDFium ships binaries for the desktop platforms only.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("macOS")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static Window RenderPdfProbeWindow()
    {
        var renderer = new GhostShell.Previews.PdfiumPreviewRenderer();
        var page = renderer
            .RenderPageAsync(
                GhostShell.Application.FilePreviewContent.FromLocalFile(Program.PdfProbePath),
                0,
                700,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult()
            ?? throw new InvalidOperationException("The PDF probe did not render.");
        using var stream = new MemoryStream(page.PngBytes.ToArray(), writable: false);
        return new Window
        {
            Width = 480,
            Height = 640,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Padding = new Thickness(10),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        // Through the same viewer the panel uses, so a page is
                        // fitted, zoomable and turnable like any other picture.
                        new GhostShell.App.Views.Components.ZoomableImageView
                        {
                            Source = new Avalonia.Media.Imaging.Bitmap(stream),
                            Height = 520,
                        },
                        new TextBlock
                        {
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            Text = $"Page {page.PageNumber} of {page.PageCount}",
                        },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Writes a JPEG comfortably larger than the 256 KB bounded preview read,
    /// so the capture proves the panel reads the file rather than its head.
    /// </summary>
    private static void WriteJpegProbe()
    {
        using var image = new ImageMagick.MagickImage(
            "gradient:#FF8400-#1A1A1A",
            new ImageMagick.MagickReadSettings { Width = 2200, Height = 1500 });
        image.AddNoise(ImageMagick.NoiseType.Gaussian);
        image.Quality = 92;
        image.Format = ImageMagick.MagickFormat.Jpeg;
        image.Write(Program.JpegProbePath);
    }

    private static async Task CaptureDialogAsync(string name, Window dialog)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = new PixelPoint(-4000, -4000);
        dialog.ShowInTaskbar = false;
        dialog.Show();
        if (dialog.Tag is Task preparation)
        {
            await preparation.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await Task.Delay(260);
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        await Task.Delay(140);

        // A "-2x" suffix renders at Retina density, so glyph-placement issues
        // that only appear under fractional-scale pixel snapping are capturable.
        var scale = name.EndsWith("-2x", StringComparison.Ordinal)
                ? 2
                : 1;
        Control captureTarget = dialog;
        ContextMenu? contextMenu = null;
        if (name.StartsWith("database-context-menu", StringComparison.Ordinal))
        {
            var grid = dialog.GetVisualDescendants()
                .OfType<DataGrid>()
                .Single(control => string.Equals(
                    AutomationProperties.GetName(control),
                    "Database rows",
                    StringComparison.Ordinal));
            contextMenu = grid.ContextMenu
                ?? throw new InvalidOperationException("The database grid context menu is missing.");
            if (!contextMenu.IsOpen)
            {
                throw new InvalidOperationException("The database grid context menu did not open.");
            }

            captureTarget = contextMenu;
        }

        FreezeNondeterministicPresentation(captureTarget);
        var width = (int)Math.Ceiling(Math.Max(captureTarget.Bounds.Width, 1)) * scale;
        var height = (int)Math.Ceiling(Math.Max(captureTarget.Bounds.Height, 1)) * scale;
        var path = Path.Combine(Program.OutputDirectory, $"{name}.png");
        using var bitmap = new RenderTargetBitmap(
            new PixelSize(width, height),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(captureTarget);
        bitmap.Save(path);

        contextMenu?.Close();
        dialog.Close();
        Console.WriteLine($"CAPTURE {name} -> {path} ({width}x{height})");
    }

    private static async Task CaptureAllAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        MainWindowViewModel viewModel)
    {
        var exitCode = 0;
        try
        {
            Directory.CreateDirectory(Program.OutputDirectory);
            if (Program.IsWebsiteExport)
            {
                WebsiteScreenshotExport.WriteChromeMask(Program.OutputDirectory);
            }
            if (!Program.IsWebsiteExport)
            {
                WriteSqliteProbe();
                WriteTiffProbe();
                WritePdfProbe();
                WriteJpegProbe();
            }
            await Task.Delay(800);

            var requested = Program.RequestedRoutes;
            var availableRoutes = Program.IsWebsiteExport
                ? [.. Routes.Where(route => WebsiteScreenshotExport.IncludesRoute(route.Name))]
                : Routes;
            var selected = requested.Length == 0
                ? availableRoutes
                : [.. availableRoutes.Where(route => requested.Contains(route.Name))];
            var selectedDialogs = Program.IsWebsiteExport
                ? []
                : requested.Length == 0
                    ? Dialogs
                    : [.. Dialogs.Where(dialog => requested.Contains(dialog.Name))];

            if (selected.Length == 0 && selectedDialogs.Length == 0)
            {
                var known = availableRoutes.Select(route => route.Name).Concat(
                    Program.IsWebsiteExport
                        ? []
                        : Dialogs.Select(dialog => dialog.Name));
                throw new InvalidOperationException(
                    $"No route matched. Known routes: {string.Join(", ", known)}");
            }

            foreach (var route in selected)
            {
                var routeTheme = route.Theme ?? ThemePreference.Default;
                ApplyTheme(
                    Program.IsWebsiteExport
                        ? WebsiteScreenshotExport.NormalizeTheme(routeTheme)
                        : routeTheme,
                    route.HighContrast);
                if (Program.IsWebsiteExport)
                {
                    await ResetWebsiteRouteStateAsync(viewModel, window);
                }
                ClearWorkspaceAgentActivity(viewModel);
                // The sample agent conversation belongs to the one route that asks
                // for it. Resetting first keeps that route from leaking a connected
                // agent into whatever is captured after it, whatever the order.
                AgentProfiles.Reset();
                AgentRuntime.Reset();
                Files.Reset();
                // The sample drag ghost belongs to the one route that shows it;
                // without this it floats over every capture that follows.
                window.HideDragGhost();
                // Likewise flyouts a route clicked open — the transfer manager
                // otherwise floats over every capture after its own.
                foreach (var popup in window.GetVisualDescendants()
                             .OfType<Avalonia.Controls.Primitives.Popup>()
                             .Where(popup => popup.IsOpen))
                {
                    popup.Close();
                }

                // A button's flyout hosts outside the window's visual tree, so
                // the sweep above never sees it. Left open, it light-dismisses
                // the next route's first click and that route silently captures
                // a screen nothing was done to.
                foreach (var button in window.GetVisualDescendants().OfType<Button>())
                {
                    button.Flyout?.Hide();
                }

                // And an editor a route left with unsaved changes, which refuses
                // to be navigated away from — correctly, in the product, and
                // ruinously here: every later route would capture the one before
                // it and report nothing wrong.
                viewModel.DismissWorkspaceEditor();
                viewModel.DismissLayoutDesigner();
                viewModel.CloseOverlay();
                if (viewModel.IsAgentPanelVisible)
                {
                    viewModel.ToggleAgentPanel();
                }

                route.Apply(viewModel);
                MaterializeCurrentPresentation(window, viewModel);
                if (Program.IsWebsiteExport
                    && route.Name.StartsWith("workspace-agent", StringComparison.Ordinal)
                    && viewModel.IsAgentPanelVisible
                    && !viewModel.IsAgentPanelDocked)
                {
                    // Floating panels use a separate platform surface, which a
                    // window-only bitmap cannot capture. Dock the same live
                    // panel for website artwork so its actual state is visible.
                    await viewModel.ToggleAgentPanelPinAsync(CancellationToken.None);
                }
                await Task.Delay(220);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Task.Delay(120);

                if (route.FocusFirst is { } focusTarget)
                {
                    var control = window.GetVisualDescendants()
                        .OfType<Control>()
                        .FirstOrDefault(candidate =>
                            string.Equals(candidate.Name, focusTarget, StringComparison.Ordinal)
                            || string.Equals(
                                AutomationProperties.GetName(candidate),
                                focusTarget,
                                StringComparison.Ordinal))
                        ?? throw new InvalidOperationException(
                            $"The route wanted to focus '{focusTarget}', which is not in the tree.");
                    if (!control.Focus(NavigationMethod.Tab))
                    {
                        throw new InvalidOperationException(
                            $"The route could not focus '{focusTarget}', so its focus "
                            + "indicator cannot be accepted.");
                    }

                    await Task.Delay(140);
                    Dispatcher.UIThread.RunJobs();
                    if (!control.IsFocused)
                    {
                        throw new InvalidOperationException(
                            $"The route lost focus from '{focusTarget}' before capture.");
                    }
                }

                if (route.ClickFirst is { } clickTarget)
                {
                    var button = window.GetVisualDescendants()
                        .OfType<Button>()
                        .FirstOrDefault(candidate =>
                            string.Equals(
                                AutomationProperties.GetName(candidate),
                                clickTarget,
                                StringComparison.Ordinal))
                        ?? throw new InvalidOperationException(
                            $"The route wanted to click '{clickTarget}', which is not in the tree.");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    // Long enough for debounced effects of the click — the
                    // appearance page coalesces edits for 220 ms before the
                    // theme commit re-publishes the resources.
                    await Task.Delay(500);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }

                var captureWidth = Program.IsWebsiteExport
                    ? WebsiteScreenshotExport.LogicalWidth
                    : route.Width;
                var captureHeight = Program.IsWebsiteExport
                    ? WebsiteScreenshotExport.LogicalHeight
                    : route.Height;
                if (window.Width != captureWidth || window.Height != captureHeight)
                {
                    window.Width = captureWidth;
                    window.Height = captureHeight;
                    await Task.Delay(200);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    await Task.Delay(120);
                }

                if (route.PrepareCapture is { } prepareCapture)
                {
                    prepareCapture(window);
                    await Task.Delay(140);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }

                await SettleDeferredPresentationAsync(window);
                FreezeNondeterministicPresentation(window);

                var path = Path.Combine(Program.OutputDirectory, $"{route.Name}.png");

                // A dialog is a window of its own, so it is rendered as one. It
                // is shown rather than shown modally: ShowDialog does not return
                // until the dialog closes, and nothing here would close it.
                if (route.Dialog is { } openDialog)
                {
                    var dialog = openDialog(viewModel);
                    if (route.HighContrast)
                    {
                        dialog.Classes.Add("high-contrast");
                    }

                    dialog.Show(window);
                    await Task.Delay(220);
                    Dispatcher.UIThread.RunJobs();
                    dialog.UpdateLayout();
                    await Task.Delay(120);
                    FreezeNondeterministicPresentation(dialog);
                    if (Program.IsWebsiteExport)
                    {
                        WebsiteScreenshotExport.WriteDialogFrame(
                            window,
                            dialog,
                            new PixelSize((int)dialog.Width, (int)dialog.Height),
                            path);
                    }
                    else
                    {
                        using var dialogBitmap = new RenderTargetBitmap(
                            new PixelSize((int)dialog.Width, (int)dialog.Height),
                            new Vector(96, 96));
                        dialogBitmap.Render(dialog);
                        dialogBitmap.Save(path);
                    }

                    dialog.Close();
                    Dispatcher.UIThread.RunJobs();
                    Console.WriteLine($"CAPTURE {route.Name} -> {path}");
                    continue;
                }

                if (Program.IsWebsiteExport)
                {
                    WebsiteScreenshotExport.WriteFrame(window, path);
                    Console.WriteLine($"CAPTURE {route.Name} -> {path}");
                    continue;
                }

                var bitmapSize = new PixelSize(captureWidth, captureHeight);
                var bitmapDpi = new Vector(96, 96);
                using (var bitmap = new RenderTargetBitmap(bitmapSize, bitmapDpi))
                {
                    bitmap.Render(window);
                    bitmap.Save(path);
                }

                Console.WriteLine($"CAPTURE {route.Name} -> {path}");

                // The workspace route again at Retina density: the rail and panel
                // chrome are the surfaces users judge at 2x, so pixel snapping
                // there must be reviewable at the density they actually see.
                if (!Program.IsWebsiteExport
                    && string.Equals(route.Name, "workspace", StringComparison.Ordinal))
                {
                    var retinaPath = Path.Combine(Program.OutputDirectory, "workspace-2x.png");
                    using var retina = new RenderTargetBitmap(
                        new PixelSize(route.Width * 2, route.Height * 2),
                        new Vector(192, 192));
                    retina.Render(window);
                    retina.Save(retinaPath);
                    Console.WriteLine($"CAPTURE workspace-2x -> {retinaPath}");
                }
            }

            foreach (var dialog in selectedDialogs)
            {
                var dialogTheme = dialog.Theme ?? ThemePreference.Default;
                ApplyTheme(Program.IsWebsiteExport
                    ? WebsiteScreenshotExport.NormalizeTheme(dialogTheme)
                    : dialogTheme);
                await CaptureDialogAsync(dialog.Name, dialog.Create());
            }

            ApplyTheme(ThemePreference.Default);
            if (Program.BaselineMode != DesignQaBaselineMode.None)
            {
                DesignQaBaseline.Process(
                    Program.BaselineMode,
                    Program.OutputDirectory,
                    Program.BaselinePath,
                    Program.RequestedRoutes);
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            Console.Error.WriteLine($"FAILED {ex}");
        }
        finally
        {
            viewModel.Dispose();
            desktop.Shutdown(exitCode);
        }
    }

    private static void MaterializeCurrentPresentation(
        MainWindow window,
        MainWindowViewModel viewModel)
    {
        if (viewModel.IsSettingsVisible)
        {
            InvokeMaterializer(window, "EnsureSettingsRoute");
        }

        var method = viewModel.Overlay switch
        {
            ShellOverlay.CommandPalette => "EnsureCommandPaletteOverlay",
            ShellOverlay.NewPanel => "EnsureNewPanelChooserOverlay",
            ShellOverlay.LayoutDesigner => "EnsureLayoutDesignerOverlay",
            ShellOverlay.DefinitionEditor => "EnsureDefinitionEditorOverlay",
            _ => null,
        };
        if (method is not null)
        {
            InvokeMaterializer(window, method);
        }
    }

    private static void InvokeMaterializer(MainWindow window, string methodName)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"The extracted presentation resource has no '{methodName}' materializer.");
        _ = method.Invoke(window, null);
    }

    private static async Task SettleDeferredPresentationAsync(MainWindow window)
    {
        const int maximumAttempts = 400;
        const int requiredStablePasses = 4;
        var stablePasses = 0;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            // Give background-priority collection synchronization and
            // renderer continuations a chance to materialize before deciding
            // that a route has no deferred work.
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var visibleMarkdown = window.GetVisualDescendants()
                .OfType<MarkdownPreviewView>()
                .Where(preview => preview.IsEffectivelyVisible
                    && !string.IsNullOrEmpty(preview.Text))
                .ToArray();
            var visibleCode = window.GetVisualDescendants()
                .OfType<CodePreviewView>()
                .Where(preview => preview.IsEffectivelyVisible)
                .ToArray();
            if (visibleMarkdown.All(preview => preview.IsPresentationReady)
                && visibleCode.All(preview => preview.IsPresentationReady))
            {
                stablePasses++;
                if (stablePasses >= requiredStablePasses)
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    return;
                }
            }
            else
            {
                stablePasses = 0;
            }
        }

        throw new InvalidOperationException(
            "The design QA route did not finish and settle deferred presentation within 10 seconds.");
    }

    private static void FreezeNondeterministicPresentation(Visual root)
    {
        foreach (var progress in root.GetVisualDescendants().OfType<ProgressBar>())
        {
            if (!progress.IsIndeterminate)
            {
                continue;
            }

            progress.IsIndeterminate = false;
            progress.Value = 65;
        }

        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (control.Classes.Remove("PanelAgentGlow"))
            {
                control.Opacity = 1;
            }

            if (control.Classes.Contains("AgentActivityPulse")
                || control.Classes.Contains("AgentToolbarActivityPulse"))
            {
                control.Classes.Remove("running");
                control.Opacity = 1;
            }

            if (control.Classes.Remove("PanelAgentActivity"))
            {
                control.Opacity = 1;
            }

            if (control.Classes.Contains("PanelNotificationPulse"))
            {
                control.IsVisible = false;
            }
        }

        Dispatcher.UIThread.RunJobs();
        if (root is Control layoutRoot)
        {
            layoutRoot.UpdateLayout();
        }
    }
}

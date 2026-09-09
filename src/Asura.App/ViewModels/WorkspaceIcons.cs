using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.ViewModels;

/// <summary>
/// The workspace icon catalog.
///
/// This is the single place that knows which icon identifiers a workspace may
/// store and what each one draws as. The identifier is what persists, so the
/// catalog can grow without touching stored definitions — but the mapping must
/// live in one place, or a workspace picks an icon in the editor and renders a
/// different one in the shell.
/// </summary>
internal static class WorkspaceIcons
{
    /// <summary>
    /// Keywords let the picker find an icon by what it is for rather than only
    /// by its own name — "prod" should reach the rocket, "db" the database.
    /// </summary>
    public static IReadOnlyList<WorkspaceIconOption> All { get; } =
    [
        new(WorkspaceDefinition.DefaultIcon, "Workspace", Symbol.Window, "default general"),
        new("terminal", "Terminal", Symbol.WindowConsole, "shell console command"),
        new("server", "Server", Symbol.Server, "host machine backend"),
        new("code", "Code", Symbol.Code, "develop source repo"),
        new("cloud", "Cloud", Symbol.Cloud, "remote hosted aws azure"),
        new("database", "Database", Symbol.Database, "db sql storage postgres"),
        new("folder", "Folder", Symbol.Folder, "files directory"),
        new("globe", "Globe", Symbol.Globe, "web internet public"),
        new("star", "Star", Symbol.Star, "favourite favorite"),
        new("rocket", "Rocket", Symbol.Rocket, "production launch deploy release prod"),
        new("pulse", "Monitoring", Symbol.PulseSquare, "metrics health observability uptime"),
        new("gauge", "Performance", Symbol.Gauge, "speed load benchmark"),
        new("layers", "Stack", Symbol.Layer, "tiers environment"),
        new("shield", "Security", Symbol.Shield, "safety auth hardening"),
        new("key", "Credentials", Symbol.Key, "secret vault password"),
        new("bot", "Agent", Symbol.Bot, "ai assistant automation"),
        new("flash", "Automation", Symbol.Flash, "fast job trigger"),
        new("branch", "Branch", Symbol.Branch, "git version control"),
        new("bug", "Debugging", Symbol.Bug, "issue defect triage"),
        new("beaker", "Experiments", Symbol.Beaker, "lab test staging trial"),
        new("building", "Organisation", Symbol.Building, "company office team"),
        new("people", "Team", Symbol.People, "group shared colleagues"),
        new("book", "Documentation", Symbol.Book, "docs notes reference"),
        new("bookmark", "Saved", Symbol.Bookmark, "pinned keep"),
        new("calendar", "Schedule", Symbol.Calendar, "date plan cron"),
        new("timer", "Timers", Symbol.Timer, "clock duration cron"),
        new("archive", "Archive", Symbol.Archive, "cold storage retired"),
        new("storage", "Storage", Symbol.Storage, "disk volume capacity"),
        new("box", "Containers", Symbol.Box, "docker image package"),
        new("desktop", "Local", Symbol.Desktop, "workstation machine"),
        new("grid", "Screens", Symbol.Grid, "layout panels"),
        new("home", "Home", Symbol.Home, "personal main"),
        new("heart", "Favourites", Symbol.Heart, "liked favorite"),
        new("fire", "Incidents", Symbol.Fire, "urgent outage oncall"),
        new("flag", "Milestones", Symbol.Flag, "marker release"),
        new("wrench", "Maintenance", Symbol.Wrench, "tools fix ops"),
    ];

    /// <summary>
    /// The icons the picker offers without being asked. The full catalog is a
    /// wall of tiles, and a wall is read as "choose carefully" rather than
    /// "choose"; these ten cover what workspaces are usually named after, and
    /// the rest stay one search away.
    /// </summary>
    public static IReadOnlyList<WorkspaceIconOption> Common { get; } =
        [.. new[]
        {
            WorkspaceDefinition.DefaultIcon,
            "rocket",
            "code",
            "database",
            "pulse",
            "terminal",
            "layers",
            "cloud",
            "server",
            "folder",
        }
        .Select(id => All.Single(option => string.Equals(option.Id, id, StringComparison.Ordinal)))];

    private static readonly Dictionary<string, WorkspaceIconOption> ById =
        All.ToDictionary(option => option.Id, StringComparer.Ordinal);

    /// <summary>
    /// The catalog entry behind an identifier, or the default one. Callers that
    /// only draw an icon want <see cref="SymbolFor"/>; this exists for the ones
    /// that also need its name.
    /// </summary>
    public static WorkspaceIconOption OptionFor(string? icon) =>
        icon is not null && ById.TryGetValue(icon, out var option) ? option : All[0];

    /// <summary>
    /// An unknown identifier draws the default rather than nothing, so a
    /// definition written by a newer build still renders here.
    /// </summary>
    public static Symbol SymbolFor(string? icon) =>
        icon is not null && ById.TryGetValue(icon, out var option)
            ? option.Symbol
            : Symbol.Window;

    /// <summary>The durable icon that initially identifies a runtime panel's tab.</summary>
    public static string ForPanel(PanelKind kind) => kind switch
    {
        PanelKind.Terminal => "terminal",
        PanelKind.Browser => "globe",
        PanelKind.FileViewer => "folder",
        PanelKind.Statistics => "pulse",
        PanelKind.ProcessMonitor => "gauge",
        PanelKind.DatabaseViewer => "database",
        PanelKind.Docker => "box",
        PanelKind.Git => "branch",
        PanelKind.Placeholder => WorkspaceDefinition.DefaultIcon,
        _ => WorkspaceDefinition.DefaultIcon,
    };

    public static IReadOnlyList<WorkspaceIconOption> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return All;
        }

        var terms = query.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Every term has to match something, so a second word narrows the result
        // rather than widening it.
        return [.. All.Where(option => terms.All(term => option.Matches(term)))];
    }
}

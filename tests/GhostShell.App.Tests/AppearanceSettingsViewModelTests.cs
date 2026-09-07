using System.Reflection;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class AppearanceSettingsViewModelTests
{
    [Fact]
    public async Task Automatic_saves_serialize_rapid_changes_and_use_the_latest_revision()
    {
        var original = Theme();
        using var settings = new AppearanceSettingsViewModel(Catalog(Snapshot(original, 7), out var recording));
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recording.BeforeSave = pending.Task;
        var first = settings.SaveThemeChangeAsync(Theme(showTabBar: false), CancellationToken.None);
        var latest = Theme(showWorkspacesPanel: false);
        var second = settings.SaveThemeChangeAsync(latest, CancellationToken.None);
        Assert.Equal(1, recording.SaveCount);
        pending.SetResult();

        Assert.True((await first).IsSuccess);
        Assert.True((await second).IsSuccess);
        Assert.Equal(2, recording.SaveCount);
        Assert.Equal(8, recording.ExpectedRevision);
        Assert.Equal(latest, recording.Snapshot.Themes.Single().Value);
        using var reopened = new AppearanceSettingsViewModel(Catalog(recording.Snapshot, out _));
        Assert.Equal(latest, reopened.ActiveTheme);
    }

    [Fact]
    public async Task Automatic_save_failure_preserves_saved_settings_and_allows_a_later_retry()
    {
        var original = Theme();
        using var settings = new AppearanceSettingsViewModel(Catalog(Snapshot(original, 7), out var recording));
        recording.SaveError = new DefinitionStoreError(DefinitionStoreErrorCode.StorageFailure, "Write failed.");
        var changed = Theme(showTabBar: false);
        Assert.False((await settings.SaveThemeChangeAsync(changed, CancellationToken.None)).IsSuccess);
        Assert.Equal(original, recording.Snapshot.Themes.Single().Value);
        recording.SaveError = null;
        Assert.True((await settings.SaveThemeChangeAsync(changed, CancellationToken.None)).IsSuccess);
        Assert.Equal(changed, recording.Snapshot.Themes.Single().Value);
    }
    [Fact]
    public void Theme_projects_every_shell_layout_choice()
    {
        var theme = Theme(
            showTabBar: true,
            showWorkspacesPanel: false,
            tabStripPlacement: TabStripPlacement.Right,
            workspacePanelPlacement: WorkspacePanelPlacement.Right);
        using var settings = new AppearanceSettingsViewModel(
            Catalog(Snapshot(theme, revision: 7), out _));

        Assert.Same(theme, settings.ActiveTheme);
        Assert.Equal("Dark", settings.ThemeMode);
        Assert.Equal("Gnome", settings.ThemeProfile);
        Assert.Equal("125%", settings.ThemeTextScale);
        Assert.Equal("#5A8DEE", settings.ThemeAccent);
        Assert.True(settings.ShowTabBar);
        Assert.False(settings.ShowWorkspacesPanel);
        Assert.False(settings.IsWorkspacePanelOnLeft);
        Assert.True(settings.IsWorkspacePanelOnRight);
        Assert.Equal(Avalonia.Controls.Dock.Right, settings.WorkspacePanelDock);
        Assert.False(settings.IsTabStripVisibleOnTop);
        Assert.False(settings.IsTabStripVisibleOnBottom);
        Assert.True(settings.IsTabStripVisibleOnSide);
        Assert.Equal(Avalonia.Controls.Dock.Right, settings.TabStripDock);
        Assert.False(settings.IsTabStripDockedLeft);
        Assert.True(settings.IsTabStripDockedRight);
        Assert.Equal(
            Avalonia.Controls.PlacementMode.LeftEdgeAlignedTop,
            settings.SideTabIconPickerPlacement);
    }

    [Fact]
    public async Task Save_uses_current_revision_and_preserves_stored_chrome()
    {
        var stored = Theme(
            showTabBar: false,
            showWorkspacesPanel: false,
            tabStripPlacement: TabStripPlacement.Bottom,
            workspacePanelPlacement: WorkspacePanelPlacement.Right);
        var catalog = Catalog(Snapshot(stored, revision: 41), out var recording);
        using var settings = new AppearanceSettingsViewModel(catalog);

        var result = await settings.SaveThemeAsync(
            AppearanceMode.Light,
            PlatformProfile.Kde,
            AccentPreference.FollowHost,
            textScaleOverride: 1.5,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(41, recording.ExpectedRevision);
        var saved = Assert.IsType<ThemePreference>(recording.SavedTheme);
        Assert.Equal(AppearanceMode.Light, saved.Appearance);
        Assert.Equal(PlatformProfile.Kde, saved.PlatformProfile);
        Assert.Equal(1.5, saved.TextScaleOverride);
        Assert.False(saved.ShowTabBar);
        Assert.False(saved.ShowWorkspacesPanel);
        Assert.Equal(TabStripPlacement.Bottom, saved.TabStripPlacement);
        Assert.Equal(WorkspacePanelPlacement.Right, saved.WorkspacePanelPlacement);
    }

    [Fact]
    public async Task Saving_an_identical_theme_is_a_no_op()
    {
        var theme = Theme();
        using var settings = new AppearanceSettingsViewModel(
            Catalog(Snapshot(theme, revision: 9), out var recording));

        var result = await settings.SaveThemeAsync(
            theme.Appearance,
            theme.PlatformProfile,
            theme.Accent,
            theme.TextScaleOverride,
            CancellationToken.None,
            ThemeChromePreference.From(theme));

        Assert.True(result.IsSuccess);
        Assert.Equal(9, result.Value?.Revision);
        Assert.Equal(0, recording.SaveCount);
    }

    [Fact]
    public async Task Save_propagates_catalog_revision_conflict()
    {
        var theme = Theme();
        var conflict = new DefinitionStoreError(
            DefinitionStoreErrorCode.RevisionConflict,
            "Theme changed.",
            CurrentRevision: 12);
        using var settings = new AppearanceSettingsViewModel(
            Catalog(Snapshot(theme, revision: 11), out var recording));
        recording.SaveError = conflict;

        var result = await settings.SaveThemeAsync(
            AppearanceMode.Light,
            theme.PlatformProfile,
            theme.Accent,
            theme.TextScaleOverride,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(conflict, result.Error);
        Assert.Equal(11, recording.ExpectedRevision);
    }

    [Fact]
    public async Task Workspace_panel_setter_saves_only_that_chrome_choice()
    {
        var theme = Theme(showWorkspacesPanel: true);
        using var settings = new AppearanceSettingsViewModel(
            Catalog(Snapshot(theme, revision: 15), out var recording));
        var started = 0;
        var completion = new TaskCompletionSource<DefinitionStoreError?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        settings.BackgroundSaveStarting += (_, _) => started++;
        settings.BackgroundSaveCompleted += (_, eventArgs) =>
            completion.TrySetResult(eventArgs.Error);

        settings.ShowWorkspacesPanel = false;

        Assert.Null(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, started);
        var saved = Assert.IsType<ThemePreference>(recording.SavedTheme);
        Assert.False(saved.ShowWorkspacesPanel);
        Assert.Equal(theme.ShowTabBar, saved.ShowTabBar);
        Assert.Equal(theme.TabStripPlacement, saved.TabStripPlacement);
        Assert.Equal(theme.WorkspacePanelPlacement, saved.WorkspacePanelPlacement);
    }

    [Fact]
    public void Catalog_application_publishes_all_projection_names()
    {
        using var settings = new AppearanceSettingsViewModel(
            Catalog(DefinitionCatalogSnapshot.Empty, out _));
        var changed = new HashSet<string?>(StringComparer.Ordinal);
        settings.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);

        settings.ApplyCatalog(Snapshot(Theme(), revision: 2));

        Assert.Contains(nameof(AppearanceSettingsViewModel.ActiveTheme), changed);
        Assert.Contains(nameof(AppearanceSettingsViewModel.ShowWorkspacesPanel), changed);
        Assert.Contains(nameof(AppearanceSettingsViewModel.WorkspacePanelDock), changed);
        Assert.Contains(nameof(AppearanceSettingsViewModel.TabStripDock), changed);
        Assert.Contains(nameof(AppearanceSettingsViewModel.SideTabIconPickerPlacement), changed);
    }

    private static ThemePreference Theme(
        bool showTabBar = true,
        bool showWorkspacesPanel = true,
        TabStripPlacement tabStripPlacement = TabStripPlacement.Top,
        WorkspacePanelPlacement workspacePanelPlacement = WorkspacePanelPlacement.Left) =>
        new(
            ThemePreference.Default.Id,
            ThemePreference.Default.Name,
            AppearanceMode.Dark,
            PlatformProfile.Gnome,
            AccentPreference.Custom(RgbColor.Parse("#5A8DEE")),
            textScaleOverride: 1.25,
            InterfaceDensity.Compact,
            showTabBar,
            showWorkspacesPanel,
            tabStripPlacement,
            workspacePanelPlacement,
            isTranslucent: true,
            backdropOpacityPercent: 72,
            hasGlassPanels: true,
            overridesBackdropOpacity: false);

    private static DefinitionCatalogSnapshot Snapshot(
        ThemePreference theme,
        long revision) =>
        DefinitionCatalogSnapshot.Empty with
        {
            Themes =
            [
                new StoredDefinition<ThemePreference>(
                    theme,
                    revision,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch),
            ],
        };

    private static IDefinitionCatalog Catalog(
        DefinitionCatalogSnapshot snapshot,
        out RecordingCatalogProxy recording)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingCatalogProxy>();
        recording = (RecordingCatalogProxy)(object)catalog;
        recording.Snapshot = snapshot;
        return catalog;
    }

    public class RecordingCatalogProxy : DispatchProxy
    {
        private EventHandler? _changed;
        public DefinitionCatalogSnapshot Snapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public ThemePreference? SavedTheme { get; private set; }

        public long? ExpectedRevision { get; private set; }

        public DefinitionStoreError? SaveError { get; set; }

        public int SaveCount { get; private set; }
        public Task BeforeSave { get; set; } = Task.CompletedTask;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "add_Changed")
            {
                _changed += (EventHandler)args![0]!;
                return null;
            }
            if (targetMethod?.Name == "remove_Changed")
            {
                _changed -= (EventHandler)args![0]!;
                return null;
            }
            return targetMethod?.Name switch
            {
                "get_Snapshot" => Snapshot,
                nameof(IDefinitionCatalog.SaveThemeAsync) => Save(args!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
        }

        private async ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> Save(
            object?[] args)
        {
            var cancellationToken = (CancellationToken)args[2]!;
            cancellationToken.ThrowIfCancellationRequested();
            SavedTheme = (ThemePreference)args[0]!;
            ExpectedRevision = (long?)args[1];
            SaveCount++;
            await BeforeSave.WaitAsync(cancellationToken);
            var result = SaveError is null
                ? DefinitionStoreResult<StoredDefinition<ThemePreference>>.Success(
                    new StoredDefinition<ThemePreference>(
                        SavedTheme,
                        (ExpectedRevision ?? 0) + 1,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch))
                : DefinitionStoreResult<StoredDefinition<ThemePreference>>.Failure(SaveError);
            if (result.IsSuccess)
            {
                Snapshot = Snapshot with { Themes = [result.Value!] };
                _changed?.Invoke(this, EventArgs.Empty);
            }
            return result;
        }
    }
}

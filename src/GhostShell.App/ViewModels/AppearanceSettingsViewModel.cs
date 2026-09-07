using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns the persisted theme and the shell-layout projections derived from it.
/// Applying those projections to a live workspace remains the shell host's job.
/// </summary>
public sealed class AppearanceSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private ThemePreference _activeTheme;
    private bool _disposed;
    private Task _pendingThemeSave = Task.CompletedTask;

    /// <summary>Serializes live selections and reads the revision after earlier writes finish.</summary>
    public Task<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeChangeAsync(
        ThemePreference theme, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var save = SaveThemeChangeAfterAsync(_pendingThemeSave, theme, cancellationToken);
        _pendingThemeSave = save;
        return save;
    }

    private async Task<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeChangeAfterAsync(
        Task precedingSave, ThemePreference theme, CancellationToken cancellationToken)
    {
        try
        {
            await precedingSave;
            var stored = _catalog.Snapshot.Themes.FirstOrDefault(item => item.Value.Id == theme.Id);
            return await SaveThemeAsync(theme, stored?.Revision, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DefinitionStoreResult<StoredDefinition<ThemePreference>>.Failure(new(
                DefinitionStoreErrorCode.Cancelled, "Appearance changes were not saved because saving was cancelled."));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return DefinitionStoreResult<StoredDefinition<ThemePreference>>.Failure(new(
                DefinitionStoreErrorCode.StorageFailure, "Appearance changes could not be saved. Try again."));
        }
    }

    public AppearanceSettingsViewModel(IDefinitionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _activeTheme = ResolveActiveTheme(_catalog.Snapshot);
    }

    public event EventHandler? BackgroundSaveStarting;

    public event EventHandler<AppearanceSaveCompletedEventArgs>? BackgroundSaveCompleted;

    public ThemePreference ActiveTheme => _activeTheme;

    public string ThemeMode => ActiveTheme.Appearance.ToString();

    public string ThemeProfile => ActiveTheme.PlatformProfile.ToString();

    public string ThemeTextScale => ActiveTheme.TextScaleOverride is { } textScale
        ? textScale.ToString("0.##%", System.Globalization.CultureInfo.InvariantCulture)
        : "Follow host";

    public string ThemeAccent => ActiveTheme.Accent.Kind == AccentPreferenceKind.Custom
        ? ActiveTheme.Accent.CustomColor?.ToString()
            ?? ThemePreference.BronzeFallback.ToString()
        : "Follow system accent";

    public bool ShowTabBar => ActiveTheme.ShowTabBar;

    /// <summary>
    /// Saves only the workspace-rail field while retaining every other stored
    /// chrome choice. Catalog publication updates the visible projection.
    /// </summary>
    public bool ShowWorkspacesPanel
    {
        get => ActiveTheme.ShowWorkspacesPanel;
        set
        {
            ThrowIfDisposed();
            var theme = ActiveTheme;
            if (value == theme.ShowWorkspacesPanel)
            {
                return;
            }

            BackgroundSaveStarting?.Invoke(this, EventArgs.Empty);
            _ = SaveShowWorkspacesPanelAsync(theme, value);
        }
    }

    public bool IsWorkspacePanelOnLeft =>
        ActiveTheme.WorkspacePanelPlacement == WorkspacePanelPlacement.Left;

    public bool IsWorkspacePanelOnRight => !IsWorkspacePanelOnLeft;

    public Avalonia.Controls.Dock WorkspacePanelDock => IsWorkspacePanelOnLeft
        ? Avalonia.Controls.Dock.Left
        : Avalonia.Controls.Dock.Right;

    public bool IsTabStripVisibleOnTop =>
        ShowTabBar && ActiveTheme.TabStripPlacement == TabStripPlacement.Top;

    public bool IsTabStripVisibleOnBottom =>
        ShowTabBar && ActiveTheme.TabStripPlacement == TabStripPlacement.Bottom;

    public bool IsTabStripVisibleOnSide =>
        ShowTabBar && ActiveTheme.TabStripPlacement
            is TabStripPlacement.Left or TabStripPlacement.Right;

    public Avalonia.Controls.Dock TabStripDock =>
        ActiveTheme.TabStripPlacement == TabStripPlacement.Right
            ? Avalonia.Controls.Dock.Right
            : Avalonia.Controls.Dock.Left;

    public bool IsTabStripDockedLeft =>
        IsTabStripVisibleOnSide && TabStripDock == Avalonia.Controls.Dock.Left;

    public bool IsTabStripDockedRight =>
        IsTabStripVisibleOnSide && TabStripDock == Avalonia.Controls.Dock.Right;

    public Avalonia.Controls.PlacementMode SideTabIconPickerPlacement =>
        IsTabStripDockedRight
            ? Avalonia.Controls.PlacementMode.LeftEdgeAlignedTop
            : Avalonia.Controls.PlacementMode.RightEdgeAlignedTop;

    public void ApplyCatalog(DefinitionCatalogSnapshot snapshot)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        _activeTheme = ResolveActiveTheme(snapshot);
        PublishThemeProperties();
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>>
        SaveThemeAsync(
            AppearanceMode appearance,
            PlatformProfile platformProfile,
            AccentPreference accent,
            double? textScaleOverride,
            CancellationToken cancellationToken,
            ThemeChromePreference? chrome = null)
    {
        ThrowIfDisposed();
        var stored = _catalog.Snapshot.Themes
            .FirstOrDefault(item => item.Value.Id == ThemePreference.Default.Id);
        var existing = stored?.Value ?? ThemePreference.Default;
        var effective = chrome ?? ThemeChromePreference.From(existing);
        var updated = new ThemePreference(
            ThemePreference.Default.Id,
            ThemePreference.Default.Name,
            appearance,
            platformProfile,
            accent,
            textScaleOverride,
            effective.Density,
            effective.ShowTabBar,
            effective.ShowWorkspacesPanel,
            effective.TabStripPlacement,
            effective.WorkspacePanelPlacement,
            effective.IsTranslucent,
            effective.BackdropOpacityPercent,
            effective.HasGlassPanels,
            effective.OverridesBackdropOpacity);
        return await SaveThemeAsync(updated, stored?.Revision, cancellationToken);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>>
        SaveThemeAsync(
            ThemePreference updated,
            long? expectedRevision,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(updated);
        var stored = _catalog.Snapshot.Themes
            .FirstOrDefault(item => item.Value.Id == updated.Id);
        if (stored is not null
            && stored.Revision == expectedRevision
            && stored.Value == updated)
        {
            return DefinitionStoreResult<StoredDefinition<ThemePreference>>.Success(stored);
        }

        return await _catalog.SaveThemeAsync(
            updated,
            expectedRevision,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BackgroundSaveStarting = null;
        BackgroundSaveCompleted = null;
    }

    private async Task SaveShowWorkspacesPanelAsync(
        ThemePreference theme,
        bool showWorkspacesPanel)
    {
        var result = await SaveThemeAsync(
            theme.Appearance,
            theme.PlatformProfile,
            theme.Accent,
            theme.TextScaleOverride,
            CancellationToken.None,
            ThemeChromePreference.From(theme) with
            {
                ShowWorkspacesPanel = showWorkspacesPanel,
            });
        BackgroundSaveCompleted?.Invoke(
            this,
            new AppearanceSaveCompletedEventArgs(result.Error));
    }

    private void PublishThemeProperties()
    {
        OnPropertyChanged(nameof(ActiveTheme));
        OnPropertyChanged(nameof(ThemeMode));
        OnPropertyChanged(nameof(ThemeProfile));
        OnPropertyChanged(nameof(ThemeTextScale));
        OnPropertyChanged(nameof(ThemeAccent));
        OnPropertyChanged(nameof(ShowTabBar));
        OnPropertyChanged(nameof(ShowWorkspacesPanel));
        OnPropertyChanged(nameof(IsWorkspacePanelOnLeft));
        OnPropertyChanged(nameof(IsWorkspacePanelOnRight));
        OnPropertyChanged(nameof(WorkspacePanelDock));
        OnPropertyChanged(nameof(IsTabStripVisibleOnTop));
        OnPropertyChanged(nameof(IsTabStripVisibleOnBottom));
        OnPropertyChanged(nameof(IsTabStripVisibleOnSide));
        OnPropertyChanged(nameof(TabStripDock));
        OnPropertyChanged(nameof(IsTabStripDockedLeft));
        OnPropertyChanged(nameof(IsTabStripDockedRight));
        OnPropertyChanged(nameof(SideTabIconPickerPlacement));
    }

    private static ThemePreference ResolveActiveTheme(
        DefinitionCatalogSnapshot snapshot) =>
        snapshot.Themes
            .FirstOrDefault(item => item.Value.Id == ThemePreference.Default.Id)?.Value
        ?? ThemePreference.Default;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed class AppearanceSaveCompletedEventArgs(
    DefinitionStoreError? error) : EventArgs
{
    public DefinitionStoreError? Error { get; } = error;
}

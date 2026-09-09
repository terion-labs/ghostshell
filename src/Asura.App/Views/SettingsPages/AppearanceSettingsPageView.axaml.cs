using Asura.Core;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Asura.App.Views.SettingsPages;

internal sealed record AppearanceSelection(
    AppearanceMode Appearance,
    PlatformProfile PlatformProfile,
    AccentPreference Accent,
    double? TextScale,
    InterfaceDensity Density,
    bool ShowTabBar,
    bool ShowWorkspacesPanel,
    TabStripPlacement TabStripPlacement,
    WorkspacePanelPlacement WorkspacePanelPlacement,
    bool IsTranslucent,
    int BackdropOpacityPercent,
    bool HasGlassPanels,
    bool OverridesBackdropOpacity);

internal sealed record AppearanceTextScaleOption(string DisplayName, double? Scale);

public sealed partial class AppearanceSettingsPageView : UserControl
{
    private IReadOnlyList<AppearanceTextScaleOption> _appearanceTextScaleOptions = [];

    public AppearanceSettingsPageView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised whenever a control on the page changes. The shell previews the
    /// selection and saves valid application changes automatically.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? ApplicationAppearanceChanged;

    public event EventHandler<RoutedEventArgs>? TerminalAppearanceChanged;

    public event EventHandler<RoutedEventArgs>? SelectTerminalPaletteRequested;

    /// <summary>
    /// Raised with the palette field to fill. The shell owns sampling because it
    /// owns the window the colour is read from.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? PickColorRequested;

    public event EventHandler<RoutedEventArgs>? ApplyRequested;

    public event EventHandler<RoutedEventArgs>? CancelRequested;

    public event EventHandler<RoutedEventArgs>? ResetRequested;

    public event EventHandler<RoutedEventArgs>? ImportRequested;

    public event EventHandler<RoutedEventArgs>? ExportRequested;

    public event EventHandler<RoutedEventArgs>? ResetTerminalPaletteRequested;

    public event EventHandler<RoutedEventArgs>? ApplyTerminalRequested;

    public event EventHandler<RoutedEventArgs>? CancelTerminalRequested;

    internal void SetValidationStatus(string message, bool isWarning)
    {
        AppearanceValidationStatus.Text = message;
        AppearanceValidationStatus.IsVisible = message.Length > 0;
        AppearanceValidationStatus.Classes.Set("warning", isWarning);
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) =>
        ApplyRequested?.Invoke(sender, e);

    private void OnCancelClick(object? sender, RoutedEventArgs e) =>
        CancelRequested?.Invoke(sender, e);

    private void OnResetClick(object? sender, RoutedEventArgs e) =>
        ResetRequested?.Invoke(sender, e);

    private void OnResetTerminalPaletteClick(object? sender, RoutedEventArgs e)
        => ResetTerminalPaletteRequested?.Invoke(sender, e);

    private void OnApplyTerminalClick(object? sender, RoutedEventArgs e) =>
        ApplyTerminalRequested?.Invoke(sender, e);

    private void OnCancelTerminalClick(object? sender, RoutedEventArgs e) =>
        CancelTerminalRequested?.Invoke(sender, e);

    private void OnImportClick(object? sender, RoutedEventArgs e) =>
        ImportRequested?.Invoke(sender, e);

    private void OnExportClick(object? sender, RoutedEventArgs e) =>
        ExportRequested?.Invoke(sender, e);

    internal void ResetApplicationAppearance(ThemePreference defaultTheme)
    {
        ArgumentNullException.ThrowIfNull(defaultTheme);
        ApplyAppearance(
            defaultTheme,
            _appearanceTextScaleOptions.First(option => option.Scale is null));
        ApplicationAppearanceChanged?.Invoke(this, new RoutedEventArgs());
    }

    internal void ConfigureAppearanceControls(
        IReadOnlyList<PlatformProfile> platformProfiles,
        IReadOnlyList<AppearanceTextScaleOption> textScaleOptions)
    {
        ArgumentNullException.ThrowIfNull(platformProfiles);
        ArgumentNullException.ThrowIfNull(textScaleOptions);

        _appearanceTextScaleOptions = textScaleOptions;
        PlatformProfilePicker.ItemsSource = platformProfiles;
        ApplicationTextScalePicker.ItemsSource = textScaleOptions;
    }

    internal void ApplyAppearance(
        ThemePreference theme,
        AppearanceTextScaleOption selectedTextScale)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(selectedTextScale);

        _isLoading = true;
        try
        {
            AppearanceModeSystem.IsChecked = theme.Appearance == AppearanceMode.System;
            AppearanceModeDark.IsChecked = theme.Appearance == AppearanceMode.Dark;
            AppearanceModeLight.IsChecked = theme.Appearance == AppearanceMode.Light;
            PlatformProfilePicker.SelectedItem = theme.PlatformProfile;
            SelectComboBoxItem(
                AccentModePicker,
                nameof(AccentModePicker),
                theme.Accent.Kind switch
                {
                    AccentPreferenceKind.Custom => "Custom",
                    AccentPreferenceKind.AsuraBronze => "Asura bronze",
                    _ => "Follow host",
                });

            ApplicationTextScalePicker.ItemsSource =
                _appearanceTextScaleOptions.Contains(selectedTextScale)
                    ? _appearanceTextScaleOptions
                    : [.. _appearanceTextScaleOptions, selectedTextScale];
            ApplicationTextScalePicker.SelectedItem = selectedTextScale;
            CustomAccentText.Text = theme.Accent.CustomColor?.ToString()
                ?? ThemePreference.BronzeFallback.ToString();
            UpdateCustomAccentAvailability();

            // A null override means "follow the platform profile"; the slider has no
            // null, so it rests at the profile's own radius until the user moves it.
            TranslucencyToggle.IsChecked = theme.IsTranslucent;
            GlassPanelsToggle.IsChecked = theme.HasGlassPanels;
            OverrideOpacityToggle.IsChecked = theme.OverridesBackdropOpacity;
            BackdropOpacitySlider.Value = theme.BackdropOpacityPercent;
            DensityCompact.IsChecked = theme.Density == InterfaceDensity.Compact;
            DensityCozy.IsChecked = theme.Density == InterfaceDensity.Cozy;
            DensityComfortable.IsChecked = theme.Density == InterfaceDensity.Comfortable;
            ShowTabBarSwitch.IsChecked = theme.ShowTabBar;
            ShowWorkspacesPanelSwitch.IsChecked = theme.ShowWorkspacesPanel;
            TabPlacementTop.IsChecked = theme.TabStripPlacement == TabStripPlacement.Top;
            TabPlacementBottom.IsChecked = theme.TabStripPlacement == TabStripPlacement.Bottom;
            // All four, or a side placement came back from disk with no tile
            // checked — and the next save fell through the empty group to Top.
            TabPlacementLeft.IsChecked = theme.TabStripPlacement == TabStripPlacement.Left;
            TabPlacementRight.IsChecked = theme.TabStripPlacement == TabStripPlacement.Right;
            WorkspacePanelLeft.IsChecked =
                theme.WorkspacePanelPlacement == WorkspacePanelPlacement.Left;
            WorkspacePanelRight.IsChecked =
                theme.WorkspacePanelPlacement == WorkspacePanelPlacement.Right;
        }
        finally
        {
            _isLoading = false;
        }

        RefreshProfileDepartureMarker();
    }

    internal AppearanceSelection CaptureAppearance()
    {
        var appearance = AppearanceModeDark.IsChecked == true
            ? AppearanceMode.Dark
            : AppearanceModeLight.IsChecked == true
                ? AppearanceMode.Light
                : AppearanceMode.System;
        var profile = PlatformProfilePicker.SelectedItem is PlatformProfile selectedProfile
            ? selectedProfile
            : throw new InvalidOperationException(
                "The platform-profile selection is unavailable.");
        var accent = SelectedText(AccentModePicker, nameof(AccentModePicker)) switch
        {
            "Custom" => AccentPreference.Custom(
                RgbColor.Parse(CustomAccentText.Text ?? "#B8793A")),
            "Asura bronze" => AccentPreference.AsuraBronze,
            _ => AccentPreference.FollowHost,
        };
        var textScale =
            ApplicationTextScalePicker.SelectedItem is AppearanceTextScaleOption selectedTextScale
                ? selectedTextScale.Scale
                : throw new InvalidOperationException(
                    "The application text-scale selection is unavailable.");

        return new(
            appearance,
            profile,
            accent,
            textScale,
            SelectedDensity(),
            ShowTabBarSwitch.IsChecked == true,
            ShowWorkspacesPanelSwitch.IsChecked == true,
            SelectedTabStripPlacement(),
            WorkspacePanelRight.IsChecked == true
                ? WorkspacePanelPlacement.Right
                : WorkspacePanelPlacement.Left,
            TranslucencyToggle.IsChecked == true,
            (int)Math.Round(BackdropOpacitySlider.Value),
            GlassPanelsToggle.IsChecked == true,
            OverrideOpacityToggle.IsChecked == true);
    }

    private TabStripPlacement SelectedTabStripPlacement() =>
        TabPlacementBottom.IsChecked == true
            ? TabStripPlacement.Bottom
            : TabPlacementLeft.IsChecked == true
                ? TabStripPlacement.Left
                : TabPlacementRight.IsChecked == true
                    ? TabStripPlacement.Right
                    : TabStripPlacement.Top;

    /// <summary>
    /// A platform profile is a preset, not just a set of metrics.
    ///
    /// Picking one says which desktop the shell is dressing as, and those
    /// desktops differ in more than their radii: the older one is tight and
    /// solid, the current one roomy and made of glass. Leaving density and
    /// translucency where they were let the shell claim one and look like the
    /// other.
    ///
    /// Only the two that name a specific desktop. Automatic follows the host
    /// and has no opinion of its own to apply.
    /// </summary>
    private void OnPlatformProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Only when someone picked it. Loading the stored theme sets this
        // selection too, and applying the preset there would put the preset's
        // density and translucency back every time anything at all was saved —
        // so a preset stopped being a starting point and started being a lock
        // on two settings that are still meant to be settings.
        if (_isLoading)
        {
            OnAppearanceChanged(sender, e);
            return;
        }

        if (PlatformProfilePicker.SelectedItem is PlatformProfile picked
            && PlatformProfileDefaults.For(picked) is { } defaults)
        {
            ApplyDensity(defaults.Density);
            TranslucencyToggle.IsChecked = defaults.IsTranslucent;
        }

        OnAppearanceChanged(sender, e);
    }

    private void ApplyDensity(InterfaceDensity density)
    {
        DensityCompact.IsChecked = density == InterfaceDensity.Compact;
        DensityCozy.IsChecked = density == InterfaceDensity.Cozy;
        DensityComfortable.IsChecked = density == InterfaceDensity.Comfortable;
    }

    private InterfaceDensity SelectedDensity() =>
        DensityCompact.IsChecked == true
            ? InterfaceDensity.Compact
            : DensityComfortable.IsChecked == true
                ? InterfaceDensity.Comfortable
                : InterfaceDensity.Cozy;

    /// <summary>
    /// The segmented control is a set of toggle buttons, so selecting one has to
    /// clear the others; a checked button clicked again stays checked rather than
    /// leaving the group with no answer.
    /// </summary>
    private void OnDensityClick(object? sender, RoutedEventArgs e)
    {
        foreach (var option in new[] { DensityCompact, DensityCozy, DensityComfortable })
        {
            option.IsChecked = ReferenceEquals(option, sender);
        }

        // The segments deliberately do not wire IsCheckedChanged — this handler
        // reassigns every segment's checked state and would echo one click as
        // three change events. It must therefore report the change itself, or
        // the density picker saves nothing and the setting is a dead control.
        OnAppearanceChanged(sender, e);
    }

    /// <summary>
    /// Loading stored values sets the same controls this page listens to, so the
    /// reload is fenced; without it every save would echo back as a fresh change.
    /// </summary>
    private bool _isLoading;

    private void OnAppearanceChanged(object? sender, RoutedEventArgs e)
    {
        RefreshProfileDepartureMarker();
        if (_isLoading)
        {
            return;
        }

        ApplicationAppearanceChanged?.Invoke(sender, e);
    }

    private void OnTerminalAppearanceChanged(object? sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            TerminalAppearanceChanged?.Invoke(sender, e);
        }
    }

    /// <summary>
    /// Says when the shell has been taken away from what its profile arrived
    /// with. A preset that can be departed from silently is one nobody can
    /// tell they have departed from.
    /// </summary>
    private void RefreshProfileDepartureMarker() =>
        ProfileDepartureMarker.IsVisible =
            PlatformProfilePicker.SelectedItem is PlatformProfile profile
            && PlatformProfileDefaults.IsDepartedFrom(
                profile,
                SelectedDensity(),
                TranslucencyToggle.IsChecked == true);

    /// <summary>
    /// Changing the accent source both re-enables the custom colour and is itself
    /// a change to preview — switching from "Follow host" to the bronze accent
    /// updates the draft like every other control on this page.
    /// </summary>
    private void OnAccentModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateCustomAccentAvailability();
        OnAppearanceChanged(sender, e);
    }

    /// <summary>
    /// The colour picker reports its own event type, so it needs a matching
    /// signature to reach the same terminal-preview path as every other control.
    /// </summary>
    private void OnColorChanged(object? sender, ColorChangedEventArgs e)
    {
        _ = e;
        OnTerminalAppearanceChanged(sender, new RoutedEventArgs());
    }

    /// <summary>Enter commits a typed value without waiting for focus to move.</summary>
    private void OnCommitKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Return)
        {
            e.Handled = true;
            if (ReferenceEquals(sender, CustomAccentText))
            {
                OnAppearanceChanged(sender, new RoutedEventArgs());
            }
            else
            {
                OnTerminalAppearanceChanged(sender, new RoutedEventArgs());
            }
        }
    }

    private void OnSelectTerminalPaletteClick(object? sender, RoutedEventArgs e) =>
        SelectTerminalPaletteRequested?.Invoke(sender, e);

    private void OnPickColorClick(object? sender, RoutedEventArgs e) =>
        PickColorRequested?.Invoke(sender, e);

    private void UpdateCustomAccentAvailability()
    {
        var isCustom = string.Equals(SelectedTextOrDefault(AccentModePicker), "Custom", StringComparison.Ordinal);
        CustomAccentText.IsEnabled = isCustom;
        CustomAccentPicker.IsEnabled = isCustom;
        CustomAccentEyedropper.IsEnabled = isCustom;
    }

    /// <summary>
    /// Writes a colour into the accent field from outside, used by the screen
    /// eyedropper. The picker and the hex box are kept in step.
    /// </summary>
    internal void SetCustomAccent(Avalonia.Media.Color color)
    {
        CustomAccentText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SyncCustomAccentPicker();
        ApplicationAppearanceChanged?.Invoke(this, new RoutedEventArgs());
    }

    /// <summary>
    /// The hex box is the stored value; the picker mirrors it. Both edit the same
    /// accent, so each has to follow the other without looping.
    /// </summary>
    private bool _syncingCustomAccent;

    private void SyncCustomAccentPicker()
    {
        if (!Avalonia.Media.Color.TryParse(CustomAccentText.Text, out var color))
        {
            return;
        }

        _syncingCustomAccent = true;
        try
        {
            CustomAccentPicker.Color = color;
        }
        finally
        {
            _syncingCustomAccent = false;
        }
    }

    private void OnCustomAccentTextChanged(object? sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_syncingCustomAccent)
        {
            SyncCustomAccentPicker();
        }
    }

    private void OnCustomAccentPicked(object? sender, ColorChangedEventArgs e)
    {
        if (_syncingCustomAccent || _isLoading)
        {
            return;
        }

        _syncingCustomAccent = true;
        try
        {
            CustomAccentText.Text = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
        }
        finally
        {
            _syncingCustomAccent = false;
        }

        OnAppearanceChanged(sender, new RoutedEventArgs());
    }

    private static string SelectedText(ComboBox comboBox, string controlName) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
        ?? throw new InvalidOperationException(
            $"The {controlName} selection is unavailable.");

    private static string? SelectedTextOrDefault(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

    private static void SelectComboBoxItem(
        ComboBox comboBox,
        string controlName,
        string content)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Content?.ToString(),
                content,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The {controlName} control has no '{content}' option.");
    }
}

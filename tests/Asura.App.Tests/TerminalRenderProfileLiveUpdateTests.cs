using System.Xml.Linq;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

/// <summary>
/// A terminal panel captured its render profile when it launched, so saving a new
/// font size changed the stored definition and nothing on screen: every open panel
/// kept rendering at the size it started with until it was closed and reopened.
/// </summary>
public sealed class TerminalRenderProfileLiveUpdateTests
{
    private static TerminalProfile Profile(double fontSize) => new(
        new TerminalProfileId("builtin.terminal.default"),
        "Default terminal",
        "JetBrains Mono",
        fontSize,
        1.4,
        TerminalCursorStyle.Block,
        cursorBlink: true,
        100_000,
        TerminalPalette.AsuraDark,
        BuiltInKeymaps.MacOsTerminalId);

    [Fact]
    public void A_render_profile_change_is_observable_on_the_panel()
    {
        var snapshot = TerminalRenderProfileSnapshot.FromProfile(Profile(14));
        var changed = new List<string?>();
        var panel = new ProbePanel();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        panel.RenderProfile = snapshot;

        Assert.Contains(nameof(ProbePanel.RenderProfile), changed, StringComparer.Ordinal);
        Assert.Equal(snapshot, panel.RenderProfile);
    }

    [Fact]
    public void Setting_the_same_profile_again_raises_nothing()
    {
        var snapshot = TerminalRenderProfileSnapshot.FromProfile(Profile(14));
        var panel = new ProbePanel { RenderProfile = snapshot };
        var changed = 0;
        panel.PropertyChanged += (_, _) => changed++;

        panel.RenderProfile = TerminalRenderProfileSnapshot.FromProfile(Profile(14));

        Assert.Equal(0, changed);
    }

    [Fact]
    public void A_different_font_size_renders_differently()
    {
        Assert.False(
            TerminalRenderProfileSnapshot.FromProfile(Profile(14))
                .RendersSameAs(TerminalRenderProfileSnapshot.FromProfile(Profile(15))));
    }

    /// <summary>
    /// Record equality cannot answer this: the palette holds its ANSI colours in
    /// an array, so two palettes built from identical colours are never equal and
    /// every catalog refresh would look like a typography change.
    /// </summary>
    [Fact]
    public void Two_snapshots_of_the_same_profile_render_the_same()
    {
        var first = TerminalRenderProfileSnapshot.FromProfile(Profile(14));
        var second = TerminalRenderProfileSnapshot.FromProfile(Profile(14));

        Assert.NotEqual(first, second);
        Assert.True(first.RendersSameAs(second));
    }

    /// <summary>
    /// The panel's live profile has to reach the renderer through its own binding.
    /// Routed through the session request instead, a font change would restart the
    /// session and take the scrollback with it.
    /// </summary>
    [Fact]
    public void The_terminal_view_binds_the_live_render_profile()
    {
        var view = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "RuntimePanels",
            "TerminalRuntimePanelView.axaml"));

        var host = Assert.Single(
            view.Descendants(),
            element => string.Equals(element.Name.LocalName, "TerminalPresentationHost", StringComparison.Ordinal));

        Assert.Equal(
            "{CompiledBinding RenderProfile}",
            (string?)host.Attribute("RenderProfile"));
    }

    [Fact]
    public void Quick_terminal_binds_the_live_render_profile_separately_from_its_session_request()
    {
        var view = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "QuickTerminalWindow.axaml"));
        var host = Assert.Single(
            view.Descendants(),
            element => string.Equals(
                element.Name.LocalName,
                "TerminalPresentationHost",
                StringComparison.Ordinal));

        Assert.Equal("{Binding RenderProfile}", (string?)host.Attribute("RenderProfile"));
        Assert.Equal("{Binding TerminalRequest}", (string?)host.Attribute("SessionRequest"));
    }

    [Fact]
    public void Appearance_preview_keeps_theme_and_terminal_drafts_independent()
    {
        var preview = new AppearancePreviewCoordinator();
        var renderProfile = TerminalRenderProfileSnapshot.FromProfile(Profile(16));
        var acquisition = preview.TryAcquire("window-1", 4, 9);
        Assert.NotNull(acquisition.Lease);
        using var lease = acquisition.Lease;

        Assert.True(lease.PreviewTerminal(renderProfile));

        Assert.Null(preview.Current.Theme);
        Assert.Same(renderProfile, preview.Current.TerminalRenderProfile);

        Assert.True(lease.PreviewTheme(ThemePreference.Default));

        Assert.Same(renderProfile, preview.Current.TerminalRenderProfile);
        Assert.Equal(ThemePreference.Default, preview.Current.Theme);

        Assert.True(lease.ClearTheme());
        Assert.True(lease.ClearTerminal());

        Assert.True(preview.Current.IsEmpty);
    }

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the Asura repository root.");
    }

    /// <summary>
    /// The observable half of the panel, without the session machinery a real one
    /// needs to construct.
    /// </summary>
    private sealed class ProbePanel : ObservableObject
    {
        private TerminalRenderProfileSnapshot? _renderProfile;

        public TerminalRenderProfileSnapshot? RenderProfile
        {
            get => _renderProfile;
            set
            {
                if (value is null || value.RendersSameAs(_renderProfile))
                {
                    return;
                }

                _renderProfile = value;
                OnPropertyChanged();
            }
        }
    }
}

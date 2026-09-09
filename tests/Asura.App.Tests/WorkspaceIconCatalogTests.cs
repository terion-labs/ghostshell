using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App.Tests;

/// <summary>
/// The icon identifier is what a workspace stores, so the catalog is a
/// persistence contract as much as a picker: an identifier that stops resolving
/// silently changes what an existing workspace draws.
/// </summary>
public sealed class WorkspaceIconCatalogTests
{
    [Fact]
    public void Every_catalog_identifier_is_storable_and_unique()
    {
        var icons = WorkspaceIcons.All;

        Assert.All(icons, icon => Assert.True(
            WorkspaceDefinition.IsValidIcon(icon.Id),
            $"'{icon.Id}' cannot be stored on a workspace definition."));
        Assert.Equal(
            icons.Count,
            icons.Select(icon => icon.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_default_icon_is_in_the_catalog_and_resolves()
    {
        Assert.Contains(WorkspaceIcons.All, icon => string.Equals(icon.Id, WorkspaceDefinition.DefaultIcon, StringComparison.Ordinal));
        Assert.Equal(
            WorkspaceIcons.SymbolFor(WorkspaceDefinition.DefaultIcon),
            WorkspaceIcons.All.Single(icon => string.Equals(icon.Id, WorkspaceDefinition.DefaultIcon, StringComparison.Ordinal)).Symbol);
    }

    /// <summary>
    /// A workspace saved by a newer build can name an icon this build has never
    /// heard of. Drawing nothing would read as a rendering fault, so it falls
    /// back to the default glyph.
    /// </summary>
    [Fact]
    public void An_unknown_identifier_falls_back_instead_of_failing()
    {
        var fallback = WorkspaceIcons.SymbolFor("not-a-real-icon");

        Assert.Equal(WorkspaceIcons.SymbolFor(WorkspaceDefinition.DefaultIcon), fallback);
        Assert.Equal(fallback, WorkspaceIcons.SymbolFor(null));
    }

    [Theory]
    [InlineData("rocket", "rocket")]
    [InlineData("prod", "rocket")]
    [InlineData("db", "database")]
    [InlineData("Docker", "box")]
    [InlineData("Git", "branch")]
    public void Search_finds_an_icon_by_name_or_purpose(string query, string expectedId)
    {
        Assert.Contains(WorkspaceIcons.Search(query), icon => string.Equals(icon.Id, expectedId, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_search_term_must_match_so_a_second_word_narrows()
    {
        var broad = WorkspaceIcons.Search("storage");
        var narrowed = WorkspaceIcons.Search("storage disk");

        Assert.True(narrowed.Count <= broad.Count);
        Assert.All(narrowed, icon => Assert.Contains(broad, candidate => string.Equals(candidate.Id, icon.Id, StringComparison.Ordinal)));
    }

    [Fact]
    public void An_empty_search_shows_the_whole_catalog()
    {
        Assert.Equal(WorkspaceIcons.All.Count, WorkspaceIcons.Search("   ").Count);
        Assert.Equal(WorkspaceIcons.All.Count, WorkspaceIcons.Search(null).Count);
    }

    [Fact]
    public void A_search_that_matches_nothing_returns_nothing_rather_than_everything()
    {
        Assert.Empty(WorkspaceIcons.Search("zzzznotanicon"));
    }

    /// <summary>
    /// The presets are an identity palette: two that read alike at tab size would
    /// make two workspaces indistinguishable.
    /// </summary>
    [Fact]
    public void Accent_presets_are_distinct_named_colours()
    {
        var accents = WorkspaceAccents.All;

        Assert.Equal(
            accents.Count,
            accents.Select(accent => accent.Hex).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(accents, accent =>
        {
            Assert.False(string.IsNullOrWhiteSpace(accent.Name));
            Assert.True(
                RgbColor.TryParse(accent.Hex, out _),
                $"'{accent.Hex}' is not a colour.");
        });
    }
}

/// <summary>
/// The counts these views bind are exposed as properties precisely because a
/// <c>Count</c> binding over their array-backed collections renders empty with
/// no error. These assert the properties exist and stay truthful.
/// </summary>
public sealed class PublishedCountTests
{
    [Fact]
    public void The_layout_designer_publishes_a_panel_count_that_tracks_its_slots()
    {
        var editor = LayoutDesignerViewModel.CreateNew();
        var initial = editor.PanelCount;

        Assert.Equal(editor.Slots.Count, initial);

        var added = editor.AddSlot();

        // The point is that the published count never diverges from the slots it
        // reports, whether or not the grid had room for another panel.
        Assert.Equal(editor.Slots.Count, editor.PanelCount);
        Assert.Equal(added.IsSuccess ? initial + 1 : initial, editor.PanelCount);
    }
}

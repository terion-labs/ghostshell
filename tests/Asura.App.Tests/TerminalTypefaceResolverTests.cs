using Asura.App.Controls;
using Avalonia.Media;

namespace Asura.App.Tests;

public sealed class TerminalTypefaceResolverTests
{
    [Fact]
    public void Exact_installed_family_is_used_only_when_fixed_pitch()
    {
        var resolved = TerminalTypefaceResolver.SelectInstalledFamily(
            ["Custom Mono"],
            ["Menlo", "Custom Mono"],
            family => family is "Menlo" or "Custom Mono");

        var rejected = TerminalTypefaceResolver.SelectInstalledFamily(
            ["Custom Mono"],
            ["Menlo", "Custom Mono"],
            family => string.Equals(family, "Menlo", StringComparison.Ordinal));

        Assert.Equal("Custom Mono", resolved);
        Assert.Null(rejected);
    }

    [Fact]
    public void Requested_family_order_is_preserved()
    {
        var resolved = TerminalTypefaceResolver.SelectInstalledFamily(
            ["Second Mono", "First Mono"],
            ["First Mono", "Second Mono"],
            _ => true);

        Assert.Equal("Second Mono", resolved);
    }

    [Fact]
    public void Proportional_platform_candidate_is_skipped()
    {
        var resolved = TerminalTypefaceResolver.SelectInstalledFamily(
            ["Inter", "Cascadia Mono", "Consolas"],
            ["Inter", "Cascadia Mono", "Consolas"],
            family => string.Equals(family, "Consolas", StringComparison.Ordinal));

        Assert.Equal("Consolas", resolved);
    }

    [Fact]
    public void Unrequested_system_family_is_not_used_as_an_implicit_fallback()
    {
        var resolved = TerminalTypefaceResolver.SelectInstalledFamily(
            [],
            ["Ubuntu", "Zeta Mono", "Alpha Sans"],
            family => string.Equals(family, "Zeta Mono", StringComparison.Ordinal));

        Assert.Null(resolved);
    }

    [Fact]
    public void Comma_separated_family_list_is_normalized_in_order()
    {
        var resolved = TerminalTypefaceResolver.NormalizeRequestedFamilies(
            " JetBrains Mono, Menlo, JetBrains Mono, Consolas ");

        Assert.Equal(["JetBrains Mono", "Menlo", "Consolas"], resolved);
    }

    [Fact]
    public void Family_matching_is_case_insensitive_and_preserves_installed_name()
    {
        var resolved = TerminalTypefaceResolver.SelectInstalledFamily(
            ["menlo"],
            ["Menlo"],
            _ => true);

        Assert.Equal("Menlo", resolved);
    }

    [Fact]
    public void No_fixed_pitch_family_returns_no_selection()
    {
        var resolved = TerminalTypefaceResolver.SelectInstalledFamily(
            ["Inter"],
            ["Inter", "Arial"],
            _ => false);

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_tolerates_a_process_without_a_platform_font_manager()
    {
        var typeface = TerminalTypefaceResolver.Resolve(
            "JetBrains Mono",
            FontStyle.Italic,
            FontWeight.Bold);

        Assert.NotNull(typeface.FontFamily);
        Assert.Equal(AsuraTerminalFontCollection.FamilyName, typeface.FontFamily.Name);
        Assert.NotNull(typeface.FontFamily.Key);
        Assert.Equal(
            AsuraTerminalFontCollection.CollectionKey,
            typeface.FontFamily.Key.Source);
        Assert.Equal(FontStyle.Italic, typeface.Style);
        Assert.Equal(FontWeight.Bold, typeface.Weight);
    }
}

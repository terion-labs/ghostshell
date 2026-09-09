using Asura.Application.Previews;
using FluentIcons.Common;

namespace Asura.App.Tests;

/// <summary>
/// The Application layer names a symbol; the panel draws it. A name the icon
/// set does not have would fail silently at render time, so it is checked here
/// where the two layers meet.
/// </summary>
public sealed class BinaryPreviewSymbolTests
{
    [Theory]
    [InlineData("track.mp3")]
    [InlineData("clip.mov")]
    [InlineData("Inter.woff2")]
    [InlineData("libghost.dylib")]
    [InlineData("ubuntu.iso")]
    [InlineData("objects.pack")]
    [InlineData("poster.psd")]
    [InlineData("budget.xlsx")]
    [InlineData("payload")]
    [InlineData("mystery.qqq")]
    public void Every_named_symbol_is_one_the_icon_set_has(string fileName)
    {
        var format = BinaryFormats.Describe(fileName, "application/octet-stream");

        Assert.True(
            Enum.TryParse<Symbol>(format.Symbol, out _),
            $"'{format.Symbol}' is not a symbol the icon set defines.");
    }

    [Fact]
    public void A_format_is_named_after_its_extension()
    {
        var format = BinaryFormats.Describe("track.flac", "application/octet-stream");

        Assert.Equal("FLAC audio", format.Name);
        Assert.Equal("Audio file", format.Detail);
    }

    [Fact]
    public void An_unknown_binary_says_so_rather_than_inventing_a_format()
    {
        var format = BinaryFormats.Describe("payload", "application/octet-stream");

        Assert.Equal("Binary file", format.Name);
        Assert.Equal("No preview for this format", format.Detail);
    }

    [Fact]
    public void A_media_type_worth_showing_is_shown()
    {
        var format = BinaryFormats.Describe("thing.xyz", "application/vnd.acme.thing");

        Assert.Equal("application/vnd.acme.thing", format.Detail);
    }
}

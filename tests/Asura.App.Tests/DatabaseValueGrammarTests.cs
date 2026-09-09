using Asura.App.ViewModels;
using Asura.Application;

namespace Asura.App.Tests;

/// <summary>
/// Grammar detection for database values: the declared kind is believed
/// outright, plain text is sniffed only when its shape is unambiguous, and
/// everything else stays uncoloured.
/// </summary>
public sealed class DatabaseValueGrammarTests
{
    [Fact]
    public void A_json_column_is_json_no_matter_what_it_holds()
    {
        Assert.Equal(
            ".json",
            DatabaseValueGrammar.DetectExtension(DatabaseValueKind.Json, "not even json"));
        Assert.Equal(
            ".json",
            DatabaseValueGrammar.DetectExtension(DatabaseValueKind.Json, null));
    }

    [Theory]
    [InlineData("""{"name": "Ada", "scores": [1, 2, 3]}""", ".json")]
    [InlineData("""[{"id": 1}, {"id": 2}]""", ".json")]
    [InlineData("<div id=\"intro\"><p>Hello</p></div>", ".html")]
    [InlineData("<!DOCTYPE html><html><body/></html>", ".html")]
    [InlineData("<config><item value=\"1\"/></config>", ".xml")]
    public void Unambiguous_text_shapes_are_recognized(string text, string expected)
    {
        Assert.Equal(
            expected,
            DatabaseValueGrammar.DetectExtension(DatabaseValueKind.Text, text));
    }

    [Theory]
    [InlineData("plain prose about braces { like this }")]
    [InlineData("{not json at all")]
    [InlineData("")]
    [InlineData(null)]
    public void Ambiguous_or_plain_text_stays_uncoloured(string? text)
    {
        Assert.Null(DatabaseValueGrammar.DetectExtension(DatabaseValueKind.Text, text));
    }

    /// <summary>Numbers, dates, and friends never reach the sniffer.</summary>
    [Theory]
    [InlineData(DatabaseValueKind.SignedInteger)]
    [InlineData(DatabaseValueKind.Timestamp)]
    [InlineData(DatabaseValueKind.Boolean)]
    [InlineData(DatabaseValueKind.Guid)]
    public void Compact_kinds_are_never_sniffed(DatabaseValueKind kind)
    {
        Assert.Null(DatabaseValueGrammar.DetectExtension(kind, """{"a": 1}"""));
    }

    [Fact]
    public void Oversized_values_are_not_parsed_for_colour()
    {
        var huge = "{" + new string(' ', 300 * 1024) + "}";
        Assert.Null(DatabaseValueGrammar.DetectExtension(DatabaseValueKind.Text, huge));
    }
}

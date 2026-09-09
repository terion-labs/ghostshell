using System.Globalization;
using Asura.App.Converters;
using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class EnumDisplayConverterTests
{
    private static string Convert(object? value) =>
        (string?)EnumDisplayConverter.Instance.Convert(
            value,
            typeof(string),
            null,
            CultureInfo.InvariantCulture)
        ?? string.Empty;

    [Theory]
    [InlineData(TerminalLinkPolicy.ConfirmBeforeOpen, "Confirm before open")]
    [InlineData(TerminalCursorStyle.Block, "Block")]
    [InlineData(TerminalShellIntegrationMode.Detect, "Detect")]
    [InlineData(TerminalBellMode.Visual, "Visual")]
    public void Enum_names_read_as_prose(object value, string expected) =>
        Assert.Equal(expected, Convert(value));

    [Theory]
    [InlineData(AiProviderKind.OpenAi, "OpenAI")]
    [InlineData(AiProviderKind.OpenAiCompatible, "OpenAI-compatible")]
    [InlineData(AiProviderKind.Anthropic, "Anthropic")]
    public void Vendor_names_keep_their_own_spelling(AiProviderKind value, string expected) =>
        Assert.Equal(expected, Convert(value));

    [Fact]
    public void Non_enum_values_pass_through_unchanged() =>
        Assert.Equal("macOS Native", Convert("macOS Native"));

    [Fact]
    public void Null_converts_to_null() =>
        Assert.Null(EnumDisplayConverter.Instance.Convert(
            null,
            typeof(string),
            null,
            CultureInfo.InvariantCulture));

    [Fact]
    public void Converting_back_is_not_supported() =>
        Assert.Throws<NotSupportedException>(() => EnumDisplayConverter.Instance.ConvertBack(
            "Block",
            typeof(TerminalCursorStyle),
            null,
            CultureInfo.InvariantCulture));
}

public sealed class KeySequenceDisplayTests
{
    [Theory]
    [InlineData("ARROWLEFT", "←")]
    [InlineData("ARROWRIGHT", "→")]
    [InlineData("PAGEUP", "Page Up")]
    [InlineData("ESCAPE", "Esc")]
    [InlineData("X", "X")]
    [InlineData("F12", "F12")]
    public void Stored_key_names_render_for_people(string key, string expected) =>
        Assert.Equal(expected, KeySequenceDisplay.Format(new KeyStroke(key)));

    [Fact]
    public void Modifiers_precede_the_key()
    {
        var stroke = new KeyStroke("B", KeyModifiers.Control);
        Assert.Equal("Ctrl+B", KeySequenceDisplay.Format(stroke));
    }

    [Fact]
    public void A_sequence_separates_its_strokes()
    {
        var sequence = new KeySequence(
        [
            new KeyStroke("B", KeyModifiers.Control),
            new KeyStroke("ARROWLEFT"),
        ]);

        Assert.Equal("Ctrl+B, ←", KeySequenceDisplay.Format(sequence));
    }

    [Fact]
    public void Formatting_never_changes_the_stored_comparison_form()
    {
        var stroke = new KeyStroke("arrowleft");
        Assert.Equal("ARROWLEFT", stroke.Key);
        Assert.Equal("←", KeySequenceDisplay.Format(stroke));
    }
}

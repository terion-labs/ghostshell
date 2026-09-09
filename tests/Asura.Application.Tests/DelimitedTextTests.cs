using Asura.Application.Previews;

namespace Asura.Application.Tests;

/// <summary>
/// Separated values as the format is actually written, not as the naive split
/// would have it: a quoted field may hold the separator, a line break, and
/// quotes of its own.
/// </summary>
public sealed class DelimitedTextTests
{
    [Fact]
    public void Plain_rows_split_on_the_separator()
    {
        var rows = DelimitedText.Parse("a,b\n1,2\n", ',', 10);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b"], rows[0]);
        Assert.Equal(["1", "2"], rows[1]);
    }

    [Fact]
    public void A_quoted_field_keeps_its_separators_and_breaks()
    {
        var rows = DelimitedText.Parse("name,note\nada,\"first, and\nsecond\"\n", ',', 10);

        Assert.Equal(["ada", "first, and\nsecond"], rows[1]);
    }

    [Fact]
    public void A_doubled_quote_is_one_quote()
    {
        var rows = DelimitedText.Parse("value\n\"she said \"\"hi\"\"\"\n", ',', 10);

        Assert.Equal("she said \"hi\"", rows[1][0]);
    }

    [Fact]
    public void Carriage_returns_do_not_become_content()
    {
        var rows = DelimitedText.Parse("a,b\r\n1,2\r\n", ',', 10);

        Assert.Equal(["1", "2"], rows[1]);
    }

    [Fact]
    public void A_last_line_without_a_break_is_still_a_row()
    {
        var rows = DelimitedText.Parse("a,b\n1,2", ',', 10);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Empty_fields_are_kept_so_columns_stay_aligned()
    {
        var rows = DelimitedText.Parse("a,,c\n", ',', 10);

        Assert.Equal(["a", string.Empty, "c"], rows[0]);
    }

    [Fact]
    public void Reading_stops_at_the_row_limit()
    {
        var rows = DelimitedText.Parse(string.Concat(Enumerable.Repeat("x\n", 100)), ',', 5);

        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void Nothing_in_is_no_rows_out() =>
        Assert.Empty(DelimitedText.Parse(string.Empty, ',', 10));

    [Fact]
    public void Tabs_separate_a_tab_separated_file()
    {
        var rows = DelimitedText.Parse("a\tb\n", '\t', 10);

        Assert.Equal(["a", "b"], rows[0]);
    }
}

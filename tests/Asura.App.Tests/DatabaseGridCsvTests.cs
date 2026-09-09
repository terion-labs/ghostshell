using Asura.App.ViewModels;

namespace Asura.App.Tests;

public sealed class DatabaseGridCsvTests
{
    [Fact]
    public void Staging_budget_bounds_the_complete_table_width()
    {
        DatabaseGridCsv.ValidateStagingSize(rowCount: 100, tableColumnCount: 1000);

        var error = Assert.Throws<InvalidDataException>(() =>
            DatabaseGridCsv.ValidateStagingSize(rowCount: 101, tableColumnCount: 1000));

        Assert.Contains("100,000 staged cells", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_quoted_multiline_unicode_and_empty_fields()
    {
        var document = DatabaseGridCsv.Parse(
            "id,title,note\r\n1,Привіт,\"first, and\nsecond\"\r\n2,,\"\"\r\n");

        Assert.Equal(["id", "title", "note"], document.Headers);
        Assert.Equal(2, document.Rows.Count);
        Assert.Equal(["1", "Привіт", "first, and\nsecond"], document.Rows[0]);
        Assert.Equal(["2", string.Empty, string.Empty], document.Rows[1]);
    }

    [Fact]
    public void Preserves_exact_quoted_identifier_headers()
    {
        var document = DatabaseGridCsv.Parse("\" Name \"\nvalue\n");

        Assert.Equal(" Name ", Assert.Single(document.Headers));
    }

    [Fact]
    public void Rejects_duplicate_headers_and_ragged_rows()
    {
        var duplicate = Assert.Throws<InvalidDataException>(() =>
            DatabaseGridCsv.Parse("id,id\n1,2\n"));
        Assert.Contains("unique", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        var ragged = Assert.Throws<InvalidDataException>(() =>
            DatabaseGridCsv.Parse("id,title\n1\n"));
        Assert.Contains("expected 2", ragged.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id,title\n1,\"unterminated\n")]
    [InlineData("id,title\n1,\"closed\"junk\n")]
    [InlineData("id,title\n1,bad\"quote\n")]
    public void Rejects_malformed_quote_structure(string text)
    {
        Assert.Throws<InvalidDataException>(() => DatabaseGridCsv.Parse(text));
    }

    [Fact]
    public void Format_round_trips_rfc_quoted_values()
    {
        var text = DatabaseGridCsv.Format(
            ["id", "title", "note"],
            [
                ["1", "Ada, Lovelace", "she said \"hi\"\nagain"],
                ["2", null, string.Empty],
            ]);

        var parsed = DatabaseGridCsv.Parse(text);
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal("Ada, Lovelace", parsed.Rows[0][1]);
        Assert.Equal("she said \"hi\"\nagain", parsed.Rows[0][2]);
        Assert.Equal(string.Empty, parsed.Rows[1][1]);
        Assert.Equal(string.Empty, parsed.Rows[1][2]);
    }

    [Fact]
    public void Parse_is_bounded_before_allocating_rows()
    {
        var oversized = new string('x', DatabaseGridCsv.MaximumCharacters + 1);

        var error = Assert.Throws<InvalidDataException>(() => DatabaseGridCsv.Parse(oversized));

        Assert.Contains("16 MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_more_than_the_maximum_data_rows()
    {
        var text = "id\n" + string.Concat(
            Enumerable.Repeat("1\n", DatabaseGridCsv.MaximumRows + 1));

        var error = Assert.Throws<InvalidDataException>(() => DatabaseGridCsv.Parse(text));

        Assert.Contains(
            DatabaseGridCsv.MaximumRows.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_rejects_excess_columns_before_allocating_a_wide_row()
    {
        var text = string.Concat(Enumerable.Repeat(",", DatabaseGridCsv.MaximumColumns));

        var error = Assert.Throws<InvalidDataException>(() => DatabaseGridCsv.Parse(text));

        Assert.Contains("columns", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_an_oversized_header_before_it_reaches_an_error_message()
    {
        var text = new string('h', DatabaseGridCsv.MaximumHeaderCharacters + 1) + "\nvalue\n";

        var error = Assert.Throws<InvalidDataException>(() => DatabaseGridCsv.Parse(text));

        Assert.Contains("headers", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_header_errors_are_bounded_and_single_line()
    {
        var longHeader = new string('h', DatabaseGridCsv.MaximumHeaderCharacters);
        var text = $"\"{longHeader}\",\"{longHeader}\"\n1,2\n";

        var error = Assert.Throws<InvalidDataException>(() => DatabaseGridCsv.Parse(text));

        Assert.InRange(error.Message.Length, 1, 256);
        Assert.DoesNotContain('\n', error.Message);
        Assert.Contains("…", error.Message, StringComparison.Ordinal);
    }
}

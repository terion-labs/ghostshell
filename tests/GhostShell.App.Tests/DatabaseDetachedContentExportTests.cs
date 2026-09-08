using System.Globalization;
using System.Text;
using System.Text.Json;
using GhostShell.App.ViewModels;
using GhostShell.Application;

namespace GhostShell.App.Tests;

public sealed class DatabaseDetachedContentExportTests
{
    [Fact]
    public void DetachedCsvQuotesWithoutPrescanAndCancellationStopsBeforeFullRead()
    {
        using var cancellation = new CancellationTokenSource();
        var content = new Content(Encoding.UTF8.GetBytes(new string('x', 2_000_000)), DatabaseValueKind.Text,
            afterRead: cancellation.Cancel);
        var (columns, row) = Create(content);
        using var output = new MemoryStream();
        using var writer = DatabaseGridExport.CreateFileWriter(output, cancellation.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => DatabaseGridExport.WriteCsv(writer, columns, [row]));
        Assert.Equal(1, content.Opens);
        Assert.InRange(content.TotalRead, 1, 8192);

        var complete = new string('x', 20_000);
        (columns, row) = Create(new Content(Encoding.UTF8.GetBytes(complete), DatabaseValueKind.Text));
        using var csv = new StringWriter(CultureInfo.InvariantCulture);
        DatabaseGridExport.WriteCsv(csv, columns, [row]);
        Assert.Contains(Environment.NewLine + "\"" + complete + "\"", csv.ToString(), StringComparison.Ordinal);
        Assert.Equal(complete, Assert.Single(DatabaseGridCsv.Parse(csv.ToString()).Rows)[0]);
    }

    [Fact]
    public void FormulaScanCancellationStopsAWhitespaceOnlyDetachedValue()
    {
        using var cancellation = new CancellationTokenSource();
        var content = new Content(Encoding.UTF8.GetBytes(new string(' ', 2_000_000)), DatabaseValueKind.Text,
            afterRead: cancellation.Cancel);
        var (_, row) = Create(content);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            DatabaseSpreadsheetRisk.ContainsFormula(row.Cells, cancellationToken: cancellation.Token));
        Assert.InRange(content.TotalRead, 1, 8192);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FileWriterChecksCancellationForEveryUsedOverload(int operation)
    {
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        using var writer = DatabaseGridExport.CreateFileWriter(output, cancellation.Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            switch (operation)
            {
                case 0: writer.Write('x'); break;
                case 1: writer.Write("text"); break;
                case 2: writer.Write("span".AsSpan()); break;
                case 3: writer.Write(['a', 'b'], 0, 2); break;
                default: writer.Flush(); break;
            }
        });
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(DatabaseValueKind.Text)]
    [InlineData(DatabaseValueKind.Other)]
    public void TextWriterExportsCompleteTextWithEscapingAcrossReadBoundaries(DatabaseValueKind declaredKind)
    {
        var original = new string('a', 4095) + "🌐\",\t\r\nO'Hara" + new string('z', 20_000);
        var content = new Content(Encoding.UTF8.GetBytes(original), DatabaseValueKind.Text);
        var (columns, row) = Create(content, declaredKind);
        using var csv = new StringWriter(CultureInfo.InvariantCulture);
        DatabaseGridExport.WriteCsv(csv, columns, [row]);
        Assert.Equal(original, Assert.Single(DatabaseGridCsv.Parse(csv.ToString()).Rows)[0]);
        using var json = new StringWriter(CultureInfo.InvariantCulture);
        DatabaseGridExport.WriteCurrentPageJson(json, columns, [row]);
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(original, document.RootElement[0].GetProperty("value").GetString());
        using var sql = new StringWriter(CultureInfo.InvariantCulture);
        DatabaseGridExport.WriteSqlInsert(sql, new DatabaseObjectId(null, null, "items"), columns, row);
        Assert.Contains(original.Replace("'", "''", StringComparison.Ordinal), sql.ToString(), StringComparison.Ordinal);
        Assert.InRange(content.MaximumRead, 1, 8192);
    }

    [Fact]
    public void ValidatedJsonStreamsCompleteHugeTokenAndInvalidJsonRemainsAString()
    {
        var original = "{\"value\":\"" + new string('x', 200_000) + "\",\"number\":123456789012345678901234567890}";
        var content = new Content(Encoding.UTF8.GetBytes(original), DatabaseValueKind.Json);
        var (columns, row) = Create(content);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        DatabaseGridExport.WriteCurrentPageJson(output, columns, [row]);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(new string('x', 200_000), document.RootElement[0].GetProperty("value").GetProperty("value").GetString());
        Assert.Equal("123456789012345678901234567890", document.RootElement[0].GetProperty("value").GetProperty("number").GetRawText());
        Assert.InRange(content.MaximumRead, 1, 8192);

        var invalid = new Content(Encoding.UTF8.GetBytes("{not valid}"), DatabaseValueKind.Text);
        (columns, row) = Create(invalid, columnKind: DatabaseValueKind.Json);
        using var escaped = new StringWriter(CultureInfo.InvariantCulture);
        DatabaseGridExport.WriteCurrentPageJson(escaped, columns, [row]);
        using var escapedDocument = JsonDocument.Parse(escaped.ToString());
        Assert.Equal("{not valid}", escapedDocument.RootElement[0].GetProperty("value").GetString());
    }

    [Fact]
    public void BinaryStreamingDoesNotPadBase64BetweenShortReads()
    {
        var original = Enumerable.Range(0, 20_003).Select(index => (byte)(index % 256)).ToArray();
        var content = new Content(original, DatabaseValueKind.Binary, readLimit: 7);
        var (columns, row) = Create(content);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        DatabaseGridExport.WriteCurrentPageJson(output, columns, [row]);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(original, document.RootElement[0].GetProperty("value").GetBytesFromBase64());
        using var sql = new StringWriter(CultureInfo.InvariantCulture);
        DatabaseGridExport.WriteSqlInsert(sql, new DatabaseObjectId(null, null, "items"), columns, row);
        Assert.Contains("X'" + Convert.ToHexString(original) + "'", sql.ToString(), StringComparison.Ordinal);
        Assert.InRange(content.MaximumRead, 1, 4095);
    }

    [Fact]
    public void DetachedArrayStreamsEachLargeElementWithoutLosingNullOrScalarIdentity()
    {
        using var payload = new MemoryStream();
        var large = new string('x', 100_000) + "🌐\"";
        DatabaseArrayContent.Write(payload, new[] { "first", large, null, "last" }, CancellationToken.None);
        var content = new Content(payload.ToArray(), DatabaseValueKind.Collection);
        var (columns, row) = Create(content);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        DatabaseGridExport.WriteCurrentPageJson(output, columns, [row]);
        using var document = JsonDocument.Parse(output.ToString());
        var array = document.RootElement[0].GetProperty("value");
        Assert.Equal("first", array[0].GetString());
        Assert.Equal(large, array[1].GetString());
        Assert.Equal(JsonValueKind.Null, array[2].ValueKind);
        Assert.Equal("last", array[3].GetString());
        Assert.InRange(content.MaximumRead, 1, 8192);
    }

    private static (DatabaseResultColumnViewModel[] Columns, DatabaseResultRowViewModel Row) Create(
        Content content, DatabaseValueKind? columnKind = null)
    {
        var descriptor = new DatabaseColumnDescriptor("value", "test", columnKind ?? content.Kind);
        var row = new DatabaseResultRowViewModel(1,
            [new DatabaseValue(content, descriptor.ValueKind, "display preview", IsTruncated: true)],
            [descriptor], [200], canEdit: false);
        return ([new DatabaseResultColumnViewModel(descriptor, 200)], row);
    }

    private sealed class Content(byte[] bytes, DatabaseValueKind kind, int readLimit = int.MaxValue, Action? afterRead = null) : DatabaseValueContent
    {
        public int MaximumRead { get; private set; }
        public int TotalRead { get; private set; }
        public int Opens { get; private set; }
        public override long Length => bytes.Length;
        public override DatabaseValueKind Kind => kind;
        public override Stream OpenRead()
        {
            Opens++;
            return new InspectingStream(bytes, readLimit, count =>
            {
                MaximumRead = Math.Max(MaximumRead, count);
                TotalRead += count;
                afterRead?.Invoke();
            });
        }
    }

    private sealed class InspectingStream(byte[] bytes, int readLimit, Action<int> observe) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, Math.Min(count, readLimit));
            observe(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer[..Math.Min(buffer.Length, readLimit)]);
            observe(read);
            return read;
        }
    }
}

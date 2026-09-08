using System.Text;
using GhostShell.Application;

namespace GhostShell.Databases.Tests;

public sealed class DatabaseValueStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeReaderValuesDoNotFallBackToLegacyMaterializationLimit(bool binary)
    {
        using var table = new System.Data.DataTable();
        table.Columns.Add("value", typeof(object));
        var text = new string('x', 10_000) + "🌐";
        var bytes = new byte[4 * 1024 * 1024 + 19];
        bytes[^1] = 197;
        table.Rows.Add(binary ? new MemoryStream(bytes) { Position = 3 } : (object)new StringReader(text));
        using var reader = table.CreateDataReader();
        Assert.True(await reader.ReadAsync());
        using var store = new RecordingStore();
        var value = await DatabaseValueMaterializer.MaterializeAsync(reader, 0,
            new DatabaseColumnDescriptor("value", "unknown", DatabaseValueKind.Other), () => store, CancellationToken.None);
        var content = Assert.IsAssignableFrom<DatabaseValueContent>(value.RawValue);
        using var complete = content.OpenRead();
        if (binary)
        {
            using var output = new MemoryStream();
            await complete.CopyToAsync(output);
            Assert.Equal(bytes, output.ToArray());
        }
        else
        {
            using var output = new StreamReader(complete);
            Assert.Equal(text, await output.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task QueryOwnsCompleteTextAndBinaryUntilResultDisposal()
    {
        using var store = new RecordingStore();
        await using var client = new DatabasePanelClient(contentStoreFactory: () => store);
        using var result = await client.QueryAsync(
            "sqlite", "Data Source=:memory:", null,
            "SELECT printf('%.*c', 10000, 'x') AS text_value, zeroblob(10001) AS binary_value",
            10, CancellationToken.None);
        var row = Assert.Single(result.ValueRows);
        Assert.Equal(10_000, Assert.IsAssignableFrom<DatabaseValueContent>(row[0].RawValue).Length);
        Assert.Equal(10_001, Assert.IsAssignableFrom<DatabaseValueContent>(row[1].RawValue).Length);
        Assert.Same(store, result.ContentStore);
        Assert.False(store.Disposed);
        result.Dispose();
        Assert.True(store.Disposed);
    }

    [Fact]
    public async Task QueryCancellationDisposesPartiallyWrittenResult()
    {
        using var cancellation = new CancellationTokenSource();
        using var store = new RecordingStore { Stored = cancellation.Cancel };
        await using var client = new DatabasePanelClient(contentStoreFactory: () => store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryAsync(
            "sqlite", "Data Source=:memory:", null,
            "SELECT printf('%.*c', 10000, 'x') AS text_value, zeroblob(10001) AS binary_value",
            10, cancellation.Token));
        Assert.True(store.Disposed);
    }

    [Theory]
    [InlineData(DatabaseValueKind.Text)]
    [InlineData(DatabaseValueKind.Json)]
    public async Task CompleteTextSurvivesPreviewAndUtf8BufferBoundaries(DatabaseValueKind kind)
    {
        var source = new string('a', 4095) + "🌐" + new string('b', 20_000) + "complete tail";
        using var store = new RecordingStore();
        using var reader = new StringReader(source);
        var value = await DatabaseValueMaterializer.ReadTextAsync(
            reader, kind, () => store, CancellationToken.None);
        var content = Assert.IsAssignableFrom<DatabaseValueContent>(value.RawValue);
        Assert.True(value.IsTruncated);
        Assert.Contains("display preview", value.DisplayText, StringComparison.Ordinal);
        Assert.DoesNotContain("complete tail", value.DisplayText, StringComparison.Ordinal);
        using var complete = new StreamReader(content.OpenRead(), Encoding.UTF8);
        Assert.Equal(source, await complete.ReadToEndAsync());
        Assert.Equal(DatabaseValueKind.Text, content.Kind);
        Assert.InRange(store.MaximumWrite, 1, 32_768);
    }

    [Fact]
    public async Task JsonKindRequiresCompleteValidationAndKeepsExactDocumentBytes()
    {
        var json = "{ \"payload\": \"" + new string('x', 10_000) + "\" }";
        using var store = new RecordingStore();
        using var reader = new StringReader(json);
        var value = await DatabaseValueMaterializer.ReadTextAsync(reader, DatabaseValueKind.Json, () => store, CancellationToken.None);
        var content = Assert.IsAssignableFrom<DatabaseValueContent>(value.RawValue);
        Assert.Equal(DatabaseValueKind.Json, content.Kind);
        using var complete = new StreamReader(content.OpenRead(), Encoding.UTF8);
        Assert.Equal(json, await complete.ReadToEndAsync());
    }

    [Fact]
    public async Task CompleteBinaryIsRetainedBeyondFormerFourMegabyteLimit()
    {
        var bytes = new byte[4 * 1024 * 1024 + 19];
        bytes[^1] = 197;
        using var store = new RecordingStore();
        using var source = new MemoryStream(bytes);
        var value = await DatabaseValueMaterializer.ReadBinaryAsync(source, () => store, CancellationToken.None);
        var content = Assert.IsAssignableFrom<DatabaseValueContent>(value.RawValue);
        Assert.Equal(bytes.Length, content.Length);
        using var complete = content.OpenRead();
        using var output = new MemoryStream();
        await complete.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
        Assert.InRange(store.MaximumWrite, 1, 8192);
    }

    [Fact]
    public async Task SmallAndEmptyTextDoNotOpenStorage()
    {
        foreach (var source in new[] { string.Empty, "hello" })
        {
            using var reader = new StringReader(source);
            var value = await DatabaseValueMaterializer.ReadTextAsync(
                reader, DatabaseValueKind.Text,
                () => throw new InvalidOperationException("Unexpected storage access."),
                CancellationToken.None);
            Assert.Equal(source, value.RawValue);
            Assert.False(value.IsTruncated);
        }
    }

    private sealed class RecordingStore : DatabaseValueContentStore
    {
        public override IDisposable Retain() => new RetainedFixture();
        private sealed class RetainedFixture : IDisposable
        {
            public void Dispose() { }
        }
        public int MaximumWrite { get; private set; }
        public bool Disposed { get; private set; }
        public Action? Stored { get; init; }

        public override async Task<DatabaseValueContent> StoreAsync(
            DatabaseValueKind kind,
            Func<Stream, CancellationToken, Task> write,
            CancellationToken cancellationToken)
        {
            using var destination = new RecordingStream(count => MaximumWrite = Math.Max(MaximumWrite, count));
            await write(destination, cancellationToken);
            Stored?.Invoke();
            return new Content(destination.ToArray(), kind);
        }

        protected override void Dispose(bool disposing) => Disposed = true;
    }

    private sealed class RecordingStream(Action<int> record) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            record(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class Content(byte[] bytes, DatabaseValueKind kind) : DatabaseValueContent
    {
        public override long Length => bytes.Length;
        public override DatabaseValueKind Kind => kind;
        public override Stream OpenRead() => new MemoryStream(bytes, writable: false);
    }
}

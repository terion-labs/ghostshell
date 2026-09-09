using System.Buffers.Binary;
using System.Text.Json;
using Asura.Application;
using Asura.ConnectionBackend;

namespace Asura.Architecture.Tests;

public sealed class DatabaseMetadataProtocolTests
{
    [Fact]
    public async Task LargeSchemaUsesBoundedFramesAndPreservesAllTables()
    {
        var graph = new DatabaseSchemaGraph([.. Enumerable.Range(0, 600).Select(index =>
            new DatabaseSchemaTable(new($"table_{index}", DatabaseTableKind.Table),
                [new("identifier", 0, "INTEGER", DatabaseValueKind.SignedInteger, IsPrimaryKey: true)], []))]);
        using var stream = new MemoryStream();
        await DatabaseMetadataProtocol.WriteAsync(stream, graph,
            DatabaseOperationJsonContext.Default.DatabaseSchemaGraph, CancellationToken.None);
        Assert.True(stream.Length > DatabaseOperationProtocol.MaximumMetadataBytes);

        stream.Position = 0;
        var frames = 0;
        while (true)
        {
            var frame = await DatabaseOperationProtocol.ReadFrameAsync(stream,
                DatabaseOperationProtocol.MaximumMetadataBytes, CancellationToken.None);
            if (frame.Length == 0) { break; }
            frames++;
        }
        Assert.True(frames > 1);
        Assert.Equal(stream.Length, stream.Position);

        stream.Position = 0;
        var actual = await DatabaseMetadataProtocol.ReadAsync(stream,
            DatabaseOperationJsonContext.Default.DatabaseSchemaGraph, CancellationToken.None);
        Assert.Equal(graph.Tables.Count, actual.Tables.Count);
        Assert.Equal(graph.Tables.Select(table => table.Object), actual.Tables.Select(table => table.Object));
        Assert.All(actual.Tables, table => Assert.True(Assert.Single(table.Columns).IsPrimaryKey));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(DatabaseOperationProtocol.MaximumMetadataBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task RejectsInvalidFrameLengthBeforeReadingPayload(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseMetadataProtocol.ReadAsync(stream,
            DatabaseOperationJsonContext.Default.DatabaseSchemaGraph, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsTruncatedPayloadOrMissingTerminator(bool completePayload)
    {
        using var stream = new MemoryStream();
        await DatabaseOperationProtocol.WriteFrameAsync(stream, "123"u8.ToArray(), CancellationToken.None);
        if (!completePayload) { stream.SetLength(stream.Length - 1); }
        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => DatabaseMetadataProtocol.ReadAsync(stream,
            DatabaseOperationJsonContext.Default.Int64, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsNullDocument()
    {
        using var stream = new MemoryStream();
        await DatabaseOperationProtocol.WriteFrameAsync(stream, "null"u8.ToArray(), CancellationToken.None);
        await DatabaseOperationProtocol.WriteFrameAsync(stream, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseMetadataProtocol.ReadAsync(stream,
            DatabaseOperationJsonContext.Default.DatabaseSchemaGraph, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsMalformedJsonDocument()
    {
        using var stream = new MemoryStream();
        await DatabaseOperationProtocol.WriteFrameAsync(stream, "{"u8.ToArray(), CancellationToken.None);
        await DatabaseOperationProtocol.WriteFrameAsync(stream, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        stream.Position = 0;
        await Assert.ThrowsAsync<JsonException>(() => DatabaseMetadataProtocol.ReadAsync(stream,
            DatabaseOperationJsonContext.Default.DatabaseSchemaGraph, CancellationToken.None));
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.DatabaseBackend;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class DatabaseOperationValueProtocolTests
{
    [Fact]
    public async Task LargeResultValuesSpillWithoutClippingAndKeepFollowingCellsAligned()
    {
        var text = new string('a', 65_535) + "🌐" + new string('z', 120_000);
        var binary = new byte[4 * 1024 * 1024 + 17];
        binary[^1] = 197;
        using var wire = new MemoryStream();
        await DatabaseOperationValueProtocol.WriteAsync(wire, new DatabaseValue(text, DatabaseValueKind.Text, "preview", true), CancellationToken.None);
        await DatabaseOperationValueProtocol.WriteAsync(wire, new DatabaseValue(binary, DatabaseValueKind.Binary, "binary preview", true), CancellationToken.None);
        await DatabaseOperationValueProtocol.WriteAsync(wire, new DatabaseValue(42, DatabaseValueKind.SignedInteger, "42"), CancellationToken.None);
        wire.Position = 0;
        using var store = new RecordingStore();
        var first = await DatabaseOperationValueProtocol.ReadResultAsync(wire, () => store, CancellationToken.None);
        var second = await DatabaseOperationValueProtocol.ReadResultAsync(wire, () => store, CancellationToken.None);
        var third = await DatabaseOperationValueProtocol.ReadResultAsync(wire, () => store, CancellationToken.None);
        using var firstReader = new StreamReader(Assert.IsAssignableFrom<DatabaseValueContent>(first.RawValue).OpenRead());
        Assert.Equal(text, await firstReader.ReadToEndAsync());
        using var secondReader = Assert.IsAssignableFrom<DatabaseValueContent>(second.RawValue).OpenRead();
        using var copied = new MemoryStream();
        await secondReader.CopyToAsync(copied);
        Assert.Equal(binary, copied.ToArray());
        Assert.IsType<int>(third.RawValue);
        Assert.Equal(42, third.RawValue);
        Assert.Equal(2, store.Writes);
        Assert.InRange(store.MaximumWrite, 1, DatabaseOperationValueProtocol.MaximumChunkBytes);
        Assert.Equal(wire.Length, wire.Position);
    }

    [Fact]
    public async Task SharedInlineBudgetSpillsSmallCellsWithoutLosingContent()
    {
        var text = new string('x', 2000);
        using var wire = new MemoryStream();
        for (var index = 0; index < 10; index++)
        {
            await DatabaseOperationValueProtocol.WriteAsync(wire,
                new DatabaseValue(text, DatabaseValueKind.Text, "preview"), CancellationToken.None);
        }
        wire.Position = 0;
        using var store = new RecordingStore();
        var remaining = 5000;
        bool Reserve(int bytes)
        {
            if (bytes > remaining) { return false; }
            remaining -= bytes;
            return true;
        }
        for (var index = 0; index < 10; index++)
        {
            var result = await DatabaseOperationValueProtocol.ReadResultAsync(wire, () => store, CancellationToken.None, Reserve);
            if (index == 0) { Assert.Equal(text, Assert.IsType<string>(result.RawValue)); }
            else
            {
                using var reader = new StreamReader(Assert.IsAssignableFrom<DatabaseValueContent>(result.RawValue).OpenRead());
                Assert.Equal(text, await reader.ReadToEndAsync());
            }
        }
        Assert.Equal(9, store.Writes);
        Assert.Equal(wire.Length, wire.Position);
    }

    [Fact]
    public async Task ScalarParametersPreserveClosedClrIdentity()
    {
        object?[] values = [null, string.Empty, "🌐", true, (byte)1, (short)-2, 3, 4L, (uint)5,
            ulong.MaxValue, 1.25m, 1.25f, 1.25d, (Half)1.25, Int128.MaxValue, UInt128.MaxValue,
            BigInteger.Pow(10, 100), new DateTime(2026, 9, 7, 12, 13, 14, DateTimeKind.Utc),
            new DateTimeOffset(2026, 9, 7, 12, 13, 14, TimeSpan.FromHours(3)),
            new DateOnly(2026, 9, 7), new TimeOnly(12, 13, 14), TimeSpan.FromTicks(-100), Guid.NewGuid(), 'Ж',
            IPAddress.Parse("fe80::1%3")];
        using var wire = new MemoryStream();
        foreach (var value in values)
        {
            await DatabaseOperationValueProtocol.WriteParameterAsync(wire, value, CancellationToken.None);
        }
        wire.Position = 0;
        foreach (var expected in values)
        {
            var actual = await DatabaseOperationValueProtocol.ReadParameterAsync(wire, CancellationToken.None);
            Assert.Equal(expected?.GetType(), actual?.GetType());
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task ArrayResultStaysDetachedAndRoundTripsAsExactParameter()
    {
        int?[] original = [1, null, int.MinValue, int.MaxValue];
        using var resultWire = new MemoryStream();
        await DatabaseOperationValueProtocol.WriteAsync(resultWire,
            new DatabaseValue(original, DatabaseValueKind.Collection, "array preview"), CancellationToken.None);
        resultWire.Position = 0;
        using var store = new RecordingStore();
        var result = await DatabaseOperationValueProtocol.ReadResultAsync(resultWire, () => store, CancellationToken.None);
        var detached = Assert.IsAssignableFrom<DatabaseValueContent>(result.RawValue);
        Assert.Equal(DatabaseValueKind.Collection, detached.Kind);
        using var parameterWire = new MemoryStream();
        await DatabaseOperationValueProtocol.WriteParameterAsync(parameterWire, detached, CancellationToken.None);
        parameterWire.Position = 0;
        var parameter = await DatabaseOperationValueProtocol.ReadParameterAsync(parameterWire, CancellationToken.None);
        Assert.Equal(original, Assert.IsType<int?[]>(parameter));
    }

    [Fact]
    public async Task InFilterSequencesKeepHeterogeneousValuesAndRejectCycles()
    {
        object?[] original = [1, "two", null, new object?[] { 3L, Guid.Empty }];
        using var wire = new MemoryStream();
        await DatabaseOperationValueProtocol.WriteParameterAsync(wire, original, CancellationToken.None);
        wire.Position = 0;
        var result = Assert.IsType<object?[]>(await DatabaseOperationValueProtocol.ReadParameterAsync(wire, CancellationToken.None));
        Assert.Equal(1, result[0]);
        Assert.Equal("two", result[1]);
        Assert.Null(result[2]);
        Assert.Equal([3L, Guid.Empty], Assert.IsType<object?[]>(result[3]));
        object?[] cycle = [null];
        cycle[0] = cycle;
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseOperationValueProtocol.WriteParameterAsync(wire, cycle, CancellationToken.None));
    }

    [Fact]
    public async Task LargeNumericParameterKeepsScalarTypeOutsideTheUiHeap()
    {
        var original = BigInteger.Pow(10, 20_000) + 17;
        using var wire = new MemoryStream();
        await DatabaseOperationValueProtocol.WriteAsync(wire,
            new DatabaseValue(original, DatabaseValueKind.SignedInteger, "numeric preview", true), CancellationToken.None);
        wire.Position = 0;
        using var store = new RecordingStore();
        var result = await DatabaseOperationValueProtocol.ReadResultAsync(wire, () => store, CancellationToken.None);
        var content = Assert.IsAssignableFrom<DatabaseValueContent>(result.RawValue);
        Assert.Equal(DatabaseArrayScalarType.BigInteger, content.ScalarType);
        using var parameterWire = new MemoryStream();
        await DatabaseOperationValueProtocol.WriteParameterAsync(parameterWire, content, CancellationToken.None);
        parameterWire.Position = 0;
        Assert.Equal(original, await DatabaseOperationValueProtocol.ReadParameterAsync(parameterWire, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidJsonKeepsTextAndUnpairedSurrogatesFailExplicitly()
    {
        using var wire = new MemoryStream();
        var invalid = "{not JSON}" + new string('x', 20_000);
        await DatabaseOperationValueProtocol.WriteAsync(wire, new DatabaseValue(invalid, DatabaseValueKind.Json, "preview"), CancellationToken.None);
        wire.Position = 0;
        using var store = new RecordingStore();
        var value = await DatabaseOperationValueProtocol.ReadResultAsync(wire, () => store, CancellationToken.None);
        var content = Assert.IsAssignableFrom<DatabaseValueContent>(value.RawValue);
        Assert.Equal(DatabaseValueKind.Text, content.Kind);
        using var text = new StreamReader(content.OpenRead());
        Assert.Equal(invalid, await text.ReadToEndAsync());
        await Assert.ThrowsAsync<EncoderFallbackException>(() => DatabaseOperationValueProtocol.WriteParameterAsync(wire, "\ud800", CancellationToken.None));
    }

    [Fact]
    public async Task PartialSpillAndOversizedBodyFailWithoutReturningAValue()
    {
        using var wire = new MemoryStream();
        var header = new DatabaseWireValueHeader(DatabaseValueKind.Text, DatabaseWireValueFormat.Text, "preview", true);
        await DatabaseOperationProtocol.WriteFrameAsync(wire,
            JsonSerializer.SerializeToUtf8Bytes(header, DatabaseWireValueJsonContext.Default.DatabaseWireValueHeader), CancellationToken.None);
        await DatabaseOperationProtocol.WriteFrameAsync(wire, new byte[20_000], CancellationToken.None);
        wire.Position = 0;
        using var store = new RecordingStore();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            DatabaseOperationValueProtocol.ReadResultAsync(wire, () => store, CancellationToken.None));
        Assert.Equal(0, store.Writes);

        var invalidLength = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(invalidLength, int.MaxValue);
        wire.Write(invalidLength);
        wire.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseOperationValueProtocol.ReadResultAsync(wire, () => store, CancellationToken.None));
        Assert.Equal(0, store.Writes);

        wire.Position = 0;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseOperationValueProtocol.ReadResultAsync(wire, () => store, canceled.Token));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforeAllocatingOrOpeningStorage()
    {
        using var wire = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        wire.Write(header);
        wire.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseOperationValueProtocol.ReadResultAsync(wire,
            () => throw new InvalidOperationException("Storage must not open for an invalid header."), CancellationToken.None));
    }

    [Fact]
    public async Task UnknownWireTypeIsRejectedBeforePayloadAllocation()
    {
        using var wire = new MemoryStream();
        var header = new DatabaseWireValueHeader(DatabaseValueKind.Text, (DatabaseWireValueFormat)255, "", false);
        await DatabaseOperationProtocol.WriteFrameAsync(wire,
            JsonSerializer.SerializeToUtf8Bytes(header, DatabaseWireValueJsonContext.Default.DatabaseWireValueHeader), CancellationToken.None);
        wire.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseOperationValueProtocol.ReadResultAsync(wire,
            () => throw new InvalidOperationException("Unexpected storage access."), CancellationToken.None));
    }

    private sealed class RecordingStore : DatabaseValueContentStore
    {
        public int Writes { get; private set; }
        public int MaximumWrite { get; private set; }
        public override IDisposable Retain() => new Lease();
        public override async Task<DatabaseValueContent> StoreAsync(DatabaseValueKind kind,
            Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
        {
            using var destination = new RecordingStream(count => MaximumWrite = Math.Max(MaximumWrite, count));
            await write(destination, cancellationToken);
            Writes++;
            return new Content(destination.ToArray(), kind);
        }
        protected override void Dispose(bool disposing) { }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    private sealed class Content(byte[] bytes, DatabaseValueKind kind) : DatabaseValueContent
    {
        public override long Length => bytes.Length;
        public override DatabaseValueKind Kind => kind;
        public override Stream OpenRead() => new MemoryStream(bytes, writable: false);
    }

    private sealed class RecordingStream(Action<int> observe) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            observe(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}

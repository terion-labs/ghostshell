using System.Numerics;
using System.Text;

namespace Asura.Application.Tests;

public sealed class DatabaseArrayContentTests
{
    [Fact]
    public void TextReaderPrefetchCannotConsumeFollowingElement()
    {
        using var stream = Encode(new[] { "first", new string('x', 100_000), "last" });
        var shape = DatabaseArrayContent.ReadShape(stream);
        Assert.Equal(3, shape.Count);
        Assert.Equal("first", Read(stream, shape));
        Assert.Equal(new string('x', 100_000), Read(stream, shape));
        Assert.Equal("last", Read(stream, shape));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional", Justification = "The provider payload must preserve a genuine rectangular array's rank and bounds.")]
    public void NullableMultidimensionalShapeAndScalarIdentityRoundTrip()
    {
        int?[,] source = { { 1, null }, { int.MinValue, int.MaxValue } };
        using var stream = Encode(source);
        var shape = DatabaseArrayContent.ReadShape(stream);
        Assert.Equal(DatabaseArrayScalarType.Int32, shape.ElementType);
        Assert.True(shape.Nullable);
        Assert.Equal([2, 2], shape.Lengths);
        Assert.Equal([0, 0], shape.LowerBounds);
        Assert.Equal(1, Read(stream, shape));
        Assert.Null(Read(stream, shape));
        Assert.Equal(int.MinValue, Read(stream, shape));
        Assert.Equal(int.MaxValue, Read(stream, shape));
    }

    [Fact]
    public void ScalarTypesRetainExactValues()
    {
        Array[] arrays =
        [
            new[] { true, false }, new[] { byte.MaxValue }, new[] { sbyte.MinValue },
            new[] { short.MinValue }, new[] { ushort.MaxValue }, new[] { uint.MaxValue },
            new[] { long.MinValue }, new[] { ulong.MaxValue }, new[] { 1.25f }, new[] { 1.25d },
            new[] { decimal.MaxValue }, new[] { new DateTime(2026, 9, 7, 12, 13, 14, DateTimeKind.Utc) },
            new[] { new DateTimeOffset(2026, 9, 7, 12, 13, 14, TimeSpan.FromHours(3)) },
            new[] { new DateOnly(2026, 9, 7) }, new[] { new TimeOnly(12, 13, 14) },
            new[] { TimeSpan.FromTicks(-100) }, new[] { Guid.NewGuid() }, new[] { 'Ж' },
            new[] { (Half)1.25 }, new[] { Int128.MaxValue }, new[] { UInt128.MaxValue },
            new[] { BigInteger.Pow(10, 100) },
        ];
        foreach (var array in arrays)
        {
            using var stream = Encode(array);
            var shape = DatabaseArrayContent.ReadShape(stream);
            foreach (var expected in array)
            {
                var actual = Read(stream, shape);
                if (expected is Int128 or UInt128 or BigInteger)
                {
                    Assert.Equal(expected.ToString(), actual);
                }
                else
                {
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    [Fact]
    public void RejectsMalformedDimensionsBeforeMaterializingElements()
    {
        using var stream = Encode(new[] { 1 });
        stream.Position = 7;
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(int.MaxValue);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => DatabaseArrayContent.ReadShape(stream));
    }

    [Fact]
    public void RejectsUnknownTypesAndInvalidBooleanBytes()
    {
        using var stream = Encode(new[] { true });
        stream.Position = stream.Length - 1;
        stream.WriteByte(2);
        stream.Position = 0;
        var shape = DatabaseArrayContent.ReadShape(stream);
        Assert.Throws<InvalidDataException>(() => Read(stream, shape));
        stream.Position = 4;
        stream.WriteByte(255);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => DatabaseArrayContent.ReadShape(stream));
    }

    [Fact]
    public void RejectsUnconsumedTextAndUnpairedSurrogatesInsteadOfLosingData()
    {
        using var stream = Encode(new[] { "text" });
        var shape = DatabaseArrayContent.ReadShape(stream);
        Assert.Throws<InvalidDataException>(() => DatabaseArrayContent.ReadScalar(stream, shape, (_, _) => null));
        using var destination = new MemoryStream();
        Assert.Throws<EncoderFallbackException>(() => DatabaseArrayContent.Write(destination, new[] { "\ud800" }, CancellationToken.None));
    }

    [Fact]
    public void CancellationStopsArrayTraversal()
    {
        using var stream = new MemoryStream();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DatabaseArrayContent.Write(stream, new[] { 1, 2 }, canceled.Token));
    }

    private static object? Read(Stream stream, DatabaseArrayShape shape) =>
        DatabaseArrayContent.ReadScalar(stream, shape, (text, _) =>
        {
            using var reader = new StreamReader(text, new UTF8Encoding(false, true));
            return reader.ReadToEnd();
        });

    private static MemoryStream Encode(Array array)
    {
        var stream = new MemoryStream();
        DatabaseArrayContent.Write(stream, array, CancellationToken.None);
        stream.Position = 0;
        return stream;
    }
}

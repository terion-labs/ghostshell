using System.Net;
using System.Text.Json;
using Asura.Application;
using Asura.Databases;

namespace Asura.Databases.Tests;

public sealed class DatabaseValueMaterializerTests
{
    [Fact]
    public void Classifies_clr_values_without_provider_specific_type_switches()
    {
        var cases = new (object Value, DatabaseValueKind Expected)[]
        {
            ("text", DatabaseValueKind.Text),
            (true, DatabaseValueKind.Boolean),
            (-1L, DatabaseValueKind.SignedInteger),
            (Int128.MaxValue, DatabaseValueKind.SignedInteger),
            (1UL, DatabaseValueKind.UnsignedInteger),
            (UInt128.MaxValue, DatabaseValueKind.UnsignedInteger),
            (12.5m, DatabaseValueKind.Decimal),
            (12.5d, DatabaseValueKind.FloatingPoint),
            (new DateOnly(2026, 8, 8), DatabaseValueKind.Date),
            (new TimeOnly(12, 30), DatabaseValueKind.Time),
            (DateTime.UnixEpoch, DatabaseValueKind.Timestamp),
            (DateTimeOffset.UnixEpoch, DatabaseValueKind.TimestampWithZone),
            (TimeSpan.FromMinutes(2), DatabaseValueKind.Duration),
            (Guid.Empty, DatabaseValueKind.Guid),
            (new byte[] { 1 }, DatabaseValueKind.Binary),
            (IPAddress.Loopback, DatabaseValueKind.Network),
            (new[] { 1, 2 }, DatabaseValueKind.Collection),
        };

        foreach (var (value, expected) in cases)
        {
            Assert.Equal(
                expected,
                DatabaseValueClassifier.Classify(value.GetType(), null, value));
        }
    }

    [Fact]
    public void Provider_type_names_refine_generic_clr_metadata()
    {
        var cases = new (string TypeName, DatabaseValueKind Expected)[]
        {
            ("jsonb", DatabaseValueKind.Json),
            ("TIMESTAMP WITH TIME ZONE", DatabaseValueKind.TimestampWithZone),
            ("TIMESTAMP(6) WITH TIME ZONE", DatabaseValueKind.TimestampWithZone),
            ("TIMESTAMP(9) WITH LOCAL TIME ZONE", DatabaseValueKind.TimestampWithZone),
            ("UUID", DatabaseValueKind.Guid),
            ("INET", DatabaseValueKind.Network),
            ("IPv6", DatabaseValueKind.Network),
            ("RAW(16)", DatabaseValueKind.Binary),
            ("Array(Nullable(Int32))", DatabaseValueKind.Collection),
            ("UInt64", DatabaseValueKind.UnsignedInteger),
            ("DECIMAL(38, 8)", DatabaseValueKind.Decimal),
            ("DECFLOAT(34)", DatabaseValueKind.Decimal),
            ("FixedString(16)", DatabaseValueKind.Text),
            ("Enum8('ready' = 1)", DatabaseValueKind.Text),
            ("TINYINT(1)", DatabaseValueKind.Boolean),
        };

        foreach (var (typeName, expected) in cases)
        {
            Assert.Equal(
                expected,
                DatabaseValueClassifier.Classify(typeof(object), typeName));
        }
    }

    [Fact]
    public void Byte_arrays_are_cloned_and_rendered_as_a_bounded_preview()
    {
        var source = Enumerable.Range(0, 40).Select(value => (byte)value).ToArray();

        var materialized = DatabaseValueMaterializer.FromProviderValue(source);
        var detached = Assert.IsType<byte[]>(materialized.RawValue);
        source[0] = byte.MaxValue;

        Assert.NotSame(source, detached);
        Assert.Equal(0, detached[0]);
        Assert.Equal(DatabaseValueKind.Binary, materialized.Kind);
        Assert.StartsWith("0x00010203", materialized.DisplayText, StringComparison.Ordinal);
        Assert.EndsWith("… (40 bytes)", materialized.DisplayText, StringComparison.Ordinal);
        Assert.True(materialized.IsTruncated);
    }

    [Fact]
    public void Provider_binary_streams_are_copied_and_detached()
    {
        using var source = new MemoryStream([0x01, 0x02, 0xFE]);
        source.Position = 1;

        var materialized = DatabaseValueMaterializer.FromProviderValue(
            source,
            DatabaseValueKind.Binary,
            "BLOB");

        Assert.Equal(1, source.Position);
        Assert.Equal([0x01, 0x02, 0xFE], Assert.IsType<byte[]>(materialized.RawValue));
        Assert.Equal(DatabaseValueKind.Binary, materialized.Kind);
        Assert.Equal("0x0102FE", materialized.DisplayText);
    }

    [Fact]
    public void Provider_binary_stream_at_limit_is_detached_with_one_byte_probe()
    {
        using var source = new CountingNonSeekableStream(
            DatabaseValueMaterializer.MaximumDetachedBinaryBytes);

        var materialized = DatabaseValueMaterializer.FromProviderValue(
            source,
            DatabaseValueKind.Binary,
            "BLOB");

        Assert.Equal(
            DatabaseValueMaterializer.MaximumDetachedBinaryBytes,
            Assert.IsType<byte[]>(materialized.RawValue).Length);
        Assert.Equal(DatabaseValueMaterializer.MaximumDetachedBinaryBytes, source.BytesRead);
        Assert.True(materialized.IsTruncated);
    }

    [Fact]
    public void Oversized_non_seekable_stream_stops_after_one_byte_probe_and_retains_no_blob()
    {
        using var source = new CountingNonSeekableStream(long.MaxValue);

        var materialized = DatabaseValueMaterializer.FromProviderValue(
            source,
            DatabaseValueKind.Binary,
            "BLOB");

        Assert.Equal(
            DatabaseValueMaterializer.MaximumDetachedBinaryBytes + 1L,
            source.BytesRead);
        Assert.IsType<string>(materialized.RawValue);
        Assert.Equal(DatabaseValueKind.Other, materialized.Kind);
        Assert.Contains("materialization limit", materialized.DisplayText, StringComparison.Ordinal);
        Assert.True(materialized.IsTruncated);
    }

    [Fact]
    public void Oversized_seekable_stream_is_rejected_without_reads_and_restores_position()
    {
        using var source = new CountingSeekableStream(
            DatabaseValueMaterializer.MaximumDetachedBinaryBytes + 1L);
        source.Position = 17;

        var materialized = DatabaseValueMaterializer.FromProviderValue(
            source,
            DatabaseValueKind.Binary,
            "BLOB");

        Assert.Equal(0, source.ReadCount);
        Assert.Equal(17, source.Position);
        Assert.Equal(DatabaseValueKind.Other, materialized.Kind);
        Assert.True(materialized.IsTruncated);
    }

    [Fact]
    public void Safe_values_use_invariant_bounded_display_text()
    {
        var number = DatabaseValueMaterializer.FromProviderValue(1234.5m);
        var timestamp = DatabaseValueMaterializer.FromProviderValue(
            new DateTimeOffset(2026, 8, 8, 12, 30, 45, TimeSpan.FromHours(3)));
        var wideInteger = DatabaseValueMaterializer.FromProviderValue(UInt128.MaxValue);
        var text = DatabaseValueMaterializer.FromProviderValue(
            "abcdefgh",
            DatabaseValueKind.Text,
            maxDisplayCharacters: 5);

        Assert.Equal("1234.5", number.DisplayText);
        Assert.Equal("2026-08-08T12:30:45.0000000+03:00", timestamp.DisplayText);
        Assert.Equal(UInt128.MaxValue, wideInteger.RawValue);
        Assert.Equal("abcd…", text.DisplayText);
        Assert.True(text.IsTruncated);
        Assert.Equal("abcdefgh", text.RawValue);
    }

    [Fact]
    public void Json_is_detached_from_its_disposable_document()
    {
        DatabaseValue materialized;
        using (var document = JsonDocument.Parse("""{"answer":42}"""))
        {
            materialized = DatabaseValueMaterializer.FromProviderValue(document);
        }

        var json = Assert.IsType<JsonElement>(materialized.RawValue);
        Assert.Equal(DatabaseValueKind.Json, materialized.Kind);
        Assert.Equal("""{"answer":42}""", json.GetRawText());
        Assert.Equal(json.GetRawText(), materialized.DisplayText);
    }

    [Fact]
    public void Unknown_provider_objects_degrade_to_invariant_display_only_text()
    {
        var materialized = DatabaseValueMaterializer.FromProviderValue(
            new ProviderSpecificValue(12.5m),
            DatabaseValueKind.Decimal);

        Assert.Equal(DatabaseValueKind.Other, materialized.Kind);
        Assert.Equal("12.5", materialized.DisplayText);
        Assert.Equal("12.5", Assert.IsType<string>(materialized.RawValue));
        Assert.False(materialized.IsNull);
    }

    [Fact]
    public void Provider_convertible_decimals_are_normalized_to_detached_clr_decimals()
    {
        var materialized = DatabaseValueMaterializer.FromProviderValue(
            new ConvertibleDecimal(12.5m),
            DatabaseValueKind.Decimal,
            "DECIMAL(12, 2)");

        Assert.Equal(12.5m, Assert.IsType<decimal>(materialized.RawValue));
        Assert.Equal(DatabaseValueKind.Decimal, materialized.Kind);
        Assert.Equal("12.5", materialized.DisplayText);
    }

    [Fact]
    public void Null_remains_distinct_from_an_empty_string()
    {
        var nullValue = DatabaseValueMaterializer.FromProviderValue(
            DBNull.Value,
            DatabaseValueKind.Text);
        var emptyValue = DatabaseValueMaterializer.FromProviderValue(
            string.Empty,
            DatabaseValueKind.Text);

        Assert.True(nullValue.IsNull);
        Assert.Equal("NULL", nullValue.DisplayText);
        Assert.False(emptyValue.IsNull);
        Assert.Equal(string.Empty, emptyValue.RawValue);
        Assert.Equal(string.Empty, emptyValue.DisplayText);
    }

    [Fact]
    public void Display_only_values_downgrade_the_result_column_to_read_only_other()
    {
        var columns = new[]
        {
            new DatabaseColumnDescriptor("amount", "NUMBER", DatabaseValueKind.Decimal),
            new DatabaseColumnDescriptor("note", "VARCHAR", DatabaseValueKind.Text),
        };
        IReadOnlyList<IReadOnlyList<DatabaseValue>> rows =
        [
            [
                new DatabaseValue("12.5", DatabaseValueKind.Other, "12.5"),
                new DatabaseValue("safe", DatabaseValueKind.Text, "safe"),
            ],
        ];

        var reconciled = DatabaseValueMaterializer.ReconcileColumnSafety(columns, rows);

        Assert.Equal(DatabaseValueKind.Other, reconciled[0].ValueKind);
        Assert.True(reconciled[0].IsReadOnly);
        Assert.Equal(DatabaseValueKind.Text, reconciled[1].ValueKind);
        Assert.False(reconciled[1].IsReadOnly);
    }

    private sealed class ProviderSpecificValue(decimal value) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) =>
            value.ToString(format, formatProvider);

        public override string ToString() =>
            value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ConvertibleDecimal(decimal value) : IConvertible
    {
        public TypeCode GetTypeCode() => TypeCode.Object;

        public decimal ToDecimal(IFormatProvider? provider) => value;

        public bool ToBoolean(IFormatProvider? provider) => throw new InvalidCastException();

        public byte ToByte(IFormatProvider? provider) => throw new InvalidCastException();

        public char ToChar(IFormatProvider? provider) => throw new InvalidCastException();

        public DateTime ToDateTime(IFormatProvider? provider) => throw new InvalidCastException();

        public double ToDouble(IFormatProvider? provider) => throw new InvalidCastException();

        public short ToInt16(IFormatProvider? provider) => throw new InvalidCastException();

        public int ToInt32(IFormatProvider? provider) => throw new InvalidCastException();

        public long ToInt64(IFormatProvider? provider) => throw new InvalidCastException();

        public sbyte ToSByte(IFormatProvider? provider) => throw new InvalidCastException();

        public float ToSingle(IFormatProvider? provider) => throw new InvalidCastException();

        public string ToString(IFormatProvider? provider) => value.ToString(provider);

        public object ToType(Type conversionType, IFormatProvider? provider) =>
            throw new InvalidCastException();

        public ushort ToUInt16(IFormatProvider? provider) => throw new InvalidCastException();

        public uint ToUInt32(IFormatProvider? provider) => throw new InvalidCastException();

        public ulong ToUInt64(IFormatProvider? provider) => throw new InvalidCastException();
    }

    private sealed class CountingNonSeekableStream(long length) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - BytesRead;
            if (remaining <= 0)
            {
                return 0;
            }

            var read = (int)Math.Min(remaining, count);
            Array.Fill(buffer, (byte)0x5A, offset, read);
            BytesRead += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingSeekableStream(long length) : Stream
    {
        public int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => Position;

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

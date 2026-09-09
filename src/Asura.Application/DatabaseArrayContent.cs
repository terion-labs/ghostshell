using System.Globalization;
using System.Numerics;
using System.Text;

namespace Asura.Application;

/// <summary>Closed scalar types in detached array content; never a CLR type name.</summary>
public enum DatabaseArrayScalarType : byte
{
    Text, Boolean, Byte, SByte, Int16, UInt16, Int32, UInt32, Int64, UInt64,
    Single, Double, Decimal, DateTime, DateTimeOffset, DateOnly, TimeOnly,
    Duration, Guid, Character, Half, Int128, UInt128, BigInteger,
}

public sealed record DatabaseArrayShape(
    DatabaseArrayScalarType ElementType, bool Nullable, int[] Lengths, int[] LowerBounds)
{
    public long Count => Lengths.Aggregate(1L, (count, length) => checked(count * length));
}

/// <summary>
/// A typed array's storage format. Length-prefixed text is visited as a stream,
/// so exporting an array containing a huge string never creates that string in
/// the UI process. Only the owned execution worker reconstructs provider arrays.
/// </summary>
public static class DatabaseArrayContent
{
    private const int Magic = 0x31415347;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static void WriteScalarValue(Stream destination, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var type = ResolveType(value.GetType());
        using var writer = new BinaryWriter(destination, Utf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write((byte)type);
        writer.Write(false);
        writer.Write((byte)1);
        writer.Write(1);
        writer.Write(0);
        writer.Write(true);
        WriteScalar(writer, type, value);
    }

    public static void Write(Stream destination, Array array, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(array);
        var element = array.GetType().GetElementType()
            ?? throw new NotSupportedException("The database array element type is unavailable.");
        var underlying = Nullable.GetUnderlyingType(element);
        var type = ResolveType(underlying ?? element);
        using var writer = new BinaryWriter(destination, Utf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write((byte)type);
        writer.Write(underlying is not null || element == typeof(string));
        writer.Write((byte)array.Rank);
        for (var dimension = 0; dimension < array.Rank; dimension++)
        {
            writer.Write(array.GetLength(dimension));
            writer.Write(array.GetLowerBound(dimension));
        }

        foreach (var value in array)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(value is not null);
            if (value is not null)
            {
                WriteScalar(writer, type, value);
            }
        }
    }

    public static DatabaseArrayShape ReadShape(Stream source)
    {
        using var reader = new BinaryReader(source, Utf8, leaveOpen: true);
        if (reader.ReadInt32() != Magic)
        {
            throw new InvalidDataException("The detached database array format is invalid.");
        }

        var type = (DatabaseArrayScalarType)reader.ReadByte();
        if (!Enum.IsDefined(type))
        {
            throw new InvalidDataException("The detached database array type is invalid.");
        }

        var nullable = reader.ReadByte();
        var rank = reader.ReadByte();
        if (nullable > 1 || rank is < 1 or > 32)
        {
            throw new InvalidDataException("The detached database array shape is invalid.");
        }

        var lengths = new int[rank];
        var bounds = new int[rank];
        long count = 1;
        for (var dimension = 0; dimension < rank; dimension++)
        {
            lengths[dimension] = reader.ReadInt32();
            bounds[dimension] = reader.ReadInt32();
            if (lengths[dimension] < 0
                || lengths[dimension] > 0 && (long)bounds[dimension] + lengths[dimension] - 1 > int.MaxValue
                || count > Array.MaxLength / Math.Max(1, lengths[dimension]))
            {
                throw new InvalidDataException("The detached database array dimensions are invalid.");
            }

            count *= lengths[dimension];
        }

        if (source.CanSeek && count > source.Length - source.Position)
        {
            throw new InvalidDataException("The detached database array payload is incomplete.");
        }

        return new DatabaseArrayShape(type, nullable != 0, lengths, bounds);
    }

    public static object? ReadScalar(Stream source, DatabaseArrayShape shape, Func<Stream, long, object?> readText)
    {
        using var reader = new BinaryReader(source, Utf8, leaveOpen: true);
        var present = reader.ReadByte();
        if (present > 1 || present == 0 && !shape.Nullable)
        {
            throw new InvalidDataException("The detached database array null marker is invalid.");
        }

        if (present == 0)
        {
            return null;
        }

        if (shape.ElementType is DatabaseArrayScalarType.Text or DatabaseArrayScalarType.Int128
            or DatabaseArrayScalarType.UInt128 or DatabaseArrayScalarType.BigInteger)
        {
            var length = reader.ReadInt64();
            if (length < 0 || source.CanSeek && length > source.Length - source.Position)
            {
                throw new InvalidDataException("The detached database array text length is invalid.");
            }

            using var text = new ScalarTextStream(source, length);
            var value = readText(text, length);
            if (text.Remaining != 0)
            {
                throw new InvalidDataException("The detached database array text was not consumed exactly.");
            }

            return value;
        }

        return shape.ElementType switch
        {
            DatabaseArrayScalarType.Boolean => reader.ReadByte() switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidDataException("The detached database array boolean is invalid."),
            },
            DatabaseArrayScalarType.Byte => reader.ReadByte(),
            DatabaseArrayScalarType.SByte => reader.ReadSByte(),
            DatabaseArrayScalarType.Int16 => reader.ReadInt16(),
            DatabaseArrayScalarType.UInt16 => reader.ReadUInt16(),
            DatabaseArrayScalarType.Int32 => reader.ReadInt32(),
            DatabaseArrayScalarType.UInt32 => reader.ReadUInt32(),
            DatabaseArrayScalarType.Int64 => reader.ReadInt64(),
            DatabaseArrayScalarType.UInt64 => reader.ReadUInt64(),
            DatabaseArrayScalarType.Single => reader.ReadSingle(),
            DatabaseArrayScalarType.Double => reader.ReadDouble(),
            DatabaseArrayScalarType.Decimal => reader.ReadDecimal(),
            DatabaseArrayScalarType.DateTime => DateTime.FromBinary(reader.ReadInt64()),
            DatabaseArrayScalarType.DateTimeOffset => new DateTimeOffset(reader.ReadInt64(), new TimeSpan(reader.ReadInt64())),
            DatabaseArrayScalarType.DateOnly => DateOnly.FromDayNumber(reader.ReadInt32()),
            DatabaseArrayScalarType.TimeOnly => new TimeOnly(reader.ReadInt64()),
            DatabaseArrayScalarType.Duration => new TimeSpan(reader.ReadInt64()),
            DatabaseArrayScalarType.Guid => new Guid(ReadExactly(reader, 16)),
            DatabaseArrayScalarType.Character => (char)reader.ReadUInt16(),
            DatabaseArrayScalarType.Half => BitConverter.UInt16BitsToHalf(reader.ReadUInt16()),
            _ => throw new InvalidDataException("The detached database array scalar is invalid."),
        };
    }

    // A text reader may prefetch. It must never consume the next array element.
    private sealed class ScalarTextStream(Stream source, long length) : Stream
    {
        public long Remaining { get; private set; } = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get => Length - Remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (Remaining == 0 || buffer.IsEmpty)
            {
                return 0;
            }

            var count = source.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            Remaining -= count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return bytes;
    }

    private static DatabaseArrayScalarType ResolveType(Type type)
    {
        var types = new Type[]
        {
            typeof(string), typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double),
            typeof(decimal), typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly),
            typeof(TimeSpan), typeof(Guid), typeof(char), typeof(Half), typeof(Int128), typeof(UInt128), typeof(BigInteger),
        };
        var index = Array.IndexOf(types, type);
        return index >= 0
            ? (DatabaseArrayScalarType)index
            : throw new NotSupportedException("This provider array element type has no detached representation.");
    }

    private static void WriteScalar(BinaryWriter writer, DatabaseArrayScalarType type, object value)
    {
        switch (type)
        {
            case DatabaseArrayScalarType.Text:
            case DatabaseArrayScalarType.Int128:
            case DatabaseArrayScalarType.UInt128:
            case DatabaseArrayScalarType.BigInteger:
                var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                writer.Write((long)Utf8.GetByteCount(text));
                using (var output = new StreamWriter(writer.BaseStream, Utf8, 8192, leaveOpen: true))
                {
                    output.Write(text);
                }
                break;
            case DatabaseArrayScalarType.Boolean: writer.Write((bool)value); break;
            case DatabaseArrayScalarType.Byte: writer.Write((byte)value); break;
            case DatabaseArrayScalarType.SByte: writer.Write((sbyte)value); break;
            case DatabaseArrayScalarType.Int16: writer.Write((short)value); break;
            case DatabaseArrayScalarType.UInt16: writer.Write((ushort)value); break;
            case DatabaseArrayScalarType.Int32: writer.Write((int)value); break;
            case DatabaseArrayScalarType.UInt32: writer.Write((uint)value); break;
            case DatabaseArrayScalarType.Int64: writer.Write((long)value); break;
            case DatabaseArrayScalarType.UInt64: writer.Write((ulong)value); break;
            case DatabaseArrayScalarType.Single: writer.Write((float)value); break;
            case DatabaseArrayScalarType.Double: writer.Write((double)value); break;
            case DatabaseArrayScalarType.Decimal: writer.Write((decimal)value); break;
            case DatabaseArrayScalarType.DateTime: writer.Write(((DateTime)value).ToBinary()); break;
            case DatabaseArrayScalarType.DateTimeOffset:
                writer.Write(((DateTimeOffset)value).Ticks);
                writer.Write(((DateTimeOffset)value).Offset.Ticks);
                break;
            case DatabaseArrayScalarType.DateOnly: writer.Write(((DateOnly)value).DayNumber); break;
            case DatabaseArrayScalarType.TimeOnly: writer.Write(((TimeOnly)value).Ticks); break;
            case DatabaseArrayScalarType.Duration: writer.Write(((TimeSpan)value).Ticks); break;
            case DatabaseArrayScalarType.Guid: writer.Write(((Guid)value).ToByteArray()); break;
            case DatabaseArrayScalarType.Character:
                if (char.IsSurrogate((char)value))
                {
                    throw new InvalidDataException("An isolated surrogate character cannot be exported as Unicode text.");
                }
                writer.Write((ushort)(char)value);
                break;
            case DatabaseArrayScalarType.Half: writer.Write(BitConverter.HalfToUInt16Bits((Half)value)); break;
            default: throw new InvalidDataException("The detached database array scalar is invalid.");
        }
    }
}

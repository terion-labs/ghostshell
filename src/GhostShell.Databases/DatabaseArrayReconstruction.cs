using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using GhostShell.Application;

namespace GhostShell.Databases;

/// <summary>Reconstructs provider parameters only inside the owned operation worker.</summary>
public static class DatabaseArrayReconstruction
{
    public static Array Read(Stream source, CancellationToken cancellationToken)
    {
        var shape = DatabaseArrayContent.ReadShape(source);
        if (!RuntimeFeature.IsDynamicCodeSupported && shape.LowerBounds.Any(bound => bound != 0))
        {
            throw new PlatformNotSupportedException(
                "This database array has nonzero lower bounds, which this NativeAOT runtime cannot represent. Its bounds have not been changed.");
        }

        var result = shape.ElementType switch
        {
            DatabaseArrayScalarType.Text => Create<string>(shape),
            DatabaseArrayScalarType.Boolean => CreateValue<bool>(shape),
            DatabaseArrayScalarType.Byte => CreateValue<byte>(shape),
            DatabaseArrayScalarType.SByte => CreateValue<sbyte>(shape),
            DatabaseArrayScalarType.Int16 => CreateValue<short>(shape),
            DatabaseArrayScalarType.UInt16 => CreateValue<ushort>(shape),
            DatabaseArrayScalarType.Int32 => CreateValue<int>(shape),
            DatabaseArrayScalarType.UInt32 => CreateValue<uint>(shape),
            DatabaseArrayScalarType.Int64 => CreateValue<long>(shape),
            DatabaseArrayScalarType.UInt64 => CreateValue<ulong>(shape),
            DatabaseArrayScalarType.Single => CreateValue<float>(shape),
            DatabaseArrayScalarType.Double => CreateValue<double>(shape),
            DatabaseArrayScalarType.Decimal => CreateValue<decimal>(shape),
            DatabaseArrayScalarType.DateTime => CreateValue<DateTime>(shape),
            DatabaseArrayScalarType.DateTimeOffset => CreateValue<DateTimeOffset>(shape),
            DatabaseArrayScalarType.DateOnly => CreateValue<DateOnly>(shape),
            DatabaseArrayScalarType.TimeOnly => CreateValue<TimeOnly>(shape),
            DatabaseArrayScalarType.Duration => CreateValue<TimeSpan>(shape),
            DatabaseArrayScalarType.Guid => CreateValue<Guid>(shape),
            DatabaseArrayScalarType.Character => CreateValue<char>(shape),
            DatabaseArrayScalarType.Half => CreateValue<Half>(shape),
            DatabaseArrayScalarType.Int128 => CreateValue<Int128>(shape),
            DatabaseArrayScalarType.UInt128 => CreateValue<UInt128>(shape),
            DatabaseArrayScalarType.BigInteger => CreateValue<BigInteger>(shape),
            _ => throw new InvalidDataException("The database array element type is unsupported."),
        };
        var indices = (int[])shape.LowerBounds.Clone();
        for (long index = 0; index < shape.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = DatabaseArrayContent.ReadScalar(source, shape, (text, _) =>
            {
                using var reader = new StreamReader(text, new UTF8Encoding(false, true));
                var complete = reader.ReadToEnd();
                return shape.ElementType switch
                {
                    DatabaseArrayScalarType.Int128 => Int128.Parse(complete, CultureInfo.InvariantCulture),
                    DatabaseArrayScalarType.UInt128 => UInt128.Parse(complete, CultureInfo.InvariantCulture),
                    DatabaseArrayScalarType.BigInteger => BigInteger.Parse(complete, CultureInfo.InvariantCulture),
                    _ => (object)complete,
                };
            });
            result.SetValue(value, indices);
            for (var dimension = indices.Length - 1; dimension >= 0; dimension--)
            {
                if ((long)indices[dimension] + 1 < (long)shape.LowerBounds[dimension] + shape.Lengths[dimension])
                {
                    indices[dimension]++;
                    break;
                }

                indices[dimension] = shape.LowerBounds[dimension];
            }
        }

        if (source.ReadByte() != -1)
        {
            throw new InvalidDataException("The database array has trailing payload bytes.");
        }

        return result;
    }

    private static Array CreateValue<T>(DatabaseArrayShape shape) where T : struct =>
        shape.Nullable ? Create<T?>(shape) : Create<T>(shape);

    private static Array Create<T>(DatabaseArrayShape shape)
    {
        // Closed generic array types are rooted for NativeAOT. No received CLR
        // name, reflection constructor, or dynamic-code suppression is involved.
        var type = shape.Lengths.Length switch
        {
            1 => typeof(T[]),
            2 => typeof(T[,]),
            3 => typeof(T[,,]),
            4 => typeof(T[,,,]),
            5 => typeof(T[,,,,]),
            6 => typeof(T[,,,,,]),
            7 => typeof(T[,,,,,,]),
            8 => typeof(T[,,,,,,,]),
            9 => typeof(T[,,,,,,,,]),
            10 => typeof(T[,,,,,,,,,]),
            11 => typeof(T[,,,,,,,,,,]),
            12 => typeof(T[,,,,,,,,,,,]),
            13 => typeof(T[,,,,,,,,,,,,]),
            14 => typeof(T[,,,,,,,,,,,,,]),
            15 => typeof(T[,,,,,,,,,,,,,,]),
            16 => typeof(T[,,,,,,,,,,,,,,,]),
            17 => typeof(T[,,,,,,,,,,,,,,,,]),
            18 => typeof(T[,,,,,,,,,,,,,,,,,]),
            19 => typeof(T[,,,,,,,,,,,,,,,,,,]),
            20 => typeof(T[,,,,,,,,,,,,,,,,,,,]),
            21 => typeof(T[,,,,,,,,,,,,,,,,,,,,]),
            22 => typeof(T[,,,,,,,,,,,,,,,,,,,,,]),
            23 => typeof(T[,,,,,,,,,,,,,,,,,,,,,,]),
            24 => typeof(T[,,,,,,,,,,,,,,,,,,,,,,,]),
            25 => typeof(T[,,,,,,,,,,,,,,,,,,,,,,,,]),
            26 => typeof(T[,,,,,,,,,,,,,,,,,,,,,,,,,]),
            27 => typeof(T[,,,,,,,,,,,,,,,,,,,,,,,,,,]),
            28 => typeof(T[,,,,,,,,,,,,,,,,,,,,,,,,,,,]),
            29 => typeof(T[,,,,,,,,,,,,,,,,,,,,,,,,,,,,]),
            30 => typeof(T[,,,,,,,,,,,,,,,,,,,,,,,,,,,,,]),
            31 => typeof(T[,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,]),
            32 => typeof(T[,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,]),
            _ => throw new InvalidDataException("The database array rank is invalid."),
        };
        if (shape.Lengths.Length == 1 && shape.LowerBounds[0] != 0)
        {
            type = NonVectorType(shape);
        }

        return Array.CreateInstanceFromArrayType(type, shape.Lengths, shape.LowerBounds);
    }

    private static Type NonVectorType(DatabaseArrayShape shape) => (shape.ElementType, shape.Nullable && shape.ElementType != DatabaseArrayScalarType.Text) switch
    {
        (DatabaseArrayScalarType.Text, false) => Type.GetType("System.String[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Boolean, false) => Type.GetType("System.Boolean[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Boolean, true) => Type.GetType("System.Nullable`1[[System.Boolean]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Byte, false) => Type.GetType("System.Byte[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Byte, true) => Type.GetType("System.Nullable`1[[System.Byte]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.SByte, false) => Type.GetType("System.SByte[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.SByte, true) => Type.GetType("System.Nullable`1[[System.SByte]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Int16, false) => Type.GetType("System.Int16[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Int16, true) => Type.GetType("System.Nullable`1[[System.Int16]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.UInt16, false) => Type.GetType("System.UInt16[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.UInt16, true) => Type.GetType("System.Nullable`1[[System.UInt16]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Int32, false) => Type.GetType("System.Int32[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Int32, true) => Type.GetType("System.Nullable`1[[System.Int32]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.UInt32, false) => Type.GetType("System.UInt32[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.UInt32, true) => Type.GetType("System.Nullable`1[[System.UInt32]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Int64, false) => Type.GetType("System.Int64[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Int64, true) => Type.GetType("System.Nullable`1[[System.Int64]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.UInt64, false) => Type.GetType("System.UInt64[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.UInt64, true) => Type.GetType("System.Nullable`1[[System.UInt64]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Single, false) => Type.GetType("System.Single[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Single, true) => Type.GetType("System.Nullable`1[[System.Single]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Double, false) => Type.GetType("System.Double[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Double, true) => Type.GetType("System.Nullable`1[[System.Double]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Decimal, false) => Type.GetType("System.Decimal[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Decimal, true) => Type.GetType("System.Nullable`1[[System.Decimal]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.DateTime, false) => Type.GetType("System.DateTime[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.DateTime, true) => Type.GetType("System.Nullable`1[[System.DateTime]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.DateTimeOffset, false) => Type.GetType("System.DateTimeOffset[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.DateTimeOffset, true) => Type.GetType("System.Nullable`1[[System.DateTimeOffset]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.DateOnly, false) => Type.GetType("System.DateOnly[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.DateOnly, true) => Type.GetType("System.Nullable`1[[System.DateOnly]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.TimeOnly, false) => Type.GetType("System.TimeOnly[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.TimeOnly, true) => Type.GetType("System.Nullable`1[[System.TimeOnly]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Duration, false) => Type.GetType("System.TimeSpan[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Duration, true) => Type.GetType("System.Nullable`1[[System.TimeSpan]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Guid, false) => Type.GetType("System.Guid[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Guid, true) => Type.GetType("System.Nullable`1[[System.Guid]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Character, false) => Type.GetType("System.Char[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Character, true) => Type.GetType("System.Nullable`1[[System.Char]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Half, false) => Type.GetType("System.Half[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Half, true) => Type.GetType("System.Nullable`1[[System.Half]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Int128, false) => Type.GetType("System.Int128[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.Int128, true) => Type.GetType("System.Nullable`1[[System.Int128]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.UInt128, false) => Type.GetType("System.UInt128[*]", throwOnError: true)!,
        (DatabaseArrayScalarType.UInt128, true) => Type.GetType("System.Nullable`1[[System.UInt128]][*]", throwOnError: true)!,
        (DatabaseArrayScalarType.BigInteger, false) => Type.GetType("System.Numerics.BigInteger[*], System.Runtime.Numerics", throwOnError: true)!,
        (DatabaseArrayScalarType.BigInteger, true) => Type.GetType("System.Nullable`1[[System.Numerics.BigInteger, System.Runtime.Numerics]][*]", throwOnError: true)!,
        _ => throw new InvalidDataException("The database array element type is unsupported."),
    };
}

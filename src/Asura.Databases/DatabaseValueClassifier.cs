using System.Net;
using System.Numerics;
using System.Text.Json;
using Asura.Application;

namespace Asura.Databases;

/// <summary>
/// Maps provider CLR types and database type names onto the small set of value
/// behaviours understood by the viewer. CLR types are authoritative; provider
/// names fill gaps such as JSON values represented as strings.
/// </summary>
internal static class DatabaseValueClassifier
{
    public static DatabaseValueKind Classify(
        Type? declaredType,
        string? providerTypeName,
        object? value = null)
    {
        var runtimeType = value is null or DBNull ? null : value.GetType();
        var type = Nullable.GetUnderlyingType(runtimeType ?? declaredType ?? typeof(object))
            ?? runtimeType
            ?? declaredType
            ?? typeof(object);

        var clrKind = ClassifyClrType(type);
        if (clrKind is DatabaseValueKind.TimestampWithZone
            or DatabaseValueKind.Date
            or DatabaseValueKind.Time
            or DatabaseValueKind.Duration
            or DatabaseValueKind.Guid
            or DatabaseValueKind.Binary
            or DatabaseValueKind.Json
            or DatabaseValueKind.Network
            or DatabaseValueKind.Collection)
        {
            return clrKind;
        }

        var providerKind = ClassifyProviderType(providerTypeName);
        return providerKind == DatabaseValueKind.Other ? clrKind : providerKind;
    }

    private static DatabaseValueKind ClassifyClrType(Type type)
    {
        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        if (type == typeof(string) || type == typeof(char) || type == typeof(char[]))
        {
            return DatabaseValueKind.Text;
        }

        if (type == typeof(bool))
        {
            return DatabaseValueKind.Boolean;
        }

        if (type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(int)
            || type == typeof(long)
            || type == typeof(Int128)
            || type == typeof(BigInteger))
        {
            return DatabaseValueKind.SignedInteger;
        }

        if (type == typeof(byte)
            || type == typeof(ushort)
            || type == typeof(uint)
            || type == typeof(ulong)
            || type == typeof(UInt128))
        {
            return DatabaseValueKind.UnsignedInteger;
        }

        if (type == typeof(decimal))
        {
            return DatabaseValueKind.Decimal;
        }

        if (type == typeof(Half) || type == typeof(float) || type == typeof(double))
        {
            return DatabaseValueKind.FloatingPoint;
        }

        if (type == typeof(DateOnly))
        {
            return DatabaseValueKind.Date;
        }

        if (type == typeof(TimeOnly))
        {
            return DatabaseValueKind.Time;
        }

        if (type == typeof(DateTimeOffset))
        {
            return DatabaseValueKind.TimestampWithZone;
        }

        if (type == typeof(DateTime))
        {
            return DatabaseValueKind.Timestamp;
        }

        if (type == typeof(TimeSpan))
        {
            return DatabaseValueKind.Duration;
        }

        if (type == typeof(Guid))
        {
            return DatabaseValueKind.Guid;
        }

        if (type == typeof(byte[])
            || type == typeof(Memory<byte>)
            || type == typeof(ReadOnlyMemory<byte>))
        {
            return DatabaseValueKind.Binary;
        }

        if (type == typeof(JsonDocument) || type == typeof(JsonElement))
        {
            return DatabaseValueKind.Json;
        }

        if (typeof(IPAddress).IsAssignableFrom(type))
        {
            return DatabaseValueKind.Network;
        }

        return type.IsArray ? DatabaseValueKind.Collection : DatabaseValueKind.Other;
    }

    private static DatabaseValueKind ClassifyProviderType(string? providerTypeName)
    {
        if (string.IsNullOrWhiteSpace(providerTypeName))
        {
            return DatabaseValueKind.Other;
        }

        var type = providerTypeName.Trim().ToLowerInvariant();
        if (type.Contains("json", StringComparison.Ordinal))
        {
            return DatabaseValueKind.Json;
        }

        if (ContainsAny(type, "inet", "cidr", "macaddr", "ipaddress")
            || type.StartsWith("ipv4", StringComparison.Ordinal)
            || type.StartsWith("ipv6", StringComparison.Ordinal))
        {
            return DatabaseValueKind.Network;
        }

        if (ContainsAny(type, "uuid", "guid", "uniqueidentifier"))
        {
            return DatabaseValueKind.Guid;
        }

        if (type.EndsWith("[]", StringComparison.Ordinal)
            || ContainsAny(type, "array", "list(", "map(", "tuple("))
        {
            return DatabaseValueKind.Collection;
        }

        if (ContainsAny(type, "blob", "binary", "bytea", "varbyte", "long raw", "image")
            || string.Equals(type, "raw"
, StringComparison.Ordinal) || type.StartsWith("raw(", StringComparison.Ordinal))
        {
            return DatabaseValueKind.Binary;
        }

        if (ContainsAny(type, "timestamptz", "datetimeoffset")
            || (type.Contains("timestamp", StringComparison.Ordinal)
                && type.Contains("time zone", StringComparison.Ordinal)))
        {
            return DatabaseValueKind.TimestampWithZone;
        }

        if (ContainsAny(type, "timestamp", "datetime", "smalldatetime"))
        {
            return DatabaseValueKind.Timestamp;
        }

        if (ContainsAny(type, "interval", "duration"))
        {
            return DatabaseValueKind.Duration;
        }

        if (string.Equals(type, "date", StringComparison.Ordinal) || type.StartsWith("date(", StringComparison.Ordinal))
        {
            return DatabaseValueKind.Date;
        }

        if (string.Equals(type, "time"
, StringComparison.Ordinal) || type.StartsWith("time(", StringComparison.Ordinal)
            || type.StartsWith("time ", StringComparison.Ordinal))
        {
            return DatabaseValueKind.Time;
        }

        if (ContainsAny(type, "bool", "boolean")
            || type is "bit" or "bit(1)" or "tinyint(1)")
        {
            return DatabaseValueKind.Boolean;
        }

        if (ContainsAny(type, "unsigned", "uint"))
        {
            return DatabaseValueKind.UnsignedInteger;
        }

        if (ContainsAny(type, "int", "serial"))
        {
            return DatabaseValueKind.SignedInteger;
        }

        if (ContainsAny(type, "decimal", "numeric", "number", "money", "decfloat"))
        {
            return DatabaseValueKind.Decimal;
        }

        if (ContainsAny(type, "float", "double", "real"))
        {
            return DatabaseValueKind.FloatingPoint;
        }

        return ContainsAny(type, "char", "text", "clob", "string", "xml", "enum8", "enum16")
            ? DatabaseValueKind.Text
            : DatabaseValueKind.Other;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));
}

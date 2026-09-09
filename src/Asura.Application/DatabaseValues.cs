using System.Globalization;

namespace Asura.Application;

/// <summary>
/// The small set of value behaviours the database viewer needs. Provider type
/// names remain available for diagnostics; editors depend on this semantic kind.
/// </summary>
public enum DatabaseValueKind
{
    Other,
    Text,
    Boolean,
    SignedInteger,
    UnsignedInteger,
    Decimal,
    FloatingPoint,
    Date,
    Time,
    Timestamp,
    TimestampWithZone,
    Duration,
    Guid,
    Binary,
    Json,
    Network,
    Collection,
}

/// <summary>
/// One materialized query value. <see cref="RawValue"/> contains only detached
/// CLR values that remain valid after the provider reader and connection close.
/// </summary>
public sealed record DatabaseValue(
    object? RawValue,
    DatabaseValueKind Kind,
    string DisplayText,
    bool IsTruncated = false)
{
    public bool IsNull => RawValue is null;

    public static DatabaseValue FromDisplay(string? value, DatabaseValueKind kind = DatabaseValueKind.Text) =>
        value is null
            ? new DatabaseValue(null, kind, "NULL")
            : new DatabaseValue(value, kind, value);

    public string ToInvariantText() => RawValue switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        DateTime value => value.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset value => value.ToString("O", CultureInfo.InvariantCulture),
        DateOnly value => value.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly value => value.ToString("O", CultureInfo.InvariantCulture),
        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
        _ => RawValue.ToString() ?? string.Empty,
    };
}

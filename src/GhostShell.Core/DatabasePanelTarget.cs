namespace GhostShell.Core;

/// <summary>
/// Legacy or explicitly supplied database panel address: a driver id and its
/// connection string. New runtime recovery persists a confidential vault token,
/// never this raw representation. Existing local addresses remain readable.
/// The first colon splits the two, so driver ids never contain one.
/// </summary>
public sealed record DatabasePanelTarget(string DriverId, string ConnectionString)
{
    public string Serialize() => $"{DriverId}:{ConnectionString}";

    public static DatabasePanelTarget? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            return null;
        }

        var driverId = value[..separator];
        var connectionString = value[(separator + 1)..];
        return driverId.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(connectionString)
            ? null
            : new DatabasePanelTarget(driverId, connectionString);
    }
}

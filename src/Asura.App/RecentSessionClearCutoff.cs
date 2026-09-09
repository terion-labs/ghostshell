namespace Asura.App;

/// <summary>
/// Freezes the confirmation time for a selective history clear. Records completed after this
/// cutoff remain visible even if persistence finishes later.
/// </summary>
public sealed record RecentSessionClearCutoff
{
    internal RecentSessionClearCutoff(DateTimeOffset throughUtc)
    {
        ThroughUtc = throughUtc.ToUniversalTime();
    }

    public DateTimeOffset ThroughUtc { get; }
}

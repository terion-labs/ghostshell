namespace Asura.Application;

public sealed record ProcessMonitorQuery(
    int MaximumResults = ProcessMonitorQuery.DefaultMaximumResults,
    ProcessMonitorSort Sort = ProcessMonitorSort.CpuDescending,
    int Offset = 0,
    string? NameContains = null,
    int? ProcessId = null)
{
    public const int DefaultMaximumResults = 250;
    public const int MaximumAllowedResults = 512;

    public bool IsValid =>
        MaximumResults is > 0 and <= MaximumAllowedResults
        && Enum.IsDefined(Sort)
        && Offset is >= 0 and <= 1_000_000
        && (NameContains is null
            || NameContains.Length is > 0 and <= 128
            && !NameContains.Any(char.IsControl))
        && ProcessId is null or > 0;
}

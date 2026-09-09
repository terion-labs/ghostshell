using Asura.AccessibilityAcceptance;

namespace Asura.SoakAcceptance;

internal enum SoakStatus
{
    Pass,
    Fail,
    Blocked,
}

internal sealed record SoakScenario(
    string Id,
    string Title,
    string LoadUnit,
    string Instructions,
    int ExpectedAbruptExits);

internal sealed record ScenarioBudget(
    string Id,
    int DurationSeconds,
    int RequiredLoad,
    string LoadUnit,
    long MaximumWorkingSetGrowthBytes,
    int MaximumFailures,
    int CleanupTimeoutSeconds,
    int MaximumLiveProcessesAfterCleanup);

internal sealed record SoakPolicy(
    int SchemaVersion,
    string PolicyKind,
    string PolicyVersion,
    string ReferenceConfigurationId,
    DateTimeOffset RatifiedAtUtc,
    IReadOnlyList<ScenarioBudget> Scenarios);

internal sealed record SoakHost(
    string ReferenceConfigurationId,
    string HostFingerprint,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    int LogicalProcessorCount,
    string PowerSource);

internal sealed record ResourceObservation(
    int SampleCount,
    long InitialWorkingSetBytes,
    long PeakWorkingSetBytes,
    long FinalWorkingSetBytes,
    long WorkingSetGrowthBytes,
    long CpuTimeMilliseconds,
    int PeakLiveProcessCount,
    int CapturedProcessCount);

internal sealed record ScenarioObservation(
    string Id,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int CompletedLoad,
    int ObservedFailures,
    int AbruptExits,
    SoakStatus OperatorResult,
    SoakStatus MachineResult,
    IReadOnlyList<string> FailureCodes,
    ResourceObservation Resources,
    bool CleanupPassed);

internal sealed record SoakReceipt(
    int SchemaVersion,
    string EvidenceKind,
    string RunnerVersion,
    string CatalogSha256,
    string PolicySha256,
    SoakPolicy Policy,
    SoakHost Host,
    BuildIdentity Build,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    SoakStatus OverallResult,
    bool PackageUnchanged,
    IReadOnlyList<ScenarioObservation> Scenarios);

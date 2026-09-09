namespace Asura.Application.ApplicationUpdates;

public sealed record ApplicationUpdateSnapshot(
    DistributionIdentity Distribution,
    ApplicationUpdateStage Stage,
    string? AvailableVersion = null,
    int? DownloadProgress = null,
    ApplicationUpdateError Error = ApplicationUpdateError.None,
    bool ApplyAllowed = true)
{
    public bool CanCheck => Stage is ApplicationUpdateStage.Idle
        or ApplicationUpdateStage.UpToDate
        or ApplicationUpdateStage.Available
        or ApplicationUpdateStage.Failed;

    public bool CanDownload =>
        Stage == ApplicationUpdateStage.Available && ApplyAllowed;

    public bool CanRestartToApply =>
        Stage == ApplicationUpdateStage.ReadyToRestart && ApplyAllowed;
}

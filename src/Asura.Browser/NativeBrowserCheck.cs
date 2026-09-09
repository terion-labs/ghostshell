namespace Asura.Browser;

internal sealed record NativeBrowserCheckResult
{
    private NativeBrowserCheckResult(NativeBrowserCheckStatus status)
    {
        Status = Enum.IsDefined(status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(status));
    }

    public NativeBrowserCheckStatus Status { get; }

    public static NativeBrowserCheckResult Checked() =>
        new(NativeBrowserCheckStatus.Checked);

    public static NativeBrowserCheckResult Stale() =>
        new(NativeBrowserCheckStatus.Stale);

    public static NativeBrowserCheckResult NotInteractable() =>
        new(NativeBrowserCheckStatus.NotInteractable);

    public static NativeBrowserCheckResult NotCheckable() =>
        new(NativeBrowserCheckStatus.NotCheckable);

    public static NativeBrowserCheckResult Unchecked() =>
        new(NativeBrowserCheckStatus.Unchecked);

    public static NativeBrowserCheckResult OutcomeUnknown() =>
        new(NativeBrowserCheckStatus.OutcomeUnknown);
}

internal enum NativeBrowserCheckStatus
{
    Checked,
    Stale,
    NotInteractable,
    NotCheckable,
    Unchecked,
    OutcomeUnknown,
}

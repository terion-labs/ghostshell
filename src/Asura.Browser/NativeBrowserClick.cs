namespace Asura.Browser;

internal sealed record NativeBrowserClickResult
{
    private NativeBrowserClickResult(NativeBrowserClickStatus status)
    {
        Status = Enum.IsDefined(status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(status));
    }

    public NativeBrowserClickStatus Status { get; }

    public static NativeBrowserClickResult Activated() =>
        new(NativeBrowserClickStatus.Activated);

    public static NativeBrowserClickResult Stale() =>
        new(NativeBrowserClickStatus.Stale);

    public static NativeBrowserClickResult NotInteractable() =>
        new(NativeBrowserClickStatus.NotInteractable);

    public static NativeBrowserClickResult OutcomeUnknown() =>
        new(NativeBrowserClickStatus.OutcomeUnknown);
}

internal enum NativeBrowserClickStatus
{
    Activated,
    Stale,
    NotInteractable,
    OutcomeUnknown,
}

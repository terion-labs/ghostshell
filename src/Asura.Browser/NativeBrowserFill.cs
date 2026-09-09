namespace Asura.Browser;

internal sealed record NativeBrowserFillResult
{
    private NativeBrowserFillResult(NativeBrowserFillStatus status)
    {
        Status = Enum.IsDefined(status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(status));
    }

    public NativeBrowserFillStatus Status { get; }

    public static NativeBrowserFillResult Filled() =>
        new(NativeBrowserFillStatus.Filled);

    public static NativeBrowserFillResult Stale() =>
        new(NativeBrowserFillStatus.Stale);

    public static NativeBrowserFillResult NotInteractable() =>
        new(NativeBrowserFillStatus.NotInteractable);

    public static NativeBrowserFillResult NotFillable() =>
        new(NativeBrowserFillStatus.NotFillable);

    public static NativeBrowserFillResult OutcomeUnknown() =>
        new(NativeBrowserFillStatus.OutcomeUnknown);

    public static NativeBrowserFillResult ValueNotSupported() =>
        new(NativeBrowserFillStatus.ValueNotSupported);
}

internal enum NativeBrowserFillStatus
{
    Filled,
    Stale,
    NotInteractable,
    NotFillable,
    OutcomeUnknown,
    ValueNotSupported,
}

namespace Asura.Browser;

internal sealed record NativeBrowserElementState(
    bool Visible,
    bool Enabled,
    bool Checked,
    bool Selected,
    bool Editable,
    bool Focused);

internal sealed record NativeBrowserElementStateResult
{
    private NativeBrowserElementStateResult(
        NativeBrowserElementState? value,
        NativeBrowserElementStateFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public NativeBrowserElementState? Value { get; }

    public NativeBrowserElementStateFailure? Failure { get; }

    public bool IsSuccess => Value is not null;

    public static NativeBrowserElementStateResult Success(
        NativeBrowserElementState value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), null);

    public static NativeBrowserElementStateResult Stale() =>
        new(null, NativeBrowserElementStateFailure.Stale);

    public static NativeBrowserElementStateResult Unavailable() =>
        new(null, NativeBrowserElementStateFailure.Unavailable);
}

internal enum NativeBrowserElementStateFailure
{
    Stale,
    Unavailable,
}

internal sealed record NativeBrowserNetworkActivity(
    bool IsObservable,
    int ActiveRequestCount,
    TimeSpan QuietFor);

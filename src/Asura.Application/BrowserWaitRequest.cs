using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// One closed browser wait condition. A request contains exactly one variant;
/// provider-defined property bags cannot broaden the wait at execution time.
/// </summary>
public abstract record BrowserWaitCondition
{
    private BrowserWaitCondition()
    {
    }

    public sealed record Delay(TimeSpan Value) : BrowserWaitCondition;

    public sealed record LoadState(BrowserLoadState Value) : BrowserWaitCondition;

    public sealed record UrlPattern(string Value) : BrowserWaitCondition;

    public sealed record Text(string Value) : BrowserWaitCondition;

    public sealed record ElementState(
        BrowserElementReferenceId Reference,
        long SourceDocumentRevision,
        BrowserElementStateKind State,
        bool Expected) : BrowserWaitCondition;

    public sealed record DocumentRevision(long After) : BrowserWaitCondition;

    public sealed record NetworkIdle(TimeSpan QuietFor) : BrowserWaitCondition;
}

public sealed record BrowserWaitRequest
{
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromHours(1);
    public const int MaximumTextBytes = 2_048;
    public const int MaximumUrlPatternBytes = 2_048;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public BrowserWaitRequest(
        SessionId sessionId,
        BrowserWaitCondition condition,
        TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(sessionId.Value))
        {
            throw new ArgumentException(
                "A browser wait requires a session ID.",
                nameof(sessionId));
        }

        Condition = ValidateCondition(
            condition ?? throw new ArgumentNullException(nameof(condition)),
            timeout);
        if (timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        SessionId = sessionId;
        Timeout = timeout;
    }

    public SessionId SessionId { get; }

    public BrowserWaitCondition Condition { get; }

    public TimeSpan Timeout { get; }

    private static BrowserWaitCondition ValidateCondition(
        BrowserWaitCondition condition,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        switch (condition)
        {
            case BrowserWaitCondition.Delay delay
                when delay.Value <= TimeSpan.Zero || delay.Value > timeout:
                throw new ArgumentOutOfRangeException(nameof(condition));
            case BrowserWaitCondition.LoadState loadState
                when !Enum.IsDefined(loadState.Value):
                throw new ArgumentOutOfRangeException(nameof(condition));
            case BrowserWaitCondition.UrlPattern pattern:
                ValidateText(
                    pattern.Value,
                    MaximumUrlPatternBytes,
                    allowEmpty: false,
                    nameof(condition));
                break;
            case BrowserWaitCondition.Text text:
                ValidateText(
                    text.Value,
                    MaximumTextBytes,
                    allowEmpty: false,
                    nameof(condition));
                break;
            case BrowserWaitCondition.ElementState element:
                if (element.SourceDocumentRevision < 0
                    || !Enum.IsDefined(element.State))
                {
                    throw new ArgumentOutOfRangeException(nameof(condition));
                }

                break;
            case BrowserWaitCondition.DocumentRevision revision
                when revision.After < 0:
                throw new ArgumentOutOfRangeException(nameof(condition));
            case BrowserWaitCondition.NetworkIdle idle
                when idle.QuietFor <= TimeSpan.Zero || idle.QuietFor > timeout:
                throw new ArgumentOutOfRangeException(nameof(condition));
            case BrowserWaitCondition.Delay
                or BrowserWaitCondition.LoadState
                or BrowserWaitCondition.DocumentRevision
                or BrowserWaitCondition.NetworkIdle:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(condition),
                    condition.GetType(),
                    "The browser wait condition is unsupported.");
        }

        return condition;
    }

    private static void ValidateText(
        string value,
        int maximumBytes,
        bool allowEmpty,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        try
        {
            if ((!allowEmpty && value.Length == 0)
                || value.Contains('\0', StringComparison.Ordinal)
                || StrictUtf8.GetByteCount(value) > maximumBytes)
            {
                throw new ArgumentException(
                    "Browser wait text is invalid or too large.",
                    parameterName);
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Browser wait text must contain valid Unicode.",
                parameterName,
                exception);
        }
    }
}

public enum BrowserElementStateKind
{
    Visible,
    Enabled,
    Checked,
    Selected,
    Editable,
    Focused,
}

public sealed record BrowserElementStateSnapshot(
    BrowserDocumentBinding Document,
    bool Visible,
    bool Enabled,
    bool Checked,
    bool Selected,
    bool Editable,
    bool Focused)
{
    public bool Read(BrowserElementStateKind state) => state switch
    {
        BrowserElementStateKind.Visible => Visible,
        BrowserElementStateKind.Enabled => Enabled,
        BrowserElementStateKind.Checked => Checked,
        BrowserElementStateKind.Selected => Selected,
        BrowserElementStateKind.Editable => Editable,
        BrowserElementStateKind.Focused => Focused,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

public sealed record BrowserNetworkActivitySnapshot
{
    public BrowserNetworkActivitySnapshot(
        bool isObservable,
        int activeRequestCount,
        TimeSpan quietFor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeRequestCount);
        if (quietFor < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietFor));
        }

        IsObservable = isObservable;
        ActiveRequestCount = activeRequestCount;
        QuietFor = quietFor;
    }

    public bool IsObservable { get; }

    public int ActiveRequestCount { get; }

    public TimeSpan QuietFor { get; }
}

public enum BrowserWaitCompletion
{
    Matched,
    TimedOut,
    Cancelled,
    SessionEnded,
}

/// <summary>
/// A wait always ends with a fresh state read. When the final document is
/// ready, it also carries a snapshot bound to that same state; otherwise the
/// typed snapshot error explains why no semantic document was available.
/// </summary>
public sealed record BrowserWaitOutcome
{
    public BrowserWaitOutcome(
        BrowserWaitCompletion completion,
        BrowserSessionState state,
        BrowserDocumentSnapshot? snapshot,
        BrowserError? snapshotError,
        DateTimeOffset completedAtUtc)
    {
        if (!Enum.IsDefined(completion))
        {
            throw new ArgumentOutOfRangeException(nameof(completion));
        }

        State = state ?? throw new ArgumentNullException(nameof(state));
        if ((snapshot is null) == (snapshotError is null))
        {
            throw new ArgumentException(
                "A browser wait outcome requires either a final snapshot or its typed error.",
                nameof(snapshot));
        }

        if (snapshot is not null && !snapshot.Document.Matches(state))
        {
            throw new ArgumentException(
                "The final browser snapshot must match the final state.",
                nameof(snapshot));
        }

        Completion = completion;
        Snapshot = snapshot;
        SnapshotError = snapshotError;
        CompletedAtUtc = completedAtUtc;
    }

    public BrowserWaitCompletion Completion { get; }

    public BrowserSessionState State { get; }

    public BrowserDocumentSnapshot? Snapshot { get; }

    public BrowserError? SnapshotError { get; }

    public DateTimeOffset CompletedAtUtc { get; }
}

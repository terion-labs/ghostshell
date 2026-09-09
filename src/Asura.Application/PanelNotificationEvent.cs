using System.Text;

namespace Asura.Application;

/// <summary>What produced a request for the user's attention.</summary>
public enum PanelNotificationKind
{
    /// <summary>
    /// An explicit desktop notification — OSC 9 or OSC 777. Something in the
    /// panel decided this was worth interrupting for, and said what about.
    /// </summary>
    Notification,

    /// <summary>
    /// The terminal bell. It carries no message, so it means only "look at
    /// me" — which is exactly what a long build or an agent waiting on input
    /// tends to send.
    /// </summary>
    Bell,

    /// <summary>An AI-agent turn completed successfully.</summary>
    AgentCompleted,

    /// <summary>An AI-agent turn ended in failure.</summary>
    AgentFailed,

    /// <summary>A file transfer completed successfully.</summary>
    FileTransferCompleted,

    /// <summary>A file transfer failed.</summary>
    FileTransferFailed,
}

/// <summary>
/// Independent effects requested by a notification producer. A system-only
/// terminal bell, for example, must not leave a visual unread mark behind.
/// </summary>
[Flags]
public enum PanelNotificationEffects
{
    None = 0,
    Visual = 1,
    System = 2,
}

/// <summary>
/// A panel or workspace-owned activity asking to be noticed.
///
/// Deliberately not a <see cref="PanelSessionEvent"/>: those describe where a
/// session is in its lifecycle, and every consumer of them treats an event as a
/// state change. This is neither a state nor a change to one — it is a moment,
/// and a consumer that misses it has missed it.
/// </summary>
/// <param name="Sequence">
/// Monotonic per producer, so a watcher can discard a duplicate observation.
/// </param>
/// <param name="Title">
/// What the notification called itself. Empty when the protocol carried only a
/// body, and always empty for a bell.
/// </param>
/// <param name="Body">The message, empty for a bell.</param>
public sealed record PanelNotificationEvent(
    long Sequence,
    PanelNotificationKind Kind,
    string Title,
    string Body,
    DateTimeOffset TimestampUtc)
{
    /// <summary>
    /// Visual is the compatibility default for existing panel producers. A
    /// producer opts into native delivery explicitly so ordinary state events
    /// can never begin interrupting the user by accident.
    /// </summary>
    public PanelNotificationEffects Effects { get; init; } =
        PanelNotificationEffects.Visual;
}

/// <summary>
/// UTF-8 byte limits for notification text crossing the terminal, shell, and
/// native-notification boundaries.
/// </summary>
public static class PanelNotificationTextBudget
{
    public const int MaximumTitleUtf8Bytes = 4 * 1024;
    public const int MaximumBodyUtf8Bytes = 16 * 1024;

    public static int Measure(PanelNotificationEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return checked(
            Encoding.UTF8.GetByteCount(notification.Title)
            + Encoding.UTF8.GetByteCount(notification.Body));
    }

    public static PanelNotificationEvent Clamp(PanelNotificationEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var title = Truncate(notification.Title, MaximumTitleUtf8Bytes);
        var body = Truncate(notification.Body, MaximumBodyUtf8Bytes);
        return ReferenceEquals(title, notification.Title)
            && ReferenceEquals(body, notification.Body)
                ? notification
                : notification with { Title = title, Body = body };
    }

    public static string TruncateTitle(string value) =>
        Truncate(value, MaximumTitleUtf8Bytes);

    public static string TruncateBody(string value) =>
        Truncate(value, MaximumBodyUtf8Bytes);

    private static string Truncate(string value, int maximumUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes)
        {
            return value;
        }

        var bytes = 0;
        var characters = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumUtf8Bytes)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }

        return value[..characters];
    }
}

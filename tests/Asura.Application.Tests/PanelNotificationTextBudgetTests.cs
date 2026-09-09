using System.Text;

namespace Asura.Application.Tests;

public sealed class PanelNotificationTextBudgetTests
{
    [Fact]
    public void Clamp_uses_utf8_bytes_and_never_splits_a_unicode_scalar()
    {
        var oversized = string.Concat(Enumerable.Repeat(
            "\U0001F680",
            (PanelNotificationTextBudget.MaximumBodyUtf8Bytes / 4) + 1));
        var notification = new PanelNotificationEvent(
            1,
            PanelNotificationKind.Notification,
            "title",
            oversized,
            DateTimeOffset.UnixEpoch);

        var bounded = PanelNotificationTextBudget.Clamp(notification);

        Assert.Equal(
            PanelNotificationTextBudget.MaximumBodyUtf8Bytes,
            Encoding.UTF8.GetByteCount(bounded.Body));
        Assert.EndsWith("\U0001F680", bounded.Body, StringComparison.Ordinal);
    }
}

using Asura.App;

namespace Asura.App.Tests;

public sealed class ShellClipboardTests
{
    [Fact]
    public async Task Text_is_forwarded_to_the_native_presentation()
    {
        string? written = null;
        var clipboard = new ShellClipboard(
            new ShellClipboardPresentation(text =>
            {
                written = text;
                return Task.CompletedTask;
            }),
            CancellationToken.None);

        await clipboard.WriteTextAsync("copy me");

        Assert.Equal("copy me", written);
    }

    [Fact]
    public async Task Cancelled_shell_lifetime_prevents_native_clipboard_access()
    {
        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        var writes = 0;
        var clipboard = new ShellClipboard(
            new ShellClipboardPresentation(_ =>
            {
                writes++;
                return Task.CompletedTask;
            }),
            lifetime.Token);

        await clipboard.WriteTextAsync("copy me");

        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task Unavailable_native_clipboard_does_not_escape_the_ui_event(
        Type exceptionType)
    {
        var clipboard = new ShellClipboard(
            new ShellClipboardPresentation(_ => Task.FromException(
                exceptionType == typeof(InvalidOperationException)
                    ? new InvalidOperationException()
                    : new ObjectDisposedException("clipboard"))),
            CancellationToken.None);

        await clipboard.WriteTextAsync("copy me");
    }
}

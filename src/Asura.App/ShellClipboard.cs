namespace Asura.App;

internal sealed record ShellClipboardPresentation(
    Func<string, Task> WriteTextAsync);

/// <summary>
/// Applies the shell lifetime and native-clipboard failure policy consistently
/// for text copied from any route.
/// </summary>
internal sealed class ShellClipboard(
    ShellClipboardPresentation presentation,
    CancellationToken lifetime)
{
    public async Task WriteTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await presentation.WriteTextAsync(text);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }
}

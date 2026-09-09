using System.Diagnostics;
using Avalonia.Input.Platform;

namespace Asura.App.Controls;

internal interface IManagedTerminalClipboard
{
    ValueTask<string?> TryGetTextAsync(CancellationToken cancellationToken);

    ValueTask SetTextAsync(string text, CancellationToken cancellationToken);
}

internal interface IManagedTerminalLinkOpener
{
    ValueTask OpenAsync(Uri uri, CancellationToken cancellationToken);
}

internal sealed class AvaloniaManagedTerminalClipboard(
    Func<IClipboard?> resolveClipboard) : IManagedTerminalClipboard
{
    private readonly Func<IClipboard?> _resolveClipboard =
        resolveClipboard ?? throw new ArgumentNullException(nameof(resolveClipboard));

    public async ValueTask<string?> TryGetTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = _resolveClipboard();
        return clipboard is null ? null : await clipboard.TryGetTextAsync();
    }

    public async ValueTask SetTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (_resolveClipboard() is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}

internal sealed class SystemManagedTerminalLinkOpener : IManagedTerminalLinkOpener
{
    public ValueTask OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        return ValueTask.CompletedTask;
    }
}

internal static class ManagedTerminalLinks
{
    public static bool TryCreateAllowedUri(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}

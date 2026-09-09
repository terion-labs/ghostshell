using Asura.App;

namespace Asura.Desktop;

/// <summary>
/// Chooses the host's screen colour sampler. Only macOS exposes a system picker
/// that needs no screen-capture permission, so elsewhere the shell reports none
/// and falls back to sampling its own window.
/// </summary>
internal static class ScreenColorSamplerSelector
{
    public static IScreenColorSampler Create() =>
        OperatingSystem.IsMacOS()
            ? new MacOsScreenColorSampler()
            : new UnavailableScreenColorSampler();
}

internal sealed class UnavailableScreenColorSampler : IScreenColorSampler
{
    public bool IsAvailable => false;

    public ValueTask<Avalonia.Media.Color?> SampleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<Avalonia.Media.Color?>(null);
}

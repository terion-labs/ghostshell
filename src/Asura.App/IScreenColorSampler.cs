using Avalonia.Media;

namespace Asura.App;

/// <summary>
/// Picks a colour from anywhere on screen, outside this application's windows.
/// </summary>
public interface IScreenColorSampler
{
    /// <summary>
    /// Whether this host can sample the screen. Where it cannot, the caller falls
    /// back to sampling the application's own window rather than offering a
    /// control that does nothing.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Shows the host's colour sampler and returns the chosen colour, or
    /// <c>null</c> when the user cancels.
    /// </summary>
    ValueTask<Color?> SampleAsync(CancellationToken cancellationToken);
}

using Avalonia;

namespace Asura.App;

/// <summary>
/// Reads the bounds of the operating system's foreground window, including
/// windows owned by applications other than Asura.
/// </summary>
public interface IActiveWindowBoundsSource
{
    /// <summary>
    /// Returns the foreground window in the desktop's global pixel coordinate
    /// space, or <c>null</c> when the host cannot expose that information.
    /// </summary>
    PixelRect? TryGetBounds();
}

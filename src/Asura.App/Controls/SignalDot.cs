using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Asura.App.Controls;

/// <summary>
/// A small mark saying something here wants you.
///
/// The shell draws this shape in a dozen places as an inline seven-pixel
/// <c>Ellipse</c> — panel headers, connection rows, the Quick Terminal's
/// registration status — each with its own size and its own idea of the colour.
/// This is the shared one, and unlike those it carries a ring in the page
/// colour: an attention dot sits on top of whatever it is marking, including a
/// saturated workspace tile, and without the ring it disappears into the ones
/// it happens to match.
/// </summary>
internal sealed class SignalDot : TemplatedControl
{
}

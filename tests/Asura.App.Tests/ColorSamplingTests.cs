using Asura.App;
using Avalonia;

namespace Asura.App.Tests;

public sealed class ColorSamplingTests
{
    // Sampling itself needs a windowing platform, so it is exercised end to end
    // through the design-QA harness rather than here; this covers the guard.
    [Fact]
    public void Sampling_requires_a_window() =>
        Assert.Throws<ArgumentNullException>(() => ColorSampling.Sample(null!, default));
}

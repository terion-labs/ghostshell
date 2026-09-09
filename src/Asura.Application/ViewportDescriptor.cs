namespace Asura.Application;

public sealed record ViewportDescriptor(
    double LogicalWidth,
    double LogicalHeight,
    double RenderScale,
    int? Columns = null,
    int? Rows = null)
{
    public static ViewportDescriptor Empty { get; } = new(0, 0, 1);
}

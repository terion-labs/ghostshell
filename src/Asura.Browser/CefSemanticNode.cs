using System.Runtime.InteropServices;

namespace Asura.Browser;

internal sealed record CefSemanticNode(
    string Id,
    int? BackendNodeId,
    bool IsIgnored,
    string Role,
    string Name,
    string? ParentId,
    IReadOnlyList<string> ChildIds,
    IReadOnlyDictionary<string, string> Properties,
    string Value);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct CefSemanticPoint(
    double X,
    double Y,
    double Width = 100,
    double Height = 40)
{
    public double TargetWidth => Math.Clamp(Math.Min(Width, Height), 4, 200);
}

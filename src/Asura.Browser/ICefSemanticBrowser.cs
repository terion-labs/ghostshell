namespace Asura.Browser;

/// <summary>
/// The narrow, private CDP seam used by semantic automation. Production uses
/// CEF's acknowledged DevTools methods; tests use a deterministic fake.
/// </summary>
internal interface ICefSemanticBrowser
{
    Task<IReadOnlyList<CefSemanticNode>> ReadAccessibilityTreeAsync();

    Task<CefSemanticNode?> ReadAccessibilityNodeAsync(int backendNodeId);

    Task<CefSemanticPoint?> PrepareClickPointAsync(int backendNodeId);

    Task<bool> HitTestIncludesAsync(
        CefSemanticPoint point,
        int backendNodeId);

    Task<bool> DispatchClickAsync(
        CefSemanticPoint point,
        int backendNodeId);

    Task ReplaceFocusedTextAsync(int backendNodeId, string text);

    Task<bool> IsVisibleAsync(int backendNodeId);
}

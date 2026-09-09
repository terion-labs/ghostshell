using System.Text.Json;
using Asura.Application;

namespace Asura.Browser.Tests;

public sealed class CefBrowserSemanticAdapterTests
{
    [Fact]
    public void CheckboxKeyboardActivationIncludesSpaceTextForChromium()
    {
        using var parameters = JsonDocument.Parse(
            CefSemanticBrowser.SerializeKeyEvent(
                "keyDown",
                " ",
                "Space",
                32,
                text: " "));

        Assert.Equal(
            " ",
            parameters.RootElement.GetProperty("text").GetString());
        Assert.Equal(
            " ",
            parameters.RootElement.GetProperty("unmodifiedText").GetString());
        Assert.Equal(
            "Space",
            parameters.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SnapshotProjectsBoundedAxNodesAndOpaqueActionLeases()
    {
        var browser = BrowserWithInteractiveNodes();
        var adapter = new CefBrowserSemanticAdapter(browser);

        var result = await adapter.CaptureSnapshotAsync();

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value!.Nodes,
            root =>
            {
                Assert.Equal("root-web-area", root.Role);
                Assert.Null(root.Handle);
            },
            button =>
            {
                Assert.Equal("button", button.Role);
                Assert.NotNull(button.Handle);
                Assert.StartsWith("el_", button.Handle!.ElementToken);
                Assert.NotEqual("2", button.Handle.ElementToken, StringComparer.Ordinal);
            },
            textbox =>
            {
                Assert.Equal("textbox", textbox.Role);
                Assert.Equal(
                    BrowserSnapshotNodeState.Required,
                    textbox.States);
                Assert.NotNull(textbox.Handle);
            },
            checkbox =>
            {
                Assert.Equal("checkbox", checkbox.Role);
                Assert.NotNull(checkbox.Handle);
            });
    }

    [Fact]
    public async Task FilterRunsBeforeNodeCapAndReturnsAUsableLateReference()
    {
        var childIds = Enumerable.Range(2, 700)
            .Select(value => value.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var nodes = new List<CefSemanticNode>
        {
            Node(
                1,
                "RootWebArea",
                "Large results page",
                parentId: null,
                childIds: childIds),
        };
        nodes.AddRange(
            Enumerable.Range(2, 699)
                .Select(value => Node(
                    value,
                    "button",
                    $"Header action {value}")));
        nodes.Add(Node(701, "link", "Target result"));
        var browser = new RecordingCefSemanticBrowser(nodes);
        var adapter = new CefBrowserSemanticAdapter(browser);

        var result = await adapter.CaptureSnapshotAsync(
            new BrowserSnapshotQuery(filter: "Target result"));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsTruncated);
        Assert.Collection(
            result.Value.Nodes,
            root => Assert.Equal("root-web-area", root.Role),
            target =>
            {
                Assert.Equal("link", target.Role);
                Assert.Equal("Target result", target.Name);
                Assert.NotNull(target.Handle);
            });

        var click = await adapter.ClickAsync(result.Value.Nodes[1].Handle!);
        Assert.Equal(NativeBrowserClickStatus.Activated, click.Status);
        Assert.Equal(1, browser.ClickDispatchCount);
    }

    [Fact]
    public async Task InteractiveSnapshotOmitsDecorativeTextButKeepsAncestors()
    {
        var browser = new RecordingCefSemanticBrowser(
        [
            Node(
                1,
                "RootWebArea",
                "Example",
                parentId: null,
                childIds: ["2", "3"]),
            Node(2, "StaticText", "Decorative copy"),
            Node(3, "link", "Continue"),
        ]);
        var adapter = new CefBrowserSemanticAdapter(browser);

        var result = await adapter.CaptureSnapshotAsync(
            new BrowserSnapshotQuery(interactiveOnly: true));

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value!.Nodes,
            root => Assert.Equal("root-web-area", root.Role),
            link => Assert.Equal("Continue", link.Name));
    }

    [Fact]
    public async Task ASecondSnapshotInvalidatesEveryPriorNativeLease()
    {
        var browser = BrowserWithInteractiveNodes();
        var adapter = new CefBrowserSemanticAdapter(browser);
        var first = await adapter.CaptureSnapshotAsync();
        var oldHandle = first.Value!.Nodes[1].Handle!;

        _ = await adapter.CaptureSnapshotAsync();
        var result = await adapter.ClickAsync(oldHandle);

        Assert.Equal(NativeBrowserClickStatus.Stale, result.Status);
        Assert.Equal(0, browser.ClickDispatchCount);
    }

    [Fact]
    public async Task IdentityChangeBeforeDispatchFailsStaleWithoutInput()
    {
        var browser = BrowserWithInteractiveNodes();
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();
        var handle = snapshot.Value!.Nodes[1].Handle!;
        browser.SetNode(Node(2, "button", "Replaced"));

        var result = await adapter.ClickAsync(handle);

        Assert.Equal(NativeBrowserClickStatus.Stale, result.Status);
        Assert.Equal(0, browser.ClickDispatchCount);
    }

    [Fact]
    public async Task GeometryOrHitTestFailureBeforeDispatchIsNotAmbiguous()
    {
        var browser = BrowserWithInteractiveNodes();
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();
        var handle = snapshot.Value!.Nodes[1].Handle!;
        browser.HitTestResult = false;

        var result = await adapter.ClickAsync(handle);

        Assert.Equal(
            NativeBrowserClickStatus.NotInteractable,
            result.Status);
        Assert.Equal(0, browser.ClickDispatchCount);
    }

    [Fact]
    public async Task SemanticClickRetriesGeometryAfterScrollingAnOffscreenElement()
    {
        var browser = BrowserWithInteractiveNodes();
        browser.HitTestFailuresBeforeSuccess = 1;
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();

        var result = await adapter.ClickAsync(
            snapshot.Value!.Nodes[1].Handle!);

        Assert.Equal(NativeBrowserClickStatus.Activated, result.Status);
        Assert.Equal(2, browser.PrepareClickPointCount);
        Assert.Equal(1, browser.ClickDispatchCount);
    }

    [Fact]
    public async Task FailureAfterInputDispatchBeginsIsOutcomeUnknown()
    {
        var browser = BrowserWithInteractiveNodes();
        browser.ThrowDuringClickDispatch = true;
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();

        var result = await adapter.ClickAsync(
            snapshot.Value!.Nodes[1].Handle!);

        Assert.Equal(
            NativeBrowserClickStatus.OutcomeUnknown,
            result.Status);
        Assert.Equal(1, browser.ClickDispatchCount);
    }

    [Fact]
    public async Task FillRequiresExactObservedValueAfterAcknowledgedInput()
    {
        var browser = BrowserWithInteractiveNodes();
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();

        var result = await adapter.FillAsync(
            snapshot.Value!.Nodes[2].Handle!,
            "Ada");

        Assert.Equal(NativeBrowserFillStatus.Filled, result.Status);
        Assert.Equal(1, browser.ReplaceTextCount);
        Assert.Equal("Ada", browser.ReadNode(3)?.Value);
    }

    [Fact]
    public async Task FillMismatchIsKnownUnsupportedValueNotSuccess()
    {
        var browser = BrowserWithInteractiveNodes();
        browser.PreserveValueOnReplace = true;
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();

        var result = await adapter.FillAsync(
            snapshot.Value!.Nodes[2].Handle!,
            "Ada");

        Assert.Equal(
            NativeBrowserFillStatus.ValueNotSupported,
            result.Status);
        Assert.Equal(1, browser.ReplaceTextCount);
    }

    [Fact]
    public async Task CheckRequiresTheCheckedPostcondition()
    {
        var browser = BrowserWithInteractiveNodes();
        browser.CheckNodeAfterClick = 4;
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();

        var result = await adapter.CheckAsync(
            snapshot.Value!.Nodes[3].Handle!);

        Assert.Equal(NativeBrowserCheckStatus.Checked, result.Status);
        Assert.Equal(1, browser.ClickDispatchCount);
        Assert.Equal("true", browser.ReadNode(4)?.Properties["checked"]);
    }

    [Fact]
    public async Task CheckWithoutPostconditionReportsObservedUncheckedState()
    {
        var browser = BrowserWithInteractiveNodes();
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();

        var result = await adapter.CheckAsync(
            snapshot.Value!.Nodes[3].Handle!);

        Assert.Equal(
            NativeBrowserCheckStatus.Unchecked,
            result.Status);
        Assert.Equal(1, browser.ClickDispatchCount);
    }

    [Fact]
    public async Task CheckUsesExactlyOneStateChangingActivation()
    {
        var browser = BrowserWithInteractiveNodes();
        browser.CheckNodeAfterClick = 4;
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();

        var result = await adapter.CheckAsync(
            snapshot.Value!.Nodes[3].Handle!);

        Assert.Equal(NativeBrowserCheckStatus.Checked, result.Status);
        Assert.Equal(1, browser.ClickDispatchCount);
        Assert.Equal("true", browser.ReadNode(4)?.Properties["checked"]);
    }

    [Fact]
    public async Task ElementStateObservationRevalidatesWithoutConsumingTheOpaqueLease()
    {
        var browser = BrowserWithInteractiveNodes();
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();
        var handle = snapshot.Value!.Nodes[3].Handle!;

        var first = await adapter.ReadElementStateAsync(handle);
        var second = await adapter.ReadElementStateAsync(handle);
        var click = await adapter.ClickAsync(handle);

        Assert.True(first.IsSuccess);
        Assert.True(first.Value!.Visible);
        Assert.True(first.Value.Enabled);
        Assert.False(first.Value.Checked);
        Assert.True(second.IsSuccess);
        Assert.Equal(NativeBrowserClickStatus.Activated, click.Status);
        Assert.Equal(1, browser.ClickDispatchCount);
    }

    [Fact]
    public async Task ElementStateIdentityDriftReturnsStaleWithoutInput()
    {
        var browser = BrowserWithInteractiveNodes();
        var adapter = new CefBrowserSemanticAdapter(browser);
        var snapshot = await adapter.CaptureSnapshotAsync();
        var handle = snapshot.Value!.Nodes[1].Handle!;
        browser.SetNode(Node(2, "button", "Replacement"));

        var result = await adapter.ReadElementStateAsync(handle);

        Assert.False(result.IsSuccess);
        Assert.Equal(NativeBrowserElementStateFailure.Stale, result.Failure);
        Assert.Equal(0, browser.ClickDispatchCount);
    }

    private static RecordingCefSemanticBrowser BrowserWithInteractiveNodes() =>
        new(
        [
            Node(
                1,
                "RootWebArea",
                "Example",
                parentId: null,
                childIds: ["2", "3", "4"]),
            Node(2, "button", "Continue", parentId: "1"),
            Node(
                3,
                "textbox",
                "Name",
                parentId: "1",
                properties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["required"] = "true",
                }),
            Node(
                4,
                "checkbox",
                "Remember",
                parentId: "1",
                properties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["checked"] = "false",
                }),
        ]);

    private static CefSemanticNode Node(
        int backendNodeId,
        string role,
        string name,
        string? parentId = "1",
        IReadOnlyList<string>? childIds = null,
        IReadOnlyDictionary<string, string>? properties = null,
        string value = "") =>
        new(
            backendNodeId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            backendNodeId,
            IsIgnored: false,
            role,
            name,
            parentId,
            childIds ?? [],
            properties
                ?? new Dictionary<string, string>(StringComparer.Ordinal),
            value);

    private sealed class RecordingCefSemanticBrowser(
        IReadOnlyList<CefSemanticNode> initialNodes)
        : ICefSemanticBrowser
    {
        private readonly Dictionary<int, CefSemanticNode> _nodes =
            initialNodes.ToDictionary(node => node.BackendNodeId!.Value);

        public bool HitTestResult { get; set; } = true;

        public int HitTestFailuresBeforeSuccess { get; set; }

        public bool ThrowDuringClickDispatch { get; set; }

        public bool PreserveValueOnReplace { get; set; }

        public int? CheckNodeAfterClick { get; set; }

        public int ClickDispatchCount { get; private set; }

        public int PrepareClickPointCount { get; private set; }

        public int ReplaceTextCount { get; private set; }

        public Task<IReadOnlyList<CefSemanticNode>>
            ReadAccessibilityTreeAsync() =>
            Task.FromResult<IReadOnlyList<CefSemanticNode>>(
                [.. _nodes.Values.OrderBy(node => node.BackendNodeId)]);

        public Task<CefSemanticNode?> ReadAccessibilityNodeAsync(
            int backendNodeId) =>
            Task.FromResult(ReadNode(backendNodeId));

        public Task<CefSemanticPoint?> PrepareClickPointAsync(
            int backendNodeId)
        {
            PrepareClickPointCount++;
            return Task.FromResult<CefSemanticPoint?>(new(20, 30));
        }

        public Task<bool> HitTestIncludesAsync(
            CefSemanticPoint point,
            int backendNodeId)
        {
            if (HitTestFailuresBeforeSuccess > 0)
            {
                HitTestFailuresBeforeSuccess--;
                return Task.FromResult(false);
            }

            return Task.FromResult(HitTestResult);
        }

        public Task<bool> DispatchClickAsync(
            CefSemanticPoint point,
            int backendNodeId)
        {
            ClickDispatchCount++;
            if (ThrowDuringClickDispatch)
            {
                throw new InvalidOperationException("release was not acknowledged");
            }

            if (CheckNodeAfterClick is { } nodeId
                && _nodes.TryGetValue(nodeId, out var node))
            {
                var properties = node.Properties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
                properties["checked"] = "true";
                _nodes[nodeId] = node with { Properties = properties };
            }

            return Task.FromResult(true);
        }

        public Task ReplaceFocusedTextAsync(int backendNodeId, string text)
        {
            ReplaceTextCount++;
            if (!PreserveValueOnReplace
                && _nodes.TryGetValue(backendNodeId, out var node))
            {
                _nodes[backendNodeId] = node with { Value = text };
            }

            return Task.CompletedTask;
        }

        public Task<bool> IsVisibleAsync(int backendNodeId) =>
            Task.FromResult(true);

        public CefSemanticNode? ReadNode(int backendNodeId) =>
            _nodes.GetValueOrDefault(backendNodeId);

        public void SetNode(CefSemanticNode node) =>
            _nodes[node.BackendNodeId!.Value] = node;
    }
}

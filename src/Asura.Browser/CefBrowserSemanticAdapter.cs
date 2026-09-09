using System.Security.Cryptography;
using System.Text;
using Asura.Application;

namespace Asura.Browser;

/// <summary>
/// Converts Chromium's accessibility tree into bounded Asura snapshots
/// and owns the short-lived native leases behind public opaque references.
/// </summary>
internal sealed class CefBrowserSemanticAdapter(ICefSemanticBrowser browser)
{
    private const int VerificationAttempts = 12;
    private const int InteractionGeometryAttempts = 3;
    private static readonly TimeSpan VerificationDelay =
        TimeSpan.FromMilliseconds(20);
    private static readonly HashSet<string> ActionableRoles =
        new(StringComparer.Ordinal)
        {
            "button",
            "checkbox",
            "combobox",
            "link",
            "menuitem",
            "menuitemcheckbox",
            "menuitemradio",
            "option",
            "radio",
            "searchbox",
            "slider",
            "spinbutton",
            "switch",
            "tab",
            "textbox",
        };
    private static readonly HashSet<string> FillableRoles =
        new(StringComparer.Ordinal)
        {
            "combobox",
            "searchbox",
            "spinbutton",
            "textbox",
        };
    private static readonly HashSet<string> CheckableRoles =
        new(StringComparer.Ordinal)
        {
            "checkbox",
            "menuitemcheckbox",
            "menuitemradio",
            "radio",
            "switch",
        };
    private static readonly HashSet<string> LeanSemanticRoles =
        new(StringComparer.Ordinal)
        {
            "alert",
            "article",
            "blockquote",
            "caption",
            "cell",
            "column-header",
            "content-info",
            "dialog",
            "document",
            "figure",
            "form",
            "heading",
            "image",
            "list",
            "list-item",
            "main",
            "navigation",
            "paragraph",
            "region",
            "root-web-area",
            "row",
            "row-header",
            "search",
            "static-text",
            "status",
            "table",
            "term",
            "tree-item",
            "web-area",
        };

    private readonly object _gate = new();
    private readonly ICefSemanticBrowser _browser = browser
        ?? throw new ArgumentNullException(nameof(browser));
    private Dictionary<string, ElementLease> _leases = [];
    private string? _snapshotNonce;
    private long _mutationEpoch;

    public async Task<NativeBrowserSnapshotResult> CaptureSnapshotAsync(
        BrowserSnapshotQuery? query = null)
    {
        query ??= BrowserSnapshotQuery.Lean;
        IReadOnlyList<CefSemanticNode> source;
        try
        {
            source = await _browser.ReadAccessibilityTreeAsync()
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return NativeBrowserSnapshotResult.Unavailable();
        }

        try
        {
            return BuildSnapshot(source, query);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException
                or EncoderFallbackException)
        {
            InvalidateDocument();
            return NativeBrowserSnapshotResult.Invalid();
        }
    }

    public async Task<NativeBrowserClickResult> ClickAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!TryGetLease(handle, out var lease))
        {
            return NativeBrowserClickResult.Stale();
        }

        CefSemanticNode? current;
        try
        {
            current = await RevalidateAsync(lease).ConfigureAwait(false);
        }
        catch (Exception)
        {
            ConsumeLeases();
            return NativeBrowserClickResult.Stale();
        }

        if (current is null)
        {
            ConsumeLeases();
            return NativeBrowserClickResult.Stale();
        }

        if (!ActionableRoles.Contains(NormalizeRole(current.Role))
            || IsUnavailable(current))
        {
            ConsumeLeases();
            return NativeBrowserClickResult.NotInteractable();
        }

        CefSemanticPoint? point;
        try
        {
            point = await PrepareVerifiedClickPointAsync(lease)
                .ConfigureAwait(false);
            if (point is null)
            {
                ConsumeLeases();
                return NativeBrowserClickResult.NotInteractable();
            }
        }
        catch (Exception)
        {
            ConsumeLeases();
            return NativeBrowserClickResult.NotInteractable();
        }

        try
        {
            if (!await _browser.DispatchClickAsync(
                    point.Value,
                    lease.BackendNodeId)
                    .ConfigureAwait(false))
            {
                ConsumeLeases();
                return NativeBrowserClickResult.NotInteractable();
            }

            ConsumeLeases();
            return NativeBrowserClickResult.Activated();
        }
        catch (Exception)
        {
            // Mouse movement or button-down may already have reached the page.
            ConsumeLeases();
            return NativeBrowserClickResult.OutcomeUnknown();
        }
    }

    public async Task<NativeBrowserFillResult> FillAsync(
        NativeBrowserElementHandle handle,
        string text)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(text);
        if (!TryGetLease(handle, out var lease))
        {
            return NativeBrowserFillResult.Stale();
        }

        CefSemanticNode? current;
        try
        {
            current = await RevalidateAsync(lease).ConfigureAwait(false);
        }
        catch (Exception)
        {
            ConsumeLeases();
            return NativeBrowserFillResult.Stale();
        }

        if (current is null)
        {
            ConsumeLeases();
            return NativeBrowserFillResult.Stale();
        }

        var role = NormalizeRole(current.Role);
        if (!FillableRoles.Contains(role))
        {
            ConsumeLeases();
            return NativeBrowserFillResult.NotFillable();
        }

        if (IsUnavailable(current) || PropertyIsTrue(current, "readonly"))
        {
            ConsumeLeases();
            return NativeBrowserFillResult.NotInteractable();
        }

        try
        {
            await _browser.ReplaceFocusedTextAsync(
                    lease.BackendNodeId,
                    text)
                .ConfigureAwait(false);
            var verification = await ReadVerifiedNodeAsync(
                    lease,
                    node => string.Equals(
                        node.Value,
                        text,
                        StringComparison.Ordinal))
                .ConfigureAwait(false);
            ConsumeLeases();
            if (verification is null)
            {
                return NativeBrowserFillResult.OutcomeUnknown();
            }

            return string.Equals(
                    verification.Value,
                    text,
                    StringComparison.Ordinal)
                ? NativeBrowserFillResult.Filled()
                : NativeBrowserFillResult.ValueNotSupported();
        }
        catch (Exception)
        {
            ConsumeLeases();
            return NativeBrowserFillResult.OutcomeUnknown();
        }
    }

    public async Task<NativeBrowserCheckResult> CheckAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!TryGetLease(handle, out var lease))
        {
            return NativeBrowserCheckResult.Stale();
        }

        CefSemanticNode? current;
        try
        {
            current = await RevalidateAsync(lease).ConfigureAwait(false);
        }
        catch (Exception)
        {
            ConsumeLeases();
            return NativeBrowserCheckResult.Stale();
        }

        if (current is null)
        {
            ConsumeLeases();
            return NativeBrowserCheckResult.Stale();
        }

        if (!CheckableRoles.Contains(NormalizeRole(current.Role)))
        {
            ConsumeLeases();
            return NativeBrowserCheckResult.NotCheckable();
        }

        if (IsUnavailable(current))
        {
            ConsumeLeases();
            return NativeBrowserCheckResult.NotInteractable();
        }

        if (PropertyIsTrue(current, "checked"))
        {
            ConsumeLeases();
            return NativeBrowserCheckResult.Checked();
        }

        CefSemanticPoint? point;
        try
        {
            point = await PrepareVerifiedClickPointAsync(lease)
                .ConfigureAwait(false);
            if (point is null)
            {
                ConsumeLeases();
                return NativeBrowserCheckResult.NotInteractable();
            }
        }
        catch (Exception)
        {
            ConsumeLeases();
            return NativeBrowserCheckResult.NotInteractable();
        }

        try
        {
            // A postcondition operation must issue exactly one toggle. The
            // former pointer-then-keyboard fallback could check the control
            // and immediately uncheck it while Chromium's AX tree was still
            // publishing the first change. Dispatch one verified pointer
            // activation and wait only for its observable postcondition.
            if (!await _browser.DispatchClickAsync(
                    point.Value,
                    lease.BackendNodeId)
                    .ConfigureAwait(false))
            {
                ConsumeLeases();
                return NativeBrowserCheckResult.NotInteractable();
            }

            var verification = await ReadVerifiedNodeAsync(
                    lease,
                    node => PropertyIsTrue(node, "checked"))
                .ConfigureAwait(false);

            ConsumeLeases();
            if (verification is null)
            {
                return NativeBrowserCheckResult.OutcomeUnknown();
            }

            return PropertyIsTrue(verification, "checked")
                ? NativeBrowserCheckResult.Checked()
                : NativeBrowserCheckResult.Unchecked();
        }
        catch (Exception)
        {
            ConsumeLeases();
            return NativeBrowserCheckResult.OutcomeUnknown();
        }
    }

    public async Task<NativeBrowserElementStateResult> ReadElementStateAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!TryGetLease(handle, out var lease))
        {
            return NativeBrowserElementStateResult.Stale();
        }

        try
        {
            var current = await RevalidateAsync(lease).ConfigureAwait(false);
            if (current is null)
            {
                return NativeBrowserElementStateResult.Stale();
            }

            var disabled = PropertyIsTrue(current, "disabled");
            var readOnly = PropertyIsTrue(current, "readonly");
            var editable = FillableRoles.Contains(NormalizeRole(current.Role))
                && !disabled
                && !readOnly;
            return NativeBrowserElementStateResult.Success(
                new NativeBrowserElementState(
                    await _browser.IsVisibleAsync(lease.BackendNodeId)
                        .ConfigureAwait(false),
                    !disabled,
                    PropertyIsTrue(current, "checked"),
                    PropertyIsTrue(current, "selected"),
                    editable,
                    PropertyIsTrue(current, "focused")));
        }
        catch (Exception)
        {
            return NativeBrowserElementStateResult.Unavailable();
        }
    }

    private async Task<CefSemanticPoint?> PrepareVerifiedClickPointAsync(
        ElementLease lease)
    {
        for (var attempt = 0; attempt < InteractionGeometryAttempts; attempt++)
        {
            var point = await _browser
                .PrepareClickPointAsync(lease.BackendNodeId)
                .ConfigureAwait(false);
            if (point is not null
                && await _browser.HitTestIncludesAsync(
                        point.Value,
                        lease.BackendNodeId)
                    .ConfigureAwait(false))
            {
                return point;
            }

            if (attempt + 1 < InteractionGeometryAttempts)
            {
                await Task.Delay(VerificationDelay).ConfigureAwait(false);
            }
        }

        return null;
    }

    public void InvalidateDocument()
    {
        lock (_gate)
        {
            _leases = [];
            _snapshotNonce = null;
            AdvanceMutationEpochUnsafe();
        }
    }

    private NativeBrowserSnapshotResult BuildSnapshot(
        IReadOnlyList<CefSemanticNode> source,
        BrowserSnapshotQuery query)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        if (source.Count == 0)
        {
            return NativeBrowserSnapshotResult.Invalid();
        }

        var byId = source
            .Where(node => !string.IsNullOrWhiteSpace(node.Id))
            .GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        if (byId.Count == 0)
        {
            return NativeBrowserSnapshotResult.Invalid();
        }

        var nonce = NewIdentifier("sn_");
        long epoch;
        lock (_gate)
        {
            epoch = _mutationEpoch;
        }

        var candidates = new List<SnapshotCandidate>(source.Count);
        var selected = new HashSet<int>();
        var output = new List<NativeBrowserSnapshotNode>(
            Math.Min(source.Count, BrowserDocumentSnapshot.MaximumNodeCount));
        var leases = new Dictionary<string, ElementLease>(
            StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        void Visit(
            CefSemanticNode node,
            int depth,
            int? parentCandidate,
            bool insideActionable)
        {
            if (!visited.Add(node.Id))
            {
                return;
            }

            var childDepth = depth;
            var childParent = parentCandidate;
            var childInsideActionable = insideActionable;
            if (!node.IsIgnored)
            {
                if (depth > BrowserSnapshotNode.MaximumDepth)
                {
                    truncated = true;
                    return;
                }

                var role = NormalizeRole(node.Role);
                var name = TruncateUtf8(
                    node.Name,
                    BrowserSnapshotNode.MaximumNameBytes);
                var actionable = node.BackendNodeId is not null
                    && ActionableRoles.Contains(role);
                if (ShouldIncludeLeanNode(
                    node,
                    role,
                    name,
                    parentCandidate,
                    insideActionable,
                    candidates))
                {
                    childParent = candidates.Count;
                    candidates.Add(
                        new SnapshotCandidate(
                            depth,
                            role,
                            name,
                            ProjectStates(node),
                            node.BackendNodeId,
                            node.Role,
                            node.Name,
                            actionable,
                            parentCandidate));
                    childDepth = depth + 1;
                }

                childInsideActionable |= actionable;
            }

            foreach (var childId in node.ChildIds)
            {
                if (byId.TryGetValue(childId, out var child))
                {
                    Visit(
                        child,
                        childDepth,
                        childParent,
                        childInsideActionable);
                }
            }
        }

        foreach (var node in source.Where(node => node.ParentId is null))
        {
            Visit(
                node,
                depth: 0,
                parentCandidate: null,
                insideActionable: false);
        }

        foreach (var node in source)
        {
            if (!visited.Contains(node.Id))
            {
                Visit(
                    node,
                    depth: 0,
                    parentCandidate: null,
                    insideActionable: false);
            }
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (query.MaximumDepth is { } maximumDepth
                && candidate.Depth > maximumDepth)
            {
                continue;
            }

            if (query.InteractiveOnly && !candidate.IsActionable)
            {
                continue;
            }

            if (query.Filter is { } filter
                && !candidate.Role.Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase)
                && !candidate.Name.Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SelectWithAncestors(index);
        }

        if (selected.Count == 0)
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                if (candidates[index].ParentIndex is null)
                {
                    selected.Add(index);
                }
            }
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            if (!selected.Contains(index))
            {
                continue;
            }

            if (output.Count >= BrowserDocumentSnapshot.MaximumNodeCount)
            {
                truncated = true;
                break;
            }

            var candidate = candidates[index];
            NativeBrowserElementHandle? handle = null;
            if (candidate.IsActionable
                && candidate.BackendNodeId is { } backendNodeId)
            {
                var token = NewUniqueToken(leases);
                handle = new NativeBrowserElementHandle(
                    nonce,
                    token,
                    epoch);
                leases.Add(
                    token,
                    new ElementLease(
                        backendNodeId,
                        candidate.LeaseRole,
                        candidate.LeaseName,
                        nonce,
                        epoch));
            }

            output.Add(
                new NativeBrowserSnapshotNode(
                    candidate.Depth,
                    candidate.Role,
                    candidate.Name,
                    candidate.States,
                    handle));
        }

        if (output.Count == 0)
        {
            return NativeBrowserSnapshotResult.Invalid();
        }

        lock (_gate)
        {
            _snapshotNonce = nonce;
            _leases = leases;
        }

        return NativeBrowserSnapshotResult.Success(
            new NativeBrowserSnapshot(output, truncated));

        void SelectWithAncestors(int index)
        {
            int? current = index;
            while (current is { } candidateIndex
                && selected.Add(candidateIndex))
            {
                current = candidates[candidateIndex].ParentIndex;
            }
        }
    }

    private static bool ShouldIncludeLeanNode(
        CefSemanticNode node,
        string role,
        string name,
        int? parentCandidate,
        bool insideActionable,
        IReadOnlyList<SnapshotCandidate> candidates)
    {
        if (parentCandidate is null || ActionableRoles.Contains(role))
        {
            return true;
        }

        if (insideActionable || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (candidates[parentCandidate.Value].Name.Equals(
            name,
            StringComparison.Ordinal))
        {
            return false;
        }

        return LeanSemanticRoles.Contains(role) || node.ChildIds.Count == 0;
    }

    private bool TryGetLease(
        NativeBrowserElementHandle handle,
        out ElementLease lease)
    {
        lock (_gate)
        {
            if (_snapshotNonce is null
                || !string.Equals(
                    handle.SnapshotNonce,
                    _snapshotNonce,
                    StringComparison.Ordinal)
                || handle.MutationEpoch != _mutationEpoch
                || !_leases.TryGetValue(handle.ElementToken, out lease!)
                || !string.Equals(lease.SnapshotNonce, handle.SnapshotNonce
, StringComparison.Ordinal) || lease.MutationEpoch != handle.MutationEpoch)
            {
                lease = null!;
                return false;
            }

            return true;
        }
    }

    private async Task<CefSemanticNode?> RevalidateAsync(ElementLease lease)
    {
        var current = await _browser
            .ReadAccessibilityNodeAsync(lease.BackendNodeId)
            .ConfigureAwait(false);
        if (current is null
            || current.IsIgnored
            || current.BackendNodeId != lease.BackendNodeId
            || !string.Equals(current.Role, lease.Role, StringComparison.Ordinal)
            || !string.Equals(current.Name, lease.Name, StringComparison.Ordinal))
        {
            return null;
        }

        return current;
    }

    private async Task<CefSemanticNode?> ReadVerifiedNodeAsync(
        ElementLease lease,
        Func<CefSemanticNode, bool> postcondition)
    {
        CefSemanticNode? last = null;
        for (var attempt = 0; attempt < VerificationAttempts; attempt++)
        {
            var current = await RevalidateAsync(lease).ConfigureAwait(false);
            if (current is not null)
            {
                last = current;
                if (postcondition(current))
                {
                    return current;
                }
            }

            if (attempt + 1 < VerificationAttempts)
            {
                await Task.Delay(VerificationDelay).ConfigureAwait(false);
            }
        }

        return last;
    }

    private void ConsumeLeases()
    {
        lock (_gate)
        {
            _leases = [];
            _snapshotNonce = null;
            AdvanceMutationEpochUnsafe();
        }
    }

    private void AdvanceMutationEpochUnsafe() =>
        _mutationEpoch = _mutationEpoch
            >= NativeBrowserElementHandle.MaximumMutationEpoch
            ? 0
            : _mutationEpoch + 1;

    private static bool IsUnavailable(CefSemanticNode node) =>
        node.IsIgnored || PropertyIsTrue(node, "disabled");

    private static bool PropertyIsTrue(
        CefSemanticNode node,
        string propertyName) =>
        node.Properties.TryGetValue(propertyName, out var value)
        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static BrowserSnapshotNodeState ProjectStates(
        CefSemanticNode node)
    {
        var states = BrowserSnapshotNodeState.None;
        Add("disabled", BrowserSnapshotNodeState.Disabled);
        Add("checked", BrowserSnapshotNodeState.Checked);
        Add("selected", BrowserSnapshotNodeState.Selected);
        Add("expanded", BrowserSnapshotNodeState.Expanded);
        Add("pressed", BrowserSnapshotNodeState.Pressed);
        Add("required", BrowserSnapshotNodeState.Required);
        Add("readonly", BrowserSnapshotNodeState.ReadOnly);
        return states;

        void Add(string property, BrowserSnapshotNodeState state)
        {
            if (PropertyIsTrue(node, property))
            {
                states |= state;
            }
        }
    }

    private static string NormalizeRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return "generic";
        }

        var normalized = new StringBuilder(role.Length + 8);
        var previousWasSeparator = true;
        foreach (var character in role)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (char.IsUpper(character)
                    && !previousWasSeparator
                    && normalized.Length != 0)
                {
                    normalized.Append('-');
                }

                normalized.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && normalized.Length != 0)
            {
                normalized.Append('-');
                previousWasSeparator = true;
            }
        }

        while (normalized.Length != 0 && normalized[^1] == '-')
        {
            normalized.Length--;
        }

        if (normalized.Length == 0)
        {
            return "generic";
        }

        if (normalized.Length > BrowserSnapshotNode.MaximumRoleBytes)
        {
            normalized.Length = BrowserSnapshotNode.MaximumRoleBytes;
            while (normalized.Length != 0 && normalized[^1] == '-')
            {
                normalized.Length--;
            }
        }

        return normalized.Length == 0 ? "generic" : normalized.ToString();
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            used += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }

    private static string NewUniqueToken(
        IReadOnlyDictionary<string, ElementLease> leases)
    {
        while (true)
        {
            var token = NewIdentifier("el_");
            if (!leases.ContainsKey(token))
            {
                return token;
            }
        }
    }

    private static string NewIdentifier(string prefix) =>
        string.Concat(
            prefix,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'));

    private sealed record ElementLease(
        int BackendNodeId,
        string Role,
        string Name,
        string SnapshotNonce,
        long MutationEpoch);

    private sealed record SnapshotCandidate(
        int Depth,
        string Role,
        string Name,
        BrowserSnapshotNodeState States,
        int? BackendNodeId,
        string LeaseRole,
        string LeaseName,
        bool IsActionable,
        int? ParentIndex);
}

using System.Reflection;
using System.Text.Json;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class BrowserContractsTests
{
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://localhost/etc/passwd")]
    [InlineData("FILE:///C:/Windows/win.ini")]
    public void A_parsed_address_can_never_name_a_local_file(string candidate)
    {
        // Navigation asked for by a person or an agent arrives as a string.
        // Local files are reachable only by code that already holds the path.
        Assert.False(BrowserAddress.TryParse(candidate, out _));
    }

    [Fact]
    public void The_shell_can_address_a_local_page_it_materialized_itself()
    {
        var path = Path.Combine(Path.GetTempPath(), "asura-preview-page.html");

        var address = BrowserAddress.ForLocalFile(path);

        Assert.True(address.Value.IsFile);
        Assert.Equal(path, address.Value.LocalPath);
        // The marker is what lets the webview recognise the one page the shell
        // asked for; a parsed address never carries it.
        Assert.True(address.IsLocalFile);
    }

    [Theory]
    [InlineData("https://example.test/")]
    [InlineData("about:blank")]
    public void A_parsed_address_is_never_a_local_page(string text)
    {
        Assert.True(BrowserAddress.TryParse(text, out var address));
        Assert.False(address!.IsLocalFile);
    }

    [Theory]
    [InlineData("relative/page.html")]
    [InlineData("")]
    public void A_local_page_address_needs_a_real_path(string candidate)
    {
        Assert.Throws<ArgumentException>(() => BrowserAddress.ForLocalFile(candidate));
    }

    [Theory]
    [InlineData("https://example.test/path?q=one#result")]
    [InlineData("http://localhost:8080/")]
    [InlineData("about:blank")]
    public void Address_accepts_supported_absolute_locations(string text)
    {
        Assert.True(BrowserAddress.TryParse($"  {text}  ", out var address));
        Assert.NotNull(address);
        Assert.Equal(text, address.ToString());

        var json = JsonSerializer.Serialize(address);
        var restored = JsonSerializer.Deserialize<BrowserAddress>(json);

        Assert.Equal(address, restored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("docs/index.html")]
    [InlineData("ftp://example.test/file")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("about:config")]
    public void Address_rejects_ambiguous_or_credential_bearing_locations(string text)
    {
        Assert.False(BrowserAddress.TryParse(text, out var address));
        Assert.Null(address);
    }

    [Fact]
    public void Address_constructor_and_parser_enforce_the_same_length_limit()
    {
        var oversized = $"https://example.test/{new string('x', BrowserAddress.MaximumLength)}";
        var escapedOversized =
            $"https://example.test/{new string('界', BrowserAddress.MaximumLength / 2)}";

        Assert.False(BrowserAddress.TryParse(oversized, out _));
        Assert.False(BrowserAddress.TryParse(escapedOversized, out _));
        Assert.Throws<ArgumentException>(() =>
            new BrowserAddress(new Uri(oversized, UriKind.Absolute)));
    }

    [Fact]
    public void State_represents_loading_and_failure_without_ambiguous_combinations()
    {
        var loading = new BrowserSessionState(
            BrowserAddress.Blank,
            "Loading",
            BrowserLoadState.Loading,
            false,
            false,
            3);
        var failure = BrowserError.Create(
            BrowserErrorCode.NavigationFailed,
            "The page could not be loaded.",
            retryable: true);
        var failed = new BrowserSessionState(
            BrowserAddress.Blank,
            string.Empty,
            BrowserLoadState.Failed,
            false,
            false,
            3,
            failure);

        Assert.Equal(3, loading.DocumentRevision);
        Assert.Null(loading.Failure);
        Assert.Same(failure, failed.Failure);
        Assert.Throws<ArgumentException>(() => new BrowserSessionState(
            BrowserAddress.Blank,
            string.Empty,
            BrowserLoadState.Failed,
            false,
            false,
            0));
        Assert.Throws<ArgumentException>(() => new BrowserSessionState(
            BrowserAddress.Blank,
            string.Empty,
            BrowserLoadState.Ready,
            false,
            false,
            0,
            failure));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrowserSessionState(
            BrowserAddress.Blank,
            string.Empty,
            BrowserLoadState.Ready,
            false,
            false,
            -1));
        Assert.Throws<ArgumentException>(() => new BrowserSessionState(
            BrowserAddress.Blank,
            new string('x', BrowserSessionState.MaximumTitleLength + 1),
            BrowserLoadState.Ready,
            false,
            false,
            0));
    }

    [Fact]
    public void Navigation_start_binding_matches_only_the_authorized_document()
    {
        var address = Address("https://example.test/document");
        var state = new BrowserSessionState(
            address,
            "Document",
            BrowserLoadState.Ready,
            false,
            false,
            7);
        var binding = BrowserNavigationStartBinding.FromState(state);

        Assert.Equal(address, binding.Address);
        Assert.Equal(7, binding.DocumentRevision);
        Assert.True(binding.Matches(state));
        Assert.False(binding.Matches(new BrowserSessionState(
            address,
            string.Empty,
            BrowserLoadState.Ready,
            false,
            false,
            8)));
        Assert.False(binding.Matches(BrowserSessionState.Initial(
            Address("https://other.example.test/"))));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserNavigationStartBinding(address, -1));
    }

    [Fact]
    public void Document_binding_matches_only_the_captured_document()
    {
        var address = Address("https://example.test/document");
        var state = new BrowserSessionState(
            address,
            "Document",
            BrowserLoadState.Ready,
            false,
            false,
            7);
        var binding = BrowserDocumentBinding.FromState(state);

        Assert.Equal(address, binding.Address);
        Assert.Equal(7, binding.DocumentRevision);
        Assert.True(binding.Matches(state));
        Assert.False(binding.Matches(new BrowserSessionState(
            address,
            state.Title,
            state.LoadState,
            state.CanGoBack,
            state.CanGoForward,
            8)));
        Assert.False(binding.Matches(BrowserSessionState.Initial(
            Address("https://other.example.test/"))));
        Assert.Throws<ArgumentNullException>(() =>
            new BrowserDocumentBinding(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserDocumentBinding(address, -1));
    }

    [Fact]
    public void Snapshot_models_are_bounded_immutable_and_document_bound()
    {
        var document = new BrowserDocumentBinding(
            Address("https://example.test/document"),
            7);
        var reference = new BrowserElementReference(
            "element_01-Ab",
            document);
        var nodes = new List<BrowserSnapshotNode>
        {
            new(0, "document", string.Empty),
            new(
                1,
                "button",
                "Deploy",
                reference,
                BrowserSnapshotNodeState.Disabled
                | BrowserSnapshotNodeState.Required),
        };
        var capturedAt = new DateTimeOffset(
            2026,
            7,
            24,
            12,
            30,
            0,
            TimeSpan.Zero);
        var snapshot = new BrowserDocumentSnapshot(
            document,
            nodes,
            capturedAt,
            isTruncated: true);

        nodes.Clear();

        Assert.Same(document, snapshot.Document);
        Assert.Equal(capturedAt, snapshot.CapturedAtUtc);
        Assert.True(snapshot.IsTruncated);
        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.Equal(reference, snapshot.Nodes[1].Reference);
        Assert.Equal(
            BrowserSnapshotNodeState.Disabled
            | BrowserSnapshotNodeState.Required,
            snapshot.Nodes[1].States);
        Assert.Equal(document, reference.Document);
        Assert.Equal(new BrowserElementReferenceId("element_01-Ab"), reference.Id);
        Assert.Equal("element_01-Ab", reference.Value);
    }

    [Fact]
    public void Snapshot_nodes_reject_invalid_text_depth_and_state()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserSnapshotNode(-1, "button", "Deploy"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserSnapshotNode(
                BrowserSnapshotNode.MaximumDepth + 1,
                "button",
                "Deploy"));
        Assert.Throws<ArgumentException>(() =>
            new BrowserSnapshotNode(0, "Button", "Deploy"));
        Assert.Throws<ArgumentException>(() =>
            new BrowserSnapshotNode(
                0,
                new string('r', BrowserSnapshotNode.MaximumRoleBytes + 1),
                "Deploy"));
        Assert.Throws<ArgumentException>(() =>
            new BrowserSnapshotNode(
                0,
                "button",
                new string('界', 86)));
        Assert.Throws<ArgumentException>(() =>
            new BrowserSnapshotNode(0, "button", "\uD800"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserSnapshotNode(
                0,
                "button",
                "Deploy",
                states: (BrowserSnapshotNodeState)(1 << 20)));
    }

    [Fact]
    public void Snapshot_query_is_bounded_and_defaults_to_lean_output()
    {
        var lean = BrowserSnapshotQuery.Lean;
        var narrowed = new BrowserSnapshotQuery(
            interactiveOnly: true,
            filter: "Checkout",
            maximumDepth: 4);

        Assert.False(lean.InteractiveOnly);
        Assert.Null(lean.Filter);
        Assert.Null(lean.MaximumDepth);
        Assert.True(narrowed.InteractiveOnly);
        Assert.Equal("Checkout", narrowed.Filter);
        Assert.Equal(4, narrowed.MaximumDepth);
        Assert.Throws<ArgumentException>(() =>
            new BrowserSnapshotQuery(filter: " result "));
        Assert.Throws<ArgumentException>(() =>
            new BrowserSnapshotQuery(
                filter: new string('x', BrowserSnapshotQuery.MaximumFilterBytes + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserSnapshotQuery(
                maximumDepth: BrowserSnapshotNode.MaximumDepth + 1));
    }

    [Fact]
    public void Snapshot_references_are_opaque_bounded_identifiers()
    {
        var document = new BrowserDocumentBinding(
            Address("https://example.test/"),
            1);
        var maximum = new BrowserElementReferenceId(
            new string('a', BrowserElementReferenceId.MaximumValueBytes));

        _ = new BrowserElementReference(
            maximum,
            document);

        Assert.Equal(maximum.Value, maximum.ToString());
        Assert.Equal(maximum, new BrowserElementReference(maximum, document).Id);
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementReferenceId(string.Empty));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementReferenceId("element.1"));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementReferenceId("élément"));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementReferenceId(
                new string(
                    'a',
                    BrowserElementReferenceId.MaximumValueBytes + 1)));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementReference(string.Empty, document));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementReference("element.1", document));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementReference(
                new string(
                    'a',
                    BrowserElementReference.MaximumValueBytes + 1),
                document));
        Assert.Throws<ArgumentNullException>(() =>
            new BrowserElementReference("element_1", null!));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementReference(default(BrowserElementReferenceId), document));
    }

    [Fact]
    public void Click_request_and_receipt_bind_the_reference_to_an_exact_document_revision()
    {
        var sessionId = new SessionId("browser-1");
        var reference = new BrowserElementReferenceId("element_1");
        var document = new BrowserDocumentBinding(
            Address("https://example.test/"),
            7);
        var request = new BrowserElementClickRequest(
            sessionId,
            reference,
            document.DocumentRevision);
        var receipt = new BrowserClickReceipt(document);

        Assert.Equal(sessionId, request.SessionId);
        Assert.Equal(reference, request.Reference);
        Assert.Equal(7, request.DocumentRevision);
        Assert.Same(document, receipt.SourceDocument);
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementClickRequest(
                sessionId,
                default,
                documentRevision: 7));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserElementClickRequest(
                sessionId,
                reference,
                documentRevision: -1));
        Assert.Throws<ArgumentNullException>(() =>
            new BrowserClickReceipt(null!));
    }

    [Fact]
    public void Check_request_and_receipt_bind_the_reference_to_an_exact_document_revision()
    {
        var sessionId = new SessionId("browser-1");
        var reference = new BrowserElementReferenceId("element_1");
        var document = new BrowserDocumentBinding(
            Address("https://example.test/"),
            7);
        var request = new BrowserElementCheckRequest(
            sessionId,
            reference,
            document.DocumentRevision);
        var receipt = new BrowserCheckReceipt(document);

        Assert.Equal(sessionId, request.SessionId);
        Assert.Equal(reference, request.Reference);
        Assert.Equal(7, request.DocumentRevision);
        Assert.Same(document, receipt.SourceDocument);
        Assert.All(
            typeof(BrowserElementCheckRequest).GetProperties(),
            property => Assert.Null(property.SetMethod));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementCheckRequest(
                default,
                reference,
                documentRevision: 7));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementCheckRequest(
                sessionId,
                default,
                documentRevision: 7));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserElementCheckRequest(
                sessionId,
                reference,
                documentRevision: -1));
        Assert.Throws<ArgumentNullException>(() =>
            new BrowserCheckReceipt(null!));
    }

    [Fact]
    public void Fill_request_and_receipt_bind_exact_immutable_operation_material()
    {
        var sessionId = new SessionId("browser-1");
        var reference = new BrowserElementReferenceId("element_1");
        var document = new BrowserDocumentBinding(
            Address("https://example.test/"),
            7);
        const string Text = "first\tline\r\nsecond 😀";
        var request = new BrowserElementFillRequest(
            sessionId,
            reference,
            document.DocumentRevision,
            Text);
        var receipt = new BrowserFillReceipt(document);

        Assert.Equal(sessionId, request.SessionId);
        Assert.Equal(reference, request.Reference);
        Assert.Equal(7, request.DocumentRevision);
        Assert.Equal(Text, request.Text);
        Assert.DoesNotContain(
            Text,
            request.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Text,
            new AgentBrowserRequest.Fill(request).ToString(),
            StringComparison.Ordinal);
        Assert.Same(document, receipt.SourceDocument);
        Assert.All(
            typeof(BrowserElementFillRequest).GetProperties(),
            property => Assert.Null(property.SetMethod));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementFillRequest(
                default,
                reference,
                documentRevision: 7,
                Text));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementFillRequest(
                sessionId,
                default,
                documentRevision: 7,
                Text));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserElementFillRequest(
                sessionId,
                reference,
                documentRevision: -1,
                Text));
        Assert.Throws<ArgumentNullException>(() =>
            new BrowserElementFillRequest(
                sessionId,
                reference,
                documentRevision: 7,
                null!));
        Assert.Throws<ArgumentNullException>(() =>
            new BrowserFillReceipt(null!));
    }

    [Fact]
    public void Fill_text_limit_is_measured_in_strict_utf8_bytes()
    {
        var sessionId = new SessionId("browser-1");
        var reference = new BrowserElementReferenceId("element_1");
        var maximumAscii = new string(
            'a',
            BrowserElementFillRequest.MaximumTextBytes);
        var maximumMultibyte = new string(
            'é',
            BrowserElementFillRequest.MaximumTextBytes / 2);

        Assert.Equal(
            maximumAscii,
            new BrowserElementFillRequest(
                sessionId,
                reference,
                documentRevision: 0,
                maximumAscii).Text);
        Assert.Equal(
            maximumMultibyte,
            new BrowserElementFillRequest(
                sessionId,
                reference,
                documentRevision: 0,
                maximumMultibyte).Text);
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementFillRequest(
                sessionId,
                reference,
                documentRevision: 0,
                new string(
                    'a',
                    BrowserElementFillRequest.MaximumTextBytes + 1)));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementFillRequest(
                sessionId,
                reference,
                documentRevision: 0,
                new string(
                    'é',
                    (BrowserElementFillRequest.MaximumTextBytes / 2) + 1)));
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\a")]
    [InlineData("\b")]
    [InlineData("\v")]
    [InlineData("\f")]
    [InlineData("\u001b")]
    [InlineData("\u007f")]
    public void Fill_text_rejects_non_text_controls(
        string text)
    {
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementFillRequest(
                new SessionId("browser-1"),
                new BrowserElementReferenceId("element_1"),
                documentRevision: 0,
                text));
    }

    [Fact]
    public void Fill_text_rejects_unpaired_utf16_surrogates()
    {
        var sessionId = new SessionId("browser-1");
        var reference = new BrowserElementReferenceId("element_1");

        Assert.Throws<ArgumentException>(() =>
            new BrowserElementFillRequest(
                sessionId,
                reference,
                documentRevision: 0,
                new string('\ud800', 1)));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementFillRequest(
                sessionId,
                reference,
                documentRevision: 0,
                new string('\udc00', 1)));
        Assert.Throws<ArgumentException>(() =>
            new BrowserElementFillRequest(
                sessionId,
                reference,
                documentRevision: 0,
                string.Concat('\ud800', 'x')));
    }

    [Fact]
    public void Snapshot_rejects_invalid_tree_and_reference_collections()
    {
        var document = new BrowserDocumentBinding(
            Address("https://example.test/"),
            1);
        var otherDocument = new BrowserDocumentBinding(
            Address("https://other.example.test/"),
            1);
        var reference = new BrowserElementReference("element_1", document);

        Assert.Throws<ArgumentException>(() =>
            new BrowserDocumentSnapshot(
                document,
                [new BrowserSnapshotNode(1, "button", "Deploy")],
                DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() =>
            new BrowserDocumentSnapshot(
                document,
                [
                    new BrowserSnapshotNode(0, "document", string.Empty),
                    new BrowserSnapshotNode(2, "button", "Deploy"),
                ],
                DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() =>
            new BrowserDocumentSnapshot(
                document,
                [
                    new BrowserSnapshotNode(
                        0,
                        "button",
                        "First",
                        reference),
                    new BrowserSnapshotNode(
                        0,
                        "button",
                        "Second",
                        reference),
                ],
                DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() =>
            new BrowserDocumentSnapshot(
                document,
                [
                    new BrowserSnapshotNode(
                        0,
                        "button",
                        "Other",
                        new BrowserElementReference(
                            "element_2",
                            otherDocument)),
                ],
                DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() =>
            new BrowserDocumentSnapshot(
                document,
                [.. Enumerable
                    .Range(
                        0,
                        BrowserDocumentSnapshot.MaximumNodeCount + 1)
                    .Select(index =>
                        new BrowserSnapshotNode(
                            0,
                            "generic",
                            index.ToString(System.Globalization.CultureInfo.InvariantCulture)))],
                DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() =>
            new BrowserDocumentSnapshot(
                document,
                [null!],
                DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Browser_errors_and_results_have_closed_stable_shapes()
    {
        (BrowserErrorCode Code, string StableCode)[] expected =
        [
            (BrowserErrorCode.UnsupportedCapability, "unsupported_capability"),
            (BrowserErrorCode.RendererUnavailable, "renderer_unavailable"),
            (BrowserErrorCode.HistoryUnavailable, "history_unavailable"),
            (BrowserErrorCode.NavigationInProgress, "navigation_in_progress"),
            (BrowserErrorCode.NavigationStateChanged, "browser_state_changed"),
            (
                BrowserErrorCode.NavigationPolicyDenied,
                "browser_domain_policy_denied"),
            (BrowserErrorCode.SnapshotInvalid, "browser_snapshot_invalid"),
            (
                BrowserErrorCode.ElementReferenceStale,
                "browser_element_reference_stale"),
            (
                BrowserErrorCode.ElementNotInteractable,
                "browser_element_not_interactable"),
            (
                BrowserErrorCode.ElementNotFillable,
                "browser_element_not_fillable"),
            (
                BrowserErrorCode.FillValueNotSupported,
                "browser_fill_value_not_supported"),
            (
                BrowserErrorCode.ElementNotCheckable,
                "browser_element_not_checkable"),
            (
                BrowserErrorCode.CheckStateNotApplied,
                "browser_check_state_not_applied"),
            (BrowserErrorCode.ScriptRejected, "browser_script_rejected"),
            (
                BrowserErrorCode.ScriptResultRejected,
                "browser_script_result_rejected"),
            (
                BrowserErrorCode.InteractionOutcomeUnknown,
                "browser_interaction_outcome_unknown"),
            (BrowserErrorCode.NavigationFailed, "navigation_failed"),
            (BrowserErrorCode.NetworkUnavailable, "network_unavailable"),
            (BrowserErrorCode.NavigationTimedOut, "navigation_timed_out"),
            (BrowserErrorCode.CertificateRejected, "certificate_rejected"),
            (BrowserErrorCode.SessionClosed, "session_closed"),
            (BrowserErrorCode.Cancelled, "cancelled"),
            (BrowserErrorCode.EngineFailed, "engine_failed"),
        ];

        Assert.Equal(Enum.GetValues<BrowserErrorCode>().Length, expected.Length);
        Assert.Equal(
            expected.Length,
            expected.Select(value => value.StableCode).Distinct(StringComparer.Ordinal).Count());
        foreach (var (code, stableCode) in expected)
        {
            var error = BrowserError.Create(code, "Visible detail.");
            Assert.Equal(stableCode, error.StableCode);
            Assert.False(error.Retryable);
        }

        var state = BrowserSessionState.Initial(BrowserAddress.Blank);
        var success = BrowserResult<BrowserSessionState>.Success(state);
        var failure = BrowserResult<BrowserSessionState>.Failure(
            BrowserError.Create(BrowserErrorCode.RendererUnavailable, "No renderer."));

        Assert.True(success.IsSuccess);
        Assert.Same(state, success.Value);
        Assert.Null(success.Error);
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.Value);
        Assert.Equal(BrowserErrorCode.RendererUnavailable, failure.Error?.Code);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BrowserError.Create((BrowserErrorCode)999, "Invalid."));
        Assert.Throws<ArgumentException>(() =>
            BrowserError.Create(
                BrowserErrorCode.EngineFailed,
                new string('x', BrowserError.MaximumMessageLength + 1)));
    }

    [Fact]
    public void Browser_control_plane_operations_and_capabilities_have_stable_names()
    {
        Assert.Equal("browser.open", ApplicationOperations.BrowserOpen);
        Assert.Equal("browser.state.read", ApplicationOperations.BrowserReadState);
        Assert.Equal("browser.snapshot", ApplicationOperations.BrowserSnapshot);
        Assert.Equal("browser.click", ApplicationOperations.BrowserClick);
        Assert.Equal("browser.fill", ApplicationOperations.BrowserFill);
        Assert.Equal("browser.check", ApplicationOperations.BrowserCheck);
        Assert.Equal("browser.navigate", ApplicationOperations.BrowserNavigate);
        Assert.Equal("browser.back", ApplicationOperations.BrowserBack);
        Assert.Equal("browser.forward", ApplicationOperations.BrowserForward);
        Assert.Equal("browser.reload", ApplicationOperations.BrowserReload);
        Assert.Equal("browser.stop", ApplicationOperations.BrowserStop);

        Assert.Equal(ApplicationOperations.BrowserReadState, SessionCapabilities.BrowserReadState);
        Assert.Equal(
            ApplicationOperations.BrowserSnapshot,
            SessionCapabilities.BrowserSnapshot);
        Assert.Equal(ApplicationOperations.BrowserClick, SessionCapabilities.BrowserClick);
        Assert.Equal(ApplicationOperations.BrowserFill, SessionCapabilities.BrowserFill);
        Assert.Equal(ApplicationOperations.BrowserCheck, SessionCapabilities.BrowserCheck);
        Assert.Equal(ApplicationOperations.BrowserNavigate, SessionCapabilities.BrowserNavigate);
        Assert.Equal(ApplicationOperations.BrowserBack, SessionCapabilities.BrowserBack);
        Assert.Equal(ApplicationOperations.BrowserForward, SessionCapabilities.BrowserForward);
        Assert.Equal(ApplicationOperations.BrowserReload, SessionCapabilities.BrowserReload);
        Assert.Equal(ApplicationOperations.BrowserStop, SessionCapabilities.BrowserStop);
        Assert.Equal(
            "browser.navigation_origin_guard",
            SessionCapabilities.BrowserOriginGuard);
        Assert.Equal(
            "browser.agent_input_barrier",
            SessionCapabilities.BrowserAgentInputBarrier);
    }

    [Theory]
    [InlineData(
        "https://Example.test/path",
        "https://example.test:443/redirect",
        true)]
    [InlineData(
        "https://example.test/",
        "http://example.test/",
        false)]
    [InlineData(
        "https://example.test/",
        "https://example.test:444/",
        false)]
    [InlineData(
        "https://example.test/",
        "https://other.example.test/",
        false)]
    [InlineData(
        "https://bücher.example/path",
        "https://xn--bcher-kva.example/redirect",
        true)]
    [InlineData("about:blank", "about:blank", true)]
    [InlineData("about:blank", "https://example.test/", false)]
    public void Navigation_origin_uses_scheme_idn_host_and_effective_port(
        string source,
        string destination,
        bool expected)
    {
        var origin = BrowserNavigationOrigin.FromAddress(Address(source));

        Assert.Equal(expected, origin.Allows(Address(destination)));
    }

    [Theory]
    [InlineData(
        "https://BÜCHER.example/path",
        "https://xn--bcher-kva.example:443")]
    [InlineData("http://example.test:8080/path", "http://example.test:8080")]
    [InlineData("https://[2001:db8::1]/path", "https://[2001:db8::1]:443")]
    [InlineData("about:blank", "about:blank")]
    public void Navigation_origin_has_stable_canonical_approval_material(
        string address,
        string expected)
    {
        var origin = BrowserNavigationOrigin.FromAddress(Address(address));

        Assert.Equal(expected, origin.CanonicalValue);
        Assert.Equal(expected, origin.ToString());
    }

    [Theory]
    [InlineData("https://example.test/")]
    [InlineData("http://other.test:8080/path")]
    [InlineData("about:blank")]
    public void Unrestricted_navigation_origin_allows_every_supported_address(
        string destination)
    {
        Assert.True(
            BrowserNavigationOrigin.Unrestricted.Allows(Address(destination)));
        Assert.Equal("*", BrowserNavigationOrigin.Unrestricted.CanonicalValue);
    }

    [Fact]
    public void Requests_keep_session_attachment_and_renderer_identity_explicit()
    {
        var sessionId = new SessionId("browser-1");
        var attachmentId = new AttachmentId("attachment-1");
        var address = new BrowserAddress(new Uri("https://example.test/"));
        var owner = new SessionOwner(
            HostMode.Desktop,
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-1"),
            new TabInstanceId("tab-1"),
            new PanelInstanceId("panel-1"));
        var renderer = new StubRenderer(address);

        var ensure = new EnsureBrowserSessionRequest(
            sessionId,
            owner,
            "Browser",
            address);
        var attach = new AttachBrowserRendererRequest(
            sessionId,
            attachmentId,
            renderer);
        var navigate = new BrowserNavigateRequest(
            sessionId,
            new BrowserAddress(new Uri("https://docs.example.test/")));

        Assert.Equal(owner, ensure.Owner);
        Assert.Equal(address, ensure.InitialAddress);
        Assert.Equal(sessionId, attach.SessionId);
        Assert.Equal(attachmentId, attach.AttachmentId);
        Assert.Same(renderer, attach.Renderer);
        Assert.Equal("docs.example.test", navigate.Address.Value.Host);
    }

    [Fact]
    public void Browser_ports_are_typed_and_do_not_expose_vendor_or_generic_execution_types()
    {
        Assert.True(typeof(IPanelSession).IsAssignableFrom(typeof(IBrowserPanelSession)));
        Assert.True(typeof(IBrowserNavigation).IsAssignableFrom(typeof(IBrowserPanelSession)));
        Assert.True(typeof(IOriginConstrainedBrowserNavigation).IsAssignableFrom(
            typeof(IBrowserPanelSession)));
        Assert.True(typeof(IOriginConstrainedBrowserElementClick).IsAssignableFrom(
            typeof(IBrowserPanelSession)));
        Assert.True(typeof(IOriginConstrainedBrowserElementFill).IsAssignableFrom(
            typeof(IBrowserPanelSession)));
        Assert.True(typeof(IOriginConstrainedBrowserElementCheck).IsAssignableFrom(
            typeof(IBrowserPanelSession)));
        Assert.True(typeof(IBrowserDocumentReader).IsAssignableFrom(
            typeof(IBrowserPanelSession)));
        Assert.True(typeof(IBrowserWaitObservation).IsAssignableFrom(
            typeof(IBrowserPanelSession)));
        Assert.True(typeof(IBrowserRendererAttachment).IsAssignableFrom(
            typeof(IBrowserPanelSession)));
        Assert.True(typeof(IBrowserNavigation).IsAssignableFrom(typeof(IBrowserRenderer)));
        Assert.True(typeof(IOriginConstrainedBrowserNavigation).IsAssignableFrom(
            typeof(IBrowserRenderer)));
        Assert.True(typeof(IOriginConstrainedBrowserElementClick).IsAssignableFrom(
            typeof(IBrowserRenderer)));
        Assert.True(typeof(IOriginConstrainedBrowserElementFill).IsAssignableFrom(
            typeof(IBrowserRenderer)));
        Assert.True(typeof(IOriginConstrainedBrowserElementCheck).IsAssignableFrom(
            typeof(IBrowserRenderer)));
        Assert.True(typeof(IBrowserDocumentReader).IsAssignableFrom(
            typeof(IBrowserRenderer)));
        Assert.True(typeof(IBrowserWaitObservation).IsAssignableFrom(
            typeof(IBrowserRenderer)));

        Assert.Equal(
            ["GoBackAsync", "GoForwardAsync", "NavigateAsync", "ReloadAsync", "StopAsync"],
            OperationNames(typeof(IBrowserNavigation)));
        Assert.Equal(
            ["AttachRendererAsync", "DetachRendererAsync"],
            OperationNames(typeof(IBrowserRendererAttachment)));
        Assert.Equal(
            ["NavigateWithinOriginAsync"],
            OperationNames(typeof(IOriginConstrainedBrowserNavigation)));
        Assert.Equal(
            ["ClickWithinOriginAsync"],
            OperationNames(typeof(IOriginConstrainedBrowserElementClick)));
        var clickMethod = Assert.Single(
            typeof(IOriginConstrainedBrowserElementClick).GetMethods());
        Assert.Equal(
            [
                typeof(BrowserElementReference),
                typeof(BrowserNavigationOrigin),
                typeof(CancellationToken),
            ],
            clickMethod.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(
            typeof(ValueTask<BrowserResult<BrowserClickReceipt>>),
            clickMethod.ReturnType);
        Assert.Equal(
            ["FillWithinOriginAsync"],
            OperationNames(typeof(IOriginConstrainedBrowserElementFill)));
        var fillMethod = Assert.Single(
            typeof(IOriginConstrainedBrowserElementFill).GetMethods());
        Assert.Equal(
            [
                typeof(BrowserElementReference),
                typeof(string),
                typeof(BrowserNavigationOrigin),
                typeof(CancellationToken),
            ],
            fillMethod.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(
            typeof(ValueTask<BrowserResult<BrowserFillReceipt>>),
            fillMethod.ReturnType);
        Assert.Equal(
            ["CheckWithinOriginAsync"],
            OperationNames(typeof(IOriginConstrainedBrowserElementCheck)));
        var checkMethod = Assert.Single(
            typeof(IOriginConstrainedBrowserElementCheck).GetMethods());
        Assert.Equal(
            [
                typeof(BrowserElementReference),
                typeof(BrowserNavigationOrigin),
                typeof(CancellationToken),
            ],
            checkMethod.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(
            typeof(ValueTask<BrowserResult<BrowserCheckReceipt>>),
            checkMethod.ReturnType);
        Assert.Equal(
            ["CaptureSnapshotAsync"],
            OperationNames(typeof(IBrowserDocumentReader)));
        Assert.Equal(
            [
                "BeginNetworkActivityObservationAsync",
                "EndNetworkActivityObservationAsync",
                "ReadElementStateAsync",
                "ReadNetworkActivityAsync",
            ],
            OperationNames(typeof(IBrowserWaitObservation)));

        Type[] ports =
        [
            typeof(IBrowserNavigation),
            typeof(IOriginConstrainedBrowserNavigation),
            typeof(IOriginConstrainedBrowserElementClick),
            typeof(IOriginConstrainedBrowserElementFill),
            typeof(IOriginConstrainedBrowserElementCheck),
            typeof(IBrowserDocumentReader),
            typeof(IBrowserWaitObservation),
            typeof(IBrowserRenderer),
            typeof(IBrowserRendererAttachment),
            typeof(IBrowserPhysicalInputBarrier),
        ];
        foreach (var member in ports.SelectMany(port => port.GetMembers()))
        {
            Assert.DoesNotContain("Execute", member.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Avalonia", member.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("WebView", member.ToString(), StringComparison.Ordinal);
        }

        foreach (var parameter in ports
                     .SelectMany(port => port.GetMethods())
                     .SelectMany(method => method.GetParameters()))
        {
            Assert.NotEqual(typeof(object), parameter.ParameterType);
        }
    }

    [Fact]
    public void BrowserWaitRequestEnforcesTheOneHourAndConditionBounds()
    {
        var sessionId = new SessionId("browser-1");
        var maximum = new BrowserWaitRequest(
            sessionId,
            new BrowserWaitCondition.Delay(TimeSpan.FromHours(1)),
            TimeSpan.FromHours(1));

        Assert.Equal(BrowserWaitRequest.MaximumTimeout, maximum.Timeout);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserWaitRequest(
                sessionId,
                new BrowserWaitCondition.Delay(TimeSpan.FromHours(1)),
                TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserWaitRequest(
                sessionId,
                new BrowserWaitCondition.Delay(TimeSpan.FromSeconds(2)),
                TimeSpan.FromSeconds(1)));
    }

    private static string[] OperationNames(Type port) => [.. port
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => !method.IsSpecialName)
        .Select(method => method.Name)
        .Order(StringComparer.Ordinal)];

    private sealed class StubRenderer(BrowserAddress address) : IBrowserRenderer
    {
        public CapabilitySet Capabilities { get; } = CapabilitySet.Empty;

        public BrowserSessionState State { get; } = BrowserSessionState.Initial(address);

#pragma warning disable CS0067
        public event EventHandler<BrowserStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

        public ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
            BrowserAddress address,
            CancellationToken cancellationToken) =>
            Success();

        public ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
            CancellationToken cancellationToken) =>
            Success();

        public ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
            CancellationToken cancellationToken) =>
            Success();

        public ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
            CancellationToken cancellationToken) =>
            Success();

        public ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
            CancellationToken cancellationToken) =>
            Success();

        public ValueTask<BrowserResult<BrowserSessionState>>
            NavigateWithinOriginAsync(
                BrowserOriginConstrainedNavigationRequest request,
                BrowserNavigationOrigin allowedOrigin,
                BrowserNavigationStartBinding startBinding,
                CancellationToken cancellationToken) =>
            Success();

        public ValueTask<BrowserResult<BrowserDocumentSnapshot>>
            CaptureSnapshotAsync(
                BrowserDocumentBinding document,
                CancellationToken cancellationToken,
                BrowserSnapshotQuery? query = null) =>
            ValueTask.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Success(
                    new BrowserDocumentSnapshot(
                        document,
                        [],
                        DateTimeOffset.UnixEpoch)));

        public ValueTask<BrowserResult<BrowserClickReceipt>>
            ClickWithinOriginAsync(
                BrowserElementReference reference,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                BrowserResult<BrowserClickReceipt>.Success(
                    new BrowserClickReceipt(reference.Document)));

        public ValueTask<BrowserResult<BrowserFillReceipt>>
            FillWithinOriginAsync(
                BrowserElementReference reference,
                string text,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                BrowserResult<BrowserFillReceipt>.Success(
                    new BrowserFillReceipt(reference.Document)));

        public ValueTask<BrowserResult<BrowserCheckReceipt>>
            CheckWithinOriginAsync(
                BrowserElementReference reference,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                BrowserResult<BrowserCheckReceipt>.Success(
                    new BrowserCheckReceipt(reference.Document)));

        private ValueTask<BrowserResult<BrowserSessionState>> Success() =>
            ValueTask.FromResult(BrowserResult<BrowserSessionState>.Success(State));
    }

    private static BrowserAddress Address(string value) =>
        new(new Uri(value, UriKind.Absolute));
}

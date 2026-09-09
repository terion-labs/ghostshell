using System.Reflection;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentBrowserActionComposerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(BrowserOperation.ReadState, BuiltInAgentTools.BrowserReadState)]
    [InlineData(BrowserOperation.Snapshot, BuiltInAgentTools.BrowserSnapshot)]
    [InlineData(BrowserOperation.Wait, BuiltInAgentTools.BrowserWait)]
    [InlineData(BrowserOperation.Click, BuiltInAgentTools.BrowserClick)]
    [InlineData(BrowserOperation.Fill, BuiltInAgentTools.BrowserFill)]
    [InlineData(BrowserOperation.Check, BuiltInAgentTools.BrowserCheck)]
    [InlineData(BrowserOperation.Mouse, BuiltInAgentTools.BrowserMouse)]
    [InlineData(BrowserOperation.Key, BuiltInAgentTools.BrowserKey)]
    [InlineData(BrowserOperation.Scroll, BuiltInAgentTools.BrowserScroll)]
    [InlineData(BrowserOperation.Evaluate, BuiltInAgentTools.BrowserEvaluate)]
    [InlineData(BrowserOperation.Navigate, BuiltInAgentTools.BrowserNavigate)]
    [InlineData(BrowserOperation.Back, BuiltInAgentTools.BrowserBack)]
    [InlineData(BrowserOperation.Forward, BuiltInAgentTools.BrowserForward)]
    [InlineData(BrowserOperation.Reload, BuiltInAgentTools.BrowserReload)]
    [InlineData(BrowserOperation.Stop, BuiltInAgentTools.BrowserStop)]
    public void Closed_request_kinds_map_to_trusted_tools(
        BrowserOperation operation,
        string expectedTool)
    {
        var request = Request(operation);
        var context = BrowserContext();

        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            context,
            request);

        Assert.Same(request, action.Request);
        Assert.Equal(expectedTool, action.Proposal.ToolName);
        Assert.Same(context.Target, action.Proposal.Target);
        Assert.Equal(context.BindingFingerprint, action.Proposal.TargetFingerprint);
        Assert.Equal(
            AgentTargetIdentity.Create(context.Target),
            action.Proposal.TargetIdentity);
    }

    [Fact]
    public void Browser_request_action_result_and_host_port_have_closed_typed_shapes()
    {
        var requestKinds = typeof(AgentBrowserRequest)
            .GetNestedTypes(BindingFlags.Public)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        var resultKinds = typeof(AgentBrowserActionResult)
            .GetNestedTypes(BindingFlags.Public)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        var hostMethod = Assert.Single(typeof(IAgentBrowserSessionHost).GetMethods());

        Assert.True(typeof(AgentBrowserRequest).IsAbstract);
        Assert.Equal(
            [
                "Back",
                "Check",
                "Click",
                "Evaluate",
                "Fill",
                "Forward",
                "Key",
                "Mouse",
                "Navigate",
                "ReadState",
                "Reload",
                "Scroll",
                "Snapshot",
                "Stop",
                "Wait",
            ],
            requestKinds.Select(type => type.Name), StringComparer.Ordinal);
        Assert.All(requestKinds, type => Assert.True(type.IsSealed));
        Assert.True(typeof(AgentBrowserActionResult).IsAbstract);
        Assert.Equal(
            ["Automation", "Completed", "Evaluation", "Snapshot", "State", "Wait"],
            resultKinds.Select(type => type.Name), StringComparer.Ordinal);
        Assert.All(resultKinds, type => Assert.True(type.IsSealed));
        Assert.Empty(typeof(AgentBrowserAction).GetConstructors());
        Assert.Empty(typeof(AgentActionExecutionBinding).GetConstructors());
        Assert.Equal("RunAgentBrowserActionAsync", hostMethod.Name);
        Assert.DoesNotContain(
            hostMethod.GetParameters(),
            parameter => parameter.ParameterType == typeof(object));
    }

    [Fact]
    public void Built_in_browser_tools_have_closed_policy_classification()
    {
        var expected = new[]
        {
            (
                BuiltInAgentTools.BrowserReadState,
                "Read browser state",
                AgentCapability.BrowserData,
                AgentActionRisk.Observation),
            (
                BuiltInAgentTools.BrowserSnapshot,
                "Capture browser snapshot",
                AgentCapability.BrowserData,
                AgentActionRisk.Observation),
            (
                BuiltInAgentTools.BrowserWait,
                "Wait for browser state",
                AgentCapability.BrowserData,
                AgentActionRisk.Routine),
            (
                BuiltInAgentTools.BrowserClick,
                "Click browser element",
                AgentCapability.BrowserInteraction,
                AgentActionRisk.Mutation),
            (
                BuiltInAgentTools.BrowserFill,
                "Fill browser element",
                AgentCapability.BrowserInteraction,
                AgentActionRisk.Mutation),
            (
                BuiltInAgentTools.BrowserCheck,
                "Check browser element",
                AgentCapability.BrowserInteraction,
                AgentActionRisk.Mutation),
            (
                BuiltInAgentTools.BrowserNavigate,
                "Navigate browser",
                AgentCapability.BrowserNavigation,
                AgentActionRisk.Mutation),
            (
                BuiltInAgentTools.BrowserBack,
                "Go back in browser",
                AgentCapability.BrowserNavigation,
                AgentActionRisk.Mutation),
            (
                BuiltInAgentTools.BrowserForward,
                "Go forward in browser",
                AgentCapability.BrowserNavigation,
                AgentActionRisk.Mutation),
            (
                BuiltInAgentTools.BrowserReload,
                "Reload browser",
                AgentCapability.BrowserNavigation,
                AgentActionRisk.Mutation),
            (
                BuiltInAgentTools.BrowserStop,
                "Stop browser loading",
                AgentCapability.BrowserNavigation,
                AgentActionRisk.Mutation),
        };

        foreach (var (name, title, capability, risk) in expected)
        {
            Assert.True(BuiltInAgentTools.Catalog.TryGet(name, out var descriptor));
            Assert.NotNull(descriptor);
            Assert.Equal(title, descriptor.Title);
            Assert.Equal(capability, descriptor.Capability);
            Assert.Equal(risk, descriptor.Risk);
        }
    }

    [Fact]
    public void Built_in_browser_tool_names_are_stable()
    {
        Assert.Equal("browser.read_state", BuiltInAgentTools.BrowserReadState);
        Assert.Equal("browser.snapshot", BuiltInAgentTools.BrowserSnapshot);
        Assert.Equal("browser.wait", BuiltInAgentTools.BrowserWait);
        Assert.Equal("browser.click", BuiltInAgentTools.BrowserClick);
        Assert.Equal("browser.fill", BuiltInAgentTools.BrowserFill);
        Assert.Equal("browser.check", BuiltInAgentTools.BrowserCheck);
        Assert.Equal("browser.navigate", BuiltInAgentTools.BrowserNavigate);
        Assert.Equal("browser.back", BuiltInAgentTools.BrowserBack);
        Assert.Equal("browser.forward", BuiltInAgentTools.BrowserForward);
        Assert.Equal("browser.reload", BuiltInAgentTools.BrowserReload);
        Assert.Equal("browser.stop", BuiltInAgentTools.BrowserStop);
    }

    [Fact]
    public void Navigate_binds_the_full_address_in_approval_and_digest()
    {
        var addressText = AddressWithLength(
            AgentBrowserActionComposer.MaximumAgentAddressLength,
            'a');
        var address = BrowserAddress(addressText);
        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(),
            Navigate(address));

        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("session_id", "browser-session-1"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("address", addressText),
                (argument.Name, argument.DisplayValue)));
        Assert.Equal(
            AgentBrowserActionComposer.MaximumAgentAddressLength,
            action.Proposal.Presentation.Arguments[1].DisplayValue.Length);

        var tailChanged = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(),
            Navigate(BrowserAddress(AddressWithLength(
                AgentBrowserActionComposer.MaximumAgentAddressLength,
                'b'))));

        Assert.NotEqual(
            action.Proposal.ArgumentDigest,
            tailChanged.Proposal.ArgumentDigest);
        Assert.NotEqual(
            ApprovalMaterial(action),
            ApprovalMaterial(tailChanged), StringComparer.Ordinal);
    }

    [Fact]
    public void Navigate_rejects_an_address_above_the_agent_limit()
    {
        var oversized = BrowserAddress(AddressWithLength(
            AgentBrowserActionComposer.MaximumAgentAddressLength + 1,
            'a'));

        var error = Assert.Throws<ArgumentException>(() =>
            new AgentBrowserActionComposer().Prepare(
                Envelope(),
                BrowserContext(),
                Navigate(oversized)));

        Assert.Contains("2048", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Click_binds_the_full_reference_and_document_revision_in_approval_and_digest()
    {
        var reference = new BrowserElementReferenceId(
            new string(
                'r',
                BrowserElementReferenceId.MaximumValueBytes));
        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 123456789),
            Click(reference, documentRevision: 123456789));

        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("session_id", "browser-session-1"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("origin", "https://example.test:443"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("reference", reference.Value),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("document_revision", "123456789"),
                (argument.Name, argument.DisplayValue)));

        var referenceChanged = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 123456789),
            Click(
                new BrowserElementReferenceId(
                    new string('r', reference.Value.Length - 1) + "s"),
                documentRevision: 123456789));
        var revisionChanged = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 123456790),
            Click(reference, documentRevision: 123456790));

        Assert.NotEqual(
            action.Proposal.ArgumentDigest,
            referenceChanged.Proposal.ArgumentDigest);
        Assert.NotEqual(
            action.Proposal.ArgumentDigest,
            revisionChanged.Proposal.ArgumentDigest);
        Assert.NotEqual(ApprovalMaterial(action), ApprovalMaterial(referenceChanged), StringComparer.Ordinal);
        Assert.NotEqual(ApprovalMaterial(action), ApprovalMaterial(revisionChanged), StringComparer.Ordinal);
    }

    [Fact]
    public void Check_binds_the_full_reference_and_document_revision_in_approval_and_digest()
    {
        var reference = new BrowserElementReferenceId(
            new string(
                'r',
                BrowserElementReferenceId.MaximumValueBytes));
        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 123456789),
            Check(reference, documentRevision: 123456789));

        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("session_id", "browser-session-1"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("origin", "https://example.test:443"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("reference", reference.Value),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("document_revision", "123456789"),
                (argument.Name, argument.DisplayValue)));

        var referenceChanged = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 123456789),
            Check(
                new BrowserElementReferenceId(
                    new string('r', reference.Value.Length - 1) + "s"),
                documentRevision: 123456789));
        var revisionChanged = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 123456790),
            Check(reference, documentRevision: 123456790));

        Assert.NotEqual(
            action.Proposal.ArgumentDigest,
            referenceChanged.Proposal.ArgumentDigest);
        Assert.NotEqual(
            action.Proposal.ArgumentDigest,
            revisionChanged.Proposal.ArgumentDigest);
        Assert.NotEqual(ApprovalMaterial(action), ApprovalMaterial(referenceChanged), StringComparer.Ordinal);
        Assert.NotEqual(ApprovalMaterial(action), ApprovalMaterial(revisionChanged), StringComparer.Ordinal);
    }

    [Fact]
    public void ElementStateWaitBindsExactOriginDocumentAndDesiredState()
    {
        var request = new AgentBrowserRequest.Wait(
            new BrowserWaitRequest(
                Session(),
                new BrowserWaitCondition.ElementState(
                    new BrowserElementReferenceId("checkbox_1"),
                    SourceDocumentRevision: 7,
                    BrowserElementStateKind.Checked,
                    Expected: false),
                TimeSpan.FromMinutes(1)));

        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 7),
            request);

        Assert.Equal(BuiltInAgentTools.BrowserWait, action.Proposal.ToolName);
        Assert.Contains(
            action.Proposal.Presentation.Arguments,
            argument => argument is
            {
                Name: "origin",
                DisplayValue: "https://example.test:443",
            });
        Assert.Contains(
            action.Proposal.Presentation.Arguments,
            argument => argument is
            {
                Name: "reference",
                DisplayValue: "checkbox_1",
            });
        Assert.Contains(
            action.Proposal.Presentation.Arguments,
            argument => argument is
            {
                Name: "expected",
                DisplayValue: "false",
            });
        Assert.Throws<ArgumentException>(() =>
            new AgentBrowserActionComposer().Prepare(
                Envelope(),
                BrowserContext(browserDocumentRevision: 8),
                request));
    }

    [Fact]
    public void Fill_binds_exact_reference_revision_and_text_in_approval_and_digest()
    {
        var reference = new BrowserElementReferenceId(
            new string(
                'r',
                BrowserElementReferenceId.MaximumValueBytes));
        var text = new string(
            'x',
            BrowserElementFillRequest.MaximumTextBytes);
        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 123456789),
            Fill(reference, documentRevision: 123456789, text));

        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("session_id", "browser-session-1"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("origin", "https://example.test:443"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("reference", reference.Value),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("document_revision", "123456789"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("text", string.Concat('"', text, '"')),
                (argument.Name, argument.DisplayValue)));

        var referenceChanged = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 123456789),
            Fill(
                new BrowserElementReferenceId(
                    new string('r', reference.Value.Length - 1) + "s"),
                documentRevision: 123456789,
                text));
        var revisionChanged = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 123456790),
            Fill(reference, documentRevision: 123456790, text));
        var textChanged = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(browserDocumentRevision: 123456789),
            Fill(
                reference,
                documentRevision: 123456789,
                new string('x', text.Length - 1) + "y"));

        Assert.NotEqual(
            action.Proposal.ArgumentDigest,
            referenceChanged.Proposal.ArgumentDigest);
        Assert.NotEqual(
            action.Proposal.ArgumentDigest,
            revisionChanged.Proposal.ArgumentDigest);
        Assert.NotEqual(
            action.Proposal.ArgumentDigest,
            textChanged.Proposal.ArgumentDigest);
        Assert.NotEqual(ApprovalMaterial(action), ApprovalMaterial(referenceChanged), StringComparer.Ordinal);
        Assert.NotEqual(ApprovalMaterial(action), ApprovalMaterial(revisionChanged), StringComparer.Ordinal);
        Assert.NotEqual(ApprovalMaterial(action), ApprovalMaterial(textChanged), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("Authorization: Bearer abc123")]
    [InlineData("https://user:hunter2@example.test")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    [InlineData("ghp_0123456789abcdef")]
    [InlineData("github_pat_0123456789abcdef")]
    [InlineData("sk-0123456789abcdef")]
    [InlineData("AKIA0123456789ABCDEF")]
    [InlineData("xoxb-0123456789abcdef")]
    [InlineData("xoxp-0123456789abcdef")]
    public void Fill_rejects_literal_secret_material_before_creating_approval(
        string text)
    {
        var composer = new AgentBrowserActionComposer();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                BrowserContext(),
                Fill(
                    new BrowserElementReferenceId("element_1"),
                    documentRevision: 7,
                    text)));
    }

    [Fact]
    public void Fill_approval_quotes_empty_whitespace_and_escaped_text_exactly()
    {
        var cases = new[]
        {
            (Text: string.Empty, Display: "\"\""),
            (Text: "   ", Display: "\"   \""),
            (Text: "\t\r\n", Display: "\"\\t\\r\\n\""),
            (Text: "\"\\", Display: "\"\\\"\\\\\""),
        };

        foreach (var item in cases)
        {
            var action = new AgentBrowserActionComposer().Prepare(
                Envelope(),
                BrowserContext(),
                Fill(
                    new BrowserElementReferenceId("element_1"),
                    documentRevision: 7,
                    item.Text));

            var argument = Assert.Single(
                action.Proposal.Presentation.Arguments,
                candidate => string.Equals(candidate.Name, "text", StringComparison.Ordinal));
            Assert.Equal(item.Display, argument.DisplayValue);
        }
    }

    [Fact]
    public void Fill_approval_bounds_worst_case_valid_escape_expansion()
    {
        var text = new string(
            '\u00ad',
            BrowserElementFillRequest.MaximumTextBytes / 2);

        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(),
            Fill(
                new BrowserElementReferenceId("element_1"),
                documentRevision: 7,
                text));

        var argument = Assert.Single(
            action.Proposal.Presentation.Arguments,
            candidate => string.Equals(candidate.Name, "text", StringComparison.Ordinal));
        Assert.StartsWith("\"\\u00AD", argument.DisplayValue);
        Assert.EndsWith("\\u00AD\"", argument.DisplayValue);
        Assert.DoesNotContain(
            text,
            argument.DisplayValue,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_fill_mutation_escalates_auto_policy_to_human_approval()
    {
        Assert.True(
            BuiltInAgentTools.Catalog.TryGet(
                BuiltInAgentTools.BrowserFill,
                out var descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal(
            AgentPermission.Ask,
            AgentPolicy.Default.GetPermission(descriptor.Capability));
        Assert.Equal(
            AgentPolicyDecision.RequiresApproval,
            AgentPolicyResolver.Evaluate(
                AgentPermission.Auto,
                descriptor.Risk));
        Assert.Equal(
            AgentPolicyDecision.RequiresApproval,
            AgentPolicyResolver.Evaluate(
                AgentPolicy.Default.GetPermission(descriptor.Capability),
                descriptor.Risk));
    }

    [Fact]
    public void Every_browser_mutation_request_change_alters_bound_material()
    {
        var composer = new AgentBrowserActionComposer();
        var context = BrowserContext();
        var envelope = Envelope();
        AgentBrowserRequest[] requests =
        [
            Navigate(BrowserAddress("https://example.test/first")),
            Navigate(BrowserAddress("https://example.test/second")),
            Click(new BrowserElementReferenceId("element_1"), documentRevision: 7),
            Fill(
                new BrowserElementReferenceId("element_1"),
                documentRevision: 7,
                "first value"),
            Fill(
                new BrowserElementReferenceId("element_1"),
                documentRevision: 7,
                "second value"),
            Check(
                new BrowserElementReferenceId("element_1"),
                documentRevision: 7),
            new AgentBrowserRequest.Back(Session()),
            new AgentBrowserRequest.Forward(Session()),
            new AgentBrowserRequest.Reload(Session()),
            new AgentBrowserRequest.Stop(Session()),
        ];

        var actions = requests
            .Select(request => composer.Prepare(envelope, context, request))
            .ToArray();

        Assert.Equal(
            actions.Length,
            actions.Select(action => action.Proposal.ArgumentDigest).Distinct().Count());
        Assert.Equal(
            8,
            actions.Select(action => action.Proposal.ToolName).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(BrowserOperation.ReadState)]
    [InlineData(BrowserOperation.Snapshot)]
    [InlineData(BrowserOperation.Wait)]
    [InlineData(BrowserOperation.Click)]
    [InlineData(BrowserOperation.Fill)]
    [InlineData(BrowserOperation.Check)]
    [InlineData(BrowserOperation.Navigate)]
    [InlineData(BrowserOperation.Back)]
    [InlineData(BrowserOperation.Forward)]
    [InlineData(BrowserOperation.Reload)]
    [InlineData(BrowserOperation.Stop)]
    public void Exact_panel_operation_material_omits_panel_selection_syntax(
        BrowserOperation operation)
    {
        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(),
            Request(operation));

        Assert.DoesNotContain(
            action.Proposal.Presentation.Arguments,
            argument => string.Equals(argument.Name, "panel_id", StringComparison.Ordinal));
        Assert.Contains(
            action.Proposal.Presentation.Arguments,
            argument => string.Equals(argument.Name, "session_id", StringComparison.Ordinal));
    }

    [Fact]
    public void Broad_action_narrows_the_selected_browser_to_one_exact_panel_and_session()
    {
        var broad = MultipleBrowserContext();
        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            broad,
            new AgentBrowserRequest.Reload(new SessionId("browser-session-2")));
        var target = Assert.IsType<AgentTarget.Panel>(action.Proposal.Target);

        Assert.Equal(new PanelInstanceId("browser-panel-2"), target.PanelId);
        Assert.Equal(
            AgentTargetIdentity.Create(target),
            action.Proposal.TargetIdentity);
        Assert.NotEqual(broad.BindingFingerprint, action.Proposal.TargetFingerprint);
        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("session_id", "browser-session-2"),
                (argument.Name, argument.DisplayValue)));
    }

    [Fact]
    public void Approval_identifies_the_exact_browser_and_has_no_terminal_directory()
    {
        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            BrowserContext(),
            Navigate(BrowserAddress("https://docs.example.test/")));

        Assert.Equal(
            "Documentation — panel browser-panel-1 — session browser-session-1",
            action.Proposal.Presentation.TargetTitle);
        Assert.Equal("Embedded browser", action.Proposal.Presentation.Host);
        Assert.Null(action.Proposal.Presentation.WorkingDirectory);
    }

    [Fact]
    public void Exact_session_target_is_supported_and_still_identifies_its_owner_panel()
    {
        var context = BrowserContext(
            target: new AgentTarget.ConnectionSession(Session()));
        var action = new AgentBrowserActionComposer().Prepare(
            Envelope(),
            context,
            new AgentBrowserRequest.ReadState(Session()));

        Assert.IsType<AgentTarget.ConnectionSession>(action.Proposal.Target);
        Assert.Equal(
            "Documentation — session browser-session-1 — panel browser-panel-1",
            action.Proposal.Presentation.TargetTitle);
    }

    [Fact]
    public void Execution_binding_preserves_authorized_target_after_fresh_validation()
    {
        var composer = new AgentBrowserActionComposer();
        var action = composer.Prepare(
            Envelope(),
            BrowserContext(graphRevision: 11, sessionRevision: 17),
            Navigate(BrowserAddress("https://example.test/")));
        var fresh = BrowserContext(graphRevision: 12, sessionRevision: 18);

        var binding = composer.BindForExecution(action, fresh);

        Assert.Equal(action.Proposal.Id, binding.ActionId);
        Assert.Equal(action.Proposal.RunId, binding.RunId);
        Assert.Equal(action.Proposal.Actor.Id, binding.ActorId);
        Assert.Equal(action.Proposal.ToolName, binding.ToolName);
        Assert.Equal(action.Proposal.TargetIdentity, binding.TargetIdentity);
        Assert.Equal(action.Proposal.ArgumentDigest, binding.ArgumentDigest);
        Assert.Equal(action.Proposal.PolicyGeneration, binding.PolicyGeneration);
        Assert.Equal(action.Proposal.TargetFingerprint, binding.TargetFingerprint);
        Assert.NotEqual(fresh.BindingFingerprint, binding.TargetFingerprint);
    }

    [Fact]
    public void Interaction_origin_or_document_drift_fails_closed_during_fresh_binding()
    {
        var composer = new AgentBrowserActionComposer();
        var action = composer.Prepare(
            Envelope(),
            BrowserContext(
                browserAddress: "https://example.test/source",
                browserDocumentRevision: 7),
            Click(
                new BrowserElementReferenceId("element_1"),
                documentRevision: 7));

        Assert.Throws<InvalidOperationException>(() =>
            composer.BindForExecution(
                action,
                BrowserContext(
                    browserAddress: "https://other.example.test/source",
                    browserDocumentRevision: 7)));
        Assert.Throws<ArgumentException>(() =>
            composer.BindForExecution(
                action,
                BrowserContext(
                    browserAddress: "https://example.test/source",
                    browserDocumentRevision: 8)));
    }

    [Fact]
    public void Interaction_requires_trusted_browser_document_metadata()
    {
        var composer = new AgentBrowserActionComposer();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                BrowserContext(includeBrowserMetadata: false),
                Click(
                    new BrowserElementReferenceId("element_1"),
                    documentRevision: 7)));
    }

    [Fact]
    public void Browser_document_metadata_changes_the_context_fingerprint()
    {
        var original = BrowserContext(
            browserAddress: "https://example.test/source",
            browserDocumentRevision: 7);
        var originChanged = BrowserContext(
            browserAddress: "https://other.example.test/source",
            browserDocumentRevision: 7);
        var revisionChanged = BrowserContext(
            browserAddress: "https://example.test/source",
            browserDocumentRevision: 8);

        Assert.NotEqual(
            original.BindingFingerprint,
            originChanged.BindingFingerprint);
        Assert.NotEqual(
            original.BindingFingerprint,
            revisionChanged.BindingFingerprint);
    }

    [Fact]
    public void Changed_execution_target_or_capability_fails_closed()
    {
        var composer = new AgentBrowserActionComposer();
        var action = composer.Prepare(
            Envelope(),
            BrowserContext(),
            Navigate(BrowserAddress("https://example.test/")));

        Assert.Throws<ArgumentException>(() =>
            composer.BindForExecution(
                action,
                BrowserContext(
                    target: new AgentTarget.Panel(
                        Window(),
                        Workspace(),
                        Tab(),
                        new PanelInstanceId("browser-panel-other")))));
        Assert.Throws<ArgumentException>(() =>
            composer.BindForExecution(
                action,
                BrowserContext(capabilities: [SessionCapabilities.BrowserReadState])));
        Assert.Throws<ArgumentException>(() =>
            composer.BindForExecution(
                action,
                BrowserContext(
                    target: new AgentTarget.Workspace(Window(), Workspace()))));
    }

    [Fact]
    public void Invalid_or_ambiguous_browser_contexts_fail_closed()
    {
        var composer = new AgentBrowserActionComposer();
        var envelope = Envelope();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                ContextWithoutSession(),
                new AgentBrowserRequest.ReadState(Session())));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                DuplicateSessionContext(),
                new AgentBrowserRequest.ReadState(Session())));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                BrowserContext(kind: PanelKind.Terminal),
                new AgentBrowserRequest.ReadState(Session())));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                BrowserContext(lifecycle: SessionLifecycle.Starting),
                new AgentBrowserRequest.ReadState(Session())));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                BrowserContext(capabilities: [SessionCapabilities.BrowserReload]),
                new AgentBrowserRequest.Reload(Session())));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                BrowserContext(capabilities: [SessionCapabilities.BrowserClick]),
                Click(
                    new BrowserElementReferenceId("element_1"),
                    documentRevision: 7)));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                BrowserContext(capabilities: [SessionCapabilities.BrowserFill]),
                Fill(
                    new BrowserElementReferenceId("element_1"),
                    documentRevision: 7,
                    "browser value")));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                BrowserContext(capabilities: [SessionCapabilities.BrowserCheck]),
                Check(
                    new BrowserElementReferenceId("element_1"),
                    documentRevision: 7)));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                BrowserContext(),
                new AgentBrowserRequest.ReadState(
                    new SessionId("browser-session-other"))));
    }

    [Fact]
    public void Broad_context_cannot_bind_a_browser_outside_its_target()
    {
        var context = BrowserContext(
            target: new AgentTarget.Workspace(Window(), new WorkspaceInstanceId("other")));

        Assert.Throws<ArgumentException>(() =>
            new AgentBrowserActionComposer().Prepare(
                Envelope(),
                context,
                new AgentBrowserRequest.ReadState(Session())));
    }

    [Fact]
    public void Navigate_requires_a_typed_navigation_value_and_address()
    {
        var composer = new AgentBrowserActionComposer();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                BrowserContext(),
                new AgentBrowserRequest.Navigate(null!)));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                BrowserContext(),
                new AgentBrowserRequest.Navigate(
                    new BrowserNavigateRequest(Session(), null!))));
    }

    [Fact]
    public void Click_requires_a_typed_request_value()
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentBrowserActionComposer().Prepare(
                Envelope(),
                BrowserContext(),
                new AgentBrowserRequest.Click(null!)));
    }

    [Fact]
    public void Fill_requires_a_typed_request_value()
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentBrowserActionComposer().Prepare(
                Envelope(),
                BrowserContext(),
                new AgentBrowserRequest.Fill(null!)));
    }

    [Fact]
    public void Check_requires_a_typed_request_value()
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentBrowserActionComposer().Prepare(
                Envelope(),
                BrowserContext(),
                new AgentBrowserRequest.Check(null!)));
    }

    private static AgentActionEnvelope Envelope() =>
        new(
            new AgentActionId("browser-action-1"),
            new AgentRunId("browser-run-1"),
            new ActorDescriptor(
                new ActorId("browser-agent-1"),
                ActorKind.Agent,
                "Browser Agent"),
            policyGeneration: 7,
            Now,
            Now.AddMinutes(1));

    private static AgentBrowserRequest Request(BrowserOperation operation) =>
        operation switch
        {
            BrowserOperation.ReadState =>
                new AgentBrowserRequest.ReadState(Session()),
            BrowserOperation.Snapshot =>
                new AgentBrowserRequest.Snapshot(Session()),
            BrowserOperation.Wait =>
                new AgentBrowserRequest.Wait(
                    new BrowserWaitRequest(
                        Session(),
                        new BrowserWaitCondition.Delay(
                            TimeSpan.FromSeconds(1)),
                        TimeSpan.FromSeconds(2))),
            BrowserOperation.Click =>
                Click(
                    new BrowserElementReferenceId("element_1"),
                    documentRevision: 7),
            BrowserOperation.Fill =>
                Fill(
                    new BrowserElementReferenceId("element_1"),
                    documentRevision: 7,
                    "browser value"),
            BrowserOperation.Check =>
                Check(
                    new BrowserElementReferenceId("element_1"),
                    documentRevision: 7),
            BrowserOperation.Mouse =>
                new AgentBrowserRequest.Mouse(
                    new BrowserMouseRequest(
                        Session(),
                        AutomationBinding(),
                        BrowserMouseAction.Click,
                        10,
                        10,
                        BrowserMouseButton.Left,
                        clickCount: 1)),
            BrowserOperation.Key =>
                new AgentBrowserRequest.Key(
                    new BrowserKeyRequest(
                        Session(),
                        AutomationBinding(),
                        BrowserKeyAction.Press,
                        BrowserKey.Enter)),
            BrowserOperation.Scroll =>
                new AgentBrowserRequest.Scroll(
                    new BrowserScrollRequest(
                        Session(),
                        AutomationBinding(),
                        10,
                        10,
                        0,
                        100)),
            BrowserOperation.Evaluate =>
                new AgentBrowserRequest.Evaluate(
                    new BrowserEvaluateRequest(
                        Session(),
                        AutomationBinding(),
                        "1 + 1")),
            BrowserOperation.Navigate =>
                Navigate(BrowserAddress("https://example.test/")),
            BrowserOperation.Back =>
                new AgentBrowserRequest.Back(Session()),
            BrowserOperation.Forward =>
                new AgentBrowserRequest.Forward(Session()),
            BrowserOperation.Reload =>
                new AgentBrowserRequest.Reload(Session()),
            BrowserOperation.Stop =>
                new AgentBrowserRequest.Stop(Session()),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static AgentBrowserRequest Navigate(BrowserAddress address) =>
        new AgentBrowserRequest.Navigate(
            new BrowserNavigateRequest(Session(), address));

    private static AgentBrowserRequest Click(
        BrowserElementReferenceId reference,
        long documentRevision) =>
        new AgentBrowserRequest.Click(
            new BrowserElementClickRequest(
                Session(),
                reference,
                documentRevision));

    private static AgentBrowserRequest Fill(
        BrowserElementReferenceId reference,
        long documentRevision,
        string text) =>
        new AgentBrowserRequest.Fill(
            new BrowserElementFillRequest(
                Session(),
                reference,
                documentRevision,
                text));

    private static AgentBrowserRequest Check(
        BrowserElementReferenceId reference,
        long documentRevision) =>
        new AgentBrowserRequest.Check(
            new BrowserElementCheckRequest(
                Session(),
                reference,
                documentRevision));

    private static BrowserAddress BrowserAddress(string address) =>
        new(new Uri(address, UriKind.Absolute));

    private static BrowserAutomationBinding AutomationBinding() =>
        new(
            new BrowserDocumentBinding(
                BrowserAddress("https://example.test/source"),
                7),
            new BrowserViewportState(800, 600, 1),
            viewportRevision: 3,
            inputEpoch: 4);

    private static string AddressWithLength(int length, char fill)
    {
        const string Prefix = "https://example.test/";
        return Prefix + new string(fill, length - Prefix.Length);
    }

    private static string ApprovalMaterial(AgentBrowserAction action) =>
        string.Join(
            "\n",
            action.Proposal.Presentation.Arguments.Select(
                argument => $"{argument.Name}:{argument.DisplayValue}"));

    private static AgentContextSnapshot BrowserContext(
        AgentTarget? target = null,
        PanelKind kind = PanelKind.Browser,
        IEnumerable<string>? capabilities = null,
        SessionLifecycle lifecycle = SessionLifecycle.Active,
        long graphRevision = 11,
        long sessionRevision = 17,
        string browserAddress = "https://example.test/source",
        long browserDocumentRevision = 7,
        bool includeBrowserMetadata = true)
    {
        var panel = new PanelInstance(Panel(), kind, "Documentation", Session());
        var tab = new TabInstance(Tab(), "Web", [panel], panel.Id);
        var workspace = new WorkspaceInstance(Workspace(), "Operations", [tab], tab.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            workspace,
            graphRevision,
            lastSequence: graphRevision);
        var contextPanel = AgentContextPanel.ForGraphPanel(
            graph,
            Tab(),
            Panel(),
            Descriptor(
                Session(),
                kind,
                Panel(),
                capabilities ?? AllBrowserCapabilities(),
                lifecycle,
                sessionRevision,
                browserAddress,
                browserDocumentRevision,
                includeBrowserMetadata));
        return new AgentContextSnapshot(
            target ?? ExactPanelTarget(),
            [contextPanel],
            Now);
    }

    private static AgentContextSnapshot ContextWithoutSession()
    {
        var panel = new PanelInstance(Panel(), PanelKind.Browser, "Documentation");
        var tab = new TabInstance(Tab(), "Web", [panel], panel.Id);
        var workspace = new WorkspaceInstance(Workspace(), "Operations", [tab], tab.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            workspace,
            revision: 11,
            lastSequence: 11);
        return new AgentContextSnapshot(
            ExactPanelTarget(),
            [AgentContextPanel.ForGraphPanel(graph, Tab(), Panel(), session: null)],
            Now);
    }

    private static AgentContextSnapshot MultipleBrowserContext()
    {
        var secondPanelId = new PanelInstanceId("browser-panel-2");
        var secondSessionId = new SessionId("browser-session-2");
        var firstPanel = new PanelInstance(
            Panel(),
            PanelKind.Browser,
            "Documentation",
            Session());
        var secondPanel = new PanelInstance(
            secondPanelId,
            PanelKind.Browser,
            "Operations",
            secondSessionId);
        var tab = new TabInstance(Tab(), "Web", [firstPanel, secondPanel], firstPanel.Id);
        var workspace = new WorkspaceInstance(Workspace(), "Operations", [tab], tab.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            workspace,
            revision: 11,
            lastSequence: 11);
        return new AgentContextSnapshot(
            new AgentTarget.Workspace(Window(), Workspace()),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    Panel(),
                    Descriptor(
                        Session(),
                        PanelKind.Browser,
                        Panel(),
                        AllBrowserCapabilities())),
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    secondPanelId,
                    Descriptor(
                        secondSessionId,
                        PanelKind.Browser,
                        secondPanelId,
                        AllBrowserCapabilities())),
            ],
            Now);
    }

    private static AgentContextSnapshot DuplicateSessionContext()
    {
        var secondPanelId = new PanelInstanceId("browser-panel-2");
        var firstPanel = new PanelInstance(
            Panel(),
            PanelKind.Browser,
            "Documentation",
            Session());
        var secondPanel = new PanelInstance(
            secondPanelId,
            PanelKind.Browser,
            "Operations",
            Session());
        var tab = new TabInstance(Tab(), "Web", [firstPanel, secondPanel], firstPanel.Id);
        var workspace = new WorkspaceInstance(Workspace(), "Operations", [tab], tab.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            workspace,
            revision: 11,
            lastSequence: 11);
        return new AgentContextSnapshot(
            new AgentTarget.Workspace(Window(), Workspace()),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    Panel(),
                    Descriptor(
                        Session(),
                        PanelKind.Browser,
                        Panel(),
                        AllBrowserCapabilities())),
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    secondPanelId,
                    Descriptor(
                        Session(),
                        PanelKind.Browser,
                        secondPanelId,
                        AllBrowserCapabilities())),
            ],
            Now);
    }

    private static SessionDescriptor Descriptor(
        SessionId sessionId,
        PanelKind kind,
        PanelInstanceId panelId,
        IEnumerable<string> capabilities,
        SessionLifecycle lifecycle = SessionLifecycle.Active,
        long revision = 17,
        string browserAddress = "https://example.test/source",
        long browserDocumentRevision = 7,
        bool includeBrowserMetadata = true) =>
        new(
            sessionId,
            kind,
            lifecycle,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                Window(),
                Workspace(),
                Tab(),
                panelId),
            new CapabilitySet(capabilities),
            Revision: revision,
            HasActiveWork: false,
            StatusDetail: "Ready",
            BrowserMetadata: kind == PanelKind.Browser && includeBrowserMetadata
                ? new BrowserSessionMetadata(
                    BrowserNavigationOrigin.FromAddress(
                        BrowserAddress(browserAddress)),
                    browserDocumentRevision,
                    new BrowserViewportState(800, 600, 1),
                    viewportRevision: 3,
                    inputEpoch: 4,
                    address: BrowserAddress(browserAddress))
                : null);

    private static string[] AllBrowserCapabilities() =>
    [
        SessionCapabilities.BrowserReadState,
        SessionCapabilities.BrowserSnapshot,
        SessionCapabilities.BrowserWait,
        SessionCapabilities.BrowserClick,
        SessionCapabilities.BrowserFill,
        SessionCapabilities.BrowserCheck,
        SessionCapabilities.BrowserMouse,
        SessionCapabilities.BrowserKey,
        SessionCapabilities.BrowserScroll,
        SessionCapabilities.BrowserEvaluate,
        SessionCapabilities.BrowserNavigate,
        SessionCapabilities.BrowserBack,
        SessionCapabilities.BrowserForward,
        SessionCapabilities.BrowserReload,
        SessionCapabilities.BrowserStop,
        SessionCapabilities.BrowserOriginGuard,
        SessionCapabilities.BrowserAgentInputBarrier,
    ];

    private static AgentTarget.Panel ExactPanelTarget() =>
        new(Window(), Workspace(), Tab(), Panel());

    private static WindowInstanceId Window() => new("browser-window-1");

    private static WorkspaceInstanceId Workspace() => new("browser-workspace-1");

    private static TabInstanceId Tab() => new("browser-tab-1");

    private static PanelInstanceId Panel() => new("browser-panel-1");

    private static SessionId Session() => new("browser-session-1");

    public enum BrowserOperation
    {
        ReadState,
        Snapshot,
        Wait,
        Click,
        Fill,
        Check,
        Mouse,
        Key,
        Scroll,
        Evaluate,
        Navigate,
        Back,
        Forward,
        Reload,
        Stop,
    }
}

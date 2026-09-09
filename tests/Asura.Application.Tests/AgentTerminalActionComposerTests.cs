using System.Globalization;
using System.Reflection;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentTerminalActionComposerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TerminalOperation.ReadScreen, BuiltInAgentTools.TerminalReadScreen)]
    [InlineData(TerminalOperation.ReadScreenDiff, BuiltInAgentTools.TerminalReadScreenDiff)]
    [InlineData(TerminalOperation.FindOnScreen, BuiltInAgentTools.TerminalFindOnScreen)]
    [InlineData(TerminalOperation.FindRenderedHistory, BuiltInAgentTools.TerminalFindRenderedHistory)]
    [InlineData(TerminalOperation.JumpToRenderedHistory, BuiltInAgentTools.TerminalJumpToRenderedHistory)]
    [InlineData(TerminalOperation.ReadScrollback, BuiltInAgentTools.TerminalReadScrollback)]
    [InlineData(TerminalOperation.FindScrollback, BuiltInAgentTools.TerminalFind)]
    [InlineData(TerminalOperation.ScrollViewport, BuiltInAgentTools.TerminalScrollViewport)]
    [InlineData(TerminalOperation.SendText, BuiltInAgentTools.TerminalSendText)]
    [InlineData(TerminalOperation.Paste, BuiltInAgentTools.TerminalPaste)]
    [InlineData(TerminalOperation.SubmitText, BuiltInAgentTools.TerminalSubmitText)]
    [InlineData(TerminalOperation.SendKey, BuiltInAgentTools.TerminalSendKeys)]
    [InlineData(TerminalOperation.SendChord, BuiltInAgentTools.TerminalSendChord)]
    [InlineData(TerminalOperation.SendMouse, BuiltInAgentTools.TerminalSendMouse)]
    [InlineData(TerminalOperation.WaitForText, BuiltInAgentTools.TerminalWait)]
    [InlineData(TerminalOperation.WaitForDelay, BuiltInAgentTools.TerminalWait)]
    [InlineData(TerminalOperation.WaitForChange, BuiltInAgentTools.TerminalWait)]
    [InlineData(TerminalOperation.WaitForPromptReady, BuiltInAgentTools.TerminalWait)]
    [InlineData(TerminalOperation.WaitForCommandFinished, BuiltInAgentTools.TerminalWait)]
    [InlineData(TerminalOperation.WaitForStable, BuiltInAgentTools.TerminalWait)]
    [InlineData(TerminalOperation.Interrupt, BuiltInAgentTools.TerminalInterrupt)]
    [InlineData(TerminalOperation.Resize, BuiltInAgentTools.TerminalResize)]
    public void Closed_request_kinds_map_to_trusted_tools(
        TerminalOperation operation,
        string expectedTool)
    {
        var request = Request(operation);
        var context = TerminalContext();

        var action = new AgentTerminalActionComposer().Prepare(
            Envelope(),
            context,
            request);

        Assert.Same(request, action.Request);
        Assert.Equal(expectedTool, action.Proposal.ToolName);
        Assert.Same(context.Target, action.Proposal.Target);
        Assert.Equal(context.BindingFingerprint, action.Proposal.TargetFingerprint);
        Assert.Equal(AgentTargetIdentity.Create(context.Target), action.Proposal.TargetIdentity);
    }

    [Fact]
    public void Request_union_is_closed_and_prepared_action_has_no_public_constructor()
    {
        var requestKinds = typeof(AgentTerminalRequest)
            .GetNestedTypes(BindingFlags.Public)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(typeof(AgentTerminalRequest).IsAbstract);
        Assert.Equal(
            [
                "FindOnScreen",
                "FindRenderedHistory",
                "FindScrollback",
                "Interrupt",
                "JumpToRenderedHistory",
                "Paste",
                "ReadScreen",
                "ReadScreenDiff",
                "ReadScrollback",
                "Resize",
                "ScrollViewport",
                "SendChord",
                "SendKey",
                "SendMouse",
                "SendText",
                "SubmitText",
                "WaitForChange",
                "WaitForCommandFinished",
                "WaitForDelay",
                "WaitForPromptReady",
                "WaitForStable",
                "WaitForText",
            ],
            requestKinds.Select(type => type.Name), StringComparer.Ordinal);
        Assert.All(requestKinds, type => Assert.True(type.IsSealed));
        Assert.Empty(typeof(AgentTerminalAction).GetConstructors());
        Assert.Empty(typeof(AgentActionExecutionBinding).GetConstructors());
    }

    [Fact]
    public void Every_material_request_mutation_changes_the_argument_digest()
    {
        var context = TerminalContext();
        var envelope = Envelope();
        var composer = new AgentTerminalActionComposer();
        var pairs = new (AgentTerminalRequest Before, AgentTerminalRequest After)[]
        {
            (
                SendText("echo first"),
                SendText("echo second")),
            (
                Paste("first line\r\nsecond line"),
                Paste("first line\r\nthird line")),
            (
                SendKey(TerminalKey.Enter, TerminalKeyModifiers.None),
                SendKey(TerminalKey.Enter, TerminalKeyModifiers.Control)),
            (
                SendKey(TerminalKey.Backspace, TerminalKeyModifiers.None),
                SendKey(
                    TerminalKey.Backspace,
                    TerminalKeyModifiers.None,
                    repeatCount: 12)),
            (
                SendChord('d', TerminalCharacterChordModifier.Control),
                SendChord('r', TerminalCharacterChordModifier.Control)),
            (
                SendChord('d', TerminalCharacterChordModifier.Control),
                SendChord('d', TerminalCharacterChordModifier.Alt)),
            (
                SendMouse(
                    TerminalMouseButton.Left,
                    TerminalMouseEventKind.Down,
                    5,
                    7,
                    TerminalKeyModifiers.None),
                SendMouse(
                    TerminalMouseButton.Left,
                    TerminalMouseEventKind.Down,
                    5,
                    7,
                    TerminalKeyModifiers.Control)),
            (
                WaitForText("ready", TimeSpan.FromSeconds(2)),
                WaitForText("finished", TimeSpan.FromSeconds(2))),
            (
                WaitForChange(5, TimeSpan.FromSeconds(2)),
                WaitForChange(6, TimeSpan.FromSeconds(2))),
            (
                WaitForStable(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2)),
                WaitForStable(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2))),
            (
                WaitForPromptReady(5, TimeSpan.FromSeconds(2)),
                WaitForPromptReady(6, TimeSpan.FromSeconds(2))),
            (
                WaitForCommandFinished(5, TimeSpan.FromSeconds(2)),
                WaitForCommandFinished(5, TimeSpan.FromSeconds(3))),
            (
                Resize(new AttachmentId("attachment-1"), columns: 80),
                Resize(new AttachmentId("attachment-1"), columns: 81)),
            (
                Resize(new AttachmentId("attachment-1"), columns: 80),
                Resize(new AttachmentId("attachment-2"), columns: 80)),
        };

        foreach (var (before, after) in pairs)
        {
            var first = composer.Prepare(envelope, context, before);
            var second = composer.Prepare(envelope, context, after);

            Assert.NotEqual(
                first.Proposal.ArgumentDigest,
                second.Proposal.ArgumentDigest);
            Assert.NotEqual(
                ApprovalMaterial(first),
                ApprovalMaterial(second), StringComparer.Ordinal);
        }
    }

    [Theory]
    [InlineData(TerminalOperation.SendText)]
    [InlineData(TerminalOperation.Paste)]
    [InlineData(TerminalOperation.SubmitText)]
    [InlineData(TerminalOperation.SendKey)]
    [InlineData(TerminalOperation.SendChord)]
    [InlineData(TerminalOperation.SendMouse)]
    [InlineData(TerminalOperation.Interrupt)]
    public void Agent_mutations_bind_execution_fields_without_an_input_lease_id(
        TerminalOperation operation)
    {
        var action = new AgentTerminalActionComposer().Prepare(
            Envelope(),
            TerminalContext(),
            Request(operation));

        Assert.DoesNotContain(
            action.Proposal.Presentation.Arguments,
            argument => argument.Name.Contains(
                "lease",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            action.Proposal.Presentation.Arguments,
            argument => string.Equals(argument.Name, "session_id", StringComparison.Ordinal));
    }

    [Fact]
    public void Approval_uses_trusted_connection_boundary_and_current_working_directory()
    {
        var terminalMetadata = new TerminalSessionMetadata(
            new ConnectionId("ssh-production"),
            "SSH: deploy@production.example:22",
            "/srv/start",
            "/srv/current");

        var action = new AgentTerminalActionComposer().Prepare(
            Envelope(),
            TerminalContext(terminalMetadata: terminalMetadata),
            SendText("uptime"));

        Assert.Equal(
            "SSH: deploy@production.example:22",
            action.Proposal.Presentation.Host);
        Assert.Equal(
            "/srv/current",
            action.Proposal.Presentation.WorkingDirectory);
    }

    [Fact]
    public void Send_mouse_tool_is_a_governed_mutation()
    {
        Assert.True(
            BuiltInAgentTools.Catalog.TryGet(
                BuiltInAgentTools.TerminalSendMouse,
                out var descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("Send terminal mouse event", descriptor.Title);
        Assert.Equal(AgentCapability.RunCommands, descriptor.Capability);
        Assert.Equal(AgentActionRisk.Mutation, descriptor.Risk);
    }

    [Fact]
    public void Send_chord_tool_is_a_governed_destructive_action()
    {
        Assert.True(
            BuiltInAgentTools.Catalog.TryGet(
                BuiltInAgentTools.TerminalSendChord,
                out var descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("Send terminal character chord", descriptor.Title);
        Assert.Equal(
            AgentCapability.DestructiveTerminalActions,
            descriptor.Capability);
        Assert.Equal(AgentActionRisk.Destructive, descriptor.Risk);
    }

    [Fact]
    public void Send_chord_binds_one_canonical_human_readable_chord()
    {
        var action = new AgentTerminalActionComposer().Prepare(
            Envelope(),
            TerminalContext(),
            SendChord('x', TerminalCharacterChordModifier.Alt));

        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("session_id", "session-1"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("chord", "Alt+X"),
                (argument.Name, argument.DisplayValue)));
    }

    [Fact]
    public void Paste_tool_is_a_governed_mutation()
    {
        Assert.True(
            BuiltInAgentTools.Catalog.TryGet(
                BuiltInAgentTools.TerminalPaste,
                out var descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("Paste terminal text", descriptor.Title);
        Assert.Equal(AgentCapability.RunCommands, descriptor.Capability);
        Assert.Equal(AgentActionRisk.Mutation, descriptor.Risk);
    }

    [Fact]
    public void Paste_binds_exact_text_with_reversible_approval_escaping()
    {
        const string text = "first\r\nsecond\tcolumn";
        var composer = new AgentTerminalActionComposer();
        var action = composer.Prepare(
            Envelope(),
            TerminalContext(),
            Paste(text));
        var literalEscapeAction = composer.Prepare(
            Envelope(),
            TerminalContext(),
            Paste(@"first\r\nsecond\tcolumn"));

        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("session_id", "session-1"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("text", @"first\r\nsecond\tcolumn"),
                (argument.Name, argument.DisplayValue)));
        Assert.Equal(
            @"first\\r\\nsecond\\tcolumn",
            Assert.Single(
                literalEscapeAction.Proposal.Presentation.Arguments,
                argument => string.Equals(argument.Name, "text", StringComparison.Ordinal)).DisplayValue);
        Assert.NotEqual(
            action.Proposal.ArgumentDigest,
            literalEscapeAction.Proposal.ArgumentDigest);
        Assert.NotEqual(
            ApprovalMaterial(action),
            ApprovalMaterial(literalEscapeAction), StringComparer.Ordinal);
    }

    [Fact]
    public void Send_mouse_binds_every_execution_field_in_canonical_order()
    {
        var action = new AgentTerminalActionComposer().Prepare(
            Envelope(),
            TerminalContext(),
            SendMouse(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                5,
                7,
                TerminalKeyModifiers.Shift | TerminalKeyModifiers.Control));

        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("session_id", "session-1"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("button", "Left"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("kind", "Down"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("column", "5"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("row", "7"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("modifiers", "Shift, Control"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("expected_content_revision", "0"),
                (argument.Name, argument.DisplayValue)));
    }

    [Fact]
    public void Every_send_mouse_execution_field_changes_approval_and_digest()
    {
        var composer = new AgentTerminalActionComposer();
        var envelope = Envelope();
        var baseline = composer.Prepare(
            envelope,
            TerminalContext(),
            SendMouse(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                5,
                7,
                TerminalKeyModifiers.None));
        var alternateSession = new SessionId("session-2");
        var alternateContext = TerminalContext(sessionId: alternateSession);
        var variants = new (AgentTerminalAction Action, AgentContextSnapshot FreshContext)[]
        {
            (
                composer.Prepare(
                    envelope,
                    alternateContext,
                    SendMouse(
                        alternateSession,
                        TerminalMouseButton.Left,
                        TerminalMouseEventKind.Down,
                        5,
                        7,
                        TerminalKeyModifiers.None)),
                alternateContext),
            (
                composer.Prepare(
                    envelope,
                    TerminalContext(),
                    SendMouse(
                        TerminalMouseButton.Right,
                        TerminalMouseEventKind.Down,
                        5,
                        7,
                        TerminalKeyModifiers.None)),
                TerminalContext()),
            (
                composer.Prepare(
                    envelope,
                    TerminalContext(),
                    SendMouse(
                        TerminalMouseButton.Left,
                        TerminalMouseEventKind.Up,
                        5,
                        7,
                        TerminalKeyModifiers.None)),
                TerminalContext()),
            (
                composer.Prepare(
                    envelope,
                    TerminalContext(),
                    SendMouse(
                        TerminalMouseButton.Left,
                        TerminalMouseEventKind.Down,
                        6,
                        7,
                        TerminalKeyModifiers.None)),
                TerminalContext()),
            (
                composer.Prepare(
                    envelope,
                    TerminalContext(),
                    SendMouse(
                        TerminalMouseButton.Left,
                        TerminalMouseEventKind.Down,
                        5,
                        8,
                        TerminalKeyModifiers.None)),
                TerminalContext()),
            (
                composer.Prepare(
                    envelope,
                    TerminalContext(),
                    SendMouse(
                        TerminalMouseButton.Left,
                        TerminalMouseEventKind.Down,
                        5,
                        7,
                        TerminalKeyModifiers.Control)),
                TerminalContext()),
        };

        Assert.All(
            variants,
            variant =>
            {
                Assert.NotEqual(
                    baseline.Proposal.ArgumentDigest,
                    variant.Action.Proposal.ArgumentDigest);
                Assert.NotEqual(
                    ApprovalMaterial(baseline),
                    ApprovalMaterial(variant.Action), StringComparer.Ordinal);

                var mismatchedAction = new AgentTerminalAction(
                    variant.Action.Request,
                    baseline.Proposal);
                Assert.Throws<InvalidOperationException>(() =>
                    composer.BindForExecution(
                        mismatchedAction,
                        variant.FreshContext));
            });
    }

    [Fact]
    public void Send_mouse_requires_mouse_capability_and_a_fresh_exact_target()
    {
        var composer = new AgentTerminalActionComposer();
        var request = SendMouse(
            TerminalMouseButton.Left,
            TerminalMouseEventKind.Down,
            5,
            7,
            TerminalKeyModifiers.None);
        var capabilitiesWithoutMouse = AllTerminalCapabilities()
            .Where(capability => !string.Equals(capability, SessionCapabilities.TerminalMouse, StringComparison.Ordinal))
            .ToArray();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                TerminalContext(capabilities: capabilitiesWithoutMouse),
                request));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                TerminalContext(
                    new AgentTarget.Panel(
                        Window(),
                        Workspace(),
                        Tab(),
                        new PanelInstanceId("panel-other"))),
                request));

        var action = composer.Prepare(
            Envelope(),
            TerminalContext(),
            request);
        var binding = composer.BindForExecution(
            action,
            TerminalContext(graphRevision: 12, sessionRevision: 18));
        Assert.Equal(BuiltInAgentTools.TerminalSendMouse, binding.ToolName);
        Assert.NotEqual(
            action.Proposal.TargetFingerprint,
            binding.TargetFingerprint);
        Assert.Throws<ArgumentException>(() =>
            composer.BindForExecution(
                action,
                TerminalContext(capabilities: capabilitiesWithoutMouse)));
        Assert.Throws<ArgumentException>(() =>
            composer.BindForExecution(
                action,
                TerminalContext(
                    new AgentTarget.Panel(
                        Window(),
                        Workspace(),
                        Tab(),
                        new PanelInstanceId("panel-other")))));
    }

    [Fact]
    public void Send_mouse_uses_bounded_typed_input()
    {
        var composer = new AgentTerminalActionComposer();
        var maximum = composer.Prepare(
            Envelope(),
            TerminalContext(),
            SendMouse(
                TerminalMouseButton.WheelDown,
                TerminalMouseEventKind.WheelDown,
                1_000_000,
                1_000_000,
                TerminalKeyModifiers.Meta));

        Assert.Contains(
            maximum.Proposal.Presentation.Arguments,
            argument => argument is
            {
                Name: "column",
                DisplayValue: "1000000",
            });
        Assert.Contains(
            maximum.Proposal.Presentation.Arguments,
            argument => argument is
            {
                Name: "row",
                DisplayValue: "1000000",
            });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalMouseInput(
                (TerminalMouseButton)999,
                TerminalMouseEventKind.Down,
                0,
                0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalMouseInput(
                TerminalMouseButton.Left,
                (TerminalMouseEventKind)999,
                0,
                0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalMouseInput(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                -1,
                0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalMouseInput(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                1_000_001,
                0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalMouseInput(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                0,
                -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalMouseInput(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                0,
                1_000_001));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalMouseInput(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                0,
                0,
                (TerminalKeyModifiers)(1 << 8)));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                TerminalContext(),
                new AgentTerminalRequest.SendMouse(
                    Session(),
                    null!,
                    ExpectedContentRevision: 0)));
    }

    [Fact]
    public void Approval_uses_explicit_local_fallbacks_when_legacy_context_has_no_metadata()
    {
        var action = new AgentTerminalActionComposer().Prepare(
            Envelope(),
            TerminalContext(),
            SendText("uptime"));

        Assert.Equal("Local terminal", action.Proposal.Presentation.Host);
        Assert.Equal("<not reported>", action.Proposal.Presentation.WorkingDirectory);
    }

    [Fact]
    public void Semantic_wait_binds_condition_baseline_and_timeout_in_canonical_order()
    {
        var action = new AgentTerminalActionComposer().Prepare(
            Envelope(),
            TerminalContext(),
            WaitForCommandFinished(7, TimeSpan.FromMilliseconds(1250)));

        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("session_id", "session-1"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("condition", "command_finished"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("after_shell_event_sequence", "7"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("timeout", "00:00:01.2500000"),
                (argument.Name, argument.DisplayValue)));
    }

    [Fact]
    public void Canonical_numbers_are_culture_invariant()
    {
        var context = TerminalContext();
        var envelope = Envelope();
        var request = new AgentTerminalRequest.Resize(
            new TerminalResizeRequest(
                Session(),
                new AttachmentId("attachment-1"),
                new ViewportDescriptor(1234.5, 678.25, 1.5, 80, 24)));
        var composer = new AgentTerminalActionComposer();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = composer.Prepare(envelope, context, request);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-EG");
            var arabic = composer.Prepare(envelope, context, request);

            Assert.Equal(french.Proposal.ArgumentDigest, arabic.Proposal.ArgumentDigest);
            Assert.Equal(
                "1234.5",
                Assert.Single(
                    french.Proposal.Presentation.Arguments,
                    argument => string.Equals(argument.Name, "logical_width", StringComparison.Ordinal)).DisplayValue);
            Assert.Equal(
                french.Proposal.Presentation.Arguments,
                arabic.Proposal.Presentation.Arguments);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData(80, null)]
    [InlineData(1, 24)]
    [InlineData(1_001, 24)]
    [InlineData(80, 0)]
    [InlineData(80, 1_001)]
    public void Resize_requires_an_exact_supported_cell_grid(
        int? columns,
        int? rows)
    {
        var request = new AgentTerminalRequest.Resize(
            new TerminalResizeRequest(
                Session(),
                new AttachmentId("attachment-1"),
                new ViewportDescriptor(800, 600, 2, columns, rows)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentTerminalActionComposer().Prepare(
                Envelope(),
                TerminalContext(),
                request));
    }

    [Fact]
    public void Resize_accepts_the_cross_platform_minimum_cell_grid()
    {
        var action = new AgentTerminalActionComposer().Prepare(
            Envelope(),
            TerminalContext(),
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    Session(),
                    new AttachmentId("attachment-1"),
                    new ViewportDescriptor(800, 600, 2, 2, 1))));

        Assert.Contains(
            action.Proposal.Presentation.Arguments,
            argument => argument is
            {
                Name: "columns",
                DisplayValue: "2",
            });
        Assert.Contains(
            action.Proposal.Presentation.Arguments,
            argument => argument is
            {
                Name: "rows",
                DisplayValue: "1",
            });
    }

    [Fact]
    public void Approval_escapes_control_format_and_backslash_characters_without_truncation()
    {
        const string text = "a\tb\nc\\d\u001b\u202e\U000e0001";

        var action = new AgentTerminalActionComposer().Prepare(
            Envelope(),
            TerminalContext(),
            SendText(text));

        var argument = Assert.Single(
            action.Proposal.Presentation.Arguments,
            candidate => string.Equals(candidate.Name, "text", StringComparison.Ordinal));
        Assert.Equal(@"a\tb\nc\\d\u001B\u202E\U000E0001", argument.DisplayValue);
        Assert.Contains("panel-1", action.Proposal.Presentation.TargetTitle);
        Assert.Contains("session-1", action.Proposal.Presentation.TargetTitle);
    }

    [Theory]
    [InlineData("export PASSWORD=hunter2")]
    [InlineData("curl -H 'Authorization: Bearer abc123' https://example.test")]
    [InlineData("curl https://user:hunter2@example.test")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    [InlineData("{\"password\":\"hunter2\"}")]
    [InlineData("{ \"token\" : \"hunter2\" }")]
    [InlineData("password = hunter2")]
    [InlineData("TOKEN = 'hunter2'")]
    [InlineData("--token=hunter2")]
    [InlineData("--password hunter2")]
    [InlineData("ghp_abcdefghijklmnop")]
    [InlineData("token = \"\" + \"hunter2\"")]
    [InlineData("password = null ?? \"hunter2\"")]
    [InlineData("jq 'select(.token == \"hunter2\")' file.json")]
    [InlineData("[ \"$token\" = \"hunter2\" ]")]
    public void Obvious_literal_secret_material_is_rejected(string text)
    {
        var composer = new AgentTerminalActionComposer();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                TerminalContext(),
                SendText(text)));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                TerminalContext(),
                Paste(text)));
    }

    [Theory]
    [InlineData("jq 'select(.token == null)' file.json")]
    [InlineData("jq 'select(.token != null)' file.json")]
    [InlineData("[ \"$token\" = \"\" ]")]
    [InlineData("{\"token\":null}")]
    [InlineData("{\"token\":true}")]
    [InlineData("{ \"password\" : false }")]
    [InlineData("password = \"\"")]
    [InlineData("TOKEN=")]
    [InlineData("--token=\"\"")]
    [InlineData("$token = $null")]
    [InlineData("echo tokenizer = value")]
    public void Comparisons_and_non_secret_assignments_are_accepted(string text)
    {
        var composer = new AgentTerminalActionComposer();

        var sendText = composer.Prepare(
            Envelope(),
            TerminalContext(),
            SendText(text));
        var paste = composer.Prepare(
            Envelope(),
            TerminalContext(),
            Paste(text));

        Assert.Equal(
            text,
            Assert.Single(
                sendText.Proposal.Presentation.Arguments,
                argument => string.Equals(argument.Name, "text", StringComparison.Ordinal)).DisplayValue);
        Assert.Equal(
            text,
            Assert.Single(
                paste.Proposal.Presentation.Arguments,
                argument => string.Equals(argument.Name, "text", StringComparison.Ordinal)).DisplayValue);
    }

    [Fact]
    public void Paste_material_is_bounded_by_strict_utf8_bytes()
    {
        var composer = new AgentTerminalActionComposer();
        var context = TerminalContext();

        var maximum = composer.Prepare(
            Envelope(),
            context,
            Paste(new string('\u00e9', 1_024)));

        Assert.Equal(
            new string('\u00e9', 1_024),
            Assert.Single(
                maximum.Proposal.Presentation.Arguments,
                argument => string.Equals(argument.Name, "text", StringComparison.Ordinal)).DisplayValue);
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                context,
                Paste(new string('\u00e9', 1_025))));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                context,
                Paste("\ud800")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("before\0after")]
    [InlineData("before\u001Bafter")]
    public void Paste_rejects_empty_or_unsupported_control_text(string text)
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentTerminalActionComposer().Prepare(
                Envelope(),
                TerminalContext(),
                Paste(text)));
    }

    [Fact]
    public void Material_that_cannot_fit_exactly_in_approval_is_rejected()
    {
        var composer = new AgentTerminalActionComposer();
        var context = TerminalContext();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                context,
                SendText(new string('x', 2_049))));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                context,
                SendText(new string('\\', 1_100))));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                context,
                SendText("\ud800")));
    }

    [Fact]
    public void Proposal_binds_the_exact_panel_or_connection_session_target()
    {
        var panelContext = TerminalContext();
        var sessionContext = TerminalContext(
            new AgentTarget.ConnectionSession(Session()));
        var composer = new AgentTerminalActionComposer();

        var panelAction = composer.Prepare(
            Envelope(),
            panelContext,
            new AgentTerminalRequest.ReadScreen(Session()));
        var sessionAction = composer.Prepare(
            Envelope(),
            sessionContext,
            new AgentTerminalRequest.ReadScreen(Session()));

        Assert.IsType<AgentTarget.Panel>(panelAction.Proposal.Target);
        Assert.IsType<AgentTarget.ConnectionSession>(sessionAction.Proposal.Target);
        Assert.NotEqual(
            panelAction.Proposal.TargetIdentity,
            sessionAction.Proposal.TargetIdentity);
    }

    [Fact]
    public void Broader_context_selects_one_matching_session_and_narrows_to_its_exact_panel()
    {
        var context = MultipleTerminalContext();

        var action = new AgentTerminalActionComposer().Prepare(
            Envelope(),
            context,
            new AgentTerminalRequest.ReadScreen(Session()));

        var target = Assert.IsType<AgentTarget.Panel>(action.Proposal.Target);
        Assert.Equal(Panel(), target.PanelId);
        Assert.Equal(Window(), target.WindowId);
        Assert.Equal(Workspace(), target.WorkspaceId);
        Assert.Equal(Tab(), target.TabId);
        Assert.NotEqual(AgentTargetIdentity.Create(context.Target), action.Proposal.TargetIdentity);
        Assert.DoesNotContain(
            "Staging",
            action.Proposal.Presentation.TargetTitle,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Execution_binding_recomputes_only_fresh_exact_context_evidence()
    {
        var composer = new AgentTerminalActionComposer();
        var action = composer.Prepare(
            Envelope(),
            TerminalContext(graphRevision: 11, sessionRevision: 17),
            SendText("echo safe"));

        var binding = composer.BindForExecution(
            action,
            TerminalContext(graphRevision: 12, sessionRevision: 18));

        Assert.Equal(action.Proposal.Id, binding.ActionId);
        Assert.Equal(action.Proposal.RunId, binding.RunId);
        Assert.Equal(action.Proposal.Actor.Id, binding.ActorId);
        Assert.Equal(action.Proposal.ToolName, binding.ToolName);
        Assert.Equal(action.Proposal.Target, binding.Target);
        Assert.Equal(action.Proposal.TargetIdentity, binding.TargetIdentity);
        Assert.Equal(action.Proposal.ArgumentDigest, binding.ArgumentDigest);
        Assert.Equal(action.Proposal.PolicyGeneration, binding.PolicyGeneration);
        Assert.NotEqual(action.Proposal.TargetFingerprint, binding.TargetFingerprint);
        Assert.Throws<ArgumentException>(() =>
            composer.BindForExecution(action, MultipleTerminalContext()));
        Assert.Throws<ArgumentException>(() =>
            composer.BindForExecution(
                action,
                TerminalContext(
                    new AgentTarget.Panel(
                        Window(),
                        Workspace(),
                        Tab(),
                        new PanelInstanceId("panel-other")))));
    }

    [Fact]
    public void Invalid_or_ambiguous_terminal_contexts_fail_closed()
    {
        var composer = new AgentTerminalActionComposer();
        var envelope = Envelope();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                ContextWithoutSession(),
                new AgentTerminalRequest.ReadScreen(Session())));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                DuplicateSessionContext(),
                new AgentTerminalRequest.ReadScreen(Session())));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                TerminalContext(kind: PanelKind.FileViewer),
                new AgentTerminalRequest.ReadScreen(Session())));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                TerminalContext(capabilities: [SessionCapabilities.TerminalReadScreen]),
                SendText("echo safe")));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                TerminalContext(),
                new AgentTerminalRequest.ReadScreen(new SessionId("session-other"))));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                TerminalContext(
                    new AgentTarget.Panel(
                        Window(),
                        Workspace(),
                        Tab(),
                        new PanelInstanceId("panel-other"))),
                new AgentTerminalRequest.ReadScreen(Session())));
    }

    private static AgentActionEnvelope Envelope() =>
        new(
            new AgentActionId("action-1"),
            new AgentRunId("run-1"),
            new ActorDescriptor(
                new ActorId("agent-1"),
                ActorKind.Agent,
                "Agent"),
            policyGeneration: 7,
            Now,
            Now.AddMinutes(1));

    private static string ApprovalMaterial(AgentTerminalAction action) =>
        string.Join(
            "\n",
            action.Proposal.Presentation.Arguments.Select(
                argument => $"{argument.Name}:{argument.DisplayValue}"));

    private static AgentTerminalRequest Request(TerminalOperation operation) =>
        operation switch
        {
            TerminalOperation.ReadScreen => new AgentTerminalRequest.ReadScreen(Session()),
            TerminalOperation.ReadScreenDiff =>
                new AgentTerminalRequest.ReadScreenDiff(
                    Session(),
                    new TerminalScreenDiffInput(7, MaximumRowCount: 24)),
            TerminalOperation.FindOnScreen =>
                new AgentTerminalRequest.FindOnScreen(
                    Session(),
                    new TerminalScreenFindInput("ready", MaximumMatchCount: 4)),
            TerminalOperation.FindRenderedHistory =>
                new AgentTerminalRequest.FindRenderedHistory(
                    Session(),
                    new TerminalRenderedHistoryFindInput(
                        "ready",
                        TerminalScrollbackFindDirection.Forward,
                        MaximumMatchCount: 4)),
            TerminalOperation.JumpToRenderedHistory =>
                new AgentTerminalRequest.JumpToRenderedHistory(
                    Session(),
                    new TerminalRenderedHistoryRowAnchor(7, 3)),
            TerminalOperation.ReadScrollback =>
                new AgentTerminalRequest.ReadScrollback(
                    Session(),
                    new TerminalScrollbackReadInput(
                        TerminalScrollbackReadOrigin.Bottom,
                        TerminalScrollbackReadInput.SmallRead)),
            TerminalOperation.FindScrollback =>
                new AgentTerminalRequest.FindScrollback(
                    Session(),
                    new TerminalScrollbackFindInput(
                        "ready",
                        TerminalScrollbackFindDirection.Forward,
                        MaximumMatchCount: 4)),
            TerminalOperation.ScrollViewport =>
                new AgentTerminalRequest.ScrollViewport(
                    Session(),
                    new TerminalViewportScrollInput(
                        TerminalViewportScrollDirection.Up,
                        TerminalViewportScrollUnit.Page,
                        Amount: 1)),
            TerminalOperation.SendText => SendText("echo safe"),
            TerminalOperation.Paste => Paste("first\r\nsecond"),
            TerminalOperation.SubmitText => SubmitText("echo submitted"),
            TerminalOperation.SendKey => SendKey(
                TerminalKey.Enter,
                TerminalKeyModifiers.Control),
            TerminalOperation.SendChord => SendChord(
                'd',
                TerminalCharacterChordModifier.Control),
            TerminalOperation.SendMouse => SendMouse(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                5,
                7,
                TerminalKeyModifiers.Control),
            TerminalOperation.WaitForText => WaitForText(
                "ready",
                TimeSpan.FromSeconds(2)),
            TerminalOperation.WaitForDelay =>
                new AgentTerminalRequest.WaitForDelay(
                    new TerminalWaitForDelayRequest(
                        Session(),
                        new TerminalWaitForDelayInput(
                            TimeSpan.FromHours(1)))),
            TerminalOperation.WaitForChange => WaitForChange(
                5,
                TimeSpan.FromSeconds(2)),
            TerminalOperation.WaitForPromptReady => WaitForPromptReady(
                5,
                TimeSpan.FromSeconds(2)),
            TerminalOperation.WaitForCommandFinished => WaitForCommandFinished(
                5,
                TimeSpan.FromSeconds(2)),
            TerminalOperation.WaitForStable => WaitForStable(
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(2)),
            TerminalOperation.Interrupt => Interrupt(),
            TerminalOperation.Resize => Resize(
                new AttachmentId("attachment-1"),
                columns: 80),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static AgentTerminalRequest SendText(string text) =>
        new AgentTerminalRequest.SendText(
            Session(),
            text);

    private static AgentTerminalRequest Paste(string text) =>
        new AgentTerminalRequest.Paste(
            Session(),
            text);

    private static AgentTerminalRequest SubmitText(string text) =>
        new AgentTerminalRequest.SubmitText(
            Session(),
            text);

    private static AgentTerminalRequest SendKey(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        int repeatCount = 1) =>
        new AgentTerminalRequest.SendKey(
            Session(),
            new TerminalKeyStroke(key, modifiers, repeatCount));

    private static AgentTerminalRequest SendChord(
        char character,
        TerminalCharacterChordModifier modifier) =>
        new AgentTerminalRequest.SendChord(
            Session(),
            new TerminalCharacterChord(character, modifier));

    private static AgentTerminalRequest SendMouse(
        TerminalMouseButton button,
        TerminalMouseEventKind kind,
        int column,
        int row,
        TerminalKeyModifiers modifiers) =>
        SendMouse(Session(), button, kind, column, row, modifiers);

    private static AgentTerminalRequest SendMouse(
        SessionId sessionId,
        TerminalMouseButton button,
        TerminalMouseEventKind kind,
        int column,
        int row,
        TerminalKeyModifiers modifiers) =>
        new AgentTerminalRequest.SendMouse(
            sessionId,
            new TerminalMouseInput(button, kind, column, row, modifiers),
            ExpectedContentRevision: 0);

    private static AgentTerminalRequest WaitForText(
        string text,
        TimeSpan timeout) =>
        new AgentTerminalRequest.WaitForText(
            new TerminalWaitForTextRequest(
                Session(),
                new TerminalWaitForTextInput(text, timeout)));

    private static AgentTerminalRequest WaitForChange(
        long revision,
        TimeSpan timeout) =>
        new AgentTerminalRequest.WaitForChange(
            new TerminalWaitForChangeRequest(
                Session(),
                new TerminalWaitForChangeInput(revision, timeout)));

    private static AgentTerminalRequest WaitForStable(
        TimeSpan stableFor,
        TimeSpan timeout) =>
        new AgentTerminalRequest.WaitForStable(
            new TerminalWaitForStableRequest(
                Session(),
                new TerminalWaitForStableInput(stableFor, timeout)));

    private static AgentTerminalRequest WaitForPromptReady(
        long afterShellEventSequence,
        TimeSpan timeout) =>
        new AgentTerminalRequest.WaitForPromptReady(
            new TerminalWaitForPromptReadyRequest(
                Session(),
                new TerminalWaitForPromptReadyInput(
                    afterShellEventSequence,
                    timeout)));

    private static AgentTerminalRequest WaitForCommandFinished(
        long afterShellEventSequence,
        TimeSpan timeout) =>
        new AgentTerminalRequest.WaitForCommandFinished(
            new TerminalWaitForCommandFinishedRequest(
                Session(),
                new TerminalWaitForCommandFinishedInput(
                    afterShellEventSequence,
                    timeout)));

    private static AgentTerminalRequest Interrupt() =>
        new AgentTerminalRequest.Interrupt(Session());

    private static AgentTerminalRequest Resize(
        AttachmentId attachmentId,
        int columns) =>
        new AgentTerminalRequest.Resize(
            new TerminalResizeRequest(
                Session(),
                attachmentId,
                new ViewportDescriptor(800, 600, 2, columns, 24)));

    private static AgentContextSnapshot TerminalContext(
        AgentTarget? target = null,
        PanelKind kind = PanelKind.Terminal,
        IEnumerable<string>? capabilities = null,
        long graphRevision = 11,
        long sessionRevision = 17,
        TerminalSessionMetadata? terminalMetadata = null,
        SessionId? sessionId = null)
    {
        var resolvedSessionId = sessionId ?? Session();
        var panel = new PanelInstance(Panel(), kind, "Production", resolvedSessionId);
        var tab = new TabInstance(Tab(), "Shells", [panel], panel.Id);
        var workspace = new WorkspaceInstance(Workspace(), "Operations", [tab], tab.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            workspace,
            graphRevision,
            lastSequence: graphRevision);
        var session = Descriptor(
            resolvedSessionId,
            kind,
            Panel(),
            capabilities ?? AllTerminalCapabilities(),
            sessionRevision,
            terminalMetadata);
        var contextPanel = AgentContextPanel.ForGraphPanel(
            graph,
            Tab(),
            Panel(),
            session);
        return new AgentContextSnapshot(
            target ?? ExactPanelTarget(),
            [contextPanel],
            Now);
    }

    private static AgentContextSnapshot ContextWithoutSession()
    {
        var panel = new PanelInstance(Panel(), PanelKind.Terminal, "Production");
        var tab = new TabInstance(Tab(), "Shells", [panel], panel.Id);
        var workspace = new WorkspaceInstance(Workspace(), "Operations", [tab], tab.Id);
        var graph = new WorkspaceGraphSnapshot(Window(), workspace, revision: 11, lastSequence: 11);
        return new AgentContextSnapshot(
            ExactPanelTarget(),
            [AgentContextPanel.ForGraphPanel(graph, Tab(), Panel(), session: null)],
            Now);
    }

    private static AgentContextSnapshot MultipleTerminalContext()
    {
        var secondPanelId = new PanelInstanceId("panel-2");
        var secondSessionId = new SessionId("session-2");
        var firstPanel = new PanelInstance(
            Panel(),
            PanelKind.Terminal,
            "Production",
            Session());
        var secondPanel = new PanelInstance(
            secondPanelId,
            PanelKind.Terminal,
            "Staging",
            secondSessionId);
        var tab = new TabInstance(Tab(), "Shells", [firstPanel, secondPanel], firstPanel.Id);
        var workspace = new WorkspaceInstance(Workspace(), "Operations", [tab], tab.Id);
        var graph = new WorkspaceGraphSnapshot(Window(), workspace, revision: 11, lastSequence: 11);
        return new AgentContextSnapshot(
            new AgentTarget.Workspace(Window(), Workspace()),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    Panel(),
                    Descriptor(
                        Session(),
                        PanelKind.Terminal,
                        Panel(),
                        AllTerminalCapabilities())),
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    secondPanelId,
                    Descriptor(
                        secondSessionId,
                        PanelKind.Terminal,
                        secondPanelId,
                        AllTerminalCapabilities())),
            ],
            Now);
    }

    private static AgentContextSnapshot DuplicateSessionContext()
    {
        var secondPanelId = new PanelInstanceId("panel-2");
        var firstPanel = new PanelInstance(
            Panel(),
            PanelKind.Terminal,
            "Production",
            Session());
        var secondPanel = new PanelInstance(
            secondPanelId,
            PanelKind.Terminal,
            "Staging",
            Session());
        var tab = new TabInstance(Tab(), "Shells", [firstPanel, secondPanel], firstPanel.Id);
        var workspace = new WorkspaceInstance(Workspace(), "Operations", [tab], tab.Id);
        var graph = new WorkspaceGraphSnapshot(Window(), workspace, revision: 11, lastSequence: 11);
        return new AgentContextSnapshot(
            new AgentTarget.Workspace(Window(), Workspace()),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    Panel(),
                    Descriptor(
                        Session(),
                        PanelKind.Terminal,
                        Panel(),
                        AllTerminalCapabilities())),
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    secondPanelId,
                    Descriptor(
                        Session(),
                        PanelKind.Terminal,
                        secondPanelId,
                        AllTerminalCapabilities())),
            ],
            Now);
    }

    private static SessionDescriptor Descriptor(
        SessionId sessionId,
        PanelKind kind,
        PanelInstanceId panelId,
        IEnumerable<string> capabilities,
        long revision = 17,
        TerminalSessionMetadata? terminalMetadata = null) =>
        new(
            sessionId,
            kind,
            SessionLifecycle.Active,
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
            TerminalMetadata: terminalMetadata);

    private static string[] AllTerminalCapabilities() =>
    [
        SessionCapabilities.TerminalReadScreen,
        SessionCapabilities.TerminalScrollback,
        SessionCapabilities.TerminalScrollbackRead,
        SessionCapabilities.TerminalScrollbackFind,
        SessionCapabilities.TerminalRenderedHistory,
        SessionCapabilities.TerminalWrite,
        SessionCapabilities.TerminalPaste,
        SessionCapabilities.TerminalEnter,
        SessionCapabilities.TerminalSendKeys,
        SessionCapabilities.TerminalSendChord,
        SessionCapabilities.TerminalMouse,
        SessionCapabilities.TerminalRevisionBoundMouse,
        SessionCapabilities.TerminalWait,
        SessionCapabilities.TerminalInterrupt,
        SessionCapabilities.TerminalResize,
    ];

    private static AgentTarget.Panel ExactPanelTarget() =>
        new(Window(), Workspace(), Tab(), Panel());

    private static WindowInstanceId Window() => new("window-1");

    private static WorkspaceInstanceId Workspace() => new("workspace-1");

    private static TabInstanceId Tab() => new("tab-1");

    private static PanelInstanceId Panel() => new("panel-1");

    private static SessionId Session() => new("session-1");

    public enum TerminalOperation
    {
        ReadScreen,
        ReadScreenDiff,
        FindOnScreen,
        FindRenderedHistory,
        ReadScrollback,
        FindScrollback,
        JumpToRenderedHistory,
        ScrollViewport,
        SendText,
        Paste,
        SubmitText,
        SendKey,
        SendChord,
        SendMouse,
        WaitForDelay,
        WaitForText,
        WaitForChange,
        WaitForPromptReady,
        WaitForCommandFinished,
        WaitForStable,
        Interrupt,
        Resize,
    }
}

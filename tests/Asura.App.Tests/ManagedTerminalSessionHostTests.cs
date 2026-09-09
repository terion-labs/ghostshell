using System.Reflection;
using Asura.App.Controls;
using Asura.Application;
using Asura.Core;
using Avalonia;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class ManagedTerminalSessionHostTests
{
    [Theory]
    [InlineData(InitializationFailureStage.Renderer)]
    [InlineData(InitializationFailureStage.Lease)]
    [InlineData(InitializationFailureStage.Snapshot)]
    [InlineData(InitializationFailureStage.Screen)]
    public async Task Failed_initialization_detaches_the_pending_attachment(
        InitializationFailureStage failureStage)
    {
        var client = new LifecycleClient(failureStage);
        var host = CreateHost(client);

        await host.InitializeForTestingAsync();

        Assert.Equal(1, client.DetachCalls);
        Assert.False(host.Surface.IsInputReady);
    }

    [Fact]
    public async Task Resize_requests_are_serialized_and_use_the_latest_arranged_viewport()
    {
        var client = new LifecycleClient();
        var host = CreateHost(client);
        Arrange(host, new Size(640, 260));
        await host.InitializeForTestingAsync();
        Arrange(host, new Size(720, 320));
        Assert.True(host.Bounds.Width > 0);
        Assert.NotEqual(host.LastViewport, host.Surface.CurrentViewport());
        var firstViewport = host.Surface.CurrentViewport();

        var firstResize = host.ResizeForTestingAsync();
        await client.FirstResizeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Arrange(host, new Size(840, 420));
        var intermediateResize = host.ResizeForTestingAsync();
        Arrange(host, new Size(980, 520));
        var latestViewport = host.Surface.CurrentViewport();
        Assert.NotEqual(firstViewport, latestViewport);
        var latestResize = host.ResizeForTestingAsync();

        Assert.False(intermediateResize.IsCompleted);
        Assert.False(latestResize.IsCompleted);
        client.ReleaseFirstResize.TrySetResult();
        await Task.WhenAll(firstResize, intermediateResize, latestResize);

        Assert.Equal(1, client.MaximumConcurrentResizes);
        Assert.Equal(firstViewport, client.ResizeRequests[0].Viewport);
        Assert.True(
            client.ResizeRequests.Count == 2,
            $"Expected two resizes; last={host.LastViewport}; latest={latestViewport}; first={firstViewport}.");
        Assert.Equal(latestViewport, client.ResizeRequests[1].Viewport);
        Assert.Equal(latestViewport, host.LastViewport);
    }

    /// <summary>
    /// A panel attaches on its way into the visual tree and is arranged after,
    /// so the viewport it attaches with is the surface's unarranged fallback —
    /// eighty by twenty-four. Nothing reports the real size afterwards: the
    /// layout pass raises its change while the attachment is still being built,
    /// and once the layout settles it stays settled. A workspace switched away
    /// from and back came home eighty columns wide.
    /// </summary>
    [Fact]
    public async Task A_terminal_attached_before_its_first_layout_is_resized_to_what_it_was_given()
    {
        var client = new LifecycleClient();
        // Nothing here is about serializing resizes, so the double's first-resize
        // latch is opened up front rather than held.
        client.ReleaseFirstResize.TrySetResult();
        var host = CreateHost(client);
        client.InterruptFirstScreenReadWith(() => Arrange(host, new Size(980, 520)));

        await host.InitializeForTestingAsync();

        var arranged = host.Surface.CurrentViewport();
        Assert.True(
            arranged.Columns > 80,
            $"The arranged surface should be wider than the fallback; it measured {arranged}.");
        Assert.Equal(arranged, host.LastViewport);
        Assert.Equal(arranged, client.ResizeRequests[^1].Viewport);
    }

    [Fact]
    public void SessionRequestAppliesItsImmutableKeymapSnapshotToTheSurface()
    {
        var keymap = TerminalKeymapSnapshot.FromProfile(BuiltInKeymaps.LinuxTerminal);
        var client = new LifecycleClient(keymap: keymap);
        var host = CreateHost(client);

        Assert.Same(keymap, host.Surface.Keymap);
    }

    [Fact]
    public async Task Buffer_commands_use_the_session_lease_and_refresh_the_surface()
    {
        var client = new LifecycleClient();
        var host = CreateHost(client);
        await host.InitializeForTestingAsync();
        var sink = (IManagedTerminalInputSink)host;

        Assert.True(await sink.ClearScrollbackAsync(default));
        var found = await sink.FindAsync(new TerminalFindInput("needle", 1), default);

        Assert.Equal(1, client.ClearScrollbackCalls);
        Assert.Equal(new TerminalFindInput("needle", 1), client.LastFindInput);
        Assert.Equal(1, found?.SelectedMatchIndex);
        Assert.True(client.ScreenReadCalls >= 3);
    }

    [Fact]
    public async Task Rich_render_frame_is_the_managed_surface_drawing_source()
    {
        var renderFrame = new TerminalRenderFrame(
            Revision: 1,
            Rows: 1,
            Columns: 1,
            [new TerminalRenderRow(
                0,
                [new TerminalRenderCell(
                    "x",
                    TerminalRenderCellWidth.Narrow,
                    TerminalCellColor.Default,
                    TerminalCellColor.Default)])],
            new TerminalRenderCursor(
                TerminalCursorVisualStyle.Bar,
                IsVisible: true,
                IsBlinking: false,
                IsPasswordInput: false,
                ViewportRow: 0,
                ViewportColumn: 0),
            new TerminalRenderDelta(TerminalRenderDamageKind.Full));
        var client = new LifecycleClient(renderFrame: renderFrame);
        var host = CreateHost(client);

        await host.InitializeForTestingAsync();

        Assert.Same(renderFrame, host.Surface.RenderFrame);
        Assert.True(client.RenderReadCalls >= 1);
        Assert.True(client.ScreenReadCalls >= 1);
    }

    [Fact]
    public async Task Every_human_pty_input_reacquires_the_exact_attachment_lease()
    {
        var client = new LifecycleClient();
        var host = CreateHost(client);
        await host.InitializeForTestingAsync();
        client.GiveInputLeaseToAgent();
        var sink = (IManagedTerminalInputSink)host;

        await sink.SendTextAsync("text", default);
        await sink.SendKeyAsync(
            new TerminalKeyStroke(TerminalKey.Enter),
            default);
        await sink.SendPhysicalKeyAsync(
            new TerminalPhysicalKeyEvent(
                TerminalPhysicalKey.A,
                "A",
                "a",
                TerminalKeyModifiers.None,
                TerminalKeyModifiers.None,
                TerminalKeyAction.Press,
                'a'),
            default);
        await sink.SendMouseAsync(
            new TerminalMouseInput(
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down,
                3,
                4),
            default);
        _ = await sink.PasteAsync(
            new TerminalPasteInput("paste"),
            default);

        Assert.Equal(6, client.InputLeaseAcquireCalls);
        Assert.Equal(1, client.AgentLeasePreemptions);
        Assert.Equal(
            ["text", "key", "physical-key", "mouse", "paste"],
            client.PhysicalInputKinds);
        Assert.Equal(
            client.HumanLeaseIds.Skip(1),
            client.PhysicalInputLeaseIds);
    }

    [Fact]
    public async Task Human_lease_acquisition_and_dispatch_are_serialized()
    {
        var client = new LifecycleClient();
        var host = CreateHost(client);
        await host.InitializeForTestingAsync();
        var sink = (IManagedTerminalInputSink)host;
        client.BlockNextPhysicalInput();

        var first = sink.SendTextAsync("first", default).AsTask();
        await client.PhysicalInputStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = sink.SendKeyAsync(
            new TerminalKeyStroke(TerminalKey.Enter),
            default).AsTask();

        Assert.False(second.IsCompleted);
        Assert.Equal(2, client.InputLeaseAcquireCalls);
        client.ReleasePhysicalInput.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(3, client.InputLeaseAcquireCalls);
        Assert.Equal(1, client.MaximumConcurrentPhysicalInputs);
        Assert.Equal(["text", "key"], client.PhysicalInputKinds);
    }

    [Fact]
    public async Task Passive_renderer_operations_do_not_reacquire_input()
    {
        var client = new LifecycleClient();
        var host = CreateHost(client);
        await host.InitializeForTestingAsync();
        var sink = (IManagedTerminalInputSink)host;
        var baseline = client.InputLeaseAcquireCalls;

        _ = await host.ReadScreenAsync();
        _ = await sink.FindAsync(new TerminalFindInput("needle"), default);
        _ = await sink.ReadSelectionAsync(default);
        await sink.UpdateSelectionAsync(
            new TerminalSelectionInput(TerminalSelectionPhase.Start, 1, 1),
            default);
        await sink.ScrollViewportAsync(
            new TerminalViewportScrollInput(-1),
            default);

        Assert.Equal(baseline, client.InputLeaseAcquireCalls);
    }

    [Fact]
    public async Task Failed_physical_input_releases_the_serialization_gate()
    {
        var client = new LifecycleClient();
        var host = CreateHost(client);
        await host.InitializeForTestingAsync();
        client.FailNextPhysicalInput();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.SendTextAsync("fails").AsTask());
        await host.SendTextAsync("recovers");

        Assert.Equal(3, client.InputLeaseAcquireCalls);
        Assert.Contains("text:recovers", client.TerminalInputs, StringComparer.Ordinal);
        Assert.Equal(1, client.MaximumConcurrentPhysicalInputs);
    }

    [Fact]
    public async Task Stopping_the_attachment_cancels_input_and_allows_clean_reinitialization()
    {
        var client = new LifecycleClient();
        var host = CreateHost(client);
        await host.InitializeForTestingAsync();
        client.BlockNextPhysicalInput();

        var running = host.SendTextAsync("blocked").AsTask();
        await client.PhysicalInputStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        host.StopForTesting();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running);
        await host.InitializeForTestingAsync();
        await host.SendTextAsync("after restart");

        Assert.Equal(1, client.DetachCalls);
        Assert.Contains("text:after restart", client.TerminalInputs, StringComparer.Ordinal);
        Assert.Equal(1, client.MaximumConcurrentPhysicalInputs);
    }

    [Fact]
    public async Task Polling_a_session_closed_by_tab_teardown_exits_without_an_engine_error()
    {
        var client = new LifecycleClient();
        var host = CreateHost(client);
        await host.InitializeForTestingAsync();
        SessionSnapshot? observedSnapshot = null;
        host.SessionSnapshotChanged += (_, args) => observedSnapshot = args.Snapshot;
        client.CloseSessionForPolling();

        await host.PollForTestingAsync();

        Assert.Equal(SessionLifecycle.Closed, observedSnapshot?.Descriptor.Lifecycle);
        Assert.Null(host.InitializationError);
        Assert.False(host.Surface.IsInputReady);
    }

    [Fact]
    public async Task ApplicationPrefixReplayUsesTypedInputAndBypassesTheManagedKeymap()
    {
        var client = new LifecycleClient();
        var presentation = new TerminalPresentationHost
        {
            SessionClient = client,
            SessionRequest = client.Request,
            ClientId = client.ClientId,
        };
        var managed = Assert.IsType<ManagedTerminalSessionHost>(presentation.Presentation);
        await managed.InitializeForTestingAsync();

        var replayed = await presentation.ReplayApplicationKeyStrokesAsync(
        [
            new KeyStroke("B", KeyModifiers.Control),
            new KeyStroke("ARROWLEFT"),
            new KeyStroke("Q"),
        ]);

        Assert.True(replayed);
        Assert.Equal(
        [
            "text:\u0002",
            "key:Left:None",
            "text:q",
        ],
            client.TerminalInputs);
    }

    [Fact]
    public void PresentationPassesTheRuntimeOwnedDispatchStateToTheManagedHost()
    {
        var panelId = PanelInstanceId.New();
        var state = new TerminalStartupCommandDispatchState(
            panelId,
            ["deploy"],
            OperationContext.ForHuman(
                ClientId.New(),
                idempotencyKey: IdempotencyKey.New()),
            failurePolicy:
                StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        var presentation = new TerminalPresentationHost
        {
            StartupCommandDispatchState = state,
        };

        var managed = Assert.IsType<ManagedTerminalSessionHost>(presentation.Presentation);

        Assert.Same(state, managed.StartupCommandDispatchState);
        managed.StopForTesting();
        Assert.Same(state, managed.StartupCommandDispatchState);
    }

    [Fact]
    public void ManagedHostWithoutBoundStateHasNoFallbackDispatchAuthority()
    {
        var client = new LifecycleClient();
        var host = CreateHost(client);

        Assert.Null(host.StartupCommandDispatchState);
        Assert.DoesNotContain(
            typeof(ManagedTerminalSessionHost).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(TerminalStartupCommandDispatchState));
        Assert.Empty(client.PhysicalInputKinds);
    }

    private static ManagedTerminalSessionHost CreateHost(LifecycleClient client)
    {
        var host = new ManagedTerminalSessionHost
        {
            SessionClient = client,
            SessionRequest = client.Request,
            ClientId = client.ClientId,
        };
        return host;
    }

    private static void Arrange(ManagedTerminalSessionHost host, Size size)
    {
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.Surface.Measure(size);
        host.Surface.Arrange(new Rect(size));
    }

    public enum InitializationFailureStage
    {
        Renderer,
        Lease,
        Snapshot,
        Screen,
    }

    private sealed class LifecycleClient : ISessionHostClient
    {
        private readonly InitializationFailureStage? _failureStage;
        private readonly SessionOwner _owner;
        private readonly SessionDescriptor _descriptor;
        private readonly SessionSnapshot _snapshot;
        private readonly TerminalRenderFrame? _renderFrame;
        private readonly object _inputGate = new();
        private readonly List<InputLeaseId> _humanLeaseIds = [];
        private readonly List<InputLeaseId> _physicalInputLeaseIds = [];
        private readonly List<string> _physicalInputKinds = [];
        private InputLeaseId? _currentInputLeaseId;
        private ActorKind? _currentInputHolderKind;
        private int _agentLeasePreemptions;
        private int _activePhysicalInputs;
        private int _maximumConcurrentPhysicalInputs;
        private int _inputLeaseAcquireCalls;
        private int _blockNextPhysicalInput;
        private int _failNextPhysicalInput;
        private int _activeResizes;
        private int _maximumConcurrentResizes;
        private int _resizeCalls;
        private int _closedForPolling;
        private Action? _duringFirstScreenRead;

        public LifecycleClient(
            InitializationFailureStage? failureStage = null,
            TerminalKeymapSnapshot? keymap = null,
            TerminalRenderFrame? renderFrame = null)
        {
            _failureStage = failureStage;
            _renderFrame = renderFrame;
            ClientId = new ClientId("managed-renderer-test-client");
            SessionId = new SessionId("managed-renderer-test-session");
            _owner = new SessionOwner(
                HostMode.Desktop,
                new WindowInstanceId("window"),
                new WorkspaceInstanceId("workspace"),
                new TabInstanceId("tab"),
                new PanelInstanceId("panel"));
            _descriptor = new SessionDescriptor(
                SessionId,
                PanelKind.Terminal,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                _owner,
                CapabilitySet.Empty,
                1,
                HasActiveWork: true,
                "ready");
            _snapshot = new SessionSnapshot(_descriptor, 1, [], null);
            Request = new EnsureTerminalSessionRequest(
                SessionId,
                _owner,
                "test",
                new TerminalLaunchRequest(null, keymap: keymap));
        }

        public ClientId ClientId { get; }

        public SessionId SessionId { get; }

        public EnsureTerminalSessionRequest Request { get; }

        public int DetachCalls { get; private set; }

        public int ClearScrollbackCalls { get; private set; }

        public TerminalFindInput? LastFindInput { get; private set; }

        public int ScreenReadCalls { get; private set; }

        public int RenderReadCalls { get; private set; }

        public int InputLeaseAcquireCalls =>
            Volatile.Read(ref _inputLeaseAcquireCalls);

        public int AgentLeasePreemptions =>
            Volatile.Read(ref _agentLeasePreemptions);

        public int MaximumConcurrentPhysicalInputs =>
            Volatile.Read(ref _maximumConcurrentPhysicalInputs);

        public IReadOnlyList<InputLeaseId> HumanLeaseIds
        {
            get
            {
                lock (_inputGate)
                {
                    return [.. _humanLeaseIds];
                }
            }
        }

        public IReadOnlyList<InputLeaseId> PhysicalInputLeaseIds
        {
            get
            {
                lock (_inputGate)
                {
                    return [.. _physicalInputLeaseIds];
                }
            }
        }

        public IReadOnlyList<string> PhysicalInputKinds
        {
            get
            {
                lock (_inputGate)
                {
                    return [.. _physicalInputKinds];
                }
            }
        }

        public List<TerminalResizeRequest> ResizeRequests { get; } = [];

        public List<string> TerminalInputs { get; } = [];

        public TaskCompletionSource FirstResizeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstResize { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PhysicalInputStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleasePhysicalInput { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaximumConcurrentResizes => Volatile.Read(ref _maximumConcurrentResizes);

        public void GiveInputLeaseToAgent()
        {
            lock (_inputGate)
            {
                _currentInputLeaseId = new InputLeaseId("agent-one-action-lease");
                _currentInputHolderKind = ActorKind.Agent;
            }
        }

        /// <summary>
        /// Runs something inside the first screen read — the step that happens
        /// after the renderer is attached and before the attachment is recorded.
        /// That is the window a layout pass lands in when a backgrounded
        /// workspace comes back to the front, and it stays on this thread
        /// because the surface belongs to it.
        /// </summary>
        public void InterruptFirstScreenReadWith(Action interruption) =>
            _duringFirstScreenRead = interruption;

        public void BlockNextPhysicalInput() =>
            Volatile.Write(ref _blockNextPhysicalInput, 1);

        public void FailNextPhysicalInput() =>
            Volatile.Write(ref _failNextPhysicalInput, 1);

        public void CloseSessionForPolling() =>
            Volatile.Write(ref _closedForPolling, 1);

        public ValueTask<HostResult<SessionSnapshot>> EnsureTerminalSessionAsync(
            EnsureTerminalSessionRequest request,
            OperationContext context,
            CancellationToken cancellationToken) =>
            Success(_snapshot);

        public ValueTask<HostResult<AttachmentResult>> AttachAsync(
            AttachSessionRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            var presence = new AttachmentPresence(
                new AttachmentId("attachment"),
                SessionId,
                ClientId,
                AttachmentKind.Interactive,
                request.Viewport,
                DateTimeOffset.UnixEpoch);
            return Success(new AttachmentResult(
                presence,
                _snapshot with { Attachments = [presence] },
                new CapabilityNegotiation(
                    CapabilitySet.Empty,
                    CapabilitySet.Empty,
                    CapabilitySet.Empty,
                    CapabilitySet.Empty,
                    CapabilitySet.Empty),
                1));
        }

        public ValueTask<HostResult<Unit>> AttachTerminalRendererAsync(
            AttachTerminalRendererRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            ThrowIf(InitializationFailureStage.Renderer);
            return Success(Unit.Value);
        }

        public ValueTask<HostResult<InputLeaseDecision>> AcquireInputLeaseAsync(
            AcquireInputLeaseRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            ThrowIf(InitializationFailureStage.Lease);
            if (request.SessionId != SessionId
                || request.AttachmentId != new AttachmentId("attachment")
                || context.Actor.ClientId != ClientId)
            {
                return ValueTask.FromResult(
                    HostResult<InputLeaseDecision>.Fail(
                        HostError.Create(
                            HostErrorCode.InvalidRequest,
                            "The managed input lease binding was not exact."),
                        1));
            }

            var call = Interlocked.Increment(ref _inputLeaseAcquireCalls);
            var leaseId = new InputLeaseId($"human-lease-{call}");
            bool preemptedAgent;
            lock (_inputGate)
            {
                preemptedAgent =
                    _currentInputHolderKind == ActorKind.Agent;
                _currentInputLeaseId = leaseId;
                _currentInputHolderKind = ActorKind.Human;
                _humanLeaseIds.Add(leaseId);
            }

            if (preemptedAgent)
            {
                Interlocked.Increment(ref _agentLeasePreemptions);
            }

            var lease = new InputLease(
                leaseId,
                SessionId,
                context.Actor,
                new AttachmentId("attachment"),
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddHours(1),
                1);
            return Success(new InputLeaseDecision(
                true,
                lease,
                "granted",
                preemptedAgent));
        }

        public ValueTask<HostResult<SessionSnapshot>> GetSnapshotAsync(
            SessionId sessionId,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            ThrowIf(InitializationFailureStage.Snapshot);
            return Volatile.Read(ref _closedForPolling) == 0
                ? Success(_snapshot)
                : Success(_snapshot with
                {
                    Descriptor = _descriptor with
                    {
                        Lifecycle = SessionLifecycle.Closed,
                        HasActiveWork = false,
                        StatusDetail = "closed",
                    },
                });
        }

        public ValueTask<HostResult<TerminalScreenSnapshot>> ReadTerminalScreenAsync(
            SessionId sessionId,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            ThrowIf(InitializationFailureStage.Screen);
            ScreenReadCalls++;
            if (Volatile.Read(ref _closedForPolling) != 0)
            {
                return ValueTask.FromResult(HostResult<TerminalScreenSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.EngineFailed,
                        "Cannot access a disposed terminal engine."),
                    2));
            }

            var interruption = _duringFirstScreenRead;
            _duringFirstScreenRead = null;
            interruption?.Invoke();
            return Success(new TerminalScreenSnapshot(
                string.Empty,
                0,
                0,
                24,
                80,
                false,
                null,
                DateTimeOffset.UnixEpoch));
        }

        public ValueTask<HostResult<TerminalRenderFrame>> ReadTerminalRenderFrameAsync(
            SessionId sessionId,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            RenderReadCalls++;
            return _renderFrame is null
                ? ValueTask.FromResult(HostResult<TerminalRenderFrame>.Fail(
                    HostError.Create(
                        HostErrorCode.CapabilityNotSupported,
                        "Rich rendering is disabled for this test client."),
                    0))
                : Success(_renderFrame);
        }

        public ValueTask<HostResult<Unit>> ClearTerminalScrollbackAsync(
            TerminalClearScrollbackRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            ClearScrollbackCalls++;
            return Success(Unit.Value);
        }

        public ValueTask<HostResult<TerminalFindResult>> FindTerminalAsync(
            TerminalFindRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            LastFindInput = request.Find;
            return Success(new TerminalFindResult(2, 1, false));
        }

        public ValueTask<HostResult<Unit>> DetachAsync(
            DetachSessionRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            DetachCalls++;
            return Success(Unit.Value);
        }

        public async ValueTask<HostResult<Unit>> ResizeTerminalAsync(
            TerminalResizeRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeResizes);
            UpdateMaximum(active);
            ResizeRequests.Add(request);
            try
            {
                if (Interlocked.Increment(ref _resizeCalls) == 1)
                {
                    FirstResizeStarted.TrySetResult();
                    await ReleaseFirstResize.Task.WaitAsync(cancellationToken);
                }

                return HostResult<Unit>.Succeed(Unit.Value, 1);
            }
            finally
            {
                Interlocked.Decrement(ref _activeResizes);
            }
        }

        public ValueTask<HostResult<Unit>> FocusTerminalAsync(
            SessionId sessionId,
            OperationContext context,
            CancellationToken cancellationToken) => Success(Unit.Value);

        public async ValueTask<HostResult<Unit>> WriteTerminalAsync(
            TerminalWriteRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            var input = await PhysicalInputAsync(
                request.LeaseId,
                "text",
                cancellationToken);
            if (input is HostResult<Unit>.Failure)
            {
                return input;
            }

            TerminalInputs.Add($"text:{request.Text}");
            return HostResult<Unit>.Succeed(Unit.Value, 1);
        }

        public async ValueTask<HostResult<Unit>> SendTerminalKeyAsync(
            TerminalKeyRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            var input = await PhysicalInputAsync(
                request.LeaseId,
                "key",
                cancellationToken);
            if (input is HostResult<Unit>.Failure)
            {
                return input;
            }

            TerminalInputs.Add(
                $"key:{request.KeyStroke.Key}:{request.KeyStroke.Modifiers}");
            return HostResult<Unit>.Succeed(Unit.Value, 1);
        }

        public async ValueTask<HostResult<Unit>> SendTerminalPhysicalKeyAsync(
            TerminalPhysicalKeyRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            var input = await PhysicalInputAsync(
                request.LeaseId,
                "physical-key",
                cancellationToken);
            if (input is HostResult<Unit>.Failure)
            {
                return input;
            }

            TerminalInputs.Add(
                $"physical-key:{request.KeyEvent.PhysicalKey}:{request.KeyEvent.Action}");
            return HostResult<Unit>.Succeed(Unit.Value, 1);
        }

        public async ValueTask<HostResult<Unit>> SendTerminalMouseAsync(
            TerminalMouseRequest request,
            OperationContext context,
            CancellationToken cancellationToken) =>
            await PhysicalInputAsync(
                request.LeaseId,
                "mouse",
                cancellationToken);

        public ValueTask<HostResult<Unit>> ScrollTerminalViewportAsync(
            TerminalViewportScrollRequest request,
            OperationContext context,
            CancellationToken cancellationToken) => Success(Unit.Value);

        public ValueTask<HostResult<Unit>> UpdateTerminalSelectionAsync(
            TerminalSelectionRequest request,
            OperationContext context,
            CancellationToken cancellationToken) => Success(Unit.Value);

        public ValueTask<HostResult<TerminalSelectionText>> ReadTerminalSelectionAsync(
            TerminalSelectionReadRequest request,
            OperationContext context,
            CancellationToken cancellationToken) =>
            Success(new TerminalSelectionText(string.Empty, false, false));

        public async ValueTask<HostResult<TerminalPasteResult>> PasteTerminalAsync(
            TerminalPasteRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            var input = await PhysicalInputAsync(
                request.LeaseId,
                "paste",
                cancellationToken);
            return input switch
            {
                HostResult<Unit>.Success => HostResult<TerminalPasteResult>.Succeed(
                    TerminalPasteResult.Completed(bracketed: false),
                    1),
                HostResult<Unit>.Failure failure =>
                    HostResult<TerminalPasteResult>.Fail(
                        failure.Error,
                        failure.CurrentRevision),
                _ => throw new InvalidOperationException(
                    "The physical input test result is unknown."),
            };
        }

        public ValueTask<HostResult<HostHello>> NegotiateAsync(
            ClientHello request,
            OperationContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<SessionStreamItem> WatchAsync(
            WatchSessionRequest request,
            OperationContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<HostResult<Unit>> ReleaseInputLeaseAsync(
            ReleaseInputLeaseRequest request,
            OperationContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<HostResult<CloseScopeResult>> CloseAsync(
            CloseScopeRequest request,
            OperationContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<HostResult<Unit>> DisconnectClientAsync(
            ClientId clientId,
            OperationContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private async ValueTask<HostResult<Unit>> PhysicalInputAsync(
            InputLeaseId leaseId,
            string kind,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activePhysicalInputs);
            UpdateMaximum(
                ref _maximumConcurrentPhysicalInputs,
                active);
            try
            {
                lock (_inputGate)
                {
                    if (_currentInputLeaseId != leaseId
                        || _currentInputHolderKind != ActorKind.Human)
                    {
                        return HostResult<Unit>.Fail(
                            HostError.Create(
                                HostErrorCode.LeaseDenied,
                                "The physical input did not use the current human lease."),
                            1);
                    }

                    _physicalInputLeaseIds.Add(leaseId);
                    _physicalInputKinds.Add(kind);
                }

                if (Interlocked.Exchange(
                        ref _blockNextPhysicalInput,
                        0) == 1)
                {
                    PhysicalInputStarted.TrySetResult();
                    await ReleasePhysicalInput.Task.WaitAsync(cancellationToken);
                }

                if (Interlocked.Exchange(
                        ref _failNextPhysicalInput,
                        0) == 1)
                {
                    return HostResult<Unit>.Fail(
                        HostError.Create(
                            HostErrorCode.EngineFailed,
                            "Injected physical input failure."),
                        1);
                }

                return HostResult<Unit>.Succeed(Unit.Value, 1);
            }
            finally
            {
                Interlocked.Decrement(ref _activePhysicalInputs);
            }
        }

        private static ValueTask<HostResult<T>> Success<T>(T value) =>
            ValueTask.FromResult(HostResult<T>.Succeed(value, 1));

        private void ThrowIf(InitializationFailureStage stage)
        {
            if (_failureStage == stage)
            {
                throw new InvalidOperationException($"Injected {stage} failure.");
            }
        }

        private void UpdateMaximum(int candidate)
        {
            UpdateMaximum(ref _maximumConcurrentResizes, candidate);
        }

        private static void UpdateMaximum(
            ref int maximum,
            int candidate)
        {
            int current;
            do
            {
                current = Volatile.Read(ref maximum);
                if (candidate <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(
                ref maximum,
                candidate,
                current) != current);
        }
    }

}

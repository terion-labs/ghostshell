using System.Runtime.CompilerServices;
using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

internal sealed class HostedSession
{
    private readonly object _gate = new();
    private readonly Dictionary<AttachmentId, AttachmentPresence> _attachments = [];
    private readonly Dictionary<AttachmentId, CancellationTokenSource>
        _attachmentAuthorities = [];
    private readonly List<SessionEvent> _events = [];
    private readonly SemaphoreSlim _resizeGate = new(1, 1);
    private readonly int _eventRetention;
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _changed = NewSignal();
    private SessionDescriptor _descriptor;
    private InputLease? _inputLease;
    private CancellationTokenSource _inputLeaseAuthority = new();
    private CancellationTokenSource _runtimeAuthority = new();
    private long _sequence;

    public HostedSession(
        IPanelSession engine,
        SessionOwner owner,
        string title,
        PanelSessionSnapshot engineSnapshot,
        int eventRetention,
        TimeProvider timeProvider,
        TerminalSessionMetadata? terminalMetadata = null,
        FileSessionMetadata? fileMetadata = null,
        PanelSessionRole role = PanelSessionRole.Primary)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(engineSnapshot);
        if (eventRetention < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(eventRetention));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, null);
        }

        if ((engine.Kind == PanelKind.Terminal) != (terminalMetadata is not null))
        {
            throw new ArgumentException(
                "Only terminal sessions require trusted terminal metadata.",
                nameof(terminalMetadata));
        }

        if ((engine.Kind == PanelKind.FileViewer) != (fileMetadata is not null))
        {
            throw new ArgumentException(
                "Only File Viewer sessions require trusted file metadata.",
                nameof(fileMetadata));
        }

        if (role == PanelSessionRole.Embedded && engine.Kind != PanelKind.Terminal)
        {
            throw new ArgumentException(
                "Only terminal sessions can currently be embedded in another panel.",
                nameof(role));
        }

        var browserMetadata = engine is IBrowserPanelSession browser
            ? BrowserSessionMetadata.FromState(browser.State)
            : null;
        if ((engine.Kind == PanelKind.Browser) != (browserMetadata is not null))
        {
            throw new ArgumentException(
                "Browser sessions require a browser engine with trusted document state.",
                nameof(engine));
        }

        var gitMetadata = engine is IGitPanelSession git
            ? git.State.Metadata
            : null;
        if ((engine.Kind == PanelKind.Git) != (gitMetadata is not null))
        {
            throw new ArgumentException(
                "Git sessions require a Git engine with trusted repository state.",
                nameof(engine));
        }

        Engine = engine;
        Owner = owner;
        Role = role;
        Title = title;
        _eventRetention = eventRetention;
        _timeProvider = timeProvider;
        _descriptor = new SessionDescriptor(
            engine.Id,
            engine.Kind,
            engineSnapshot.Lifecycle,
            engineSnapshot.Health,
            owner,
            engine.Capabilities,
            0,
            engineSnapshot.HasActiveWork,
            engineSnapshot.StatusDetail,
            engineSnapshot.Failure,
            terminalMetadata,
            fileMetadata,
            browserMetadata,
            gitMetadata);
        AppendEvent(SessionEventKind.Created, "Session created.");
    }

    public IPanelSession Engine { get; }

    public SessionOwner Owner { get; private set; }

    public PanelSessionRole Role { get; }

    public string Title { get; }

    public SessionId Id => Engine.Id;

    public SessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            ExpireLeaseIfNeeded();
            return SnapshotUnsafe();
        }
    }

    public void TransferOwner(SessionOwner expectedSource, SessionOwner destination)
    {
        ArgumentNullException.ThrowIfNull(expectedSource);
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            if (Owner != expectedSource)
            {
                throw new InvalidOperationException(
                    "The live session owner changed before the graph transfer committed.");
            }

            Owner = destination;
            _descriptor = _descriptor with { Owner = destination };
            AppendEventUnsafe(
                SessionEventKind.StateChanged,
                "Session ownership transferred without recreating the session.");
        }
    }

    public bool ApplyEngineSnapshot(PanelSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_descriptor.Lifecycle == snapshot.Lifecycle
                && _descriptor.Health == snapshot.Health
                && _descriptor.HasActiveWork == snapshot.HasActiveWork
                && string.Equals(_descriptor.StatusDetail, snapshot.StatusDetail
, StringComparison.Ordinal) && _descriptor.Failure == snapshot.Failure)
            {
                return false;
            }

            _descriptor = _descriptor with
            {
                Lifecycle = snapshot.Lifecycle,
                Health = snapshot.Health,
                HasActiveWork = snapshot.HasActiveWork,
                StatusDetail = snapshot.StatusDetail,
                Failure = snapshot.Failure,
            };
            AppendEventUnsafe(SessionEventKind.StateChanged, snapshot.StatusDetail);
            return true;
        }
    }

    public void RecordStateChange(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        lock (_gate)
        {
            AppendEventUnsafe(SessionEventKind.StateChanged, detail);
        }
    }

    public bool UpdateTerminalWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return false;
        }

        lock (_gate)
        {
            if (_descriptor.TerminalMetadata is not { } terminalMetadata)
            {
                return false;
            }

            TerminalSessionMetadata updated;
            try
            {
                updated = terminalMetadata.WithCurrentWorkingDirectory(workingDirectory);
            }
            catch (ArgumentException)
            {
                // A terminal-controlled value cannot replace previously trusted,
                // bounded presentation metadata when it is malformed or oversized.
                return false;
            }

            if (updated == terminalMetadata)
            {
                return false;
            }

            _descriptor = _descriptor with { TerminalMetadata = updated };
            AppendEventUnsafe(
                SessionEventKind.StateChanged,
                "Terminal working directory changed.");
            return true;
        }
    }

    public HostResult<AttachmentResult> Attach(
        AttachSessionRequest request,
        CapabilitySet hostCapabilities)
    {
        lock (_gate)
        {
            if (_descriptor.Lifecycle is SessionLifecycle.Closed or SessionLifecycle.Failed)
            {
                return HostResult<AttachmentResult>.Fail(
                    HostError.Create(HostErrorCode.SessionClosed, "The session is not attachable."),
                    _descriptor.Revision);
            }

            if (request.Kind == AttachmentKind.Interactive
                && _attachments.Values.Any(item => item.Kind == AttachmentKind.Interactive))
            {
                return HostResult<AttachmentResult>.Fail(
                    HostError.Create(
                        HostErrorCode.CapabilityNotSupported,
                        "This terminal engine currently supports one interactive renderer attachment."),
                    _descriptor.Revision);
            }

            var presence = new AttachmentPresence(
                AttachmentId.New(),
                Id,
                request.ClientId,
                request.Kind,
                request.Viewport,
                _timeProvider.GetUtcNow());
            _attachments.Add(presence.Id, presence);
            _attachmentAuthorities.Add(
                presence.Id,
                new CancellationTokenSource());
            AppendEventUnsafe(SessionEventKind.AttachmentAdded, "Client attached.", presence);

            var engineCapabilities = Engine.Capabilities;
            var sessionCapabilities = _descriptor.Capabilities;
            var effective = request.ClientCapabilities
                .Intersect(hostCapabilities)
                .Intersect(engineCapabilities)
                .Intersect(sessionCapabilities);
            var negotiation = new CapabilityNegotiation(
                request.ClientCapabilities,
                hostCapabilities,
                engineCapabilities,
                sessionCapabilities,
                effective);
            var snapshot = SnapshotUnsafe();
            return HostResult<AttachmentResult>.Succeed(
                new AttachmentResult(presence, snapshot, negotiation, _sequence),
                _descriptor.Revision);
        }
    }

    public HostResult<Unit> UpdateViewport(AttachmentId attachmentId, ViewportDescriptor viewport)
    {
        lock (_gate)
        {
            if (!_attachments.TryGetValue(attachmentId, out var attachment))
            {
                return HostResult<Unit>.Fail(
                    HostError.Create(HostErrorCode.NotFound, "The attachment was not found."),
                    _descriptor.Revision);
            }

            return UpdateViewportUnsafe(attachment, viewport);
        }
    }

    public ValueTask WaitForResizeAsync(CancellationToken cancellationToken) =>
        new(_resizeGate.WaitAsync(cancellationToken));

    public void ReleaseResize() => _resizeGate.Release();

    public bool HasAttachment(AttachmentId attachmentId, AttachmentKind? kind = null)
    {
        lock (_gate)
        {
            return _attachments.TryGetValue(attachmentId, out var attachment)
                && (kind is null || attachment.Kind == kind);
        }
    }

    public bool CanBindPhysicalInputGate(
        AttachmentId attachmentId,
        ActorDescriptor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            return CanBindPhysicalInputGateUnsafe(attachmentId, actor);
        }
    }

    public bool IsInteractiveAttachmentOwner(
        AttachmentId attachmentId,
        ActorDescriptor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            return CanBindPhysicalInputGateUnsafe(attachmentId, actor);
        }
    }

    public bool IsInteractiveAttachmentOwner(
        AttachmentId attachmentId,
        ClientId clientId)
    {
        lock (_gate)
        {
            return _attachments.TryGetValue(attachmentId, out var attachment)
                && attachment.Kind == AttachmentKind.Interactive
                && attachment.ClientId == clientId;
        }
    }

    public bool TryCaptureBrowserAttachmentAuthority(
        ClientId clientId,
        long expectedSessionRevision,
        out AttachmentId attachmentId,
        out CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            AttachmentPresence? interactiveAttachment = null;
            foreach (var attachment in _attachments.Values)
            {
                if (attachment.Kind != AttachmentKind.Interactive)
                {
                    continue;
                }

                if (interactiveAttachment is not null)
                {
                    attachmentId = default;
                    cancellationToken = new CancellationToken(canceled: true);
                    return false;
                }

                interactiveAttachment = attachment;
            }

            if (Engine.Kind != PanelKind.Browser
                || _descriptor.Revision != expectedSessionRevision
                || interactiveAttachment is null
                || interactiveAttachment.ClientId != clientId
                || !_attachmentAuthorities.TryGetValue(
                    interactiveAttachment.Id,
                    out var authority))
            {
                attachmentId = default;
                cancellationToken = new CancellationToken(canceled: true);
                return false;
            }

            attachmentId = interactiveAttachment.Id;
            cancellationToken = authority.Token;
            return true;
        }
    }

    public bool TryCaptureAgentBrowserAttachmentAuthority(
        long expectedSessionRevision,
        out AttachmentId attachmentId,
        out ClientId clientId,
        out CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            AttachmentPresence? interactiveAttachment = null;
            foreach (var attachment in _attachments.Values)
            {
                if (attachment.Kind != AttachmentKind.Interactive)
                {
                    continue;
                }

                if (interactiveAttachment is not null)
                {
                    attachmentId = default;
                    clientId = default;
                    cancellationToken = new CancellationToken(canceled: true);
                    return false;
                }

                interactiveAttachment = attachment;
            }

            if (Engine.Kind != PanelKind.Browser
                || _descriptor.Revision != expectedSessionRevision
                || interactiveAttachment is null
                || !_attachmentAuthorities.TryGetValue(
                    interactiveAttachment.Id,
                    out var authority))
            {
                attachmentId = default;
                clientId = default;
                cancellationToken = new CancellationToken(canceled: true);
                return false;
            }

            attachmentId = interactiveAttachment.Id;
            clientId = interactiveAttachment.ClientId;
            cancellationToken = authority.Token;
            return true;
        }
    }

    public bool CanExecuteAgentBrowserAction(
        AttachmentId attachmentId,
        ClientId approvingClientId,
        long expectedSessionRevision,
        CancellationToken attachmentAuthority)
    {
        lock (_gate)
        {
            return Engine.Kind == PanelKind.Browser
                && _descriptor.Revision == expectedSessionRevision
                && _attachments.TryGetValue(
                    attachmentId,
                    out var attachment)
                && attachment.Kind == AttachmentKind.Interactive
                && attachment.ClientId == approvingClientId
                && _attachmentAuthorities.TryGetValue(
                    attachmentId,
                    out var currentAuthority)
                && currentAuthority.Token.Equals(attachmentAuthority)
                && !attachmentAuthority.IsCancellationRequested;
        }
    }

    public bool CanExecuteAgentFileAction(
        IFilePanelSession files,
        FileSessionMetadata metadata,
        long expectedSessionRevision,
        string requiredSessionCapability,
        FilePanelCapability requiredProviderCapability,
        CancellationToken runtimeAuthority)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredSessionCapability);
        lock (_gate)
        {
            try
            {
                return _descriptor.Lifecycle == SessionLifecycle.Active
                    && _descriptor.Revision == expectedSessionRevision
                    && _descriptor.FileMetadata == metadata
                    && Engine.Kind == PanelKind.FileViewer
                    && ReferenceEquals(Engine, files)
                    && files.Metadata == metadata
                    && files.Capabilities.Contains(requiredSessionCapability)
                    && metadata.Capabilities.HasFlag(requiredProviderCapability)
                    && _runtimeAuthority.Token.Equals(runtimeAuthority)
                    && !runtimeAuthority.IsCancellationRequested;
            }
            catch (Exception)
            {
                // Provider/session metadata is an untrusted boundary. A failed
                // live revalidation cannot preserve execution authority.
                return false;
            }
        }
    }

    public bool CanExecuteAgentProcessList(
        IProcessMonitorPanelSession processes,
        long expectedSessionRevision,
        CancellationToken runtimeAuthority)
    {
        ArgumentNullException.ThrowIfNull(processes);
        lock (_gate)
        {
            try
            {
                return _descriptor.Lifecycle == SessionLifecycle.Active
                    && _descriptor.Revision == expectedSessionRevision
                    && Engine.Kind == PanelKind.ProcessMonitor
                    && ReferenceEquals(Engine, processes)
                    && _descriptor.Capabilities.Contains(
                        SessionCapabilities.ProcessesList)
                    && processes.Capabilities.Contains(
                        SessionCapabilities.ProcessesList)
                    && _runtimeAuthority.Token.Equals(runtimeAuthority)
                    && !runtimeAuthority.IsCancellationRequested;
            }
            catch (Exception)
            {
                // A monitor implementation cannot retain execution authority by
                // throwing while its live capabilities are revalidated.
                return false;
            }
        }
    }

    public bool CanExecuteAgentStatisticsRead(
        IStatisticsPanelSession statistics,
        long expectedSessionRevision,
        CancellationToken runtimeAuthority)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        lock (_gate)
        {
            try
            {
                return _descriptor.Lifecycle == SessionLifecycle.Active
                    && _descriptor.Revision == expectedSessionRevision
                    && Engine.Kind == PanelKind.Statistics
                    && ReferenceEquals(Engine, statistics)
                    && _descriptor.Capabilities.Contains(
                        SessionCapabilities.StatisticsRead)
                    && statistics.Capabilities.Contains(
                        SessionCapabilities.StatisticsRead)
                    && _runtimeAuthority.Token.Equals(runtimeAuthority)
                    && !runtimeAuthority.IsCancellationRequested;
            }
            catch (Exception)
            {
                // A monitor implementation cannot retain execution authority by
                // throwing while its live capabilities are revalidated.
                return false;
            }
        }
    }

    public bool CanExecuteAgentDatabaseRead(
        IDatabasePanelSession database,
        long expectedSessionRevision,
        string requiredCapability,
        CancellationToken runtimeAuthority)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredCapability);
        lock (_gate)
        {
            try
            {
                return _descriptor.Lifecycle == SessionLifecycle.Active
                    && _descriptor.Revision == expectedSessionRevision
                    && Engine.Kind == PanelKind.DatabaseViewer
                    && ReferenceEquals(Engine, database)
                    && _descriptor.Capabilities.Contains(requiredCapability)
                    && database.Capabilities.Contains(requiredCapability)
                    && _runtimeAuthority.Token.Equals(runtimeAuthority)
                    && !runtimeAuthority.IsCancellationRequested;
            }
            catch (Exception)
            {
                // A provider cannot preserve agent execution authority by
                // throwing during live capability revalidation.
                return false;
            }
        }
    }

    public bool CanExecuteAgentDockerRead(
        IDockerPanelSession docker,
        DockerSessionBinding expectedBinding,
        DockerEngineGeneration expectedEngineGeneration,
        long expectedSessionRevision,
        string requiredCapability,
        CancellationToken runtimeAuthority)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(expectedBinding);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredCapability);
        lock (_gate)
        {
            try
            {
                return _descriptor.Lifecycle == SessionLifecycle.Active
                    && _descriptor.Revision == expectedSessionRevision
                    && Engine.Kind == PanelKind.Docker
                    && ReferenceEquals(Engine, docker)
                    && docker.Binding == expectedBinding
                    && docker.State.EngineGeneration == expectedEngineGeneration
                    && _descriptor.Capabilities.Contains(requiredCapability)
                    && docker.Capabilities.Contains(requiredCapability)
                    && _runtimeAuthority.Token.Equals(runtimeAuthority)
                    && !runtimeAuthority.IsCancellationRequested;
            }
            catch (Exception)
            {
                // A Docker provider cannot preserve agent authority by throwing
                // while its exact binding, generation, and live capability are
                // revalidated.
                return false;
            }
        }
    }

    public bool CanExecuteAgentGitAction(
        IGitPanelSession git,
        GitSessionBinding expectedBinding,
        long expectedSessionRevision,
        string requiredCapability,
        CancellationToken runtimeAuthority)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredCapability);
        lock (_gate)
        {
            try
            {
                return _descriptor.Lifecycle == SessionLifecycle.Active
                    && _descriptor.Revision == expectedSessionRevision
                    && Engine.Kind == PanelKind.Git
                    && ReferenceEquals(Engine, git)
                    && git.Binding == expectedBinding
                    && _descriptor.GitMetadata == git.State.Metadata
                    && _descriptor.Capabilities.Contains(requiredCapability)
                    && git.Capabilities.Contains(requiredCapability)
                    && _runtimeAuthority.Token.Equals(runtimeAuthority)
                    && !runtimeAuthority.IsCancellationRequested;
            }
            catch (Exception)
            {
                // A Git provider cannot preserve authority by throwing while
                // its exact repository binding is revalidated.
                return false;
            }
        }
    }

    /// <summary>
    /// Reclaims terminal input for the exact interactive human attachment before a native
    /// renderer delivers one physical input. This method is intentionally synchronous:
    /// the renderer calls it from its native UI event stack and no transport work is allowed.
    /// </summary>
    public bool TryAcceptPhysicalInput(
        AttachmentId attachmentId,
        ActorDescriptor actor,
        TimeSpan leaseDuration)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            ExpireLeaseIfNeeded();
            if (leaseDuration <= TimeSpan.Zero
                || !CanBindPhysicalInputGateUnsafe(attachmentId, actor))
            {
                return false;
            }

            if (_inputLease is
                {
                    Holder.Id: var holderId,
                    Holder.Kind: ActorKind.Human,
                    AttachmentId: var heldAttachmentId,
                }
                && holderId == actor.Id
                && heldAttachmentId == attachmentId)
            {
                return true;
            }

            var now = _timeProvider.GetUtcNow();
            var preempted = _inputLease;
            RevokeInputLeaseAuthorityUnsafe();
            var lease = new InputLease(
                InputLeaseId.New(),
                Id,
                actor,
                attachmentId,
                now,
                now + leaseDuration,
                _descriptor.Revision + 1);
            _inputLease = lease;
            AppendEventUnsafe(
                preempted is null
                    ? SessionEventKind.InputLeaseGranted
                    : SessionEventKind.InputLeasePreempted,
                preempted is null
                    ? "Physical human input acquired the terminal."
                    : "Physical human input preempted the previous lease.",
                inputLease: lease);
            return true;
        }
    }

    private bool CanBindPhysicalInputGateUnsafe(
        AttachmentId attachmentId,
        ActorDescriptor actor) =>
        actor is
        {
            Kind: ActorKind.Human,
            ClientId: { } clientId,
        }
        && string.Equals(actor.Id.Value, clientId.Value
, StringComparison.Ordinal) && _attachments.TryGetValue(attachmentId, out var attachment)
        && attachment.Kind == AttachmentKind.Interactive
        && attachment.ClientId == clientId;

    public bool TryCaptureResizeAttachmentAuthority(
        AttachmentId attachmentId,
        ViewportDescriptor requestedViewport,
        long expectedSessionRevision,
        out CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedViewport);
        lock (_gate)
        {
            if (!_attachments.TryGetValue(attachmentId, out var attachment)
                || _descriptor.Revision != expectedSessionRevision
                || attachment.Kind != AttachmentKind.Interactive
                || attachment.Viewport.LogicalWidth
                    != requestedViewport.LogicalWidth
                || attachment.Viewport.LogicalHeight
                    != requestedViewport.LogicalHeight
                || attachment.Viewport.RenderScale
                    != requestedViewport.RenderScale
                || !_attachmentAuthorities.TryGetValue(
                    attachmentId,
                    out var authority))
            {
                cancellationToken = new CancellationToken(canceled: true);
                return false;
            }

            cancellationToken = authority.Token;
            return true;
        }
    }

    public bool CanExecuteAgentResize(
        AttachmentId attachmentId,
        ViewportDescriptor requestedViewport,
        ClientId approvingClientId,
        long expectedSessionRevision,
        CancellationToken attachmentAuthority)
    {
        ArgumentNullException.ThrowIfNull(requestedViewport);
        lock (_gate)
        {
            return _descriptor.Revision == expectedSessionRevision
                && MatchesAgentResizeAttachmentUnsafe(
                attachmentId,
                requestedViewport,
                approvingClientId,
                attachmentAuthority,
                out _);
        }
    }

    public HostResult<Unit> UpdateAgentResizeViewport(
        AttachmentId attachmentId,
        ViewportDescriptor viewport,
        ClientId approvingClientId,
        CancellationToken attachmentAuthority)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        lock (_gate)
        {
            if (!MatchesAgentResizeAttachmentUnsafe(
                    attachmentId,
                    viewport,
                    approvingClientId,
                    attachmentAuthority,
                    out var attachment))
            {
                return HostResult<Unit>.Fail(
                    new HostError(
                        HostErrorCode.Cancelled,
                        "attachment_revoked",
                        "The exact interactive attachment changed during resize."),
                    _descriptor.Revision);
            }

            return UpdateViewportUnsafe(attachment!, viewport);
        }
    }

    private bool MatchesAgentResizeAttachmentUnsafe(
        AttachmentId attachmentId,
        ViewportDescriptor requestedViewport,
        ClientId approvingClientId,
        CancellationToken attachmentAuthority,
        out AttachmentPresence? attachment)
    {
        if (!_attachments.TryGetValue(attachmentId, out attachment)
            || attachment.Kind != AttachmentKind.Interactive
            || attachment.ClientId != approvingClientId
            || attachment.Viewport.LogicalWidth
                != requestedViewport.LogicalWidth
            || attachment.Viewport.LogicalHeight
                != requestedViewport.LogicalHeight
            || attachment.Viewport.RenderScale
                != requestedViewport.RenderScale
            || !_attachmentAuthorities.TryGetValue(
                attachmentId,
                out var currentAuthority)
            || !currentAuthority.Token.Equals(attachmentAuthority)
            || attachmentAuthority.IsCancellationRequested)
        {
            attachment = null;
            return false;
        }

        return true;
    }

    private HostResult<Unit> UpdateViewportUnsafe(
        AttachmentPresence attachment,
        ViewportDescriptor viewport)
    {
        if (_attachmentAuthorities.TryGetValue(
                attachment.Id,
                out var attachmentAuthority)
            && attachment.Viewport != viewport)
        {
            _attachmentAuthorities[attachment.Id] =
                new CancellationTokenSource();
            BeginAuthorityRevocation(attachmentAuthority);
        }

        _attachments[attachment.Id] = attachment with { Viewport = viewport };
        AppendEventUnsafe(SessionEventKind.StateChanged, "Attachment viewport changed.");
        return HostResult<Unit>.Succeed(Unit.Value, _descriptor.Revision);
    }

    public IReadOnlyList<AttachmentPresence> AttachmentsForClient(ClientId clientId)
    {
        lock (_gate)
        {
            return [.. _attachments.Values.Where(item => item.ClientId == clientId)];
        }
    }

    public HostResult<Unit> Detach(AttachmentId attachmentId)
    {
        lock (_gate)
        {
            if (!_attachments.Remove(attachmentId, out var attachment))
            {
                return HostResult<Unit>.Succeed(Unit.Value, _descriptor.Revision);
            }

            if (_attachmentAuthorities.Remove(
                    attachmentId,
                    out var attachmentAuthority))
            {
                BeginAuthorityRevocation(attachmentAuthority);
            }

            if (_inputLease?.AttachmentId == attachmentId)
            {
                var released = _inputLease;
                _inputLease = null;
                RevokeInputLeaseAuthorityUnsafe();
                AppendEventUnsafe(
                    SessionEventKind.InputLeaseReleased,
                    "The input lease ended with its attachment.",
                    inputLease: released);
            }

            AppendEventUnsafe(SessionEventKind.AttachmentRemoved, "Client detached.", attachment);
            return HostResult<Unit>.Succeed(Unit.Value, _descriptor.Revision);
        }
    }

    public HostResult<InputLeaseDecision> AcquireLease(
        AcquireInputLeaseRequest request,
        ActorDescriptor actor)
    {
        lock (_gate)
        {
            ExpireLeaseIfNeeded();
            if (request.Duration <= TimeSpan.Zero)
            {
                return HostResult<InputLeaseDecision>.Fail(
                    HostError.Create(HostErrorCode.InvalidRequest, "The input lease duration must be positive."),
                    _descriptor.Revision);
            }

            if (request.AttachmentId is { } attachmentId
                && !_attachments.ContainsKey(attachmentId))
            {
                return HostResult<InputLeaseDecision>.Fail(
                    HostError.Create(HostErrorCode.NotFound, "The attachment was not found."),
                    _descriptor.Revision);
            }

            var now = _timeProvider.GetUtcNow();
            if (_inputLease is not null && _inputLease.Holder.Id == actor.Id)
            {
                var renewed = _inputLease with
                {
                    ExpiresAtUtc = now + request.Duration,
                    Revision = _descriptor.Revision + 1,
                };
                _inputLease = renewed;
                AppendEventUnsafe(SessionEventKind.InputLeaseGranted, "Input lease renewed.", inputLease: renewed);
                return HostResult<InputLeaseDecision>.Succeed(
                    new InputLeaseDecision(true, renewed, "Input lease renewed."),
                    _descriptor.Revision);
            }

            if (_inputLease is not null && actor.Kind != ActorKind.Human)
            {
                return HostResult<InputLeaseDecision>.Succeed(
                    new InputLeaseDecision(false, _inputLease, "Another actor holds the input lease."),
                    _descriptor.Revision);
            }

            var preempted = _inputLease;
            RevokeInputLeaseAuthorityUnsafe();
            var lease = new InputLease(
                InputLeaseId.New(),
                Id,
                actor,
                request.AttachmentId,
                now,
                now + request.Duration,
                _descriptor.Revision + 1);
            _inputLease = lease;
            AppendEventUnsafe(
                preempted is null
                    ? SessionEventKind.InputLeaseGranted
                    : SessionEventKind.InputLeasePreempted,
                preempted is null
                    ? "Input lease granted."
                    : "Human input preempted the previous lease.",
                inputLease: lease);
            return HostResult<InputLeaseDecision>.Succeed(
                new InputLeaseDecision(
                    true,
                    lease,
                    preempted is null ? "Input lease granted." : "Previous input holder preempted.",
                    preempted is not null),
                _descriptor.Revision);
        }
    }

    public HostResult<Unit> ReleaseLease(InputLeaseId leaseId, ActorDescriptor actor)
    {
        lock (_gate)
        {
            ExpireLeaseIfNeeded();
            if (_inputLease is null)
            {
                return HostResult<Unit>.Succeed(Unit.Value, _descriptor.Revision);
            }

            if (_inputLease.Id != leaseId || _inputLease.Holder.Id != actor.Id)
            {
                return HostResult<Unit>.Fail(
                    HostError.Create(HostErrorCode.LeaseDenied, "Only the current holder can release this input lease."),
                    _descriptor.Revision);
            }

            var released = _inputLease;
            _inputLease = null;
            RevokeInputLeaseAuthorityUnsafe();
            AppendEventUnsafe(SessionEventKind.InputLeaseReleased, "Input lease released.", inputLease: released);
            return HostResult<Unit>.Succeed(Unit.Value, _descriptor.Revision);
        }
    }

    /// <summary>
    /// Exchanges live one-action authorization for the terminal's input
    /// authority. The generated lease never crosses the agent request boundary.
    /// </summary>
    public HostResult<OneActionAgentLease> AcquireOneActionAgentLease(
        AgentActionAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (_gate)
        {
            ExpireLeaseIfNeeded();
            var now = _timeProvider.GetUtcNow();
            if (authorization.ExpiresAtUtc <= now)
            {
                return HostResult<OneActionAgentLease>.Fail(
                    HostError.Create(
                        HostErrorCode.DeadlineExceeded,
                        "The one-action input authority has expired."),
                    _descriptor.Revision);
            }

            var current = _inputLease;
            var sameAgent = current is
            {
                Holder.Kind: ActorKind.Agent,
            } && current.Holder.Id == authorization.ActorId;
            var approvedHumanHandoff = current is
            {
                Holder.Kind: ActorKind.Human,
                Holder.ClientId: { } holderClientId,
            }
                && holderClientId == authorization.ApprovingClientId
                && authorization.Source is
                    AgentAuthorizationSource.HumanApproval
                    or AgentAuthorizationSource.YoloPolicy;
            if (current is not null && !sameAgent && !approvedHumanHandoff)
            {
                return HostResult<OneActionAgentLease>.Fail(
                    HostError.Create(
                        HostErrorCode.LeaseDenied,
                        "Another actor holds the terminal input lease."),
                    _descriptor.Revision);
            }

            RevokeInputLeaseAuthorityUnsafe();
            var lease = new InputLease(
                InputLeaseId.New(),
                Id,
                authorization.Agent,
                AttachmentId: null,
                now,
                authorization.ExpiresAtUtc,
                _descriptor.Revision + 1);
            _inputLease = lease;
            AppendEventUnsafe(
                current is null
                    ? SessionEventKind.InputLeaseGranted
                    : SessionEventKind.InputLeasePreempted,
                current switch
                {
                    null => "One-action agent input lease granted.",
                    { Holder.Kind: ActorKind.Agent } =>
                        "One-action agent input lease replaced.",
                    _ => "Approved agent action received terminal input.",
                },
                inputLease: lease);
            return HostResult<OneActionAgentLease>.Succeed(
                new OneActionAgentLease(
                    lease.Id,
                    _inputLeaseAuthority.Token),
                _descriptor.Revision);
        }
    }

    public void ReleaseOneActionAgentLease(
        InputLeaseId leaseId,
        ActorId agentId)
    {
        lock (_gate)
        {
            if (_inputLease is not { } lease
                || lease.Id != leaseId
                || lease.Holder.Kind != ActorKind.Agent
                || lease.Holder.Id != agentId)
            {
                return;
            }

            _inputLease = null;
            RevokeInputLeaseAuthorityUnsafe();
            AppendEventUnsafe(
                SessionEventKind.InputLeaseReleased,
                "One-action agent input lease released.",
                inputLease: lease);
        }
    }

    public bool HoldsLease(InputLeaseId leaseId, ActorId actorId)
    {
        lock (_gate)
        {
            ExpireLeaseIfNeeded();
            return _inputLease is { } lease
                && lease.Id == leaseId
                && lease.Holder.Id == actorId;
        }
    }

    public bool TryCaptureLeaseAuthority(
        InputLeaseId leaseId,
        ActorId actorId,
        out CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ExpireLeaseIfNeeded();
            if (_inputLease is not { } lease
                || lease.Id != leaseId
                || lease.Holder.Id != actorId)
            {
                cancellationToken = new CancellationToken(canceled: true);
                return false;
            }

            cancellationToken = _inputLeaseAuthority.Token;
            return true;
        }
    }

    public CancellationToken CaptureRuntimeAuthority()
    {
        lock (_gate)
        {
            return _runtimeAuthority.Token;
        }
    }

    public void RevokeRuntimeAuthority()
    {
        lock (_gate)
        {
            RotateRuntimeAuthorityUnsafe();
            RotateAttachmentAuthoritiesUnsafe();
            if (_inputLease is not null)
            {
                _inputLease = null;
                RevokeInputLeaseAuthorityUnsafe();
            }
        }
    }

    public void MarkCloseRequested(string detail)
    {
        lock (_gate)
        {
            RotateRuntimeAuthorityUnsafe();
            RotateAttachmentAuthoritiesUnsafe();
            if (_inputLease is { } released)
            {
                _inputLease = null;
                RevokeInputLeaseAuthorityUnsafe();
                AppendEventUnsafe(
                    SessionEventKind.InputLeaseReleased,
                    "The input lease ended because the session is closing.",
                    inputLease: released);
            }

            _descriptor = _descriptor with
            {
                Lifecycle = SessionLifecycle.Closing,
                StatusDetail = detail,
            };
            AppendEventUnsafe(SessionEventKind.CloseRequested, detail);
        }
    }

    public void ApplyCloseOutcome(PanelCloseOutcome outcome, string detail)
    {
        lock (_gate)
        {
            if (outcome is PanelCloseOutcome.GracefullyClosed
                    or PanelCloseOutcome.ForceTerminated
                    or PanelCloseOutcome.AlreadyClosed
                    or PanelCloseOutcome.EngineFailed
                && _inputLease is { } released)
            {
                _inputLease = null;
                RevokeInputLeaseAuthorityUnsafe();
                AppendEventUnsafe(
                    SessionEventKind.InputLeaseReleased,
                    "The input lease ended with the terminal session.",
                    inputLease: released);
            }

            if (outcome is PanelCloseOutcome.GracefullyClosed
                    or PanelCloseOutcome.ForceTerminated
                    or PanelCloseOutcome.AlreadyClosed
                    or PanelCloseOutcome.EngineFailed)
            {
                RotateRuntimeAuthorityUnsafe();
                RotateAttachmentAuthoritiesUnsafe();
            }

            switch (outcome)
            {
                case PanelCloseOutcome.GracefullyClosed:
                case PanelCloseOutcome.ForceTerminated:
                case PanelCloseOutcome.AlreadyClosed:
                    _descriptor = _descriptor with
                    {
                        Lifecycle = SessionLifecycle.Closed,
                        Health = SessionHealth.Ended,
                        HasActiveWork = false,
                        StatusDetail = detail,
                    };
                    AppendEventUnsafe(SessionEventKind.Closed, detail);
                    break;
                case PanelCloseOutcome.EngineFailed:
                    _descriptor = _descriptor with
                    {
                        Lifecycle = SessionLifecycle.Failed,
                        Health = SessionHealth.Failed,
                        Failure = new SessionFailure("engine_failed", detail, false),
                        StatusDetail = detail,
                    };
                    AppendEventUnsafe(SessionEventKind.Failed, detail);
                    break;
                case PanelCloseOutcome.ConfirmationRequired:
                case PanelCloseOutcome.Cancelled:
                    ApplyNonTerminalCloseOutcomeUnsafe(outcome, detail);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
            }
        }
    }

    public async IAsyncEnumerable<SessionStreamItem> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            SessionEvent[] pending;
            SessionSnapshot? resynchronizationSnapshot = null;
            Task waitTask;
            lock (_gate)
            {
                var oldestSequence = _events.Count == 0 ? _sequence + 1 : _events[0].Sequence;
                if (afterSequence < oldestSequence - 1)
                {
                    resynchronizationSnapshot = SnapshotUnsafe();
                    pending = [];
                    waitTask = Task.CompletedTask;
                }
                else
                {
                    pending = [.. _events.Where(item => item.Sequence > afterSequence)];
                    waitTask = _changed.Task;
                }
            }

            if (resynchronizationSnapshot is not null)
            {
                yield return new SessionStreamItem.ResynchronizationRequired(
                    resynchronizationSnapshot,
                    resynchronizationSnapshot.LastSequence);
                yield break;
            }

            if (pending.Length > 0)
            {
                foreach (var sessionEvent in pending)
                {
                    yield return new SessionStreamItem.Event(sessionEvent);
                    afterSequence = sessionEvent.Sequence;
                }

                continue;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyNonTerminalCloseOutcomeUnsafe(PanelCloseOutcome outcome, string detail)
    {
        _descriptor = _descriptor with
        {
            Lifecycle = SessionLifecycle.Active,
            StatusDetail = detail,
        };
        AppendEventUnsafe(SessionEventKind.StateChanged, detail);
    }

    private void ExpireLeaseIfNeeded()
    {
        if (_inputLease is null || _inputLease.ExpiresAtUtc > _timeProvider.GetUtcNow())
        {
            return;
        }

        var expired = _inputLease;
        _inputLease = null;
        RevokeInputLeaseAuthorityUnsafe();
        AppendEventUnsafe(SessionEventKind.InputLeaseReleased, "Input lease expired.", inputLease: expired);
    }

    private void RevokeInputLeaseAuthorityUnsafe()
    {
        var revoked = _inputLeaseAuthority;
        _inputLeaseAuthority = new CancellationTokenSource();
        BeginAuthorityRevocation(revoked);
    }

    private static void BeginAuthorityRevocation(
        CancellationTokenSource revoked)
    {
        try
        {
            _ = ObserveLeaseRevocationAsync(revoked.CancelAsync());
        }
        catch (ObjectDisposedException)
        {
            // The last operation can complete while preemption is observed.
        }
    }

    private void RotateAttachmentAuthoritiesUnsafe()
    {
        foreach (var attachmentId in _attachmentAuthorities.Keys.ToArray())
        {
            var revoked = _attachmentAuthorities[attachmentId];
            _attachmentAuthorities[attachmentId] =
                new CancellationTokenSource();
            BeginAuthorityRevocation(revoked);
        }
    }

    private void RotateRuntimeAuthorityUnsafe()
    {
        var revoked = _runtimeAuthority;
        _runtimeAuthority = new CancellationTokenSource();
        BeginAuthorityRevocation(revoked);
    }

    private static async Task ObserveLeaseRevocationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (AggregateException)
        {
            // Callback failures cannot restore an expired or preempted lease.
        }
        catch (ObjectDisposedException)
        {
            // A consumer can complete while callbacks are draining.
        }
    }

    private SessionSnapshot SnapshotUnsafe()
    {
        RefreshBrowserMetadataUnsafe();
        RefreshGitMetadataUnsafe();
        return new(
            _descriptor,
            _sequence,
            [.. _attachments.Values.OrderBy(item => item.AttachedAtUtc)],
            _inputLease);
    }

    private void RefreshGitMetadataUnsafe()
    {
        if (Engine is not IGitPanelSession git)
        {
            return;
        }

        var metadata = git.State.Metadata;
        if (_descriptor.GitMetadata == metadata)
        {
            return;
        }

        _descriptor = _descriptor with { GitMetadata = metadata };
        AppendEventUnsafe(
            SessionEventKind.StateChanged,
            "Git repository authority state changed.");
    }

    private void RefreshBrowserMetadataUnsafe()
    {
        if (Engine is not IBrowserPanelSession browser)
        {
            return;
        }

        var metadata = BrowserSessionMetadata.FromState(browser.State);
        if (_descriptor.BrowserMetadata == metadata)
        {
            return;
        }

        _descriptor = _descriptor with { BrowserMetadata = metadata };
        AppendEventUnsafe(
            SessionEventKind.StateChanged,
            "Browser document identity changed.");
    }

    private void AppendEvent(
        SessionEventKind kind,
        string detail,
        AttachmentPresence? attachment = null,
        InputLease? inputLease = null)
    {
        lock (_gate)
        {
            AppendEventUnsafe(kind, detail, attachment, inputLease);
        }
    }

    private void AppendEventUnsafe(
        SessionEventKind kind,
        string detail,
        AttachmentPresence? attachment = null,
        InputLease? inputLease = null)
    {
        _descriptor = _descriptor with { Revision = _descriptor.Revision + 1 };
        _sequence++;
        _events.Add(new SessionEvent(
            Id,
            _sequence,
            _descriptor.Revision,
            kind,
            1,
            _timeProvider.GetUtcNow(),
            _descriptor,
            attachment,
            inputLease,
            detail));
        if (_events.Count > _eventRetention)
        {
            _events.RemoveRange(0, _events.Count - _eventRetention);
        }

        var changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record OneActionAgentLease(
    InputLeaseId Id,
    CancellationToken CancellationToken);

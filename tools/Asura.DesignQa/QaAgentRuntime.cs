using System.Collections.Immutable;
using Asura.Application;
using Asura.Core;

namespace Asura.DesignQa;

/// <summary>
/// An offline governed-agent runtime. It reports the same "no provider
/// configured" boundary the shipped application shows before the user sets up a
/// provider, and it refuses every action, so no capture can imply that a
/// connected agent exists.
///
/// It can also publish a scripted transcript. Nothing is sent anywhere and no
/// action is ever performed — the sample exists so the panel's conversation
/// layout is reviewable at all, since the empty state is otherwise the only
/// thing this harness can render.
/// </summary>
internal sealed class QaOfflineAgentRuntime : IGovernedAgentRuntime
{
    private GovernedAgentSnapshot _snapshot = Offline;

    private static readonly GovernedAgentSnapshot Offline = new(
        GovernedAgentState.Ready,
        RunId: null,
        ProviderId: null,
        Target: null,
        TargetTitle: string.Empty,
        [],
        Messages: [],
        EffectivePolicy: AgentPolicy.Default,
        ProvisionalAssistantText: string.Empty,
        Status: "Add an AI provider to start a governed run.");

    public event EventHandler? Changed;

    public GovernedAgentSnapshot Snapshot => _snapshot;

    /// <summary>
    /// Publishes a sample conversation so the transcript, the tool card, and the
    /// capability boundary are all on screen at once.
    /// </summary>
    public void PublishSampleConversation()
    {
        _snapshot = Offline with
        {
            State = GovernedAgentState.RunningTool,
            TargetTitle = "production-api",
            Messages =
            [
                new AgentChatMessage(
                    AgentChatMessageRole.User,
                    "The prod deploy just failed. Can you look at api-server and fix it?"),
                new AgentChatMessage(
                    AgentChatMessageRole.Assistant,
                    "The build passed but the deploy exited with a missing DATABASE_URL. "
                    + "I'll read it from the vault and retry the deploy."),
            ],
            ActiveTool = new GovernedAgentToolActivity(
                "terminal.run",
                "Run a bounded command",
                AgentActionRisk.Mutation,
                "production-api"),
            TerminalMutationAvailable = true,
            CapabilityNotice =
                "Terminal writes are asked for one action at a time, in this panel only.",
            Status = "Running a bounded command in production-api.",
        };

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PublishSampleReasoningConversation()
    {
        _snapshot = Offline with
        {
            State = GovernedAgentState.Ready,
            RunId = new AgentRunId("qa-reasoning-run"),
            ProviderId = new AiProviderProfileId("qa-openai"),
            Target = new AgentTarget.Workspace(
                new WindowInstanceId("qa-window"),
                new WorkspaceInstanceId("qa-workspace")),
            TargetTitle = "Current workspace",
            Messages =
            [
                new AgentChatMessage(
                    AgentChatMessageRole.User,
                    "Keep the conversation, but move the next turn to **GPT-5.6 Terra**."),
                new AgentChatMessage(
                    AgentChatMessageRole.Assistant,
                    "The conversation stays intact. The next turn will use the selected model; "
                    + "provider-private replay data is reused only when its original route still matches.\n\n"
                    + "- Visible messages remain in the chat\n"
                    + "- The branch can continue independently",
                    "**Checked the conversation boundary**\n\n"
                    + "Confirmed that visible messages belong to the chat, not to one provider route.\n\n"
                    + "**Compared the selected model**\n\n"
                    + "Kept provider-private replay data only where its original route still matches.\n\n"
                    + "**Prepared the next turn**\n\n"
                    + "Preserved the transcript and applied GPT-5.6 Terra to the next request.",
                    new AgentChatUsage(3177, 72, 0, 56, 3249),
                    RequestedReasoningEffort: AgentReasoningEffort.High,
                    ForkPoint: new AgentConversationForkPoint(3)),
            ],
            EffectivePolicy = AgentPolicy.Default with
            {
                Provider = "qa-openai",
                Model = "gpt-5.6-terra",
            },
            CapabilityNotice = string.Empty,
            Status = string.Empty,
        };

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Publishes a pending run-local capability request so the decision card is
    /// reviewable. The card asks the panel's one governance question, and it
    /// rotted unreviewed precisely because no capture could ever render it.
    /// </summary>
    public void PublishSampleCapabilityRequest()
    {
        var target = new AgentTarget.Panel(
            new WindowInstanceId("qa-window"),
            new WorkspaceInstanceId("qa-workspace"),
            new TabInstanceId("qa-tab"),
            new PanelInstanceId("qa-panel"));
        _snapshot = Offline with
        {
            State = GovernedAgentState.RunningTool,
            Target = target,
            TargetTitle = "Process Monitor · local machine",
            Messages =
            [
                new AgentChatMessage(
                    AgentChatMessageRole.User,
                    "List the busiest local processes."),
            ],
            PendingCapabilityRequest = new GovernedAgentCapabilityRequest(
                new AgentCapabilityRequestId("qa-capability-request"),
                new AgentRunId("qa-run"),
                AgentCapability.ProcessControl,
                "Process inspection",
                ["List local processes"],
                target,
                "Process Monitor · local machine",
                policyGeneration: 1,
                expiresAtUtc: new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero)),
            CapabilityNotice =
                "Terminal writes are asked for one action at a time, in this panel only.",
            Status = "Waiting for your run-local capability decision.",
        };

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PublishSampleFailure()
    {
        _snapshot = Offline with
        {
            State = GovernedAgentState.Failed,
            RunId = new AgentRunId("qa-failed-run"),
            ProviderId = new AiProviderProfileId("qa-openai"),
            Target = new AgentTarget.Workspace(
                new WindowInstanceId("qa-window"),
                new WorkspaceInstanceId("qa-workspace")),
            TargetTitle = "Current workspace",
            Status = "The configured AI model is unavailable.",
            EffectivePolicy = AgentPolicy.Default with
            {
                Provider = "qa-openai",
                Model = "gpt-5.6-terra",
            },
        };

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns to the offline boundary this harness reports by default.</summary>
    public void Reset()
    {
        if (_snapshot == Offline)
        {
            return;
        }

        _snapshot = Offline;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static ValueTask<T> Unsupported<T>() =>
        throw new NotSupportedException("The design QA harness runs the agent offline.");

    public ValueTask<GovernedAgentSendResult> SendAsync(
        GovernedAgentPrompt request, CancellationToken cancellationToken) =>
        Unsupported<GovernedAgentSendResult>();

    public ValueTask<GovernedAgentSteeringResult> SteerAsync(
        GovernedAgentSteering request, CancellationToken cancellationToken) =>
        Unsupported<GovernedAgentSteeringResult>();

    public ValueTask<GovernedAgentDecisionResult> DecideAsync(
        AgentApprovalId approvalId, bool approved, CancellationToken cancellationToken) =>
        Unsupported<GovernedAgentDecisionResult>();

    public ValueTask<GovernedAgentQuestionResponseResult> RespondToQuestionAsync(
        AgentQuestionId questionId,
        GovernedAgentQuestionResponse response,
        CancellationToken cancellationToken) =>
        Unsupported<GovernedAgentQuestionResponseResult>();

    public ValueTask<GovernedAgentCapabilityDecisionResult> DecideCapabilityRequestAsync(
        AgentCapabilityRequestId requestId,
        GovernedAgentCapabilityDecision decision,
        CancellationToken cancellationToken) =>
        Unsupported<GovernedAgentCapabilityDecisionResult>();

    public ValueTask<GovernedAgentActionCancellationResult> CancelActiveActionAsync(
        CancellationToken cancellationToken) =>
        Unsupported<GovernedAgentActionCancellationResult>();

    public ValueTask<GovernedAgentStopResult> StopAsync(CancellationToken cancellationToken) =>
        Unsupported<GovernedAgentStopResult>();

    public ValueTask<GovernedAgentPolicyResult> EnableYoloAsync(
        TimeSpan lifetime, CancellationToken cancellationToken) =>
        Unsupported<GovernedAgentPolicyResult>();

    public ValueTask<GovernedAgentPolicyResult> DisableYoloAsync(
        CancellationToken cancellationToken) =>
        Unsupported<GovernedAgentPolicyResult>();

    public ValueTask<bool> ClearAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class QaAiProfileRuntime : IAiProviderProfileRuntime
{
    public event EventHandler? ProfilesChanged;

    /// <summary>
    /// Empty until a route asks for the connected panel. The harness holds no
    /// credential and reaches no endpoint; the descriptor supplies realistic
    /// model choices so the run configuration can be visually reviewed.
    /// </summary>
    public IReadOnlyList<AiProviderProfileDescriptor> Profiles { get; private set; } = [];

    public void PublishSampleProfile()
    {
        Profiles =
        [
            new AiProviderProfileDescriptor(
                new AiProviderProfileId("qa-openai"),
                "OpenAI",
                AiProviderKind.OpenAi,
                new Uri("https://api.openai.com/v1/"),
                "gpt-5.6-terra",
                Order: 0,
                IsEnabled: true,
                RequiresCredential: false,
                Models:
                [
                    new AiProviderModelDescriptor("gpt-5.6-terra", "GPT-5.6 Terra"),
                    new AiProviderModelDescriptor("gpt-5.6-sol", "GPT-5.6 Sol"),
                    new AiProviderModelDescriptor("gpt-5.6-luna", "GPT-5.6 Luna"),
                ]),
        ];

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        if (Profiles.Count == 0)
        {
            return;
        }

        Profiles = [];
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<AiProviderRuntimeDiagnostic> Diagnostics => [];

    public ValueTask<AiProviderTestResult> TestAsync(
        AiProviderProfile profile, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask ReloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public void Dispose()
    {
    }
}

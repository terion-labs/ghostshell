using System.Collections.Immutable;
using System.Xml.Linq;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;
using Asura.Testing;

namespace Asura.App.Tests;

public sealed partial class AgentChatViewModelTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Constructor_projects_enabled_providers_and_pending_capability_state()
    {
        var first = Provider("first", "Alpha", order: 1);
        var selected = Provider("selected", "Zulu", order: 2);
        var disabled = Provider("disabled", "Disabled", order: 0, isEnabled: false);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(providerId: selected.Id),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [selected, disabled, first],
        };

        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.Collection(
            viewModel.Providers,
            provider => Assert.Equal(first.Id, provider.Id),
            provider => Assert.Equal(selected.Id, provider.Id));
        Assert.Equal(selected.Id, viewModel.SelectedProvider?.Id);
        Assert.Equal("model", viewModel.SelectedModel?.Id);
        Assert.True(viewModel.HasMultipleProviders);
        Assert.False(viewModel.HasMultipleModels);
        Assert.Equal("Ready", viewModel.ConnectionStatus);
        Assert.Equal("Capability check", viewModel.CapabilityLabel);
        Assert.False(viewModel.TerminalMutationAvailable);
        Assert.Contains("verified", viewModel.CapabilityNotice);
        Assert.Equal(0, profiles.DiscoverModelsCount);
    }

    [Fact]
    public async Task Model_discovery_starts_only_after_explicit_refresh()
    {
        var provider = Provider("provider", "OpenAI", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(providerId: provider.Id),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.Equal(0, profiles.DiscoverModelsCount);

        await viewModel.RefreshModelsAsync(CancellationToken.None);

        Assert.Equal(1, profiles.DiscoverModelsCount);
        Assert.Equal(provider.Id, profiles.LastDiscoveredProviderId);
    }

    [Fact]
    public void Reasoning_selector_offers_only_route_supported_levels_and_resets_invalid_choice()
    {
        var reasoning = Provider(
            "reasoning",
            "Reasoning",
            order: 0,
            supportedReasoningEfforts:
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.High,
            ]);
        var compatible = Provider("compatible", "Compatible", order: 1);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(providerId: reasoning.Id),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [reasoning, compatible],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.CanSelectReasoningEffort);
        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.Low,
                AgentReasoningEffort.High,
            ],
            viewModel.ReasoningEfforts.Select(option => option.Value));
        viewModel.SelectedReasoningEffort = viewModel.ReasoningEfforts.Single(option =>
            option.Value == AgentReasoningEffort.High);

        viewModel.SelectedProvider = compatible;

        Assert.False(viewModel.CanSelectReasoningEffort);
        Assert.Equal(
            AgentReasoningEffort.Automatic,
            viewModel.SelectedReasoningEffort.Value);
    }

    [Fact]
    public async Task Model_selector_projects_exact_reasoning_and_service_tier_capabilities()
    {
        var gpt56 = new AiProviderModelDescriptor(
            "gpt-5.6-terra",
            "GPT-5.6 Terra",
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.High,
                AgentReasoningEffort.ExtraHigh,
                AgentReasoningEffort.Max,
            ],
            [
                AgentServiceTier.Automatic,
                AgentServiceTier.Default,
                AgentServiceTier.Flex,
                AgentServiceTier.Priority,
            ]);
        var basicModel = new AiProviderModelDescriptor(
            "model",
            "Basic model",
            [AgentReasoningEffort.Automatic, AgentReasoningEffort.High]);
        var provider = Provider(
            "provider",
            "OpenAI",
            order: 0,
            models: [basicModel, gpt56]);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        await viewModel.SelectModelAsync(gpt56, CancellationToken.None);

        Assert.Equal(
            [
                AgentReasoningEffort.Automatic,
                AgentReasoningEffort.High,
                AgentReasoningEffort.ExtraHigh,
                AgentReasoningEffort.Max,
            ],
            viewModel.ReasoningEfforts.Select(option => option.Value));
        Assert.Equal(
            [
                AgentServiceTier.Automatic,
                AgentServiceTier.Default,
                AgentServiceTier.Flex,
                AgentServiceTier.Priority,
            ],
            viewModel.ServiceTiers.Select(option => option.Value));
        viewModel.SelectedReasoningEffort = viewModel.ReasoningEfforts.Single(option =>
            option.Value == AgentReasoningEffort.ExtraHigh);
        viewModel.SelectedServiceTier = viewModel.ServiceTiers.Single(option =>
            option.Value == AgentServiceTier.Priority);
        viewModel.Prompt = "Inspect it.";

        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        Assert.Equal(AgentServiceTier.Priority, runtime.LastRequest!.ServiceTier);
        Assert.Equal(AgentReasoningEffort.ExtraHigh, runtime.LastRequest.ReasoningEffort);

        runtime.Snapshot = Snapshot();
        runtime.RaiseChanged();
        viewModel.SelectedModel = basicModel;

        Assert.Empty(viewModel.ServiceTiers);
        Assert.Equal(AgentServiceTier.Automatic, viewModel.SelectedServiceTier.Value);
        Assert.Equal(AgentReasoningEffort.Automatic, viewModel.SelectedReasoningEffort.Value);
    }

    [Fact]
    public void Context_window_projects_model_capacity_and_latest_provider_usage()
    {
        var model = new AiProviderModelDescriptor(
            "model",
            "GPT-5.6 Terra",
            contextWindowTokens: 272_000);
        var provider = Provider(
            "provider",
            "OpenAI",
            order: 0,
            models: [model]);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                providerId: provider.Id,
                messages:
                [
                    new AgentChatMessage(AgentChatMessageRole.User, "Question"),
                    new AgentChatMessage(
                        AgentChatMessageRole.Assistant,
                        "Answer",
                        Usage: new AgentChatUsage(140_000, 1_000, 0, 500, 141_000)),
                ]),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.HasContextWindow);
        Assert.Equal(255_616, viewModel.ContextEffectiveLimit);
        Assert.Equal(141_000, viewModel.ContextUsedTokens);
        Assert.Equal(55.16, viewModel.ContextWindowPercent, precision: 2);
        Assert.Equal("141k / 256k tokens used", viewModel.ContextWindowUsageLabel);
    }

    [Fact]
    public void Assistant_reasoning_status_distinguishes_requested_effort_from_provider_usage()
    {
        var unused = new AgentChatMessageViewModel(
            AgentChatMessageRole.Assistant,
            "Answer",
            Usage: new AgentChatUsage(20, 4, 0, 0, 24),
            RequestedReasoningEffort: AgentReasoningEffort.High);
        var used = new AgentChatMessageViewModel(
            AgentChatMessageRole.Assistant,
            "Answer",
            Usage: new AgentChatUsage(20, 12, 0, 8, 32),
            RequestedReasoningEffort: AgentReasoningEffort.High);

        Assert.True(unused.HasReasoningRequest);
        Assert.Equal(
            "High reasoning requested · provider reported 0 reasoning tokens",
            unused.ReasoningRequestLabel);
        Assert.True(used.HasReasoningRequest);
        Assert.Equal(
            "High reasoning requested · provider reported 8 reasoning tokens",
            used.ReasoningRequestLabel);
        Assert.Equal("Reasoned · High", unused.ReasoningTitle);
        Assert.Equal("Reasoned · 8 tokens", used.ReasoningTitle);
    }

    [Fact]
    public void Adjacent_reasoning_parts_are_presented_as_separate_paragraphs()
    {
        var message = new AgentChatMessageViewModel(
            AgentChatMessageRole.Assistant,
            "Answer",
            "**Analyzing the premise****Checking the contradiction****Writing the answer**");

        Assert.Equal(
            "Analyzing the premise\n\nChecking the contradiction\n\nWriting the answer",
            message.ReasoningSummaryDisplay);
        Assert.Equal(
            "**Analyzing the premise****Checking the contradiction****Writing the answer**",
            message.ReasoningSummary);
    }

    [Fact]
    public void Mixed_adjacent_and_paragraph_reasoning_parts_hide_emphasis_boundaries()
    {
        const string summary =
            "**Assessing conditions****Analyzing state**\n\n"
            + "**Concluding ambiguity****Checking consistency**\n\n"
            + "**Confirming the result**";
        var message = new AgentChatMessageViewModel(
            AgentChatMessageRole.Assistant,
            "Answer",
            summary);

        Assert.Equal(
            "Assessing conditions\n\nAnalyzing state\n\nConcluding ambiguity\n\n"
            + "Checking consistency\n\nConfirming the result",
            message.ReasoningSummaryDisplay);
        Assert.DoesNotContain("**", message.ReasoningSummaryDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_reasoning_markdown_is_not_rewritten()
    {
        const string summary = "Checked **two** constraints.\n\n- First\n- Second";
        var message = new AgentChatMessageViewModel(
            AgentChatMessageRole.Assistant,
            "Answer",
            summary);

        Assert.Equal(summary, message.ReasoningSummaryDisplay);
    }

    [Fact]
    public async Task Provisional_reasoning_presents_only_the_latest_stage()
    {
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(state: GovernedAgentState.StreamingProvider) with
            {
                ProvisionalReasoningSummary =
                    "**Reading the request****Inspecting the workspace****Writing the answer**",
            },
        };
        using var profiles = new StubProfileRuntime();
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.Equal(
            "Reading the request\n\nInspecting the workspace\n\nWriting the answer",
            viewModel.ProvisionalReasoningSummaryDisplay);
        Assert.Equal("Writing the answer", viewModel.ProvisionalReasoningStageDisplay);
        Assert.True(viewModel.ShowProvisionalReasoningLoader);

        runtime.Snapshot = runtime.Snapshot with
        {
            ProvisionalAssistantText = "The answer is streaming.",
        };
        runtime.RaiseChanged();
        await WaitUntilAsync(() => viewModel.HasProvisionalAssistantText);

        Assert.False(viewModel.ShowProvisionalReasoningLoader);
    }

    [Fact]
    public async Task Streaming_and_completion_preserve_committed_message_instances()
    {
        var committedMessages = new[]
        {
            new AgentChatMessage(AgentChatMessageRole.User, "Test every panel."),
            new AgentChatMessage(AgentChatMessageRole.Assistant, "Testing now."),
        };
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.StreamingProvider,
                messages: committedMessages) with
            {
                ProvisionalAssistantText = "Writing the final report",
            },
        };
        using var profiles = new StubProfileRuntime();
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        var first = viewModel.Messages[0];
        var second = viewModel.Messages[1];

        runtime.Snapshot = runtime.Snapshot with
        {
            ProvisionalAssistantText = "Writing the final report…",
        };
        runtime.RaiseChanged();
        await WaitUntilAsync(() =>
            viewModel.ProvisionalAssistantText.EndsWith('…'));

        Assert.Same(first, viewModel.Messages[0]);
        Assert.Same(second, viewModel.Messages[1]);

        runtime.Snapshot = runtime.Snapshot with
        {
            State = GovernedAgentState.Ready,
            Messages =
            [
                .. committedMessages,
                new AgentChatMessage(
                    AgentChatMessageRole.Assistant,
                    "The full test passed."),
            ],
            ProvisionalAssistantText = string.Empty,
        };
        runtime.RaiseChanged();
        await WaitUntilAsync(() => viewModel.Messages.Count == 3);

        Assert.Same(first, viewModel.Messages[0]);
        Assert.Same(second, viewModel.Messages[1]);
        Assert.Equal("The full test passed.", viewModel.Messages[2].Content);
    }

    [Fact]
    public async Task Assistant_fork_point_is_projected_and_forwarded()
    {
        var forkPoint = new AgentConversationForkPoint(2);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(messages:
            [
                new AgentChatMessage(AgentChatMessageRole.User, "Question"),
                new AgentChatMessage(
                    AgentChatMessageRole.Assistant,
                    "Answer",
                    ForkPoint: forkPoint),
            ]),
        };
        using var profiles = new StubProfileRuntime();
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        var assistant = viewModel.Messages[1];
        Assert.True(assistant.CanFork);
        Assert.Equal(forkPoint, assistant.ForkPoint);

        await viewModel.ForkConversationAsync(forkPoint, CancellationToken.None);

        Assert.Equal(1, runtime.ForkCount);
        Assert.Equal(forkPoint, runtime.LastForkPoint);
    }

    [Fact]
    public void Empty_assistant_turn_has_no_message_actions()
    {
        var message = new AgentChatMessageViewModel(
            AgentChatMessageRole.Assistant,
            string.Empty,
            ReasoningSummary: "Checked the request.",
            Usage: new AgentChatUsage(20, 8, 0, 8, 28),
            ForkPoint: new AgentConversationForkPoint(2));

        Assert.False(message.HasMessageText);
        Assert.False(message.CanFork);
    }

    [Fact]
    public void Current_progress_is_an_immutable_untrusted_projection_and_agent_content()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.StreamingProvider,
                runId: new AgentRunId("run-progress"),
                providerId: provider.Id,
                currentProgress: new GovernedAgentProgress(
                    "Reviewed 12 of 20 hosts",
                    percent: 60)),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };

        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        var progress = Assert.IsType<AgentProgressViewModel>(
            viewModel.CurrentProgress);
        Assert.Equal("Reviewed 12 of 20 hosts", progress.Message);
        Assert.Equal(60, progress.Percent);
        Assert.Equal(60d, progress.ProgressValue);
        Assert.Equal("60%", progress.PercentLabel);
        Assert.True(progress.HasPercent);
        Assert.False(progress.IsIndeterminate);
        Assert.Equal(
            GovernedAgentProgress.UntrustedModelContentOrigin,
            progress.ContentOrigin);
        Assert.Equal(
            "AI agent progress · Reviewed 12 of 20 hosts · 60 percent",
            progress.AccessibleName);
        Assert.True(viewModel.HasCurrentProgress);
        Assert.True(viewModel.HasAgentContent);
        Assert.False(viewModel.HasNoConversation);
        Assert.False(viewModel.HasConversation);
        Assert.Empty(viewModel.Messages);
        Assert.Empty(viewModel.AuditEntries);
    }

    [Fact]
    public void Runtime_progress_replaces_then_clears_without_entering_transcript()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var runId = new AgentRunId("run-progress");
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.StreamingProvider,
                runId: runId,
                providerId: provider.Id,
                currentProgress: new GovernedAgentProgress(
                    "Checking service health",
                    percent: 10)),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        var first = Assert.IsType<AgentProgressViewModel>(
            viewModel.CurrentProgress);

        runtime.Snapshot = Snapshot(
            state: GovernedAgentState.StreamingProvider,
            runId: runId,
            providerId: provider.Id,
            currentProgress: new GovernedAgentProgress("Waiting for the service"));
        runtime.RaiseChanged();

        var replacement = Assert.IsType<AgentProgressViewModel>(
            viewModel.CurrentProgress);
        Assert.NotSame(first, replacement);
        Assert.Equal("Waiting for the service", replacement.Message);
        Assert.Null(replacement.Percent);
        Assert.Equal(0d, replacement.ProgressValue);
        Assert.Equal(string.Empty, replacement.PercentLabel);
        Assert.False(replacement.HasPercent);
        Assert.True(replacement.IsIndeterminate);
        Assert.Equal(
            "AI agent progress · Waiting for the service · in progress",
            replacement.AccessibleName);
        Assert.Empty(viewModel.Messages);

        runtime.Snapshot = Snapshot(
            runId: runId,
            providerId: provider.Id);
        runtime.RaiseChanged();

        Assert.Null(viewModel.CurrentProgress);
        Assert.False(viewModel.HasCurrentProgress);
        Assert.False(viewModel.HasAgentContent);
        Assert.True(viewModel.HasNoConversation);
        Assert.Empty(viewModel.Messages);
        Assert.Empty(viewModel.AuditEntries);
    }

    [Fact]
    public void Progress_card_is_keyboard_accessible_without_a_native_live_region()
    {
        XNamespace viewNamespace = "https://github.com/avaloniaui";
        XNamespace ControlsNamespace = "using:Asura.App.Controls";
        var card = ApplicationViews
            .FindUniqueNamedElement("AgentCurrentProgress")
            .Element;

        Assert.Equal(ControlsNamespace + "SurfaceCard", card.Name);
        Assert.Equal("True", card.Attribute("Focusable")?.Value);
        Assert.Equal(
            "True",
            card.Attribute("KeyboardNavigation.IsTabStop")?.Value);
        Assert.Equal(
            "{Binding AgentChat.HasCurrentProgress, FallbackValue=False}",
            card.Attribute("IsVisible")?.Value);
        Assert.Null(card.Attribute("AutomationProperties.LiveSetting"));
        Assert.Equal(
            "AI agent progress",
            card.Attribute("AutomationProperties.Name")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(
            card.Attribute("AutomationProperties.HelpText")?.Value));
        Assert.Contains(
            card.Descendants(viewNamespace + "TextBlock"),
            element => string.Equals(
                element.Attribute("Text")?.Value,
                "{Binding AgentChat.CurrentProgress.Message}",
                StringComparison.Ordinal));

        var progressBar = Assert.Single(
            card.Descendants(viewNamespace + "ProgressBar"));
        Assert.Equal("0", progressBar.Attribute("Minimum")?.Value);
        Assert.Equal("100", progressBar.Attribute("Maximum")?.Value);
        Assert.Equal(
            "{Binding AgentChat.CurrentProgress.ProgressValue}",
            progressBar.Attribute("Value")?.Value);
        Assert.Equal(
            "{Binding AgentChat.CurrentProgress.IsIndeterminate}",
            progressBar.Attribute("IsIndeterminate")?.Value);
        Assert.Equal(
            "{Binding AgentChat.CurrentProgress.AccessibleName}",
            progressBar.Attribute("AutomationProperties.Name")?.Value);

        var theme = XDocument.Load(
            Path.Combine(
                ApplicationViews.RepositoryRoot,
                "src",
                "Asura.App",
                "Styles",
                "AsuraTheme.axaml"));
        // A visible focus ring is the card component's guarantee now, stated once
        // for every card rather than per style class. The theme file this used to
        // live in no longer decides what a card looks like.
        Assert.Contains(
            DesignSystem().Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "Style", StringComparison.Ordinal)),
            style => string.Equals(
                style.Attribute("Selector")?.Value,
                "^:focus-visible",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Pending_question_preserves_the_question_and_keeps_the_queue_composer_available()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var question = Question();
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.AwaitingUserInput,
                runId: new AgentRunId("run-question"),
                providerId: provider.Id,
                target: Target(),
                status: "Waiting for your non-sensitive clarification…",
                pendingQuestion: question),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Keep this separate main prompt.",
        };

        var pending = Assert.IsType<AgentQuestionCardViewModel>(
            viewModel.PendingQuestion);
        Assert.Equal(question.Id, pending.Id);
        Assert.Equal(question.Question, pending.Question);
        Assert.Equal(question.ContentOrigin, pending.ContentOrigin);
        Assert.Contains("2026", pending.ExpiresAt, StringComparison.Ordinal);
        Assert.Contains(question.Question, pending.AccessibleName, StringComparison.Ordinal);
        Assert.Equal("Input needed", viewModel.StateLabel);
        Assert.Equal("Input needed", viewModel.ConnectionStatus);
        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.IsStreaming);
        Assert.True(viewModel.HasPendingQuestion);
        Assert.True(viewModel.HasAgentContent);
        Assert.True(viewModel.CanRespondToQuestion);
        Assert.True(viewModel.CanDeclineQuestion);
        Assert.False(viewModel.CanSubmitQuestionResponse);
        Assert.True(viewModel.CanEnterPrompt);
        Assert.True(viewModel.CanQueueFollowUp);
        Assert.True(viewModel.CanSubmitPrompt);
        Assert.False(viewModel.CanSend);
        Assert.True(viewModel.CanStop);
        Assert.True(viewModel.CanRequestStop);
        Assert.True(viewModel.NeedsProviderAttention);
        Assert.Equal("Keep this separate main prompt.", viewModel.Prompt);
    }

    [Fact]
    public async Task Submitted_question_answer_is_forwarded_and_cleared_when_accepted()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var question = Question();
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.AwaitingUserInput,
                runId: new AgentRunId("run-question"),
                providerId: provider.Id,
                target: Target(),
                pendingQuestion: question),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            QuestionResponseDraft = "Use the staging service.",
        };
        using var cancellation = new CancellationTokenSource();

        await viewModel.SubmitQuestionResponseAsync(cancellation.Token);

        Assert.Equal(1, runtime.QuestionResponseCount);
        Assert.Equal(question.Id, runtime.LastQuestionId);
        Assert.Equal(cancellation.Token, runtime.LastQuestionCancellationToken);
        var response = Assert.IsType<GovernedAgentQuestionResponse.Submitted>(
            runtime.LastQuestionResponse);
        Assert.Equal("Use the staging service.", response.Answer);
        Assert.Equal(string.Empty, viewModel.QuestionResponseDraft);
        Assert.Null(viewModel.PendingQuestion);
        Assert.False(viewModel.HasPendingQuestion);
    }

    [Fact]
    public async Task Invalid_or_rejected_question_answer_preserves_the_draft()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var question = Question();
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.AwaitingUserInput,
                runId: new AgentRunId("run-question"),
                providerId: provider.Id,
                target: Target(),
                pendingQuestion: question),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            QuestionResponseDraft = "first line\nsecond line",
        };

        await viewModel.SubmitQuestionResponseAsync(CancellationToken.None);

        Assert.Equal(0, runtime.QuestionResponseCount);
        Assert.Equal("first line\nsecond line", viewModel.QuestionResponseDraft);
        Assert.Contains("single-line", viewModel.Status);

        runtime.QuestionResponseResult = new GovernedAgentQuestionResponseResult(
            false,
            "question_response_pending",
            "A response is already being applied.");
        viewModel.QuestionResponseDraft = "Keep this valid answer.";

        await viewModel.SubmitQuestionResponseAsync(CancellationToken.None);

        Assert.Equal(1, runtime.QuestionResponseCount);
        Assert.Equal("Keep this valid answer.", viewModel.QuestionResponseDraft);
        Assert.NotNull(viewModel.PendingQuestion);
        Assert.Equal("A response is already being applied.", viewModel.Status);
    }

    [Fact]
    public async Task Declined_or_stale_question_clears_the_response_draft()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var question = Question();
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.AwaitingUserInput,
                runId: new AgentRunId("run-question"),
                providerId: provider.Id,
                target: Target(),
                pendingQuestion: question),
            QuestionResponseResult = new GovernedAgentQuestionResponseResult(
                true,
                "question_declined",
                "The question was skipped."),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            QuestionResponseDraft = "Do not submit this.",
        };

        await viewModel.DeclineQuestionAsync(CancellationToken.None);

        Assert.IsType<GovernedAgentQuestionResponse.Declined>(
            runtime.LastQuestionResponse);
        Assert.Equal(string.Empty, viewModel.QuestionResponseDraft);
        Assert.Null(viewModel.PendingQuestion);

        runtime.Snapshot = Snapshot(
            state: GovernedAgentState.AwaitingUserInput,
            runId: new AgentRunId("run-question"),
            providerId: provider.Id,
            target: Target(),
            pendingQuestion: question);
        runtime.QuestionResponseResult = new GovernedAgentQuestionResponseResult(
            false,
            "question_expired",
            "That agent question expired.");
        runtime.RaiseChanged();
        viewModel.QuestionResponseDraft = "A now-stale answer.";

        await viewModel.SubmitQuestionResponseAsync(CancellationToken.None);

        Assert.Equal(string.Empty, viewModel.QuestionResponseDraft);
        Assert.Null(viewModel.PendingQuestion);
        Assert.Equal("That agent question expired.", viewModel.Status);
    }

    [Fact]
    public void Question_card_is_assertive_single_line_and_keyboard_accessible()
    {
        XNamespace viewNamespace = "https://github.com/avaloniaui";
        XNamespace ControlsNamespace = "using:Asura.App.Controls";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var card = ApplicationViews
            .FindUniqueNamedElement("AgentPendingQuestion")
            .Element;

        Assert.Equal(ControlsNamespace + "SurfaceCard", card.Name);
        Assert.Equal("True", card.Attribute("Focusable")?.Value);
        Assert.Equal(
            "True",
            card.Attribute("KeyboardNavigation.IsTabStop")?.Value);
        Assert.Equal(
            "{Binding AgentChat.HasPendingQuestion, FallbackValue=False}",
            card.Attribute("IsVisible")?.Value);
        Assert.Null(card.Attribute("AutomationProperties.LiveSetting"));
        Assert.Equal(
            "{Binding AgentChat.PendingQuestion.AccessibleName}",
            card.Attribute("AutomationProperties.Name")?.Value);
        Assert.Null(card.Attribute("AutomationProperties.HelpText"));
        Assert.DoesNotContain(
            card.Descendants(),
            element => string.Equals(element.Name.LocalName, "Callout", StringComparison.Ordinal));
        Assert.Contains(
            card.Descendants(viewNamespace + "TextBlock"),
            element => string.Equals(
                element.Attribute("Text")?.Value,
                "{Binding AgentChat.PendingQuestion.Question}",
                StringComparison.Ordinal));

        var answer = Assert.Single(
            card.Descendants(viewNamespace + "TextBox"),
            element => string.Equals(
                element.Attribute(x + "Name")?.Value,
                "AgentQuestionResponseInput",
                StringComparison.Ordinal));
        Assert.Equal("False", answer.Attribute("AcceptsReturn")?.Value);
        Assert.Equal("2048", answer.Attribute("MaxLength")?.Value);
        Assert.Equal(
            "{Binding AgentChat.QuestionResponseDraft, Mode=TwoWay}",
            answer.Attribute("Text")?.Value);
        Assert.Equal(
            "{Binding AgentChat.CanRespondToQuestion}",
            answer.Attribute("IsEnabled")?.Value);
        Assert.Equal(
            "OnAgentQuestionResponseKeyDown",
            answer.Attribute("KeyDown")?.Value);
        Assert.Equal("0", answer.Attribute("TabIndex")?.Value);
        Assert.Null(answer.Attribute("IsFocused"));

        var buttons = card.Descendants(viewNamespace + "Button").ToArray();
        var actionGrid = Assert.IsType<XElement>(buttons[0].Parent);
        Assert.Same(actionGrid, buttons[1].Parent);
        Assert.Equal(
            "Auto,*",
            actionGrid.Attribute("ColumnDefinitions")?.Value);
        Assert.Collection(
            buttons,
            skip =>
            {
                Assert.Equal("Skip / decline", skip.Attribute("Content")?.Value);
                Assert.Equal(
                    "{Binding AgentChat.CanDeclineQuestion}",
                    skip.Attribute("IsEnabled")?.Value);
                Assert.Equal("1", skip.Attribute("TabIndex")?.Value);
                Assert.False(string.IsNullOrWhiteSpace(
                    skip.Attribute("AutomationProperties.Name")?.Value));
            },
            send =>
            {
                Assert.Equal("Send answer", send.Attribute("Content")?.Value);
                Assert.Equal(
                    "{Binding AgentChat.CanSubmitQuestionResponse}",
                    send.Attribute("IsEnabled")?.Value);
                Assert.Equal("2", send.Attribute("TabIndex")?.Value);
                Assert.False(string.IsNullOrWhiteSpace(
                    send.Attribute("AutomationProperties.Name")?.Value));
            });

        var theme = XDocument.Load(
            Path.Combine(
                ApplicationViews.RepositoryRoot,
                "src",
                "Asura.App",
                "Styles",
                "AsuraTheme.axaml"));
        // A visible focus ring is the card component's guarantee now, stated once
        // for every card rather than per style class. The theme file this used to
        // live in no longer decides what a card looks like.
        Assert.Contains(
            DesignSystem().Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "Style", StringComparison.Ordinal)),
            style => string.Equals(
                style.Attribute("Selector")?.Value,
                "^:focus-visible",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Pending_capability_request_is_trusted_and_keeps_the_queue_composer_available()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var request = CapabilityRequest();
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.AwaitingCapabilityDecision,
                runId: request.RunId,
                providerId: provider.Id,
                target: request.Target,
                status: "Waiting for a capability decision…",
                pendingCapabilityRequest: request),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Keep this separate main prompt.",
        };

        var pending = Assert.IsType<AgentCapabilityRequestCardViewModel>(
            viewModel.PendingCapabilityRequest);
        Assert.Equal(request.Id, pending.Id);
        Assert.Equal(request.DisplayTitle, pending.DisplayTitle);
        Assert.Equal(request.CapabilityToken, pending.CapabilityToken);
        Assert.Equal(request.TargetTitle, pending.TargetTitle);
        Assert.Contains("panel/panel-1", pending.ExactTarget);
        Assert.Equal(request.AffectedToolTitles, pending.AffectedToolTitles);
        Assert.Contains("2026", pending.ExpiresAt, StringComparison.Ordinal);
        Assert.Contains("Off to Ask for this run", pending.AccessibleName);
        Assert.Contains("grants no action", pending.GrantWarning);
        Assert.Contains("ordinary exact approval", pending.GrantWarning);

        Assert.Equal("Capability request", viewModel.StateLabel);
        Assert.Equal("Capability request", viewModel.ConnectionStatus);
        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.IsStreaming);
        Assert.True(viewModel.HasPendingCapabilityRequest);
        Assert.True(viewModel.HasAgentContent);
        Assert.True(viewModel.CanDecideCapabilityRequest);
        Assert.Null(viewModel.PendingQuestion);
        Assert.Null(viewModel.PendingApproval);
        Assert.False(viewModel.CanRespondToQuestion);
        Assert.False(viewModel.CanDecideApproval);
        Assert.True(viewModel.CanEnterPrompt);
        Assert.True(viewModel.CanQueueFollowUp);
        Assert.True(viewModel.CanSubmitPrompt);
        Assert.False(viewModel.CanSend);
        Assert.True(viewModel.CanStop);
        Assert.True(viewModel.CanRequestStop);
        Assert.True(viewModel.NeedsProviderAttention);
        Assert.Equal("Keep this separate main prompt.", viewModel.Prompt);
    }

    [Fact]
    public async Task Capability_allow_is_claimed_once_and_forwards_only_the_exact_request()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var request = CapabilityRequest();
        var pendingDecision =
            new TaskCompletionSource<GovernedAgentCapabilityDecisionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.AwaitingCapabilityDecision,
                runId: request.RunId,
                providerId: provider.Id,
                target: request.Target,
                pendingCapabilityRequest: request),
            PendingCapabilityDecision = pendingDecision,
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        using var cancellation = new CancellationTokenSource();

        var allow = viewModel.EnableCapabilityAskAsync(cancellation.Token);
        await WaitUntilAsync(() => runtime.CapabilityDecisionCount == 1);

        Assert.False(viewModel.CanDecideCapabilityRequest);
        await viewModel.KeepCapabilityOffAsync(CancellationToken.None);
        Assert.Equal(1, runtime.CapabilityDecisionCount);
        Assert.Equal(request.Id, runtime.LastCapabilityRequestId);
        Assert.IsType<GovernedAgentCapabilityDecision.AllowAsk>(
            runtime.LastCapabilityDecision);
        Assert.Equal(
            cancellation.Token,
            runtime.LastCapabilityDecisionCancellationToken);
        Assert.Equal(0, runtime.DecisionCount);
        Assert.Equal(0, runtime.QuestionResponseCount);

        pendingDecision.SetResult(
            new GovernedAgentCapabilityDecisionResult(
                true,
                "capability_request_allowed",
                "Ask is enabled for this run."));
        await allow;

        Assert.Null(viewModel.PendingCapabilityRequest);
        Assert.False(viewModel.HasPendingCapabilityRequest);
        Assert.False(viewModel.CanDecideCapabilityRequest);
    }

    [Fact]
    public async Task Keep_off_forwards_a_separate_capability_decision_and_grants_no_action()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var request = CapabilityRequest();
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.AwaitingCapabilityDecision,
                runId: request.RunId,
                providerId: provider.Id,
                target: request.Target,
                pendingCapabilityRequest: request),
            CapabilityDecisionResult =
                new GovernedAgentCapabilityDecisionResult(
                    true,
                    "capability_request_denied",
                    "The capability remains Off."),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        await viewModel.KeepCapabilityOffAsync(CancellationToken.None);

        Assert.Equal(1, runtime.CapabilityDecisionCount);
        Assert.Equal(request.Id, runtime.LastCapabilityRequestId);
        Assert.IsType<GovernedAgentCapabilityDecision.KeepOff>(
            runtime.LastCapabilityDecision);
        Assert.Equal(0, runtime.DecisionCount);
        Assert.Equal(0, runtime.QuestionResponseCount);
        Assert.Null(viewModel.PendingCapabilityRequest);
    }

    [Theory]
    [InlineData("capability_request_not_found")]
    [InlineData("capability_request_expired")]
    [InlineData("capability_request_cancelled")]
    [InlineData("capability_request_unavailable")]
    [InlineData("policy_changed")]
    [InlineData("target_changed")]
    public async Task Terminally_stale_capability_request_is_cleared(
        string resultCode)
    {
        var provider = Provider("provider", "Provider", order: 0);
        var request = CapabilityRequest();
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.AwaitingCapabilityDecision,
                runId: request.RunId,
                providerId: provider.Id,
                target: request.Target,
                pendingCapabilityRequest: request),
            CapabilityDecisionResult =
                new GovernedAgentCapabilityDecisionResult(
                    false,
                    resultCode,
                    "This capability request is no longer current."),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        await viewModel.EnableCapabilityAskAsync(CancellationToken.None);

        Assert.Null(viewModel.PendingCapabilityRequest);
        Assert.Equal(
            "This capability request is no longer current.",
            viewModel.Status);
    }

    [Fact]
    public async Task In_flight_capability_rejection_keeps_the_authenticated_card()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var request = CapabilityRequest();
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.AwaitingCapabilityDecision,
                runId: request.RunId,
                providerId: provider.Id,
                target: request.Target,
                pendingCapabilityRequest: request),
            CapabilityDecisionResult =
                new GovernedAgentCapabilityDecisionResult(
                    false,
                    "capability_request_decision_pending",
                    "A capability decision is already being applied."),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        await viewModel.EnableCapabilityAskAsync(CancellationToken.None);

        Assert.NotNull(viewModel.PendingCapabilityRequest);
        Assert.True(viewModel.CanDecideCapabilityRequest);
        Assert.Equal(
            "A capability decision is already being applied.",
            viewModel.Status);
    }

    [Fact]
    public void Capability_request_card_uses_trusted_bindings_and_does_not_take_focus()
    {
        XNamespace viewNamespace = "https://github.com/avaloniaui";
        XNamespace ControlsNamespace = "using:Asura.App.Controls";
        var card = ApplicationViews
            .FindUniqueNamedElement("AgentPendingCapabilityRequest")
            .Element;

        Assert.Equal(ControlsNamespace + "SurfaceCard", card.Name);
        Assert.Equal("True", card.Attribute("Focusable")?.Value);
        Assert.Equal(
            "True",
            card.Attribute("KeyboardNavigation.IsTabStop")?.Value);
        Assert.Equal(
            "{Binding AgentChat.HasPendingCapabilityRequest, FallbackValue=False}",
            card.Attribute("IsVisible")?.Value);
        Assert.Null(card.Attribute("AutomationProperties.LiveSetting"));
        Assert.Equal(
            "{Binding AgentChat.PendingCapabilityRequest.AccessibleName}",
            card.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal(
            "{Binding AgentChat.PendingCapabilityRequest.GrantWarning}",
            card.Attribute("AutomationProperties.HelpText")?.Value);
        Assert.Null(card.Attribute("IsFocused"));

        var serializedCard = card.ToString();
        Assert.Contains(
            "{Binding AgentChat.PendingCapabilityRequest.DisplayTitle}",
            serializedCard);
        Assert.Contains(
            "{Binding AgentChat.PendingCapabilityRequest.AffectedToolTitles}",
            serializedCard);
        Assert.Contains(
            "{Binding AgentChat.PendingCapabilityRequest.ExactTarget}",
            serializedCard);
        Assert.Contains(
            "{Binding AgentChat.PendingCapabilityRequest.TargetTitle}",
            serializedCard);
        Assert.Contains(
            "{Binding AgentChat.PendingCapabilityRequest.GrantWarning}",
            serializedCard);
        Assert.DoesNotContain("PendingQuestion", serializedCard);
        Assert.DoesNotContain("PendingApproval", serializedCard);

        var buttons = card.Descendants(viewNamespace + "Button").ToArray();
        Assert.Collection(
            buttons,
            keepOff =>
            {
                Assert.Equal("Keep Off", keepOff.Attribute("Content")?.Value);
                Assert.Equal(
                    "OnKeepAgentCapabilityOffClick",
                    keepOff.Attribute("Click")?.Value);
                Assert.Equal(
                    "{Binding AgentChat.CanDecideCapabilityRequest}",
                    keepOff.Attribute("IsEnabled")?.Value);
                Assert.Equal("0", keepOff.Attribute("TabIndex")?.Value);
            },
            enableAsk =>
            {
                Assert.Equal(
                    "Enable Ask for this run",
                    enableAsk.Attribute("Content")?.Value);
                Assert.Equal(
                    "OnEnableAgentCapabilityAskClick",
                    enableAsk.Attribute("Click")?.Value);
                Assert.Equal(
                    "{Binding AgentChat.CanDecideCapabilityRequest}",
                    enableAsk.Attribute("IsEnabled")?.Value);
                Assert.Equal("1", enableAsk.Attribute("TabIndex")?.Value);
            });
    }

    [Fact]
    public async Task Initial_provider_stream_keeps_send_and_stop_and_queues_steering()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var runId = new AgentRunId("run-steering");
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.StreamingProvider,
                runId: runId,
                providerId: provider.Id,
                target: Target(),
                messages:
                [
                    new AgentChatMessage(
                        AgentChatMessageRole.User,
                        "Inspect the deployment."),
                ],
                provisional: "I will inspect",
                steeringAvailable: true,
                steeringGeneration: 7),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        using var cancellation = new CancellationTokenSource();

        Assert.True(viewModel.CanEnterPrompt);
        Assert.False(viewModel.CanSubmitPrompt);
        Assert.True(viewModel.CanShowPrimaryAction);
        Assert.True(viewModel.ShowStopAction);
        Assert.Equal("Queue message", viewModel.PrimaryActionLabel);
        Assert.Equal(
            "Queue a message for the AI agent",
            viewModel.PrimaryActionAccessibleName);
        Assert.Equal("Ask Asura…", viewModel.PromptPlaceholder);

        viewModel.Prompt = "Check the canary before production.";

        Assert.True(viewModel.CanSubmitPrompt);
        Assert.True(viewModel.CanShowPrimaryAction);
        Assert.True(viewModel.ShowStopAction);

        await viewModel.QueueSteeringAsync(cancellation.Token);

        var steering = Assert.IsType<GovernedAgentFollowUp>(
            runtime.LastFollowUp);
        Assert.Equal(
            "Check the canary before production.",
            steering.Message);
        Assert.Equal(
            GovernedAgentFollowUpDelivery.Steering,
            steering.Delivery);
        Assert.Equal(
            cancellation.Token,
            runtime.LastFollowUpCancellationToken);
        Assert.Equal(1, runtime.FollowUpCount);
        Assert.Equal(0, runtime.SendCount);
        Assert.Equal(string.Empty, viewModel.Prompt);
    }

    [Fact]
    public async Task Rejected_queued_steering_restores_draft_and_blocks_duplicate_submission()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var pending = new TaskCompletionSource<GovernedAgentFollowUpResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.StreamingProvider,
                runId: new AgentRunId("run-steering"),
                providerId: provider.Id,
                target: Target(),
                steeringAvailable: true,
                steeringGeneration: 11),
            PendingFollowUp = pending,
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Preserve this steering update.",
        };

        var steering = viewModel.QueueSteeringAsync(CancellationToken.None);
        await viewModel.QueueSteeringAsync(CancellationToken.None);

        Assert.Equal(1, runtime.FollowUpCount);
        Assert.True(viewModel.CanEnterPrompt);
        Assert.False(viewModel.CanQueueFollowUp);
        Assert.Equal(string.Empty, viewModel.Prompt);

        pending.SetResult(
            new GovernedAgentFollowUpResult(
                false,
                "agent_steering_unavailable",
                "The active generation can no longer be steered.",
                0));
        await steering;

        Assert.Equal("Preserve this steering update.", viewModel.Prompt);
        Assert.Equal(
            "The active generation can no longer be steered.",
            viewModel.Status);
        Assert.True(viewModel.CanQueueFollowUp);
    }

    [Theory]
    [InlineData(GovernedAgentState.AwaitingUserInput)]
    [InlineData(GovernedAgentState.AwaitingCapabilityDecision)]
    [InlineData(GovernedAgentState.AwaitingApproval)]
    public void Authority_bearing_and_user_decision_states_accept_queued_messages(
        GovernedAgentState state)
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: state,
                runId: new AgentRunId("run-non-steerable"),
                providerId: provider.Id,
                target: Target(),
                steeringAvailable: true,
                steeringGeneration: 13),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Do not reinterpret this as a decision.",
        };

        Assert.True(viewModel.CanSubmitPrompt);
        Assert.True(viewModel.CanEnterPrompt);
        Assert.Equal("Queue message", viewModel.PrimaryActionLabel);
    }

    [Fact]
    public void Running_tool_offers_a_separate_follow_up_queue_not_steering()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.RunningTool,
                runId: new AgentRunId("run-follow-up"),
                providerId: provider.Id,
                target: Target()),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Continue with this after the tool finishes.",
        };

        Assert.True(viewModel.CanOfferFollowUpQueue);
        Assert.True(viewModel.CanQueueFollowUp);
        Assert.True(viewModel.CanEnterPrompt);
        Assert.True(viewModel.CanSubmitPrompt);
    }

    [Fact]
    public void Tool_continuation_stream_does_not_offer_steering()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.StreamingProvider,
                runId: new AgentRunId("run-tool-continuation"),
                providerId: provider.Id,
                target: Target(),
                steeringAvailable: false),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "This must remain unavailable.",
        };

        Assert.True(viewModel.CanSubmitPrompt);
        Assert.True(viewModel.CanEnterPrompt);
        Assert.True(viewModel.CanOfferFollowUpQueue);
        Assert.True(viewModel.CanShowPrimaryAction);
    }

    [Fact]
    public void Agent_composer_binds_dynamic_steering_copy_and_primary_action()
    {
        var viewNamespace = (XNamespace)"https://github.com/avaloniaui";
        var ownedInput = ApplicationViews.FindUniqueNamedElement(
            "AgentChatPromptInput");
        var document = ownedInput.Owner.Document;
        var input = ownedInput.Element;

        Assert.Equal(viewNamespace + "TextBox", input.Name);
        Assert.Equal(
            "{Binding AgentChat.PromptPlaceholder}",
            input.Attribute("PlaceholderText")?.Value);
        Assert.Equal(
            "{Binding AgentChat.CanEnterPrompt}",
            input.Attribute("IsEnabled")?.Value);
        Assert.Equal(
            "{Binding !AgentChat.HasFailedTurn}",
            input.Attribute("IsVisible")?.Value);
        var composer = Assert.Single(
            input.Ancestors(viewNamespace + "StackPanel"),
            panel => string.Equals(
                panel.Attribute("Grid.Row")?.Value,
                "3",
                StringComparison.Ordinal));
        Assert.Contains(
            composer.Elements(viewNamespace + "Border"),
            border => string.Equals(border.Attribute("IsVisible")?.Value
, "{Binding AgentChat.HasProvider, FallbackValue=False}", StringComparison.Ordinal));

        var action = Assert.Single(
            document.Descendants(viewNamespace + "Button"),
            element => string.Equals(
                element.Attribute("Click")?.Value,
                "OnSendAgentChatClick",
                StringComparison.Ordinal));
        Assert.Equal(
            "ArrowUp",
            Assert.Single(action.Elements(), element => string.Equals(element.Name.LocalName, "SymbolIcon", StringComparison.Ordinal))
                .Attribute("Symbol")?.Value);
        Assert.Equal(
            "{Binding AgentChat.CanSubmitPrompt}",
            action.Attribute("IsEnabled")?.Value);
        Assert.Equal(
            "{Binding AgentChat.ShowPrimaryAction}",
            action.Attribute("IsVisible")?.Value);
        Assert.Equal(
            "{Binding AgentChat.PrimaryActionAccessibleName}",
            action.Attribute("AutomationProperties.Name")?.Value);

        var stop = Assert.Single(
            document.Descendants(viewNamespace + "Button"),
            element => string.Equals(
                element.Attribute("Click")?.Value,
                "OnCancelAgentChatClick",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding AgentChat.ShowStopAction, FallbackValue=False}",
            stop.Attribute("IsVisible")?.Value);
        Assert.DoesNotContain(
            stop.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_forwards_provider_prompt_and_exact_panel_target()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var target = Target();
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Inspect the active service.",
        };
        using var cancellation = new CancellationTokenSource();

        await viewModel.SendAsync(target, Policy(provider), cancellation.Token);

        var request = Assert.IsType<GovernedAgentPrompt>(runtime.LastRequest);
        Assert.Equal(provider.Id, request.ProviderId);
        Assert.Equal("Inspect the active service.", request.Message);
        Assert.Equal(target, request.Target);
        Assert.Equal("model", Assert.IsType<AgentPolicy>(request.Policy).Model);
        Assert.Equal(cancellation.Token, runtime.LastCancellationToken);
        Assert.Equal(string.Empty, viewModel.Prompt);
        Assert.Equal(1, runtime.SendCount);
    }

    [Fact]
    public async Task New_run_preserves_conversation_maintenance_policy()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var policy = new AgentPolicy(
            provider.Id.Value,
            provider.DefaultModel,
            AgentPolicy.Default.Permissions)
        {
            CompactionModel = new AgentModelSelection("provider", "compact-model"),
            TitleModel = new AgentModelSelection("provider", "title-model"),
            SystemPrompt = "Workspace instructions.",
        };
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(effectivePolicy: policy),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Inspect the workspace.",
        };

        await viewModel.SendAsync(Target(), policy, CancellationToken.None);

        var requestedPolicy = Assert.IsType<AgentPolicy>(runtime.LastRequest?.Policy);
        Assert.Equal(policy.CompactionModel, requestedPolicy.CompactionModel);
        Assert.Equal(policy.TitleModel, requestedPolicy.TitleModel);
        Assert.Equal(policy.SystemPrompt, requestedPolicy.SystemPrompt);
    }

    [Fact]
    public async Task New_run_never_changes_the_explicit_compaction_route()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var configuredPolicy = AgentPolicyResolver.Resolve(AgentPolicy.Default);
        Assert.Equal(
            new AgentModelSelection(AgentPolicy.Default.Provider, AgentPolicy.Default.Model),
            configuredPolicy.CompactionModel);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(effectivePolicy: configuredPolicy),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Inspect the workspace.",
        };

        await viewModel.SendAsync(
            Target(),
            configuredPolicy.SelectPrimaryModel(
                provider.Id.Value,
                provider.DefaultModel),
            CancellationToken.None);

        var requestedPolicy = Assert.IsType<AgentPolicy>(runtime.LastRequest?.Policy);
        Assert.Equal(provider.Id.Value, requestedPolicy.Provider);
        Assert.Equal(provider.DefaultModel, requestedPolicy.Model);
        Assert.Equal(configuredPolicy.CompactionModel, requestedPolicy.CompactionModel);
    }

    [Fact]
    public void Idle_conversation_shows_send_without_an_overlapping_stop_action()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.Ready,
                runId: new AgentRunId("run-idle"),
                providerId: provider.Id,
                target: Target(),
                messages:
                [
                    new AgentChatMessage(AgentChatMessageRole.User, "Hello."),
                    new AgentChatMessage(AgentChatMessageRole.Assistant, "Hello!"),
                ]),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Tell me a story.",
        };

        Assert.True(viewModel.CanEnterPrompt);
        Assert.True(viewModel.CanSend);
        Assert.True(viewModel.ShowPrimaryAction);
        Assert.False(viewModel.CanStop);
        Assert.False(viewModel.ShowStopAction);
    }

    [Fact]
    public async Task Stopped_conversation_is_editable_and_restarts_on_the_next_message()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.Cancelled,
                runId: new AgentRunId("run-stopped"),
                providerId: provider.Id,
                target: Target(),
                messages:
                [
                    new AgentChatMessage(AgentChatMessageRole.User, "Hello."),
                    new AgentChatMessage(AgentChatMessageRole.Assistant, "Hello!"),
                ]),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Tell me a story.",
        };

        Assert.True(viewModel.CanEnterPrompt);
        Assert.True(viewModel.CanSend);
        Assert.True(viewModel.ShowPrimaryAction);
        Assert.False(viewModel.CanStop);
        Assert.False(viewModel.ShowStopAction);

        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        var request = Assert.IsType<GovernedAgentPrompt>(runtime.LastRequest);
        Assert.Equal("Tell me a story.", request.Message);
        Assert.NotNull(request.Policy);
        Assert.Equal(AgentApprovalMode.Ask, request.ApprovalMode);
        Assert.Equal(1, runtime.SendCount);
    }

    [Fact]
    public async Task Model_selector_forwards_the_exact_selected_model_for_a_new_run()
    {
        var provider = Provider(
            "provider",
            "Provider",
            order: 0,
            models:
            [
                new AiProviderModelDescriptor("model", "Default model"),
                new AiProviderModelDescriptor("model-fast", "Fast model"),
            ]);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Inspect the workspace.",
        };
        await viewModel.SelectModelAsync(
            viewModel.Models.Single(model => string.Equals(model.Id, "model-fast", StringComparison.Ordinal)),
            CancellationToken.None);

        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        Assert.False(viewModel.HasMultipleProviders);
        Assert.True(viewModel.HasMultipleModels);
        var request = Assert.IsType<GovernedAgentPrompt>(runtime.LastRequest);
        Assert.Equal(
            "model-fast",
            Assert.IsType<AgentPolicy>(request.Policy).Model);
    }

    [Fact]
    public void Model_search_filters_discovered_models_by_name_and_identifier()
    {
        var provider = Provider(
            "provider",
            "Provider",
            order: 0,
            models:
            [
                new AiProviderModelDescriptor("model", "Default model"),
                new AiProviderModelDescriptor("model-fast", "Fast model"),
                new AiProviderModelDescriptor("reasoning-pro", "Reasoning Pro"),
            ]);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        viewModel.ModelSearch = "fast";
        Assert.Equal("model-fast", Assert.Single(viewModel.FilteredModels).Id);

        viewModel.ModelSearch = "reasoning-pro";
        Assert.Equal("Reasoning Pro", Assert.Single(viewModel.FilteredModels).DisplayName);
    }

    [Fact]
    public async Task Favorite_models_sort_first_and_identify_the_configured_provider()
    {
        var provider = Provider(
            "provider",
            "Production OpenAI",
            order: 0,
            models:
            [
                new AiProviderModelDescriptor("model", "Default model"),
                new AiProviderModelDescriptor("model-fast", "Fast model"),
                new AiProviderModelDescriptor("reasoning-pro", "Reasoning Pro"),
            ]);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        var fastModel = viewModel.FilteredModels.Single(item => string.Equals(item.Id, "model-fast", StringComparison.Ordinal));
        await viewModel.ToggleFavoriteModelAsync(fastModel, CancellationToken.None);

        Assert.Equal("model-fast", viewModel.FilteredModels[0].Id);
        Assert.True(viewModel.FilteredModels[0].IsFavorite);
        Assert.All(
            viewModel.FilteredModels,
            item => Assert.Equal("Production OpenAI", item.ProviderName));

        await viewModel.ToggleFavoriteModelAsync(
            viewModel.FilteredModels[0],
            CancellationToken.None);

        Assert.Equal(
            ["model", "model-fast", "reasoning-pro"],
            viewModel.FilteredModels.Select(item => item.Id), StringComparer.Ordinal);
        Assert.DoesNotContain(viewModel.FilteredModels, item => item.IsFavorite);
    }

    [Fact]
    public void Conversation_search_filters_titles_and_models_without_losing_details()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var currentRun = new AgentRunId("run-current");
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: currentRun,
                providerId: provider.Id,
                conversations:
                [
                    new GovernedAgentConversationSummary(
                        currentRun,
                        "Plan the release",
                        provider.Id,
                        "gpt-5.6-terra",
                        4,
                        new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.Zero)),
                    new GovernedAgentConversationSummary(
                        new AgentRunId("run-second"),
                        "Review database migrations",
                        provider.Id,
                        "gpt-5.6-sol",
                        6,
                        new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero)),
                ]),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        viewModel.ConversationSearch = "database";

        var byTitle = Assert.Single(viewModel.FilteredConversations);
        Assert.Equal("Review database migrations", byTitle.Title);
        Assert.Contains("gpt-5.6-sol", byTitle.Details, StringComparison.Ordinal);

        viewModel.ConversationSearch = "terra";

        var byModel = Assert.Single(viewModel.FilteredConversations);
        Assert.Equal("Plan the release", byModel.Title);
        Assert.True(byModel.IsCurrent);

        viewModel.ConversationSearch = "no-match";
        Assert.True(viewModel.HasNoConversationMatches);
    }

    [Fact]
    public async Task SelectingAnotherModelPreservesTheCurrentConversation()
    {
        var provider = Provider(
            "provider",
            "Provider",
            order: 0,
            models:
            [
                new AiProviderModelDescriptor("model", "Default model"),
                new AiProviderModelDescriptor("model-fast", "Fast model"),
            ]);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.Ready,
                messages:
                [
                    new AgentChatMessage(AgentChatMessageRole.User, "Hello"),
                    new AgentChatMessage(AgentChatMessageRole.Assistant, "Hi"),
                ],
                providerId: provider.Id,
                effectivePolicy: new AgentPolicy(
                    provider.Id.Value,
                    "model",
                    AgentPolicy.Default.Permissions)
                {
                    CompactionModel = new AgentModelSelection(provider.Id.Value, "model"),
                    TitleModel = new AgentModelSelection(provider.Id.Value, "model"),
                }),
            SnapshotOnClear = Snapshot(),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        var selected = viewModel.Models.Single(model => string.Equals(model.Id, "model-fast", StringComparison.Ordinal));

        await viewModel.SelectModelAsync(selected, CancellationToken.None);

        Assert.Equal(0, runtime.ClearCount);
        Assert.Equal(["Hello", "Hi"], viewModel.Messages.Select(message => message.Content), StringComparer.Ordinal);
        Assert.Equal("model-fast", viewModel.SelectedModel?.Id);

        viewModel.Prompt = "Continue with the faster model.";
        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        Assert.Equal("model-fast", runtime.LastRequest?.Model);
    }

    [Fact]
    public async Task Image_capable_provider_sends_a_bounded_image_only_prompt()
    {
        var provider = Provider(
            "provider",
            "Provider",
            order: 0,
            supportsImageInput: true);
        var image = new AgentImageAttachment(
            "screen.png",
            "image/png",
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.CanAttachImages);
        viewModel.AddPendingImage(image);
        Assert.True(viewModel.CanSend);

        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        var request = Assert.IsType<GovernedAgentPrompt>(runtime.LastRequest);
        Assert.Equal(string.Empty, request.Message);
        Assert.Same(image, Assert.Single(request.Images));
        Assert.Empty(viewModel.PendingImages);
        Assert.False(viewModel.HasPendingImages);
    }

    [Fact]
    public async Task Non_image_provider_rejects_pending_images_before_runtime()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        viewModel.AddPendingImage(
            new AgentImageAttachment(
                "screen.png",
                "image/png",
                [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));

        Assert.False(viewModel.CanAttachImages);
        Assert.False(viewModel.CanSend);

        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        Assert.Equal(0, runtime.SendCount);
        Assert.Contains(
            "does not support image input",
            viewModel.Status,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(viewModel.PendingImages);
    }

    [Fact]
    public async Task ExplicitPolicySelectsItsExactProfileAndForwardsExactModel()
    {
        var initiallySelected = Provider("initial", "Initial", order: 0);
        var provider = Provider("saved-provider", "Saved provider", order: 1);
        var policy = AgentPolicyResolver.Resolve(
            AgentPolicy.Default,
            screen: new AgentPolicy(
                provider.Id.Value,
                "saved-model",
                AgentPolicy.Capabilities.ToImmutableDictionary(
                    capability => capability,
                    capability => capability == AgentCapability.RunCommands
                        ? AgentPermission.Off
                        : AgentPermission.Auto))
            {
                CompactionModel = new AgentModelSelection(provider.Id.Value, "saved-model"),
                TitleModel = new AgentModelSelection(provider.Id.Value, "saved-model"),
            });
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime
        {
            Profiles = [initiallySelected, provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Inspect under the saved policy.",
        };

        await viewModel.SendAsync(Target(), policy, CancellationToken.None);

        var request = Assert.IsType<GovernedAgentPrompt>(runtime.LastRequest);
        var forwardedPolicy = Assert.IsType<AgentPolicy>(request.Policy);
        Assert.Equal(policy.Provider, forwardedPolicy.Provider);
        Assert.Equal(policy.Model, forwardedPolicy.Model);
        Assert.All(
            AgentPolicy.Capabilities,
            capability => Assert.Equal(
                policy.GetPermission(capability),
                forwardedPolicy.GetPermission(capability)));
        Assert.Equal(provider.Id, request.ProviderId);
        Assert.Equal(provider.Id, viewModel.SelectedProvider?.Id);
    }

    [Fact]
    public async Task ExplicitPolicyWithUnavailableProfileFailsBeforeRuntime()
    {
        var provider = Provider("available", "Available", order: 0);
        var policy = AgentPolicy.Default with
        {
            Provider = "missing-provider",
            Model = "saved-model",
        };
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Keep this trusted prompt.",
        };

        await viewModel.SendAsync(Target(), policy, CancellationToken.None);

        Assert.Equal(0, runtime.SendCount);
        Assert.Null(runtime.LastRequest);
        Assert.Equal("Keep this trusted prompt.", viewModel.Prompt);
        Assert.Contains(
            "unavailable AI-provider profile",
            viewModel.Status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_send_restores_prompt_and_projects_safe_failure_message()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            SendResult = new GovernedAgentSendResult(
                false,
                "agent_provider_unavailable",
                "The provider is unavailable."),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Keep this prompt.",
        };

        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        Assert.Equal("Keep this prompt.", viewModel.Prompt);
        Assert.Equal("The provider is unavailable.", viewModel.Status);
        Assert.Equal(1, runtime.SendCount);
    }

    [Fact]
    public void Failed_empty_run_exposes_one_recovery_surface()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                GovernedAgentState.Failed,
                new AgentRunId("failed-run"),
                provider.Id,
                Target(),
                status: "The configured model is unavailable.",
                effectivePolicy: AgentPolicy.Default with
                {
                    Provider = provider.Id.Value,
                    Model = "model",
                }),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.HasFailedTurn);
        Assert.False(viewModel.ShowPrimaryAction);
        Assert.False(viewModel.ShowFooterStatus);
        Assert.False(viewModel.CanStop);
        Assert.True(viewModel.CanClear);
    }

    [Fact]
    public async Task FailureAfterCommittedPromptRestoresOnlyOrderedFollowUps()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            SendResult = new GovernedAgentSendResult(
                false,
                "agent_provider_failed",
                "The follow-up failed safely.",
                InitialPromptCommitted: true,
                RecoverableFollowUps:
                [
                    new GovernedAgentFollowUp("First preserved follow-up."),
                    new GovernedAgentFollowUp(
                        "Second preserved follow-up.",
                        AgentReasoningEffort.High),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "This initial prompt was already committed.",
        };

        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        Assert.Equal(
            "First preserved follow-up."
            + Environment.NewLine
            + Environment.NewLine
            + "Second preserved follow-up.",
            viewModel.Prompt);
        Assert.DoesNotContain("already committed", viewModel.Prompt);
        Assert.Equal("The follow-up failed safely.", viewModel.Status);
    }

    [Fact]
    public async Task FailureAfterCommittedPromptDoesNotRestoreCommittedImages()
    {
        var provider = Provider(
            "provider",
            "Provider",
            order: 0,
            supportsImageInput: true);
        var image = new AgentImageAttachment(
            "screen.png",
            "image/png",
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        using var runtime = new StubGovernedRuntime
        {
            SendResult = new GovernedAgentSendResult(
                false,
                "agent_tool_failed",
                "The tool continuation failed safely.",
                InitialPromptCommitted: true),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Inspect this screenshot.",
        };
        viewModel.AddPendingImage(image);

        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        Assert.Empty(viewModel.PendingImages);
        Assert.Equal(string.Empty, viewModel.Prompt);
        Assert.Equal("The tool continuation failed safely.", viewModel.Status);
    }

    [Fact]
    public void Runtime_change_projects_transcript_capability_and_material_approval()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var target = Target();
        var approvalId = new AgentApprovalId("approval-1");
        var expiry = new DateTimeOffset(2026, 7, 24, 12, 30, 0, TimeSpan.Zero);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        runtime.Snapshot = Snapshot(
            state: GovernedAgentState.AwaitingApproval,
            runId: new AgentRunId("run-1"),
            providerId: provider.Id,
            target: target,
            messages: [new AgentChatMessage(AgentChatMessageRole.User, "Restart the API")],
            provisional: "I need approval before continuing.",
            status: "Waiting for approval.",
            pendingApproval: new GovernedAgentApproval(
                approvalId,
                "terminal.send_text",
                "Send text to terminal",
                AgentActionRisk.Mutation,
                AgentPermission.Ask,
                target,
                new AgentApprovalPresentation(
                    "Production API shell",
                    "api.example.test",
                    "/srv/api",
                    [
                        new AgentApprovalArgument(
                            "text",
                            "sudo systemctl restart api"),
                        new AgentApprovalArgument(
                            "credential",
                            "must-not-appear",
                            isSensitive: true),
                    ]),
                expiry,
                TemporarilyYieldsTerminalInput: true),
            terminalMutationAvailable: true,
            capabilityNotice:
                "Governed terminal input is available. Human input preempts the agent.",
            connectionBoundary: "SSH · api.example.test",
            workingDirectory: "/srv/api");
        runtime.RaiseChanged();

        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.CanStop);
        Assert.True(viewModel.CanRequestStop);
        Assert.Equal("Approval", viewModel.ConnectionStatus);
        Assert.Equal("Terminal access", viewModel.CapabilityLabel);
        Assert.True(viewModel.TerminalMutationAvailable);
        Assert.Equal("SSH · api.example.test", viewModel.ConnectionBoundary);
        Assert.Equal("/srv/api", viewModel.WorkingDirectory);
        Assert.Equal(
            "SSH · api.example.test · /srv/api",
            viewModel.TargetContextLabel);
        Assert.True(viewModel.HasTargetContext);
        Assert.True(viewModel.HasConversation);
        Assert.True(viewModel.HasProvisionalAssistantText);
        Assert.Equal("I need approval before continuing.", viewModel.ProvisionalAssistantText);
        Assert.Equal("Restart the API", Assert.Single(viewModel.Messages).Content);

        var approval = Assert.IsType<AgentApprovalCardViewModel>(viewModel.PendingApproval);
        Assert.Equal(approvalId, approval.Id);
        Assert.Equal("terminal.send_text", approval.ToolName);
        Assert.Equal("Send text to terminal", approval.ToolTitle);
        Assert.Equal("Mutation", approval.Risk);
        Assert.Equal("Ask", approval.Permission);
        Assert.Equal("Production API shell", approval.TargetTitle);
        Assert.Contains("window/window-1", approval.ExactTarget);
        Assert.Contains("workspace/workspace-1", approval.ExactTarget);
        Assert.Contains("tab/tab-1", approval.ExactTarget);
        Assert.Contains("panel/panel-1", approval.ExactTarget);
        Assert.Equal("api.example.test", approval.Host);
        Assert.Equal("/srv/api", approval.WorkingDirectory);
        Assert.Equal(2, approval.Arguments.Count);
        Assert.Equal("sudo systemctl restart api", approval.Arguments[0].DisplayValue);
        Assert.Equal("<secret reference>", approval.Arguments[1].DisplayValue);
        Assert.True(approval.Arguments[1].IsSensitive);
        Assert.True(approval.TemporarilyYieldsTerminalInput);
        Assert.Contains("physical input preempts", approval.InputYieldWarning);
        Assert.Contains("2026", approval.ExpiresAt);
        Assert.True(viewModel.CanDecideApproval);
    }

    [Fact]
    public void RuntimeChangeProjectsTheCompleteEffectivePolicy()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var policy = new AgentPolicy(
            "Saved provider",
            "saved-model",
            AgentPolicy.Capabilities.ToImmutableDictionary(
                capability => capability,
                capability => capability switch
                {
                    AgentCapability.RunCommands => AgentPermission.Off,
                    AgentCapability.ReadFiles => AgentPermission.Auto,
                    _ => AgentPermission.Ask,
                }))
        {
            CompactionModel = new AgentModelSelection("Saved provider", "compact-model"),
            TitleModel = new AgentModelSelection("Saved provider", "title-model"),
        };
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        runtime.Snapshot = Snapshot(effectivePolicy: policy);
        runtime.RaiseChanged();

        Assert.Equal("Saved provider", viewModel.EffectivePolicyProvider);
        Assert.Equal("saved-model", viewModel.EffectivePolicyModel);
        Assert.Equal(
            "Saved provider · saved-model",
            viewModel.EffectivePolicySummary);
        Assert.Equal(
            AgentPolicy.Capabilities.Length,
            viewModel.EffectivePolicyCapabilities.Count);
        Assert.Contains(
            viewModel.EffectivePolicyCapabilities,
            item => string.Equals(item.Capability, "Run commands"
, StringComparison.Ordinal) && string.Equals(item.Permission, "Off", StringComparison.Ordinal));
        Assert.Contains(
            viewModel.EffectivePolicyCapabilities,
            item => string.Equals(item.Capability, "Read files"
, StringComparison.Ordinal) && string.Equals(item.Permission, "Auto", StringComparison.Ordinal));
    }

    [Fact]
    public void Browser_approval_does_not_project_temporary_terminal_input_yield()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var target = Target();
        var expiry = new DateTimeOffset(2026, 7, 24, 12, 30, 0, TimeSpan.Zero);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        runtime.Snapshot = Snapshot(
            state: GovernedAgentState.AwaitingApproval,
            runId: new AgentRunId("browser-run-1"),
            providerId: provider.Id,
            target: target,
            pendingApproval: new GovernedAgentApproval(
                new AgentApprovalId("browser-approval-1"),
                BuiltInAgentTools.BrowserNavigate,
                "Navigate browser",
                AgentActionRisk.Mutation,
                AgentPermission.Ask,
                target,
                new AgentApprovalPresentation(
                    "Runbook — panel panel-1 — session browser-session-1",
                    "Embedded browser",
                    workingDirectory: null,
                    [
                        new AgentApprovalArgument(
                            "address",
                            "https://docs.example.test/runbook"),
                    ]),
                expiry,
                TemporarilyYieldsTerminalInput: false),
            contextItems:
            [
                ContextItem(
                    panelId: "panel-1",
                    sessionId: "browser-session-1",
                    panelTitle: "Runbook",
                    tabTitle: "Documentation",
                    isVisible: true,
                    isFocused: true,
                    hasActiveWork: false,
                    operations:
                    [
                        BuiltInAgentTools.BrowserReadState,
                        BuiltInAgentTools.BrowserNavigate,
                    ],
                    kind: PanelKind.Browser),
            ]);
        runtime.RaiseChanged();

        var approval = Assert.IsType<AgentApprovalCardViewModel>(
            viewModel.PendingApproval);
        Assert.Equal(BuiltInAgentTools.BrowserNavigate, approval.ToolName);
        Assert.False(approval.TemporarilyYieldsTerminalInput);
        Assert.Equal("Embedded browser", approval.Host);
        Assert.Equal("Not reported", approval.WorkingDirectory);
        Assert.Equal(
            "https://docs.example.test/runbook",
            Assert.Single(approval.Arguments).DisplayValue);
    }

    [Fact]
    public void Host_verified_mutation_capability_is_projected_without_renderer_heuristics()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                providerId: provider.Id,
                target: Target(),
                terminalMutationAvailable: true,
                capabilityNotice: null),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };

        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.TerminalMutationAvailable);
        Assert.Equal("Terminal access", viewModel.CapabilityLabel);
        Assert.Equal("Terminal tools are available.", viewModel.CapabilityNotice);
    }

    [Fact]
    public void Runtime_context_items_are_inspectable_and_clear_collapses_the_card()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: new AgentRunId("run-context"),
                providerId: provider.Id,
                target: Target(),
                contextItems:
                [
                    ContextItem(
                        panelId: "panel-1",
                        sessionId: "session-1",
                        panelTitle: "Production shell",
                        tabTitle: "Operations",
                        isVisible: true,
                        isFocused: true,
                        hasActiveWork: true,
                        operations:
                        [
                            BuiltInAgentTools.TerminalReadScreen,
                            BuiltInAgentTools.TerminalSendKeys,
                        ]),
                    ContextItem(
                        panelId: "panel-2",
                        sessionId: "session-2",
                        panelTitle: null,
                        tabTitle: null,
                        isVisible: false,
                        isFocused: false,
                        hasActiveWork: false,
                        operations: [BuiltInAgentTools.TerminalReadScreen]),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.HasContextItems);
        Assert.Equal("2 terminals", viewModel.ContextInspectorSummary);
        Assert.Contains(
            "2 terminals",
            viewModel.ContextInspectorAccessibleName,
            StringComparison.Ordinal);
        Assert.Collection(
            viewModel.ContextItems,
            first =>
            {
                Assert.Equal("Production shell", first.Title);
                Assert.Equal("Operations", first.TabTitle);
                Assert.Contains("panel/panel-1", first.ExactIdentity);
                Assert.Contains("session/session-1", first.ExactIdentity);
                Assert.Equal("SSH · api.example.test · /srv/api", first.Context);
                Assert.Contains("Focused · Active · Healthy", first.State);
                Assert.Contains("active work", first.State);
                Assert.Contains(
                    BuiltInAgentTools.TerminalSendKeys,
                    first.Operations,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "exact identity",
                    first.AccessibleName,
                    StringComparison.Ordinal);
            },
            second =>
            {
                Assert.Equal("Unnamed terminal", second.Title);
                Assert.Equal("Unnamed tab", second.TabTitle);
                Assert.Contains("Background", second.State);
            });

        viewModel.IsContextInspectorExpanded = true;
        runtime.Snapshot = Snapshot(providerId: provider.Id);
        runtime.RaiseChanged();

        Assert.Empty(viewModel.ContextItems);
        Assert.False(viewModel.HasContextItems);
        Assert.False(viewModel.IsContextInspectorExpanded);
    }

    [Fact]
    public void Mixed_context_inspector_projects_browser_kind_title_and_operations()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: new AgentRunId("mixed-context"),
                providerId: provider.Id,
                target: Target(),
                contextItems:
                [
                    ContextItem(
                        panelId: "terminal-panel",
                        sessionId: "terminal-session",
                        panelTitle: "Production shell",
                        tabTitle: "Operations",
                        isVisible: true,
                        isFocused: false,
                        hasActiveWork: false,
                        operations: [BuiltInAgentTools.TerminalReadScreen]),
                    ContextItem(
                        panelId: "browser-panel",
                        sessionId: "browser-session",
                        panelTitle: "Runbook",
                        tabTitle: "Documentation",
                        isVisible: true,
                        isFocused: true,
                        hasActiveWork: false,
                        operations:
                        [
                            BuiltInAgentTools.BrowserReadState,
                            BuiltInAgentTools.BrowserNavigate,
                        ],
                        kind: PanelKind.Browser),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.Equal("2 panels", viewModel.ContextInspectorSummary);
        Assert.Contains(
            "2 panels",
            viewModel.ContextInspectorAccessibleName,
            StringComparison.Ordinal);
        Assert.Collection(
            viewModel.ContextItems,
            terminal => Assert.Equal(PanelKind.Terminal, terminal.Kind),
            browser =>
            {
                Assert.Equal(PanelKind.Browser, browser.Kind);
                Assert.Equal("Runbook", browser.Title);
                Assert.Equal("Documentation", browser.TabTitle);
                Assert.StartsWith("browser · ", browser.ExactIdentity);
                Assert.Equal(
                    "Browser tools are available",
                    browser.Context);
                Assert.Contains(
                    BuiltInAgentTools.BrowserReadState,
                    browser.Operations,
                    StringComparison.Ordinal);
                Assert.Contains(
                    BuiltInAgentTools.BrowserNavigate,
                    browser.Operations,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "operations browser.read_state",
                    browser.AccessibleName,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void File_context_inspector_projects_trusted_relative_scope_and_operations()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: new AgentRunId("file-context"),
                providerId: provider.Id,
                target: Target(),
                capabilityNotice: string.Empty,
                contextItems:
                [
                    ContextItem(
                        panelId: "files-panel",
                        sessionId: "files-session",
                        panelTitle: "Production files",
                        tabTitle: "Operations",
                        isVisible: true,
                        isFocused: true,
                        hasActiveWork: false,
                        operations:
                        [
                            BuiltInAgentTools.FilesList,
                            BuiltInAgentTools.FilesStat,
                            BuiltInAgentTools.FilesRead,
                        ],
                        kind: PanelKind.FileViewer,
                        fileProviderProfileId: "builtin.files.home",
                        fileRootDisplay: "."),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.Equal("1 File Viewer", viewModel.ContextInspectorSummary);
        var item = Assert.Single(viewModel.ContextItems);
        Assert.Equal(PanelKind.FileViewer, item.Kind);
        Assert.StartsWith("File Viewer · ", item.ExactIdentity);
        Assert.Equal(
            "builtin.files.home · trusted root .",
            item.Context);
        Assert.Contains(
            BuiltInAgentTools.FilesRead,
            item.Operations,
            StringComparison.Ordinal);
        Assert.Equal(
            "File tools are limited to the selected File Viewer location.",
            viewModel.CapabilityNotice);
    }

    [Fact]
    public void Process_context_inspector_is_explicitly_local_only_and_governed()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: new AgentRunId("process-context"),
                providerId: provider.Id,
                target: Target(),
                capabilityNotice: string.Empty,
                contextItems:
                [
                    ContextItem(
                        panelId: "process-panel",
                        sessionId: "process-session",
                        panelTitle: "Local processes",
                        tabTitle: "Operations",
                        isVisible: true,
                        isFocused: true,
                        hasActiveWork: false,
                        operations: [BuiltInAgentTools.ProcessesList],
                        kind: PanelKind.ProcessMonitor),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.Equal("1 Process Monitor", viewModel.ContextInspectorSummary);
        var item = Assert.Single(viewModel.ContextItems);
        Assert.Equal(PanelKind.ProcessMonitor, item.Kind);
        Assert.StartsWith("Process Monitor · ", item.ExactIdentity);
        Assert.Equal(
            "Local processes are available",
            item.Context);
        Assert.Contains(
            BuiltInAgentTools.ProcessesList,
            item.Operations,
            StringComparison.Ordinal);
        Assert.Equal(
            "Process tools are disabled in this workspace.",
            viewModel.CapabilityNotice);

        runtime.Snapshot = runtime.Snapshot with
        {
            EffectivePolicy = AgentPolicy.Default with
            {
                Permissions = AgentPolicy.Default.Permissions.SetItem(
                    AgentCapability.ProcessData,
                    AgentPermission.Ask),
            },
        };
        runtime.RaiseChanged();

        Assert.Equal(
            "Local process information is available.",
            viewModel.CapabilityNotice);
    }

    [Fact]
    public void Statistics_context_inspector_is_explicitly_local_only_and_governed()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: new AgentRunId("statistics-context"),
                providerId: provider.Id,
                target: Target(),
                capabilityNotice: string.Empty,
                contextItems:
                [
                    ContextItem(
                        panelId: "statistics-panel",
                        sessionId: "statistics-session",
                        panelTitle: "Local resources",
                        tabTitle: "Operations",
                        isVisible: true,
                        isFocused: true,
                        hasActiveWork: false,
                        operations: [BuiltInAgentTools.StatisticsRead],
                        kind: PanelKind.Statistics),
                ]),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.Equal("1 Statistics panel", viewModel.ContextInspectorSummary);
        var item = Assert.Single(viewModel.ContextItems);
        Assert.Equal(PanelKind.Statistics, item.Kind);
        Assert.StartsWith("Statistics · ", item.ExactIdentity);
        Assert.Equal(
            "Local resource statistics are available",
            item.Context);
        Assert.Contains(
            BuiltInAgentTools.StatisticsRead,
            item.Operations,
            StringComparison.Ordinal);
        Assert.Equal(
            "Local resource statistics are disabled in this workspace.",
            viewModel.CapabilityNotice);

        runtime.Snapshot = runtime.Snapshot with
        {
            EffectivePolicy = AgentPolicy.Default with
            {
                Permissions = AgentPolicy.Default.Permissions.SetItem(
                    AgentCapability.SystemData,
                    AgentPermission.Ask),
            },
        };
        runtime.RaiseChanged();

        Assert.Equal(
            "Local resource statistics are available.",
            viewModel.CapabilityNotice);
    }

    [Theory]
    [InlineData(GovernedAgentState.StreamingProvider, true)]
    [InlineData(GovernedAgentState.AwaitingCapabilityDecision, true)]
    [InlineData(GovernedAgentState.AwaitingApproval, true)]
    [InlineData(GovernedAgentState.RunningTool, true)]
    [InlineData(GovernedAgentState.Cancelling, false)]
    public void Stop_remains_visible_through_every_active_state(
        GovernedAgentState state,
        bool canRequestStop)
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: state,
                runId: null,
                providerId: provider.Id,
                target: Target()),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Do not send yet.",
        };

        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.CanStop);
        Assert.Equal(canRequestStop, viewModel.CanRequestStop);
        Assert.False(viewModel.CanSend);
        Assert.False(viewModel.CanClear);
    }

    [Fact]
    public async Task Approval_decision_forwards_exact_pending_id_and_user_choice()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var approvalId = new AgentApprovalId("approval-1");
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.AwaitingApproval,
                runId: new AgentRunId("run-1"),
                providerId: provider.Id,
                target: Target(),
                pendingApproval: Approval(approvalId)),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        using var cancellation = new CancellationTokenSource();

        await viewModel.DecideAsync(approved: false, cancellation.Token);

        Assert.Equal(approvalId, runtime.LastApprovalId);
        Assert.False(runtime.LastApprovalDecision);
        Assert.Equal(cancellation.Token, runtime.LastDecisionCancellationToken);
        Assert.Equal(1, runtime.DecisionCount);
    }

    [Fact]
    public async Task Stop_and_clear_forward_to_governed_runtime()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.RunningTool,
                runId: new AgentRunId("run-1"),
                providerId: provider.Id,
                target: Target()),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        using var cancellation = new CancellationTokenSource();

        await viewModel.StopAsync(cancellation.Token);

        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(cancellation.Token, runtime.LastStopCancellationToken);

        runtime.Snapshot = Snapshot(
            state: GovernedAgentState.Cancelled,
            runId: new AgentRunId("run-1"),
            providerId: provider.Id,
            target: Target(),
            messages: [new AgentChatMessage(AgentChatMessageRole.User, "Stopped")]);
        runtime.RaiseChanged();
        await viewModel.ClearAsync(cancellation.Token);

        Assert.Equal(1, runtime.ClearCount);
        Assert.Equal(cancellation.Token, runtime.LastClearCancellationToken);
    }

    [Fact]
    public void Active_tool_projects_visible_activity()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var panelId = new PanelInstanceId("production-shell");
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.RunningTool,
                runId: new AgentRunId("run-1"),
                providerId: provider.Id,
                target: Target(),
                activeTool: new GovernedAgentToolActivity(
                    "terminal.read_screen",
                    "Read terminal screen",
                    AgentActionRisk.Observation,
                    "Production shell",
                    PanelId: panelId)),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };

        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        var activity = Assert.IsType<AgentToolActivityViewModel>(viewModel.ActiveTool);
        Assert.Equal("terminal.read_screen", activity.ToolName);
        Assert.Equal("Read terminal screen", activity.ToolTitle);
        Assert.Equal("Observation", activity.Risk);
        Assert.Equal("Production shell", activity.TargetTitle);
        Assert.Equal(panelId, activity.PanelId);
        Assert.Equal(activity.PanelId, viewModel.PanelActivity?.PanelId);
        Assert.False(activity.CancellationRequested);
        Assert.True(viewModel.HasActiveTool);
        Assert.True(viewModel.HasAgentContent);
        Assert.True(viewModel.CanCancelActiveAction);
        Assert.Equal("Cancel action", viewModel.ActiveActionCancellationLabel);

        runtime.Snapshot = runtime.Snapshot with
        {
            State = GovernedAgentState.StreamingProvider,
            ActiveTool = null,
        };
        runtime.RaiseChanged();

        Assert.Null(viewModel.ActiveTool);
        Assert.Equal(activity.PanelId, viewModel.PanelActivity?.PanelId);

        var nextPanelId = new PanelInstanceId("production-files");
        var nextActivity = new GovernedAgentToolActivity(
            "files.list",
            "List files",
            AgentActionRisk.Observation,
            "Production files",
            PanelId: nextPanelId);
        runtime.Snapshot = runtime.Snapshot with
        {
            State = GovernedAgentState.RunningTool,
            ActiveTool = nextActivity,
            PanelActivity = nextActivity,
        };
        runtime.RaiseChanged();

        Assert.Equal(nextPanelId, viewModel.PanelActivity?.PanelId);

        runtime.Snapshot = runtime.Snapshot with
        {
            State = GovernedAgentState.Ready,
            ActiveTool = null,
            PanelActivity = null,
        };
        runtime.RaiseChanged();

        Assert.Null(viewModel.PanelActivity);
    }

    [Fact]
    public async Task One_action_cancel_is_forwarded_once_and_does_not_replace_run_stop()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var active = new GovernedAgentToolActivity(
            BuiltInAgentTools.TerminalWait,
            "Wait for terminal state",
            AgentActionRisk.Routine,
            "Production shell");
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.RunningTool,
                runId: new AgentRunId("run-1"),
                providerId: provider.Id,
                target: Target(),
                activeTool: active),
            SnapshotOnActionCancel = Snapshot(
                state: GovernedAgentState.RunningTool,
                runId: new AgentRunId("run-1"),
                providerId: provider.Id,
                target: Target(),
                status: "Cancelling this action…",
                activeTool: active with { CancellationRequested = true }),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        using var cancellation = new CancellationTokenSource();

        await viewModel.CancelActiveActionAsync(cancellation.Token);
        await viewModel.CancelActiveActionAsync(cancellation.Token);

        Assert.Equal(1, runtime.ActionCancellationCount);
        Assert.Equal(
            cancellation.Token,
            runtime.LastActionCancellationToken);
        Assert.False(viewModel.CanCancelActiveAction);
        Assert.Equal(
            "Cancelling action…",
            viewModel.ActiveActionCancellationLabel);
        Assert.True(viewModel.CanRequestStop);
        Assert.Equal(0, runtime.StopCount);
    }

    [Fact]
    public async Task One_action_cancel_reentry_is_blocked_while_request_is_in_flight()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var completion =
            new TaskCompletionSource<GovernedAgentActionCancellationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.RunningTool,
                runId: new AgentRunId("run-1"),
                providerId: provider.Id,
                target: Target(),
                activeTool: new GovernedAgentToolActivity(
                    BuiltInAgentTools.TerminalWait,
                    "Wait for terminal state",
                    AgentActionRisk.Routine,
                    "Production shell")),
            PendingActionCancellation = completion,
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        var first = viewModel.CancelActiveActionAsync(CancellationToken.None);
        var reentry = viewModel.CancelActiveActionAsync(CancellationToken.None);

        Assert.Equal(1, runtime.ActionCancellationCount);
        Assert.False(viewModel.CanCancelActiveAction);
        Assert.Equal(
            "Cancelling action…",
            viewModel.ActiveActionCancellationLabel);
        Assert.True(viewModel.CanRequestStop);

        completion.SetResult(runtime.ActionCancellationResult);
        await Task.WhenAll(first, reentry);
    }

    [Fact]
    public async Task Run_local_yolo_projects_scope_and_forwards_enable_and_disable()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var target = Target();
        var runId = new AgentRunId("run-1");
        var confirmedAt = new DateTimeOffset(
            2026,
            7,
            24,
            10,
            0,
            0,
            TimeSpan.Zero);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: runId,
                providerId: provider.Id,
                target: target),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.CanOfferYolo);
        Assert.True(viewModel.CanEnableYolo);
        Assert.Equal("Ask", viewModel.PolicyModeLabel);
        Assert.DoesNotContain("YOLO requires an exact terminal panel", viewModel.CapabilityNotice);

        await viewModel.SelectFullAccessAsync(CancellationToken.None);

        Assert.Equal(1, runtime.EnableFullAccessCount);

        runtime.Snapshot = Snapshot(
            state: GovernedAgentState.RunningTool,
            runId: runId,
            providerId: provider.Id,
            target: target,
            terminalMutationPermission: AgentPermission.Yolo,
            yoloAuthority: new GovernedAgentYoloAuthority(
                runId,
                target,
                confirmedAt,
                AgentYoloConfirmation.RunLifetimeExpiry));
        runtime.RaiseChanged();

        Assert.True(viewModel.HasYoloAuthority);
        Assert.True(viewModel.CanDisableYolo);
        Assert.False(viewModel.CanOfferYolo);
        Assert.Equal("Full access", viewModel.PolicyModeLabel);
        Assert.Contains("window/window-1", viewModel.YoloAuthority!.Scope);

        await viewModel.DisableYoloAsync(CancellationToken.None);

        Assert.Equal(1, runtime.DisableYoloCount);
    }

    [Fact]
    public async Task Fresh_conversation_selects_full_access_for_its_first_prompt()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.CanEnableYolo);

        await viewModel.SelectFullAccessAsync(CancellationToken.None);

        Assert.True(viewModel.CanDisableYolo);
        Assert.Equal("Full access", viewModel.AccessModeLabel);
        Assert.Equal(0, runtime.EnableFullAccessCount);

        viewModel.Prompt = "Inspect the terminal.";
        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        Assert.Equal(
            AgentApprovalMode.FullAccess,
            runtime.LastRequest!.ApprovalMode);
    }

    [Fact]
    public async Task Restored_conversation_selects_full_access_for_its_next_prompt()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                providerId: provider.Id,
                messages:
                [
                    new AgentChatMessage(AgentChatMessageRole.User, "Earlier question"),
                    new AgentChatMessage(AgentChatMessageRole.Assistant, "Earlier answer"),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        await viewModel.SelectFullAccessAsync(CancellationToken.None);

        Assert.Equal("Full access", viewModel.AccessModeLabel);
        Assert.Equal(0, runtime.EnableFullAccessCount);

        viewModel.Prompt = "Continue with full access.";
        await viewModel.SendAsync(Target(), Policy(provider), CancellationToken.None);

        Assert.Equal(AgentApprovalMode.FullAccess, runtime.LastRequest!.ApprovalMode);
    }

    [Fact]
    public async Task Approval_mode_remains_selectable_for_a_browser_run()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: new AgentRunId("browser-run"),
                providerId: provider.Id,
                target: Target(),
                contextItems:
                [
                    ContextItem(
                        panelId: "panel-1",
                        sessionId: "browser-session-1",
                        panelTitle: "Runbook",
                        tabTitle: "Documentation",
                        isVisible: true,
                        isFocused: true,
                        hasActiveWork: false,
                        operations:
                        [
                            BuiltInAgentTools.BrowserReadState,
                            BuiltInAgentTools.BrowserNavigate,
                        ],
                        kind: PanelKind.Browser),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.CanOfferYolo);
        Assert.True(viewModel.CanEnableYolo);
        Assert.Contains(
            "Browser tools are available.",
            viewModel.CapabilityNotice);
        Assert.DoesNotContain(
            "Run-only YOLO",
            viewModel.CapabilityNotice,
            StringComparison.Ordinal);

        await viewModel.SelectFullAccessAsync(CancellationToken.None);

        Assert.Equal("Full access", viewModel.AccessModeLabel);
        Assert.Equal(1, runtime.EnableFullAccessCount);
    }

    [Fact]
    public async Task Approval_mode_can_be_selected_while_a_turn_is_running()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var runId = new AgentRunId("run-1");
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.RunningTool,
                runId: runId,
                providerId: provider.Id,
                target: Target()),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        await viewModel.SelectFullAccessAsync(CancellationToken.None);

        Assert.Equal("Full access", viewModel.AccessModeLabel);
        Assert.Equal(1, runtime.EnableFullAccessCount);
    }

    [Theory]
    [MemberData(nameof(BroaderRunTargets))]
    public async Task Broader_terminal_run_offers_full_access(
        AgentTarget broaderTarget)
    {
        var provider = Provider("provider", "Provider", order: 0);
        var runId = new AgentRunId("run-1");
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: runId,
                providerId: provider.Id,
                target: Target()),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.CanOfferYolo);

        runtime.Snapshot = Snapshot(
            runId: runId,
            providerId: provider.Id,
            target: broaderTarget,
            capabilityNotice: "Two governed terminals are available.");
        runtime.RaiseChanged();

        Assert.True(viewModel.CanOfferYolo);
        Assert.True(viewModel.CanEnableYolo);
        Assert.Contains("Two governed terminals are available.", viewModel.CapabilityNotice);

        await viewModel.SelectFullAccessAsync(CancellationToken.None);

        Assert.Equal(1, runtime.EnableFullAccessCount);
    }

    [Fact]
    public void Missing_bound_provider_requires_clear_instead_of_selecting_another()
    {
        var removed = Provider("removed", "Removed", order: 0);
        var replacement = Provider("replacement", "Replacement", order: 1);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.Ready,
                runId: new AgentRunId("run-1"),
                providerId: removed.Id,
                target: Target(),
                messages:
                [
                    new AgentChatMessage(AgentChatMessageRole.User, "Question"),
                    new AgentChatMessage(AgentChatMessageRole.Assistant, "Answer"),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [replacement],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.Null(viewModel.SelectedProvider);
        Assert.False(viewModel.CanSend);
        Assert.False(viewModel.CanEnterPrompt);
        Assert.True(viewModel.CanClear);
        Assert.True(viewModel.NeedsProviderAttention);
        Assert.Equal("Clear required", viewModel.ConnectionStatus);
        Assert.Equal(
            "This run's provider is no longer enabled. Clear the run to choose another.",
            viewModel.Status);
    }

    [Fact]
    public async Task Quiesce_stops_and_waits_for_active_send_to_drain()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var pendingSend = new TaskCompletionSource<GovernedAgentSendResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = new StubGovernedRuntime
        {
            PendingSend = pendingSend,
            SnapshotOnSend = Snapshot(
                state: GovernedAgentState.StreamingProvider,
                runId: new AgentRunId("run-1"),
                providerId: provider.Id,
                target: Target(),
                messages: [new AgentChatMessage(AgentChatMessageRole.User, "Wait")]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            Prompt = "Wait for this response.",
        };

        var send = viewModel.SendAsync(
            Target(),
            Policy(provider),
            CancellationToken.None);
        var quiesce = viewModel.QuiesceAsync(CancellationToken.None);

        Assert.Equal(1, runtime.StopCount);
        Assert.False(quiesce.IsCompleted);
        pendingSend.SetResult(
            new GovernedAgentSendResult(
                true,
                "agent_turn_completed",
                "Completed."));

        await Task.WhenAll(send, quiesce);
    }

    [Fact]
    public void Profile_change_preserves_available_selection()
    {
        var first = Provider("first", "First", order: 0);
        var selected = Provider("selected", "Selected", order: 1);
        var replacement = Provider("replacement", "Replacement", order: -1);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime
        {
            Profiles = [first, selected],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance)
        {
            SelectedProvider = selected,
        };

        profiles.Profiles =
        [
            selected,
            replacement,
            Provider("off", "Off", 2, isEnabled: false),
        ];
        profiles.RaiseProfilesChanged();

        Assert.Collection(
            viewModel.Providers,
            provider => Assert.Equal(replacement.Id, provider.Id),
            provider => Assert.Equal(selected.Id, provider.Id));
        Assert.Equal(selected.Id, viewModel.SelectedProvider?.Id);
    }

    [Fact]
    public void Profile_change_notifies_provider_setup_visibility()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime();
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        var notifications = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } propertyName)
            {
                notifications.Add(propertyName);
            }
        };

        Assert.True(viewModel.HasNoProvider);

        profiles.Profiles = [provider];
        profiles.RaiseProfilesChanged();

        Assert.True(viewModel.HasProvider);
        Assert.False(viewModel.HasNoProvider);
        Assert.Contains(nameof(AgentChatViewModel.HasProvider), notifications, StringComparer.Ordinal);
        Assert.Contains(nameof(AgentChatViewModel.HasNoProvider), notifications, StringComparer.Ordinal);
    }

    [Fact]
    public void Removing_last_provider_preserves_retained_run_clear_recovery()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                state: GovernedAgentState.Ready,
                runId: new AgentRunId("run-1"),
                providerId: provider.Id,
                target: Target(),
                messages:
                [
                    new AgentChatMessage(AgentChatMessageRole.User, "Question"),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.True(viewModel.CanClear);

        profiles.Profiles = [];
        profiles.RaiseProfilesChanged();

        Assert.True(viewModel.HasNoProvider);
        Assert.True(viewModel.CanClear);
        Assert.True(viewModel.NeedsProviderAttention);
    }

    [Fact]
    public void Dispose_unsubscribes_without_disposing_shared_runtimes()
    {
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime();
        var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.Equal(1, runtime.SubscriberCount);
        Assert.Equal(1, profiles.SubscriberCount);

        viewModel.Dispose();
        viewModel.Dispose();

        Assert.Equal(0, runtime.SubscriberCount);
        Assert.Equal(0, profiles.SubscriberCount);
        Assert.Equal(0, runtime.DisposeCount);
        Assert.Equal(0, profiles.DisposeCount);
    }

    [Fact]
    public void Run_finished_is_raised_once_when_active_work_reaches_a_terminal_state()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: new AgentRunId("run-finished"),
                providerId: provider.Id,
                target: Target()),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);
        var finished = 0;
        viewModel.RunFinished += (_, _) => finished++;

        runtime.Snapshot = runtime.Snapshot with
        {
            State = GovernedAgentState.StreamingProvider,
        };
        runtime.RaiseChanged();
        runtime.Snapshot = runtime.Snapshot with
        {
            State = GovernedAgentState.Ready,
        };
        runtime.RaiseChanged();
        runtime.RaiseChanged();

        Assert.Equal(1, finished);
    }

    [Fact]
    public void Audit_evidence_ui_is_available_in_every_build_configuration()
    {
        Assert.True(AgentChatViewModel.AuditEvidenceUiEnabled);
    }

    [Fact]
    public async Task Restored_conversation_owns_production_audit_and_policy_comparison()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var selectedRun = new AgentRunId("run-restored-history");
        var baseline = AgentPolicy.Default.SelectPrimaryModel("provider", "model");
        var runPolicy = baseline with
        {
            Permissions = baseline.Permissions.SetItem(
                AgentCapability.ProcessControl,
                AgentPermission.Ask),
        };
        var effective = runPolicy with
        {
            Permissions = runPolicy.Permissions.SetItem(
                AgentCapability.ProcessControl,
                AgentPermission.Yolo),
        };
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                providerId: provider.Id,
                messages:
                [
                    new AgentChatMessage(
                        AgentChatMessageRole.User,
                        "Restored transcript."),
                ],
                effectivePolicy: effective,
                selectedConversationRunId: selectedRun,
                baselinePolicy: baseline,
                runPolicy: runPolicy,
                policyGeneration: 7),
        };
        using var profiles = new StubProfileRuntime { Profiles = [provider] };
        var auditReader = new StubAgentRunAuditReader(
            AuditStoreResult<AgentRunAuditPage>.Success(
                new AgentRunAuditPage([], null)));
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance,
            auditReader);

        Assert.True(viewModel.CanShowAudit);
        viewModel.IsAuditExpanded = true;
        await WaitUntilAsync(() => auditReader.ReadCount == 1);

        Assert.Equal(selectedRun, auditReader.Queries[0].RunId);
        Assert.Equal(7, viewModel.PolicyGeneration);
        var processControl = Assert.Single(
            viewModel.PolicyComparison,
            item => item.Capability == "Process control");
        Assert.Equal("Off", processControl.Baseline);
        Assert.Equal("Ask", processControl.Run);
        Assert.Equal("Full access", processControl.Effective);
        Assert.True(processControl.HasDifference);
    }

    [Fact]
    public async Task Audit_is_lazy_run_owned_and_uses_opaque_pagination()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var runId = new AgentRunId("run-audit");
        var cursor = new AgentRunAuditCursor("AQ");
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: runId,
                providerId: provider.Id,
                target: Target(),
                messages:
                [
                    new AgentChatMessage(
                        AgentChatMessageRole.User,
                        "Inspect the terminal."),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        var auditReader = new StubAgentRunAuditReader(
            AuditStoreResult<AgentRunAuditPage>.Success(
                new AgentRunAuditPage(
                    [AuditAction("newer-action", "terminal_read_succeeded")],
                    cursor)),
            AuditStoreResult<AgentRunAuditPage>.Success(
                new AgentRunAuditPage(
                    [AuditAction("older-action", "terminal_wait_succeeded")],
                    next: null)));
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance,
            auditReader);

        if (!AgentChatViewModel.AuditEvidenceUiEnabled)
        {
            Assert.False(viewModel.CanShowAudit);
            return;
        }

        Assert.True(viewModel.CanShowAudit);
        Assert.Equal(0, auditReader.ReadCount);

        viewModel.IsAuditExpanded = true;
        await WaitUntilAsync(() => auditReader.ReadCount == 1);

        Assert.Equal(runId, auditReader.Queries[0].RunId);
        Assert.Null(auditReader.Queries[0].Before);
        var newest = Assert.Single(viewModel.AuditEntries);
        Assert.Equal("Action", newest.Kind);
        Assert.Equal(BuiltInAgentTools.TerminalReadScreen, newest.ToolName);
        Assert.Contains(
            "Requested → Approved → Started → Succeeded",
            newest.Timeline,
            StringComparison.Ordinal);
        Assert.Contains(
            "terminal_read_succeeded",
            newest.Result,
            StringComparison.Ordinal);
        Assert.True(viewModel.CanLoadOlderAudit);

        await viewModel.LoadOlderAuditAsync(CancellationToken.None);

        Assert.Equal(2, auditReader.ReadCount);
        Assert.Equal(cursor, auditReader.Queries[1].Before);
        Assert.Equal(2, viewModel.AuditEntries.Count);
        Assert.False(viewModel.CanLoadOlderAudit);
    }

    [Fact]
    public async Task Audit_failure_is_isolated_and_does_not_surface_storage_detail()
    {
        var provider = Provider("provider", "Provider", order: 0);
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: new AgentRunId("run-audit-failure"),
                providerId: provider.Id,
                target: Target(),
                messages:
                [
                    new AgentChatMessage(
                        AgentChatMessageRole.User,
                        "Inspect the terminal."),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        var auditReader = new StubAgentRunAuditReader(
            AuditStoreResult<AgentRunAuditPage>.Failure(
                new AuditStoreError(
                    AuditStoreErrorCode.StorageFailure,
                    "must-not-surface-secret")));
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance,
            auditReader)
        {
            Prompt = "Continue the run.",
        };

        if (!AgentChatViewModel.AuditEvidenceUiEnabled)
        {
            Assert.False(viewModel.CanShowAudit);
            return;
        }

        viewModel.IsAuditExpanded = true;
        await WaitUntilAsync(() => auditReader.ReadCount == 1);

        Assert.Empty(viewModel.AuditEntries);
        Assert.Equal(
            "The recorded audit chain is incomplete or corrupt and was hidden.",
            viewModel.AuditStatus);
        Assert.DoesNotContain(
            "must-not-surface-secret",
            viewModel.AuditStatus,
            StringComparison.Ordinal);
        Assert.True(viewModel.CanSend);
        Assert.Equal("Agent ready.", viewModel.Status);
    }

    [Fact]
    public async Task Audit_run_change_discards_entries_and_loads_only_the_new_runtime_run()
    {
        var provider = Provider("provider", "Provider", order: 0);
        var firstRun = new AgentRunId("run-audit-first");
        var secondRun = new AgentRunId("run-audit-second");
        using var runtime = new StubGovernedRuntime
        {
            Snapshot = Snapshot(
                runId: firstRun,
                providerId: provider.Id,
                target: Target(),
                messages:
                [
                    new AgentChatMessage(
                        AgentChatMessageRole.User,
                        "Inspect the terminal."),
                ]),
        };
        using var profiles = new StubProfileRuntime
        {
            Profiles = [provider],
        };
        var auditReader = new StubAgentRunAuditReader(
            AuditStoreResult<AgentRunAuditPage>.Success(
                new AgentRunAuditPage(
                    [AuditAction("first-run-action", "first_run_succeeded")],
                    next: null)),
            AuditStoreResult<AgentRunAuditPage>.Success(
                new AgentRunAuditPage(
                    [AuditAction("second-run-action", "second_run_succeeded")],
                    next: null)));
        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance,
            auditReader);

        if (!AgentChatViewModel.AuditEvidenceUiEnabled)
        {
            Assert.False(viewModel.CanShowAudit);
            return;
        }

        viewModel.IsAuditExpanded = true;
        await WaitUntilAsync(() => auditReader.ReadCount == 1);

        runtime.Snapshot = Snapshot(
            runId: secondRun,
            providerId: provider.Id,
            target: Target(),
            messages:
            [
                new AgentChatMessage(
                    AgentChatMessageRole.User,
                    "Inspect the terminal again."),
            ]);
        runtime.RaiseChanged();
        await WaitUntilAsync(() => auditReader.ReadCount == 2);

        Assert.Equal(firstRun, auditReader.Queries[0].RunId);
        Assert.Equal(secondRun, auditReader.Queries[1].RunId);
        var current = Assert.Single(viewModel.AuditEntries);
        Assert.Contains(
            "second_run_succeeded",
            current.Result,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "first_run_succeeded",
            current.Result,
            StringComparison.Ordinal);
    }

    private static AiProviderProfileDescriptor Provider(
        string id,
        string name,
        int order,
        bool isEnabled = true,
        bool supportsImageInput = false,
        IReadOnlyList<AgentReasoningEffort>? supportedReasoningEfforts = null,
        IReadOnlyList<AiProviderModelDescriptor>? models = null) =>
        new(
            new AiProviderProfileId(id),
            name,
            AiProviderKind.OpenAiCompatible,
            new Uri("https://provider.example.test/v1/"),
            "model",
            order,
            isEnabled,
            RequiresCredential: true,
            SupportsImageInput: supportsImageInput,
            SupportedReasoningEfforts: supportedReasoningEfforts,
            Models: models
                ?? [new AiProviderModelDescriptor(
                    "model",
                    "model",
                    supportedReasoningEfforts)]);

    private static AgentPolicy Policy(AiProviderProfileDescriptor provider) =>
        new(
            provider.Id.Value,
            provider.DefaultModel,
            AgentPolicy.Default.Permissions)
        {
            CompactionModel = new AgentModelSelection(
                provider.Id.Value,
                provider.DefaultModel),
            TitleModel = new AgentModelSelection(
                provider.Id.Value,
                provider.DefaultModel),
        };

    private static AgentTarget.Panel Target() =>
        new(
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-1"),
            new TabInstanceId("tab-1"),
            new PanelInstanceId("panel-1"));

    private static AgentRunAuditActionEntry AuditAction(
        string identity,
        string resultCode)
    {
        var occurredAt =
            new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);
        return new AgentRunAuditActionEntry(
            AgentActionDigest.FromUtf8(identity),
            BuiltInAgentTools.TerminalReadScreen,
            AgentCapability.TerminalRead,
            AgentActionRisk.Observation,
            AgentPermission.Auto,
            AgentPolicyDecision.AuthorizedByAuto,
            AgentAuthorizationSource.AutoPolicy,
            errorCode: null,
            resultCode,
            policyGeneration: 3,
            AgentActionDigest.FromUtf8("target"),
            executionDurationMilliseconds: 25,
            resultCount: 1,
            [
                new AgentRunAuditPhase(
                    AuditOutcome.Requested,
                    ActorKind.Agent,
                    occurredAt),
                new AgentRunAuditPhase(
                    AuditOutcome.Approved,
                    ActorKind.System,
                    occurredAt.AddMilliseconds(1)),
                new AgentRunAuditPhase(
                    AuditOutcome.Started,
                    ActorKind.Agent,
                    occurredAt.AddMilliseconds(2)),
                new AgentRunAuditPhase(
                    AuditOutcome.Succeeded,
                    ActorKind.Agent,
                    occurredAt.AddMilliseconds(3)),
            ]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The asynchronous condition was not observed.");
    }

#pragma warning disable CA1825 // xUnit's TheoryData collection builder is misidentified as an empty array.
    public static TheoryData<AgentTarget> BroaderRunTargets =>
        [
            new AgentTarget.OpenTab(
                new WindowInstanceId("window-1"),
                new WorkspaceInstanceId("workspace-1"),
                new TabInstanceId("tab-1")),
            new AgentTarget.Workspace(
                new WindowInstanceId("window-1"),
                new WorkspaceInstanceId("workspace-1")),
        ];
#pragma warning restore CA1825

    private static GovernedAgentApproval Approval(AgentApprovalId approvalId) =>
        new(
            approvalId,
            "terminal.send_key",
            "Send terminal key",
            AgentActionRisk.Mutation,
            AgentPermission.Ask,
            Target(),
            new AgentApprovalPresentation(
                "Active terminal",
                "host.example.test",
                "/srv/app"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            TemporarilyYieldsTerminalInput: true);

    private static GovernedAgentQuestion Question() =>
        new(
            new AgentQuestionId("question-1"),
            "Which non-sensitive environment should I inspect?",
            new DateTimeOffset(2026, 7, 25, 12, 30, 0, TimeSpan.Zero));

    private static GovernedAgentCapabilityRequest CapabilityRequest() =>
        new(
            new AgentCapabilityRequestId("capability-request-1"),
            new AgentRunId("run-capability-request"),
            AgentCapability.ProcessControl,
            "Process control",
            ["List local processes"],
            Target(),
            "Active terminal",
            policyGeneration: 4,
            new DateTimeOffset(2026, 7, 25, 12, 30, 0, TimeSpan.Zero));

    private static GovernedAgentSnapshot Snapshot(
        GovernedAgentState state = GovernedAgentState.Ready,
        AgentRunId? runId = null,
        AiProviderProfileId? providerId = null,
        AgentTarget? target = null,
        IReadOnlyList<AgentChatMessage>? messages = null,
        string provisional = "",
        string status = "Agent ready.",
        GovernedAgentApproval? pendingApproval = null,
        GovernedAgentToolActivity? activeTool = null,
        bool terminalMutationAvailable = false,
        string? capabilityNotice = null,
        AgentPermission terminalMutationPermission = AgentPermission.Ask,
        GovernedAgentYoloAuthority? yoloAuthority = null,
        string? connectionBoundary = null,
        string? workingDirectory = null,
        IReadOnlyList<GovernedAgentContextItem>? contextItems = null,
        AgentPolicy? effectivePolicy = null,
        GovernedAgentProgress? currentProgress = null,
        GovernedAgentQuestion? pendingQuestion = null,
        GovernedAgentCapabilityRequest? pendingCapabilityRequest = null,
        bool steeringAvailable = false,
        long? steeringGeneration = null,
        IReadOnlyList<GovernedAgentConversationSummary>? conversations = null,
        GovernedAgentToolActivity? panelActivity = null,
        AgentRunId? selectedConversationRunId = null,
        AgentPolicy? baselinePolicy = null,
        AgentPolicy? runPolicy = null,
        long policyGeneration = 1)
    {
        var routeProvider = providerId?.Value ?? "provider";
        var policy = effectivePolicy ?? new AgentPolicy(
            routeProvider,
            "model",
            AgentPolicy.Default.Permissions)
        {
            CompactionModel = new AgentModelSelection(routeProvider, "model"),
            TitleModel = new AgentModelSelection(routeProvider, "model"),
        };
        return new(
            state,
            runId,
            providerId,
            target,
            target is null ? "No panel selected" : "Active panel",
            contextItems?.ToImmutableArray() ?? [],
            messages ?? [],
            policy,
            provisional,
            status,
            pendingApproval,
            activeTool,
            terminalMutationAvailable,
            capabilityNotice,
            terminalMutationPermission,
            yoloAuthority,
            connectionBoundary,
            workingDirectory,
            currentProgress,
            pendingQuestion,
            pendingCapabilityRequest,
            steeringAvailable,
            steeringGeneration,
            ProvisionalReasoningSummary: string.Empty,
            QueuedFollowUpCount: 0,
            Conversations: conversations?.ToImmutableArray() ?? [],
            PanelActivity: panelActivity ?? activeTool,
            SelectedConversationRunId: selectedConversationRunId,
            BaselinePolicy: baselinePolicy,
            RunPolicy: runPolicy,
            PolicyGeneration: policyGeneration);
    }

    private static GovernedAgentContextItem ContextItem(
        string panelId,
        string sessionId,
        string? panelTitle,
        string? tabTitle,
        bool isVisible,
        bool isFocused,
        bool hasActiveWork,
        IReadOnlyList<string> operations,
        PanelKind kind = PanelKind.Terminal,
        string? fileProviderProfileId = null,
        string? fileRootDisplay = null) =>
        new(
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-1"),
            new TabInstanceId("tab-1"),
            new PanelInstanceId(panelId),
            new SessionId(sessionId),
            kind,
            "Production workspace",
            tabTitle,
            panelTitle,
            kind == PanelKind.Terminal
                ? "SSH · api.example.test"
                : null,
            kind == PanelKind.Terminal
                ? "/srv/api"
                : null,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            isVisible,
            isFocused,
            hasActiveWork,
            operations,
            fileProviderProfileId,
            fileRootDisplay);

    private sealed class StubGovernedRuntime : IGovernedAgentRuntime
    {
        private EventHandler? _changed;

        public event EventHandler? Changed
        {
            add
            {
                _changed += value;
                SubscriberCount++;
            }
            remove
            {
                _changed -= value;
                SubscriberCount--;
            }
        }

        public GovernedAgentSnapshot Snapshot { get; set; } =
            AgentChatViewModelTests.Snapshot();

        public GovernedAgentSnapshot? SnapshotOnSend { get; set; }

        public GovernedAgentSnapshot? SnapshotOnActionCancel { get; set; }

        public GovernedAgentSnapshot? SnapshotOnClear { get; set; }

        public GovernedAgentSendResult SendResult { get; set; } =
            new(true, "agent_turn_completed", "Completed.");

        public GovernedAgentSteeringResult SteeringResult { get; set; } =
            new(true, "agent_steering_accepted", "Steering accepted.");

        public GovernedAgentFollowUpResult FollowUpResult { get; set; } =
            new(true, "agent_follow_up_queued", "Queued.", 1);

        public GovernedAgentDecisionResult DecisionResult { get; set; } =
            new(true, "agent_decision_accepted", "Accepted.");

        public GovernedAgentQuestionResponseResult QuestionResponseResult
        { get; set; } =
            new(true, "question_answered", "The answer was accepted.");

        public GovernedAgentCapabilityDecisionResult CapabilityDecisionResult
        { get; set; } =
            new(
                true,
                "capability_request_allowed",
                "Ask is enabled for this run.");

        public GovernedAgentStopResult StopResult { get; set; } =
            new(true, "agent_stopped", "Stopped.");

        public GovernedAgentActionCancellationResult ActionCancellationResult { get; set; } =
            new(true, "agent_action_cancel_requested", "Cancellation requested.");

        public GovernedAgentPolicyResult PolicyResult { get; set; } =
            new(true, "policy_updated", "Updated.");

        public bool ClearResult { get; set; } = true;

        public bool ForkResult { get; set; } = true;

        public TaskCompletionSource<GovernedAgentSendResult>? PendingSend { get; set; }

        public TaskCompletionSource<GovernedAgentSteeringResult>? PendingSteering
        { get; set; }

        public TaskCompletionSource<GovernedAgentFollowUpResult>? PendingFollowUp
        { get; set; }

        public TaskCompletionSource<GovernedAgentActionCancellationResult>?
            PendingActionCancellation
        { get; set; }

        public TaskCompletionSource<GovernedAgentCapabilityDecisionResult>?
            PendingCapabilityDecision
        { get; set; }

        public GovernedAgentPrompt? LastRequest { get; private set; }

        public GovernedAgentSteering? LastSteering { get; private set; }

        public GovernedAgentFollowUp? LastFollowUp { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public CancellationToken LastSteeringCancellationToken { get; private set; }

        public CancellationToken LastFollowUpCancellationToken { get; private set; }

        public AgentApprovalId? LastApprovalId { get; private set; }

        public bool? LastApprovalDecision { get; private set; }

        public CancellationToken LastDecisionCancellationToken { get; private set; }

        public AgentQuestionId? LastQuestionId { get; private set; }

        public GovernedAgentQuestionResponse? LastQuestionResponse { get; private set; }

        public CancellationToken LastQuestionCancellationToken { get; private set; }

        public AgentCapabilityRequestId? LastCapabilityRequestId { get; private set; }

        public GovernedAgentCapabilityDecision? LastCapabilityDecision
        { get; private set; }

        public CancellationToken LastCapabilityDecisionCancellationToken
        { get; private set; }

        public CancellationToken LastStopCancellationToken { get; private set; }

        public CancellationToken LastActionCancellationToken { get; private set; }

        public CancellationToken LastClearCancellationToken { get; private set; }

        public AgentConversationForkPoint? LastForkPoint { get; private set; }

        public TimeSpan? LastYoloLifetime { get; private set; }

        public CancellationToken LastYoloCancellationToken { get; private set; }

        public int SendCount { get; private set; }

        public int SteeringCount { get; private set; }

        public int FollowUpCount { get; private set; }

        public int DecisionCount { get; private set; }

        public int QuestionResponseCount { get; private set; }

        public int CapabilityDecisionCount { get; private set; }

        public int StopCount { get; private set; }

        public int ActionCancellationCount { get; private set; }

        public int ClearCount { get; private set; }

        public int ForkCount { get; private set; }

        public int EnableYoloCount { get; private set; }

        public int EnableFullAccessCount { get; private set; }

        public int DisableYoloCount { get; private set; }

        public int SubscriberCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask<GovernedAgentSendResult> SendAsync(
            GovernedAgentPrompt request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastCancellationToken = cancellationToken;
            SendCount++;
            if (SnapshotOnSend is { } snapshot)
            {
                Snapshot = snapshot;
                RaiseChanged();
            }

            return PendingSend is null
                ? ValueTask.FromResult(SendResult)
                : new ValueTask<GovernedAgentSendResult>(PendingSend.Task);
        }

        public ValueTask<GovernedAgentSteeringResult> SteerAsync(
            GovernedAgentSteering request,
            CancellationToken cancellationToken)
        {
            LastSteering = request;
            LastSteeringCancellationToken = cancellationToken;
            SteeringCount++;
            return PendingSteering is null
                ? ValueTask.FromResult(SteeringResult)
                : new ValueTask<GovernedAgentSteeringResult>(
                    PendingSteering.Task);
        }

        public ValueTask<GovernedAgentFollowUpResult> QueueFollowUpAsync(
            GovernedAgentFollowUp request,
            CancellationToken cancellationToken)
        {
            LastFollowUp = request;
            LastFollowUpCancellationToken = cancellationToken;
            FollowUpCount++;
            return PendingFollowUp is null
                ? ValueTask.FromResult(FollowUpResult)
                : new ValueTask<GovernedAgentFollowUpResult>(PendingFollowUp.Task);
        }

        public ValueTask<GovernedAgentDecisionResult> DecideAsync(
            AgentApprovalId approvalId,
            bool approved,
            CancellationToken cancellationToken)
        {
            LastApprovalId = approvalId;
            LastApprovalDecision = approved;
            LastDecisionCancellationToken = cancellationToken;
            DecisionCount++;
            return ValueTask.FromResult(DecisionResult);
        }

        public ValueTask<GovernedAgentQuestionResponseResult>
            RespondToQuestionAsync(
                AgentQuestionId questionId,
                GovernedAgentQuestionResponse response,
                CancellationToken cancellationToken)
        {
            LastQuestionId = questionId;
            LastQuestionResponse = response;
            LastQuestionCancellationToken = cancellationToken;
            QuestionResponseCount++;
            return ValueTask.FromResult(QuestionResponseResult);
        }

        public ValueTask<GovernedAgentCapabilityDecisionResult>
            DecideCapabilityRequestAsync(
                AgentCapabilityRequestId requestId,
                GovernedAgentCapabilityDecision decision,
                CancellationToken cancellationToken)
        {
            LastCapabilityRequestId = requestId;
            LastCapabilityDecision = decision;
            LastCapabilityDecisionCancellationToken = cancellationToken;
            CapabilityDecisionCount++;
            return PendingCapabilityDecision is null
                ? ValueTask.FromResult(CapabilityDecisionResult)
                : new ValueTask<GovernedAgentCapabilityDecisionResult>(
                    PendingCapabilityDecision.Task);
        }

        public ValueTask<GovernedAgentStopResult> StopAsync(
            CancellationToken cancellationToken)
        {
            LastStopCancellationToken = cancellationToken;
            StopCount++;
            return ValueTask.FromResult(StopResult);
        }

        public ValueTask<GovernedAgentActionCancellationResult>
            CancelActiveActionAsync(CancellationToken cancellationToken)
        {
            LastActionCancellationToken = cancellationToken;
            ActionCancellationCount++;
            if (SnapshotOnActionCancel is { } snapshot)
            {
                Snapshot = snapshot;
                RaiseChanged();
            }

            return PendingActionCancellation is null
                ? ValueTask.FromResult(ActionCancellationResult)
                : new ValueTask<GovernedAgentActionCancellationResult>(
                    PendingActionCancellation.Task);
        }

        public ValueTask<GovernedAgentPolicyResult> EnableYoloAsync(
            TimeSpan lifetime,
            CancellationToken cancellationToken)
        {
            LastYoloLifetime = lifetime;
            LastYoloCancellationToken = cancellationToken;
            EnableYoloCount++;
            return ValueTask.FromResult(PolicyResult);
        }

        public ValueTask<GovernedAgentPolicyResult> EnableFullAccessAsync(
            CancellationToken cancellationToken)
        {
            LastYoloCancellationToken = cancellationToken;
            EnableFullAccessCount++;
            return ValueTask.FromResult(PolicyResult);
        }

        public ValueTask<GovernedAgentPolicyResult> DisableYoloAsync(
            CancellationToken cancellationToken)
        {
            LastYoloCancellationToken = cancellationToken;
            DisableYoloCount++;
            return ValueTask.FromResult(PolicyResult);
        }

        public ValueTask<bool> ClearAsync(CancellationToken cancellationToken)
        {
            LastClearCancellationToken = cancellationToken;
            ClearCount++;
            if (SnapshotOnClear is { } snapshot)
            {
                Snapshot = snapshot;
                RaiseChanged();
            }

            return ValueTask.FromResult(ClearResult);
        }

        public ValueTask<bool> ForkConversationAsync(
            AgentConversationForkPoint forkPoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastForkPoint = forkPoint;
            ForkCount++;
            return ValueTask.FromResult(ForkResult);
        }

        public void RaiseChanged() => _changed?.Invoke(this, EventArgs.Empty);

        public void Dispose() => DisposeCount++;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubProfileRuntime : IAiProviderProfileRuntime
    {
        private EventHandler? _profilesChanged;

        public event EventHandler? ProfilesChanged
        {
            add
            {
                _profilesChanged += value;
                SubscriberCount++;
            }
            remove
            {
                _profilesChanged -= value;
                SubscriberCount--;
            }
        }

        public IReadOnlyList<AiProviderProfileDescriptor> Profiles { get; set; } = [];

        public IReadOnlyList<AiProviderRuntimeDiagnostic> Diagnostics => [];

        public int SubscriberCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int DiscoverModelsCount { get; private set; }

        public AiProviderProfileId? LastDiscoveredProviderId { get; private set; }

        public ValueTask<AiProviderTestResult> TestAsync(
            AiProviderProfile profile,
            CancellationToken cancellationToken)
        {
            _ = profile;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new AiProviderTestResult(
                    false,
                    "ai_provider_unavailable",
                    "Unavailable.",
                    [],
                    AiProviderRuntimeErrorCode.ProviderUnavailable));
        }

        public ValueTask ReloadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<AiProviderModelDiscoveryResult> DiscoverModelsAsync(
            AiProviderProfileId profileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscoverModelsCount++;
            LastDiscoveredProviderId = profileId;
            return ValueTask.FromResult(new AiProviderModelDiscoveryResult(
                true,
                "ai_provider_models_discovered",
                "Models refreshed.",
                []));
        }

        public void RaiseProfilesChanged() =>
            _profilesChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose() => DisposeCount++;
    }

    private sealed class StubAgentRunAuditReader : IAgentRunAuditReader
    {
        private readonly Queue<AuditStoreResult<AgentRunAuditPage>> _results;

        public StubAgentRunAuditReader(
            params AuditStoreResult<AgentRunAuditPage>[] results)
        {
            _results = new Queue<AuditStoreResult<AgentRunAuditPage>>(results);
        }

        public List<AgentRunAuditQuery> Queries { get; } = [];

        public int ReadCount => Queries.Count;

        public ValueTask<AuditStoreResult<AgentRunAuditPage>> ReadAsync(
            AgentRunAuditQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class ImmediateUiThreadDispatcher : IUiThreadDispatcher
    {
        public static ImmediateUiThreadDispatcher Instance { get; } = new();

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private static XDocument DesignSystem() => XDocument.Load(
        Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Styles",
            "DesignSystem.axaml"));
}

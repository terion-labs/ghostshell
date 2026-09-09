using System.Collections.Concurrent;
using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.App.Views;
using Asura.App.Views.Components;
using Asura.Application;
using Asura.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed partial class AgentChatViewModelTests
{
    [Fact]
    public Task Rendered_agent_completion_replaces_progress_without_live_region_updates() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            var committedMessages = Enumerable.Range(0, 100)
                .SelectMany(index => new[]
                {
                    new AgentChatMessage(
                        AgentChatMessageRole.User,
                        $"Test request {index}."),
                    new AgentChatMessage(
                        AgentChatMessageRole.Assistant,
                        $"Test result {index}."),
                })
                .ToArray();
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    state: GovernedAgentState.StreamingProvider,
                    runId: new AgentRunId("run-rendered-completion"),
                    providerId: provider.Id,
                    target: Target(),
                    messages: committedMessages,
                    currentProgress: new GovernedAgentProgress(
                        "Panel test complete",
                        percent: 100)) with
                {
                    ProvisionalAssistantText = "Writing the final report…",
                },
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = view,
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.DoesNotContain(
                    view.GetVisualDescendants().OfType<Control>(),
                    control => AutomationProperties.GetLiveSetting(control)
                        != AutomationLiveSetting.Off);

                runtime.Snapshot = runtime.Snapshot with
                {
                    State = GovernedAgentState.Ready,
                    Messages =
                    [
                        .. committedMessages,
                        new AgentChatMessage(
                            AgentChatMessageRole.Assistant,
                            "The full panel test passed."),
                    ],
                    ProvisionalAssistantText = string.Empty,
                    CurrentProgress = null,
                    Status = string.Empty,
                };
                runtime.RaiseChanged();
                await WaitForVisualAsync<SelectableMarkdownDocument>(
                    view,
                    window,
                    document => document.Text.Contains(
                        "The full panel test passed.",
                        StringComparison.Ordinal),
                    "the committed assistant response");

                Assert.False(viewModel.HasCurrentProgress);
                Assert.Equal(201, viewModel.Messages.Count);
                Assert.Contains(
                    view.GetVisualDescendants().OfType<SelectableMarkdownDocument>(),
                    document => document.Text.Contains(
                        "The full panel test passed.",
                        StringComparison.Ordinal));
                var messages = Assert.IsType<ItemsControl>(
                    view.FindControl<ItemsControl>("AgentChatMessages"));
                Assert.Equal(24, messages.ItemCount);
                Assert.InRange(messages.GetRealizedContainers().Count(), 1, 24);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Replacing_a_long_transcript_with_a_short_one_rebuilds_the_tail() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    runId: new AgentRunId("run-replaced-history"),
                    providerId: provider.Id,
                    target: Target(),
                    messages:
                    [
                        .. Enumerable.Range(0, 80)
                        .Select(index => new AgentChatMessage(
                            AgentChatMessageRole.Assistant,
                            $"Old response {index}.")),
                    ]),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 460,
                Content = view,
            };

            try
            {
                window.Show();
                await Task.Delay(80);
                window.UpdateLayout();
                var materialized = Assert.IsType<ItemsControl>(
                    view.FindControl<ItemsControl>("AgentChatMessages"));
                Assert.Equal(24, materialized.ItemCount);

                runtime.Snapshot = runtime.Snapshot with
                {
                    Messages =
                    [
                        new AgentChatMessage(
                            AgentChatMessageRole.Assistant,
                            "Replacement response."),
                    ],
                };
                runtime.RaiseChanged();
                await Task.Delay(80);
                window.UpdateLayout();

                Assert.Equal(1, materialized.ItemCount);
                Assert.Equal("Replacement response.", viewModel.Messages[0].Content);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Long_agent_history_materializes_in_exact_height_pages_and_remains_scrollable() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            var messages = Enumerable.Range(0, 80)
                .Select(index => new AgentChatMessage(
                    index % 2 == 0
                        ? AgentChatMessageRole.User
                        : AgentChatMessageRole.Assistant,
                    string.Join(
                        "\n\n",
                        Enumerable.Range(0, (index % 7) + 1)
                            .Select(paragraph =>
                                $"Message {index}, paragraph {paragraph}. "
                                + "Variable-height Markdown must keep an exact scroll position."))))
                .ToArray();
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    runId: new AgentRunId("run-materialized-history"),
                    providerId: provider.Id,
                    target: Target(),
                    messages: messages),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 460,
                Content = view,
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                var transcript = Assert.IsType<ScrollViewer>(
                    view.FindControl<ScrollViewer>("AgentChatTranscript"));
                var scrollTrace = new List<string>();
                transcript.ScrollChanged += (_, args) => scrollTrace.Add(
                    $"offset={transcript.Offset.Y:F0}, extent={transcript.Extent.Height:F0}, "
                    + $"offset-delta={args.OffsetDelta.Y:F0}, extent-delta={args.ExtentDelta.Y:F0}");
                var materialized = Assert.IsType<ItemsControl>(
                    view.FindControl<ItemsControl>("AgentChatMessages"));
                Assert.Equal(24, materialized.ItemCount);
                Assert.IsType<StackPanel>(materialized.ItemsPanelRoot);
                Assert.True(transcript.Extent.Height > transcript.Viewport.Height);

                transcript.ScrollToEnd();
                window.UpdateLayout();
                Assert.True(transcript.Offset.Y > 0);
                transcript.Offset = new Vector(0, 0);
                transcript.RaiseEvent(new ScrollChangedEventArgs(
                    Vector.Zero,
                    new Vector(0, -1),
                    Vector.Zero));
                for (var attempt = 0;
                     attempt < 80 && materialized.ItemCount == 24;
                     attempt++)
                {
                    await Task.Delay(10);
                    window.UpdateLayout();
                }

                Assert.Equal(48, materialized.ItemCount);
                await Task.Delay(80);
                window.UpdateLayout();
                scrollTrace.Clear();
                var downwardTarget = Math.Min(
                    transcript.Offset.Y + 120,
                    transcript.Extent.Height - transcript.Viewport.Height);
                transcript.Offset = new Vector(0, downwardTarget);
                await Task.Delay(80);
                window.UpdateLayout();
                var settledOffset = transcript.Offset.Y;
                var secondTarget = Math.Min(
                    settledOffset + 120,
                    transcript.Extent.Height - transcript.Viewport.Height);
                transcript.Offset = new Vector(0, secondTarget);
                await Task.Delay(80);
                window.UpdateLayout();

                Assert.True(
                    transcript.Offset.Y > settledOffset,
                    $"Expected downward progress from {settledOffset:F0}, "
                    + $"actual {transcript.Offset.Y:F0}. "
                    + string.Join("; ", scrollTrace));
                Assert.True(
                    transcript.Offset.Y
                        < transcript.Extent.Height - transcript.Viewport.Height,
                    "Manual scrolling must not snap the transcript back to the end.");
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Idle_conversation_renders_send_and_enter_submits_without_a_stop_overlay() =>
        RunAgentComposerHeadlessAsync(() =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    state: GovernedAgentState.Ready,
                    runId: new AgentRunId("run-rendered-idle"),
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
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = view,
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                var send = Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<Button>(),
                    button => string.Equals(AutomationProperties.GetName(button)
, "Send AI agent prompt", StringComparison.Ordinal));
                var stop = Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<Button>(),
                    button => AutomationProperties.GetName(button)
                        == "Stop AI agent run");

                Assert.True(send.IsEffectivelyVisible);
                Assert.True(send.IsEnabled);
                Assert.False(stop.IsEffectivelyVisible);

                var submitted = 0;
                view.SendAgentChatRequested += (_, _) => submitted++;
                var prompt = view.FindControl<TextBox>("AgentChatPromptInput");
                Assert.NotNull(prompt);
                var status = Assert.Single(
                    view.GetVisualDescendants().OfType<TextBlock>(),
                    text => string.Equals(AutomationProperties.GetName(text)
, "AI agent status", StringComparison.Ordinal));
                var statusTop = Assert.NotNull(
                    status.TranslatePoint(default, view)).Y;
                var promptTop = Assert.NotNull(
                    prompt.TranslatePoint(default, view)).Y;
                Assert.True(
                    statusTop + status.Bounds.Height <= promptTop,
                    "The agent status must render above the composer.");
                prompt.Focus();

                window.KeyPress(
                    Key.Enter,
                    RawInputModifiers.None,
                    PhysicalKey.Enter,
                    null);
                Assert.Equal(1, submitted);

                prompt.Text = "line one";
                prompt.CaretIndex = prompt.Text.Length;
                window.KeyPress(
                    Key.Enter,
                    RawInputModifiers.Shift,
                    PhysicalKey.Enter,
                    null);
                Assert.Equal(1, submitted);
                Assert.Equal("line one\n", prompt.Text);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    [Fact]
    public Task Busy_conversation_renders_ordered_queue_arrow_and_separate_stop() =>
        RunAgentComposerHeadlessAsync(() =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    state: GovernedAgentState.StreamingProvider,
                    runId: new AgentRunId("run-rendered-queue"),
                    providerId: provider.Id,
                    target: Target()) with
                {
                    QueuedFollowUpCount = 3,
                    QueuedFollowUps =
                    [
                        new GovernedAgentQueuedFollowUp(
                            new AgentQueuedFollowUpId("queued-steering"),
                            "Check this next.",
                            AgentReasoningEffort.High,
                            GovernedAgentFollowUpDelivery.Steering),
                        new GovernedAgentQueuedFollowUp(
                            new AgentQueuedFollowUpId("queued-steering-second"),
                            "Then inspect the result.",
                            AgentReasoningEffort.High,
                            GovernedAgentFollowUpDelivery.Steering),
                        new GovernedAgentQueuedFollowUp(
                            new AgentQueuedFollowUpId("queued-follow-up"),
                            "Then summarize.",
                            AgentReasoningEffort.Automatic,
                            GovernedAgentFollowUpDelivery.FollowUp),
                    ],
                },
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance)
            {
                Prompt = "Another queued message.",
            };
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = view,
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Equal(
                    ["Check this next.", "Then inspect the result.", "Then summarize."],
                    viewModel.QueuedFollowUps.Select(item => item.Message), StringComparer.Ordinal);
                var dragHandles = view.GetVisualDescendants()
                    .OfType<Border>()
                    .Where(border => string.Equals(AutomationProperties.GetName(border)
, "Drag queued agent message to reorder", StringComparison.Ordinal))
                    .ToArray();
                Assert.Equal(3, dragHandles.Length);
                Assert.DoesNotContain(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => button.Content as string is "Move earlier" or "Move later");

                AgentQueuedFollowUpMoveRequestedEventArgs? move = null;
                view.MoveQueuedFollowUpRequested += (_, eventArgs) => move = eventArgs;
                var dragStart = Assert.NotNull(dragHandles[0].TranslatePoint(
                    new Point(dragHandles[0].Bounds.Width / 2, dragHandles[0].Bounds.Height / 2),
                    window));
                var dragEnd = Assert.NotNull(dragHandles[1].TranslatePoint(
                    new Point(dragHandles[1].Bounds.Width / 2, dragHandles[1].Bounds.Height - 2),
                    window));
                window.MouseDown(dragStart, MouseButton.Left);
                window.MouseMove(dragEnd, RawInputModifiers.LeftMouseButton);
                window.MouseUp(dragEnd, MouseButton.Left, RawInputModifiers.None);

                Assert.NotNull(move);
                Assert.Equal("Check this next.", move.Item.Message);
                Assert.Equal(1, move.DestinationIndex);
                var send = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(AutomationProperties.GetName(button)
, "Queue a message for the AI agent", StringComparison.Ordinal));
                var stop = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => AutomationProperties.GetName(button)
                        == "Stop AI agent run");
                Assert.True(send.IsEffectivelyVisible);
                Assert.True(send.IsEnabled);
                Assert.True(stop.IsEffectivelyVisible);
                Assert.True(stop.IsEnabled);

                var normalSubmissions = 0;
                var steeringSubmissions = 0;
                view.SendAgentChatRequested += (_, _) => normalSubmissions++;
                view.QueueAgentSteeringRequested += (_, _) => steeringSubmissions++;
                var prompt = view.FindControl<TextBox>("AgentChatPromptInput");
                Assert.NotNull(prompt);
                prompt.Focus();
                window.KeyPress(
                    Key.Enter,
                    RawInputModifiers.Meta,
                    PhysicalKey.Enter,
                    null);
                Assert.Equal(0, normalSubmissions);
                Assert.Equal(1, steeringSubmissions);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    [Fact]
    public Task Rendered_reasoning_selector_reaches_the_governed_send_request() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider(
                "provider",
                "Provider",
                order: 0,
                supportedReasoningEfforts:
                [
                    AgentReasoningEffort.Automatic,
                    AgentReasoningEffort.High,
                ]);
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    providerId: provider.Id,
                    target: Target()),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance)
            {
                Prompt = "Think carefully.",
            };
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = view,
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var picker = view.FindControl<Button>("AgentModelPickerButton");
                Assert.NotNull(picker);
                picker.Flyout!.ShowAt(picker);
                window.UpdateLayout();

                var reasoning = Assert.Single(
                    window.GetVisualDescendants().OfType<ComboBox>(),
                    combo => string.Equals(AutomationProperties.GetName(combo)
, "AI reasoning effort", StringComparison.Ordinal));
                reasoning.SelectedItem = viewModel.ReasoningEfforts.Single(option =>
                    option.Value == AgentReasoningEffort.High);
                Assert.Equal(
                    AgentReasoningEffort.High,
                    viewModel.SelectedReasoningEffort.Value);

                Task send = Task.CompletedTask;
                view.SendAgentChatRequested += (_, _) =>
                    send = viewModel.SendAsync(
                        Target(),
                        AgentPolicy.Default.SelectPrimaryModel(
                            provider.Id.Value,
                            provider.DefaultModel),
                        CancellationToken.None);
                var prompt = view.FindControl<TextBox>("AgentChatPromptInput");
                Assert.NotNull(prompt);
                prompt.Focus();
                window.KeyPress(
                    Key.Enter,
                    RawInputModifiers.None,
                    PhysicalKey.Enter,
                    null);
                await send;

                Assert.Equal(
                    AgentReasoningEffort.High,
                    Assert.IsType<GovernedAgentPrompt>(runtime.LastRequest)
                        .ReasoningEffort);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Rendered_model_picker_preserves_history_and_routes_the_next_turn() =>
        RunAgentComposerHeadlessAsync(async () =>
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
                    runId: new AgentRunId("run-rendered-model-switch"),
                    providerId: provider.Id,
                    target: Target(),
                    messages:
                    [
                        new AgentChatMessage(AgentChatMessageRole.User, "Hello."),
                        new AgentChatMessage(AgentChatMessageRole.Assistant, "Hello!"),
                    ],
                    effectivePolicy: new AgentPolicy(
                        provider.Id.Value,
                        "model",
                        AgentPolicy.Default.Permissions)
                    {
                        CompactionModel = new AgentModelSelection(provider.Id.Value, "model"),
                        TitleModel = new AgentModelSelection(provider.Id.Value, "model"),
                    }),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance)
            {
                Prompt = "Continue.",
            };
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = view,
            };

            try
            {
                Task selection = Task.CompletedTask;
                view.SelectAgentModelRequested += (sender, _) =>
                {
                    var button = Assert.IsType<Button>(sender);
                    selection = viewModel.SelectModelAsync(
                        Assert.IsType<AiProviderModelDescriptor>(button.Tag),
                        CancellationToken.None);
                };

                window.Show();
                window.UpdateLayout();
                var picker = view.FindControl<Button>("AgentModelPickerButton");
                Assert.NotNull(picker);
                picker.Flyout!.ShowAt(picker);
                window.UpdateLayout();

                var fastModel = Assert.Single(
                    window.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(AutomationProperties.GetName(button)
, "Use model Fast model", StringComparison.Ordinal));
                fastModel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await selection;

                Assert.Equal(0, runtime.ClearCount);
                Assert.Equal(
                    ["Hello.", "Hello!"],
                    viewModel.Messages.Select(message => message.Content), StringComparer.Ordinal);
                Assert.Equal("model-fast", viewModel.SelectedModel?.Id);

                await viewModel.SendAsync(
                    Target(),
                    AgentPolicy.Default.SelectPrimaryModel(
                        provider.Id.Value,
                        provider.DefaultModel),
                    CancellationToken.None);
                Assert.Equal("model-fast", runtime.LastRequest?.Model);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Restored_chat_renders_context_usage_and_accepts_full_access_immediately() =>
        RunAgentComposerHeadlessAsync(async () =>
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
                    target: Target(),
                    messages:
                    [
                        new AgentChatMessage(AgentChatMessageRole.User, "Earlier question"),
                        new AgentChatMessage(
                            AgentChatMessageRole.Assistant,
                            "Earlier answer",
                            Usage: new AgentChatUsage(140_000, 1_000, 0, 500, 141_000)),
                    ]),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 420,
                Height = 900,
                Content = view,
            };

            try
            {
                Task selection = Task.CompletedTask;
                view.EnableAgentYoloRequested += (_, _) =>
                    selection = viewModel.SelectFullAccessAsync(CancellationToken.None);
                view.DisableAgentYoloRequested += (_, _) =>
                    selection = viewModel.SelectAskApprovalAsync(CancellationToken.None);
                window.Show();
                window.UpdateLayout();

                var context = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(AutomationProperties.GetName(button)
, "141k / 256k tokens used", StringComparison.Ordinal));
                Assert.True(context.IsEffectivelyVisible);
                var donut = Assert.IsType<ContextWindowDonut>(context.Content);
                Assert.Equal(viewModel.ContextWindowPercent, donut.Percentage);
                Assert.Equal(17, donut.Bounds.Width);
                Assert.Equal(17, donut.Bounds.Height);
                var toolbar = view.FindControl<Grid>("AgentComposerToolbar");
                Assert.NotNull(toolbar);
                var visibleButtons = toolbar.Children
                    .OfType<Button>()
                    .Where(button => button.IsEffectivelyVisible)
                    .OrderBy(button => button.Bounds.Left)
                    .ToArray();
                Assert.Equal(5, visibleButtons.Length);
                for (var index = 1; index < visibleButtons.Length; index++)
                {
                    Assert.True(
                        visibleButtons[index - 1].Bounds.Right
                            <= visibleButtons[index].Bounds.Left,
                        $"{AutomationProperties.GetName(visibleButtons[index - 1])} "
                            + "overlaps "
                            + AutomationProperties.GetName(visibleButtons[index]));
                }

                context.Flyout!.ShowAt(context);
                window.UpdateLayout();
                Assert.Contains(
                    window.GetVisualDescendants().OfType<TextBlock>(),
                    block => string.Equals(block.Text, "Context window", StringComparison.Ordinal) && block.IsEffectivelyVisible);

                var access = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(AutomationProperties.GetName(button)
, "Choose how AI actions are approved", StringComparison.Ordinal));
                access.Flyout!.ShowAt(access);
                window.UpdateLayout();
                var fullAccess = Assert.Single(
                    window.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(AutomationProperties.GetName(button)
, "Enable full access for agent actions", StringComparison.Ordinal));
                fullAccess.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await selection;

                Assert.Equal("Full access", viewModel.AccessModeLabel);
                Assert.Equal(0, runtime.EnableFullAccessCount);
                Assert.False(access.Flyout.IsOpen);

                access.Flyout.ShowAt(access);
                window.UpdateLayout();
                var askApproval = Assert.Single(
                    window.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(AutomationProperties.GetName(button)
, "Ask for approval for agent actions", StringComparison.Ordinal));
                askApproval.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await selection;

                Assert.Equal("Ask approval", viewModel.AccessModeLabel);
                Assert.False(access.Flyout.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Rendered_transcript_uses_selectable_markdown_and_relays_copy_and_fork() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            var forkPoint = new AgentConversationForkPoint(2);
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    state: GovernedAgentState.Ready,
                    runId: new AgentRunId("run-rendered-markdown"),
                    providerId: provider.Id,
                    target: Target(),
                    messages:
                    [
                        new AgentChatMessage(
                            AgentChatMessageRole.User,
                            "**Hello**"),
                        new AgentChatMessage(
                            AgentChatMessageRole.Assistant,
                            "# Answer\n\n- First item",
                            "**Checked the constraints.****Compared alternatives.**\n\n"
                            + "**Concluded.**",
                            new AgentChatUsage(20, 8, 0, 4, 28),
                            ForkPoint: forkPoint),
                    ]),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = view,
            };

            try
            {
                window.Show();
                var reasoningDisclosure = await WaitForVisualAsync<ToggleButton>(
                    view,
                    window,
                    toggle => string.Equals(AutomationProperties.GetName(toggle)
, "Show or hide AI reasoning summary"
, StringComparison.Ordinal) && toggle.IsEffectivelyVisible,
                    "the reasoning disclosure");
                Assert.False(reasoningDisclosure.IsChecked);
                reasoningDisclosure.IsChecked = true;
                _ = await WaitForVisualAsync<SelectableMarkdownDocument>(
                    view,
                    window,
                    block => block.Text.Contains("Hello", StringComparison.Ordinal),
                    "the rendered user message");
                _ = await WaitForVisualAsync<SelectableMarkdownDocument>(
                    view,
                    window,
                    block => block.Text.Contains("Answer", StringComparison.Ordinal)
                        && block.Text.Contains("First item", StringComparison.Ordinal),
                    "the rendered assistant answer");
                _ = await WaitForVisualAsync<SelectableMarkdownDocument>(
                    view,
                    window,
                    block => block.Text.Contains("Checked", StringComparison.Ordinal),
                    "the rendered reasoning summary");
                window.UpdateLayout();

                var renderedText = view.GetVisualDescendants()
                    .OfType<Control>()
                    .Select(RenderedMarkdownText)
                    .OfType<string>()
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
                Assert.Contains(renderedText, text => text.Contains("Hello", StringComparison.Ordinal));
                Assert.Contains(renderedText, text => text.Contains("Answer", StringComparison.Ordinal));
                Assert.Contains(renderedText, text => text.Contains("First item", StringComparison.Ordinal));
                Assert.Contains(renderedText, text => text.Contains("Checked", StringComparison.Ordinal));
                Assert.DoesNotContain(renderedText, text => text.Contains("**", StringComparison.Ordinal));

                var assistantProse = Assert.Single(
                    view.GetVisualDescendants().OfType<SelectableMarkdownDocument>(),
                    block => block.Text.Contains("Answer", StringComparison.Ordinal)
                        && block.Text.Contains("First item", StringComparison.Ordinal));
                Assert.Single(assistantProse.ListLayouts);
                assistantProse.SelectAllText();
                Assert.Contains("Answer", assistantProse.SelectedText, StringComparison.Ordinal);
                Assert.Contains("First item", assistantProse.SelectedText, StringComparison.Ordinal);

                var reasoningProse = Assert.Single(
                    view.GetVisualDescendants().OfType<SelectableMarkdownDocument>(),
                    block => block.Text.Contains("Checked", StringComparison.Ordinal));
                reasoningProse.SelectAllText();
                Assert.Contains("Checked", reasoningProse.SelectedText, StringComparison.Ordinal);
                Assert.Contains("Compared alternatives", reasoningProse.SelectedText, StringComparison.Ordinal);
                Assert.Contains("Concluded", reasoningProse.SelectedText, StringComparison.Ordinal);
                assistantProse.SelectAllText();
                Assert.Empty(reasoningProse.SelectedText);
                reasoningProse.SelectAllText();
                Assert.Empty(assistantProse.SelectedText);

                var transcript = view.FindControl<ScrollViewer>("AgentChatTranscript");
                Assert.NotNull(transcript);
                Assert.False(transcript.BringIntoViewOnFocusChange);

                AgentChatMessageViewModel? copied = null;
                AgentConversationForkPoint? forked = null;
                view.CopyAgentMessageRequested += (sender, _) =>
                    copied = Assert.IsType<Button>(sender).Tag as AgentChatMessageViewModel;
                view.ForkAgentConversationRequested += (sender, _) =>
                    forked = Assert.IsType<AgentConversationForkPoint>(
                        Assert.IsType<Button>(sender).Tag);

                var assistant = viewModel.Messages[1];
                var copy = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => AutomationProperties.GetName(button) == "Copy this message"
                        && ReferenceEquals(button.Tag, assistant)
                        && button.IsEffectivelyVisible);
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(assistant, copied);

                var fork = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => AutomationProperties.GetName(button)
                        == "Fork conversation from this message"
                        && button.IsEffectivelyVisible);
                fork.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(forkPoint, forked);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Empty_assistant_turn_hides_copy_and_fork_actions() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    state: GovernedAgentState.Ready,
                    runId: new AgentRunId("run-rendered-empty-assistant"),
                    providerId: provider.Id,
                    target: Target(),
                    messages:
                    [
                        new AgentChatMessage(
                            AgentChatMessageRole.Assistant,
                            string.Empty,
                            "Inspected the request.",
                            new AgentChatUsage(20, 8, 0, 4, 28),
                            ForkPoint: new AgentConversationForkPoint(1)),
                    ]),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = view,
            };

            try
            {
                window.Show();
                await Task.Delay(100);
                window.UpdateLayout();

                Assert.DoesNotContain(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => button.IsEffectivelyVisible
                        && (AutomationProperties.GetName(button) is
                            "Copy this message"
                            or "Fork conversation from this message"));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Moving_selection_between_reasoning_and_answer_keeps_the_transcript_still() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            var answer = string.Join(
                "\n\n",
                Enumerable.Range(1, 30).Select(index => $"Answer paragraph {index}."));
            var reasoning = string.Join(
                "\n\n",
                Enumerable.Range(1, 18).Select(index => $"Reasoning checkpoint {index}."));
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    runId: new AgentRunId("run-selection-focus"),
                    providerId: provider.Id,
                    target: Target(),
                    messages:
                    [
                        new AgentChatMessage(
                            AgentChatMessageRole.Assistant,
                            answer,
                            reasoning),
                    ]),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 460,
                Content = view,
            };

            try
            {
                window.Show();
                await Task.Delay(100);
                window.UpdateLayout();

                var disclosure = Assert.Single(
                    view.GetVisualDescendants().OfType<ToggleButton>(),
                    toggle => AutomationProperties.GetName(toggle)
                        == "Show or hide AI reasoning summary"
                        && toggle.IsEffectivelyVisible);
                Assert.False(disclosure.IsChecked);
                disclosure.IsChecked = true;
                await Task.Delay(80);
                window.UpdateLayout();

                var transcript = view.FindControl<ScrollViewer>("AgentChatTranscript");
                Assert.NotNull(transcript);
                Assert.False(transcript.BringIntoViewOnFocusChange);
                Assert.True(transcript.Extent.Height > transcript.Viewport.Height);

                // The reasoning and answer are intentionally rendered only
                // after each enters the viewport. Visit both once so this
                // selection test has both documents to move between.
                var reasoningPreview = Assert.Single(
                    view.GetVisualDescendants().OfType<MarkdownPreviewView>(),
                    preview => preview.Text?.Contains(
                        "Reasoning checkpoint",
                        StringComparison.Ordinal) == true
                        && preview.IsEffectivelyVisible);
                reasoningPreview.BringIntoView();
                await Task.Delay(80);
                window.UpdateLayout();
                var answerPreview = Assert.Single(
                    view.GetVisualDescendants().OfType<MarkdownPreviewView>(),
                    preview => preview.Text?.Contains(
                        "Answer paragraph",
                        StringComparison.Ordinal) == true
                        && preview.IsEffectivelyVisible);
                answerPreview.BringIntoView();
                await Task.Delay(80);
                window.UpdateLayout();

                var prose = view.GetVisualDescendants()
                    .OfType<SelectableMarkdownDocument>()
                    .ToArray();
                var answerDocument = Assert.Single(
                    prose,
                    block => block.Text.Contains("Answer paragraph", StringComparison.Ordinal));
                var reasoningDocument = Assert.Single(
                    prose,
                    block => block.Text.Contains("Reasoning checkpoint", StringComparison.Ordinal));

                reasoningDocument.SelectAllText();
                var targetOffset = Math.Min(
                    80,
                    transcript.Extent.Height - transcript.Viewport.Height);
                transcript.Offset = new Vector(0, targetOffset);
                window.UpdateLayout();
                var stableOffset = transcript.Offset.Y;

                answerDocument.SelectAllText();
                window.UpdateLayout();
                Assert.Empty(reasoningDocument.SelectedText);
                Assert.InRange(Math.Abs(transcript.Offset.Y - stableOffset), 0, 0.01);

                reasoningDocument.SelectAllText();
                window.UpdateLayout();
                Assert.Empty(answerDocument.SelectedText);
                Assert.InRange(Math.Abs(transcript.Offset.Y - stableOffset), 0, 0.01);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Clicking_a_code_block_taller_than_the_viewport_keeps_the_transcript_still() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            var code = string.Join(
                '\n',
                Enumerable.Range(1, 120).Select(index => $"const value{index} = {index};"));
            var answer = $"Before the code.\n\n```javascript\n{code}\n```\n\nAfter the code.";
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    runId: new AgentRunId("run-code-block-pointer"),
                    providerId: provider.Id,
                    target: Target(),
                    messages:
                    [
                        new AgentChatMessage(
                            AgentChatMessageRole.Assistant,
                            answer),
                    ]),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 460,
                Content = view,
            };

            try
            {
                window.Show();
                CodePreviewView? codePreview = null;
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    await Task.Delay(25);
                    window.UpdateLayout();
                    codePreview = view.GetVisualDescendants()
                        .OfType<CodePreviewView>()
                        .SingleOrDefault();
                    if (codePreview is { Bounds.Height: > 0 })
                    {
                        break;
                    }
                }

                Assert.NotNull(codePreview);
                var transcript = view.FindControl<ScrollViewer>("AgentChatTranscript");
                Assert.NotNull(transcript);
                Assert.True(codePreview.Bounds.Height > transcript.Viewport.Height);

                await Task.Delay(100);
                window.UpdateLayout();
                var codeTop = codePreview.TranslatePoint(default, transcript);
                Assert.NotNull(codeTop);
                var codeContentTop = codeTop.Value.Y + transcript.Offset.Y;
                var targetOffset = codeContentTop + (codePreview.Bounds.Height * 0.55);
                transcript.Offset = new Vector(0, targetOffset);
                await Task.Delay(25);
                window.UpdateLayout();
                var stableOffset = transcript.Offset.Y;
                var clickPoint = codePreview.TranslatePoint(
                    new Point(codePreview.Bounds.Width / 2, targetOffset - codeContentTop + 40),
                    window);
                Assert.NotNull(clickPoint);
                var hit = window.InputHitTest(clickPoint.Value) as Visual;
                Assert.True(
                    hit is not null,
                    $"Nothing hit at {clickPoint.Value}; window={window.Bounds}; transcript={transcript.Bounds}; code={codePreview.Bounds}; stable={stableOffset}; top={codeContentTop}; target={targetOffset}.");
                Assert.True(
                    ReferenceEquals(hit, codePreview)
                        || hit!.GetVisualAncestors().Contains(codePreview),
                    $"Hit {hit.GetType().Name} instead of the code preview at {clickPoint.Value}.");
                var editor = Assert.Single(
                    codePreview.GetVisualDescendants().OfType<TextEditor>());
                var dragEnd = clickPoint.Value + new Vector(80, 40);

                window.MouseDown(clickPoint.Value, MouseButton.Left);
                window.MouseMove(dragEnd, RawInputModifiers.LeftMouseButton);
                window.MouseUp(dragEnd, MouseButton.Left, RawInputModifiers.None);
                await Task.Delay(50);
                window.UpdateLayout();

                Assert.NotEmpty(editor.SelectedText);
                Assert.InRange(Math.Abs(transcript.Offset.Y - stableOffset), 0, 0.01);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Docked_agent_resize_target_occupies_the_visible_panel_gutter() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    runId: new AgentRunId("run-agent-resize-target"),
                    providerId: provider.Id,
                    target: Target()),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
                Width = 352,
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                ClipToBounds = false,
            };
            view.Classes.Add("edgeResizable");
            view.Classes.Add("edgeRight");
            var host = new Panel { ClipToBounds = false };
            host.Children.Add(view);
            var window = new Window
            {
                Width = 700,
                Height = 700,
                Content = host,
            };

            try
            {
                window.Show();
                await Task.Delay(100);
                window.UpdateLayout();

                var resizeTarget = view.FindControl<Thumb>("FloatingResizeHandle");
                Assert.NotNull(resizeTarget);
                Assert.Equal(8, resizeTarget.Bounds.Width);
                Assert.Equal(view.Bounds.Height, resizeTarget.Bounds.Height);
                await WaitForResizeTargetToOwnGutterAsync(
                    window,
                    view,
                    resizeTarget);

                window.Width = 760;

                Assert.Equal(8, resizeTarget.Bounds.Width);
                Assert.Equal(view.Bounds.Height, resizeTarget.Bounds.Height);
                await WaitForResizeTargetToOwnGutterAsync(
                    window,
                    view,
                    resizeTarget);
            }
            finally
            {
                window.Close();
            }
        });

    private static async Task WaitForResizeTargetToOwnGutterAsync(
        Window window,
        AgentWorkspaceView view,
        Thumb resizeTarget)
    {
        Point? gutterPoint = null;
        Visual? hit = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            window.UpdateLayout();
            var panelEdge = view.TranslatePoint(
                new Point(0, view.Bounds.Height / 2),
                window);
            Assert.NotNull(panelEdge);
            gutterPoint = panelEdge.Value + new Vector(-4, 0);
            hit = window.InputHitTest(gutterPoint.Value) as Visual;
            if (ReferenceEquals(hit, resizeTarget)
                || hit?.GetVisualAncestors().Contains(resizeTarget) == true)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail(
            $"The resize target did not own the dock gutter at {gutterPoint}; hit {hit?.GetType().Name ?? "nothing"}.");
    }

    [Fact]
    public Task Streaming_reasoning_burst_is_coalesced_and_renders_the_latest_markdown() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    state: GovernedAgentState.StreamingProvider,
                    runId: new AgentRunId("run-stream-burst"),
                    providerId: provider.Id,
                    target: Target()),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            var dispatcher = new CountingUiThreadDispatcher();
            using var viewModel = new AgentChatViewModel(runtime, profiles, dispatcher);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = view,
            };

            try
            {
                window.Show();
                runtime.Snapshot = runtime.Snapshot with
                {
                    ProvisionalReasoningSummary = "**Preparing the answer**",
                };
                runtime.RaiseChanged();
                var reasoningLoader = await WaitForVisualAsync<ProgressBar>(
                    view,
                    window,
                    progress => AutomationProperties.GetName(progress)
                        == "Reasoning in progress",
                    "the reasoning progress indicator");
                var reasoningTitle = await WaitForVisualAsync<TextBlock>(
                    view,
                    window,
                    text => AutomationProperties.GetName(text)
                        == "Reasoning status",
                    "the reasoning status label");
                Assert.True(reasoningLoader.IsEffectivelyVisible);
                Assert.True(reasoningLoader.Bounds.Width > reasoningTitle.Bounds.Width);
                Assert.True(reasoningLoader.Bounds.Top >= reasoningTitle.Bounds.Bottom);

                for (var index = 0; index < 500; index++)
                {
                    runtime.Snapshot = runtime.Snapshot with
                    {
                        ProvisionalAssistantText = $"**Answer {index}**",
                        ProvisionalReasoningSummary =
                            $"**Checking step {Math.Max(0, index - 1)}****Checking step {index}**",
                    };
                    runtime.RaiseChanged();
                }

                await WaitForVisualAsync<SelectableMarkdownDocument>(
                    view,
                    window,
                    block => block.Text.Contains("Answer 499", StringComparison.Ordinal),
                    "the latest streamed answer");
                await WaitForVisualAsync<SelectableMarkdownDocument>(
                    view,
                    window,
                    block => block.Text.Contains("step 499", StringComparison.Ordinal),
                    "the latest streamed reasoning step");

                Assert.Equal("**Answer 499**", viewModel.ProvisionalAssistantText);
                Assert.Equal(
                    "**Checking step 498****Checking step 499**",
                    viewModel.ProvisionalReasoningSummary);
                Assert.Equal("Checking step 499", viewModel.ProvisionalReasoningStageDisplay);
                Assert.False(viewModel.ShowProvisionalReasoningLoader);
                Assert.False(reasoningLoader.IsEffectivelyVisible);
                Assert.InRange(dispatcher.InvocationCount, 1, 8);
                Assert.Contains(
                    view.GetVisualDescendants().OfType<SelectableMarkdownDocument>(),
                    block => block.Text.Contains("Answer 499", StringComparison.Ordinal));
                Assert.Contains(
                    view.GetVisualDescendants().OfType<SelectableMarkdownDocument>(),
                    block => block.Text.Contains("step 499", StringComparison.Ordinal));
                Assert.DoesNotContain(
                    view.GetVisualDescendants().OfType<SelectableMarkdownDocument>(),
                    block => block.Text.Contains("step 498", StringComparison.Ordinal));
                var disclosure = Assert.Single(
                    view.GetVisualDescendants().OfType<ToggleButton>(),
                    toggle => AutomationProperties.GetName(toggle)
                        == "Show or hide reasoning in progress"
                        && toggle.IsEffectivelyVisible);
                Assert.True(disclosure.IsChecked);
                disclosure.IsChecked = false;
                window.UpdateLayout();
                Assert.DoesNotContain(
                    view.GetVisualDescendants().OfType<SelectableMarkdownDocument>(),
                    block => block.IsEffectivelyVisible
                        && block.Text.Contains("step 499", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Collapsed_reasoning_header_stretches_and_legacy_parts_render_separately() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    runId: new AgentRunId("run-reasoning-layout"),
                    providerId: provider.Id,
                    target: Target(),
                    messages:
                    [
                        new AgentChatMessage(
                            AgentChatMessageRole.Assistant,
                            "Answer",
                            "**Analyzing the premise****Checking the contradiction****Writing the answer**",
                            new AgentChatUsage(100, 30, 0, 20, 130)),
                    ]),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = view,
            };

            try
            {
                window.Show();
                var disclosure = await WaitForVisualAsync<ToggleButton>(
                    view,
                    window,
                    toggle => string.Equals(AutomationProperties.GetName(toggle)
, "Show or hide AI reasoning summary"
, StringComparison.Ordinal) && toggle.IsEffectivelyVisible,
                    "the collapsed reasoning disclosure");

                Assert.False(disclosure.IsChecked);
                window.UpdateLayout();
                Assert.InRange(disclosure.Bounds.Width, 600, 700);

                disclosure.IsChecked = true;
                var rendered = await WaitForVisualAsync<SelectableMarkdownDocument>(
                    view,
                    window,
                    block => block.IsEffectivelyVisible
                        && block.Text.Contains("Analyzing the premise", StringComparison.Ordinal),
                    "the expanded reasoning summary");
                Assert.Contains("Checking the contradiction", rendered.Text, StringComparison.Ordinal);
                Assert.Contains("Writing the answer", rendered.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("premiseChecking", rendered.Text, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Long_markdown_realizes_only_viewport_editors_and_keeps_complete_copy() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var markdown = string.Join("\n\n", Enumerable.Range(0, 200)
                .Select(index => $"```text\nblock-{index:D3}\n```"));
            var preview = new MarkdownPreviewView { Text = markdown, ContinuousSelection = true };
            var window = new Window { Width = 700, Height = 650, Content = preview };
            try
            {
                window.Show();
                for (var attempt = 0; attempt < 80 && !preview.IsPresentationReady; attempt++)
                {
                    await Task.Delay(25);
                    window.UpdateLayout();
                }

                var list = Assert.Single(preview.GetVisualDescendants().OfType<ListBox>());
                Assert.Equal(200, list.ItemCount);
                Assert.InRange(preview.GetVisualDescendants().OfType<CodePreviewView>().Count(), 1, 32);
                list.ScrollIntoView(list.Items[^1]!);
                for (var attempt = 0; attempt < 40
                    && !preview.GetVisualDescendants().OfType<CodePreviewView>().Any(code => code.Text == "block-199"); attempt++)
                {
                    await Task.Delay(25);
                    window.UpdateLayout();
                }

                Assert.Contains(preview.GetVisualDescendants().OfType<CodePreviewView>(), code => code.Text == "block-199");
                Assert.InRange(preview.GetVisualDescendants().OfType<CodePreviewView>().Count(), 1, 32);
                var copy = Assert.Single(preview.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Copy all Markdown"));
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(markdown, await window.Clipboard!.TryGetTextAsync());
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Huge_markdown_table_keeps_formatted_rows_accessible_in_bounded_pages() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var markdown = "|A|B|\n|-|-|\n" + string.Join('\n', Enumerable.Range(0, 2000).Select(index => $"|{index}|value|"));
            var preview = new MarkdownPreviewView { Text = markdown, ContinuousSelection = true };
            var window = new Window { Width = 700, Height = 650, Content = preview };
            try
            {
                window.Show();
                for (var attempt = 0; attempt < 80 && !preview.IsPresentationReady; attempt++)
                {
                    await Task.Delay(25);
                    window.UpdateLayout();
                }

                Assert.Empty(preview.GetVisualDescendants().OfType<CodePreviewView>());
                Assert.InRange(preview.GetVisualDescendants().OfType<SelectableTextBlock>().Count(), 1, 40);
                var nextRows = Assert.Single(preview.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Next rows"));
                while (nextRows.IsEnabled)
                {
                    nextRows.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                window.UpdateLayout();
                Assert.Contains(preview.GetVisualDescendants().OfType<SelectableTextBlock>(),
                    text => text.Inlines?.OfType<Run>().Any(run => run.Text == "1999") == true);
                Assert.InRange(preview.GetVisualDescendants().OfType<SelectableTextBlock>().Count(), 1, 40);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Markdown_chat_mode_preserves_heading_and_list_styling() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var preview = new MarkdownPreviewView
            {
                Text = "# Answer\n\n- First item with enough words to wrap onto another visual line in a narrow reader.\n- Second item",
                ContinuousSelection = true,
            };
            var window = new Window
            {
                Width = 320,
                Height = 400,
                Content = preview,
            };

            try
            {
                Assert.False(preview.IsPresentationReady);
                window.Show();
                for (var attempt = 0; attempt < 80
                    && !preview.GetVisualDescendants().OfType<SelectableMarkdownDocument>().Any();
                    attempt++)
                {
                    await Task.Delay(25);
                    window.UpdateLayout();
                }
                var prose = Assert.Single(
                    preview.GetVisualDescendants().OfType<SelectableMarkdownDocument>(),
                    block => block.Text.Contains("First item", StringComparison.Ordinal));
                Assert.True(preview.IsPresentationReady);
                Assert.Contains("# Answer", "# " + prose.Text.Split('\n')[0], StringComparison.Ordinal);
                Assert.Equal(2, prose.ListLayouts.Length);
                Assert.True(prose.ListLayouts[0].VisualLineCount > 1);
                Assert.Equal(20, prose.ListLayouts[0].ContentX - prose.ListLayouts[0].MarkerX);
                var dragStart = prose.TranslatePoint(new Point(4, 4), window);
                var dragEnd = prose.TranslatePoint(
                    new Point(prose.Bounds.Width - 4, prose.Bounds.Height - 4),
                    window);
                Assert.NotNull(dragStart);
                Assert.NotNull(dragEnd);
                Assert.True(
                    prose.Bounds.Width <= window.Bounds.Width,
                    $"prose={prose.Bounds}; window={window.Bounds}; start={dragStart}; end={dragEnd}");
                window.MouseDown(
                    dragStart.Value,
                    MouseButton.Left);
                Assert.True(prose.IsFocused, $"Pointer did not reach prose at {dragStart} within {prose.Bounds}.");
                window.MouseMove(dragEnd.Value, RawInputModifiers.LeftMouseButton);
                window.MouseUp(dragEnd.Value, MouseButton.Left, RawInputModifiers.None);
                Assert.Contains("Answer", prose.SelectedText, StringComparison.Ordinal);
                Assert.Contains("Second item", prose.SelectedText, StringComparison.Ordinal);
                prose.SelectAllText();
                Assert.Contains("First item", prose.SelectedText, StringComparison.Ordinal);
                Assert.Contains("Second item", prose.SelectedText, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Markdown_table_scrolls_to_its_rightmost_column_in_a_narrow_reader() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            const string markdown = """
                | Date/time | Finding | Source address | Recovery action |
                |---|---|---|---|
                | 27 Aug 19:05:12 | Encryption began at exactly the same second | 89.117.94.35 | Rebuild from a clean host and rotate every key |
                """;
            var preview = new MarkdownPreviewView
            {
                Text = markdown,
                ContinuousSelection = true,
            };
            var window = new Window
            {
                Width = 300,
                Height = 320,
                Content = preview,
            };

            try
            {
                window.Show();
                await WaitForVisualAsync<SelectableTextBlock>(
                    preview,
                    window,
                    cell => RenderedText(cell).Contains(
                        "Recovery action",
                        StringComparison.Ordinal),
                    "the rightmost Markdown table column");

                var tableViewport = Assert.Single(
                    preview.GetVisualDescendants().OfType<ScrollViewer>(),
                    viewer => string.Equals(
                        AutomationProperties.GetName(viewer),
                        "Scrollable Markdown table",
                        StringComparison.Ordinal));
                window.UpdateLayout();
                Assert.True(
                    tableViewport.Bounds.Width <= preview.Bounds.Width,
                    $"table viewport {tableViewport.Bounds.Width:F0} exceeds reader {preview.Bounds.Width:F0}");
                var maximumOffset = Math.Max(
                    0,
                    tableViewport.Extent.Width - tableViewport.Viewport.Width);
                Assert.True(
                    maximumOffset > 0,
                    $"Expected horizontal overflow, extent={tableViewport.Extent}, viewport={tableViewport.Viewport}.");

                tableViewport.Offset = new Vector(maximumOffset, 0);
                window.UpdateLayout();
                Assert.True(
                    tableViewport.Offset.X >= maximumOffset - 1,
                    $"Expected right edge {maximumOffset:F0}, actual {tableViewport.Offset.X:F0}.");
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Markdown_two_column_table_wraps_to_the_narrow_reader_width() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            const string markdown = """
                | Date/time | Finding |
                |---|---|
                | 27 Aug 19:05:12 | Malicious webshell requests started encryption at exactly the same second |
                """;
            var preview = new MarkdownPreviewView
            {
                Text = markdown,
                ContinuousSelection = true,
            };
            var window = new Window
            {
                Width = 300,
                Height = 320,
                Content = preview,
            };

            try
            {
                window.Show();
                var finding = await WaitForVisualAsync<SelectableTextBlock>(
                    preview,
                    window,
                    cell => RenderedText(cell).Contains(
                        "Malicious webshell",
                        StringComparison.Ordinal),
                    "the wrapped Markdown table finding");
                var tableViewport = Assert.Single(
                    preview.GetVisualDescendants().OfType<ScrollViewer>(),
                    viewer => string.Equals(
                        AutomationProperties.GetName(viewer),
                        "Scrollable Markdown table",
                        StringComparison.Ordinal));
                window.UpdateLayout();

                Assert.True(
                    tableViewport.Extent.Width
                        <= tableViewport.Viewport.Width + 1,
                    $"Two columns should fit: extent={tableViewport.Extent}, viewport={tableViewport.Viewport}.");
                Assert.True(
                    finding.Bounds.Height > 20,
                    $"Expected the finding to wrap, actual bounds={finding.Bounds}.");
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Incident_report_markdown_never_collapses_to_a_blank_message() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            const string markdown = """
                ## Assessment

                **Treat this as a confirmed data breach, not merely ransomware.** Read-only triage found direct evidence that sensitive data was collected before encryption.

                ### What was collected

                A Zimbra-owned archive was created in `/tmp` on **20 Aug 2026, 17:03 EEST**:

                - Compressed size: **29.9 MB**
                - Uncompressed: **123.7 MB**
                - Contents: **1,260 files**

                It contains:

                - Complete LDAP user exports
                - **1,214 MySQL CSV exports** covering mailbox groups
                - Zimbra authentication-token and pre-authentication keys
                - Numerous TLS private keys

                ## Reconstructed timeline

                | Time, EEST | Activity |
                |---|---|
                | **14 Aug** | Malicious JSP webshell `c1c5b4f8.jsp` active |
                | **20 Aug 17:01–17:03** | LDAP, mailbox databases, keys, and secrets exported and zipped |
                | **27 Aug 19:05:12** | Encryption begins at exactly the same second |

                ## Required response

                1. **Snapshot the Droplet and attached volumes first** for evidence.
                2. Then isolate or power it down.
                3. Build a new mail server from a clean, supported OS.
                4. Rotate every password, certificate, private key, and session.

                **Bottom line:** account-directory data, mailbox metadata, password material, and server-side cryptographic secrets should be considered stolen.
                """;
            var preview = new MarkdownPreviewView
            {
                Text = markdown,
                ContinuousSelection = true,
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = preview,
            };

            try
            {
                window.Show();
                var document = await WaitForVisualAsync<SelectableMarkdownDocument>(
                    preview,
                    window,
                    candidate => candidate.Bounds.Height > 0
                        && candidate.Text.Contains("Bottom line", StringComparison.Ordinal),
                    "the incident report Markdown body");

                Assert.Contains("Snapshot the Droplet", document.Text, StringComparison.Ordinal);
                var renderedText = preview.GetVisualDescendants()
                    .OfType<Control>()
                    .Select(RenderedMarkdownText)
                    .OfType<string>()
                    .ToArray();
                Assert.Contains(
                    renderedText,
                    text => text.Contains("Reconstructed timeline", StringComparison.Ordinal));
                Assert.Contains(
                    renderedText,
                    text => text.Contains("27 Aug 19:05:12", StringComparison.Ordinal));
                Assert.False(Assert.IsType<SelectableTextBlock>(
                    preview.FindControl<SelectableTextBlock>("PlainTextFallback"))
                    .IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Code_only_fence_renders_in_chat_mode() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            const string markdown = """
                ```json
                {
                  "ok": true,
                  "results": []
                }
                ```
                """;
            var preview = new MarkdownPreviewView
            {
                Text = markdown,
                ContinuousSelection = true,
            };
            var window = new Window
            {
                Width = 640,
                Height = 480,
                Content = preview,
            };

            try
            {
                window.Show();
                CodePreviewView? code = null;
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    await Task.Delay(25);
                    window.UpdateLayout();
                    code = preview.GetVisualDescendants()
                        .OfType<CodePreviewView>()
                        .SingleOrDefault();
                    if (code is { Bounds.Height: > 0 })
                    {
                        break;
                    }
                }

                Assert.NotNull(code);
                Assert.True(code.IsEffectivelyVisible);
                Assert.Contains("\"ok\": true", code.Text, StringComparison.Ordinal);
                Assert.True(code.Bounds.Height > 0);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task LaTeX_from_an_assistant_message_renders_in_the_shared_markdown_surface() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            const string markdown = """
                Let \(B\) be true.

                \[
                B \rightarrow G_T
                \]

                And \(C\land S_1=\text{false}\).
                """;
            var preview = new MarkdownPreviewView
            {
                Text = markdown,
                ContinuousSelection = true,
            };
            var window = new Window
            {
                Width = 640,
                Height = 480,
                Content = preview,
            };

            try
            {
                window.Show();
                SelectableMarkdownDocument? document = null;
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    await Task.Delay(25);
                    window.UpdateLayout();
                    document = preview.GetVisualDescendants()
                        .OfType<SelectableMarkdownDocument>()
                        .SingleOrDefault();
                    if (document?.MathFormulaCount == 3)
                    {
                        break;
                    }
                }

                Assert.NotNull(document);
                Assert.Equal(3, document.MathFormulaCount);
                Assert.Contains("B \\rightarrow G_T", document.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("\\[", document.Text, StringComparison.Ordinal);
                document.SelectAllText();
                Assert.Contains("C\\land S_1", document.SelectedText, StringComparison.Ordinal);

                var properties = new GenericTextRunProperties(
                    Typeface.Default,
                    13,
                    foregroundBrush: Brushes.White);
                Assert.True(MarkdownMathDrawableTextRun.TryCreate(
                    "\\text{true}\\oplus\\text{false}=\\text{true}",
                    properties,
                    displayStyle: true,
                    out var rendered));
                Assert.True(rendered.Size.Width > 1);
                Assert.True(rendered.Size.Height >= 13);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Mermaid_fence_from_an_assistant_message_renders_in_the_real_transcript() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            var provider = Provider("provider", "Provider", order: 0);
            using var runtime = new StubGovernedRuntime
            {
                Snapshot = Snapshot(
                    state: GovernedAgentState.Ready,
                    runId: new AgentRunId("run-rendered-mermaid"),
                    providerId: provider.Id,
                    target: Target(),
                    messages:
                    [
                        new AgentChatMessage(
                            AgentChatMessageRole.Assistant,
                            """
                            Here is the flow.

                            ```mermaid
                            flowchart LR
                                Start --> Finish
                            ```
                            """),
                    ]),
            };
            using var profiles = new StubProfileRuntime { Profiles = [provider] };
            using var viewModel = new AgentChatViewModel(
                runtime,
                profiles,
                ImmediateUiThreadDispatcher.Instance);
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(viewModel),
            };
            var window = new Window
            {
                Width = 700,
                Height = 800,
                Content = view,
            };

            try
            {
                window.Show();
                DatabaseMermaidDiagramView? diagram = null;
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    await Task.Delay(25);
                    window.UpdateLayout();
                    diagram = view.GetVisualDescendants()
                        .OfType<DatabaseMermaidDiagramView>()
                        .SingleOrDefault();
                    if (diagram?.HasRenderedDiagram == true)
                    {
                        break;
                    }
                }

                Assert.NotNull(diagram);
                Assert.True(diagram.HasRenderedDiagram);
                Assert.Contains("<svg", diagram.RenderedSvg, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    view.GetVisualDescendants().OfType<CodePreviewView>(),
                    block => block.Text?.Contains("flowchart LR", StringComparison.Ordinal) == true);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Markdown_mermaid_fences_render_in_chat_and_file_preview_modes() =>
        RunAgentComposerHeadlessAsync(async () =>
        {
            const string markdown = """
                Before the diagram.

                ```mermaid
                flowchart LR
                    Start --> Finish
                ```

                After the diagram.
                """;
            var chatPreview = new MarkdownPreviewView
            {
                Text = markdown,
                ContinuousSelection = true,
            };
            var filePreview = new MarkdownPreviewView
            {
                Text = markdown,
            };
            var window = new Window
            {
                Width = 760,
                Height = 900,
                Content = new StackPanel
                {
                    Children =
                    {
                        chatPreview,
                        filePreview,
                    },
                },
            };

            try
            {
                window.Show();
                DatabaseMermaidDiagramView[] diagrams = [];
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    await Task.Delay(25);
                    window.UpdateLayout();
                    diagrams = [.. window.GetVisualDescendants().OfType<DatabaseMermaidDiagramView>()];
                    if (diagrams.Length == 2 && diagrams.All(diagram => diagram.HasRenderedDiagram))
                    {
                        break;
                    }
                }

                Assert.Equal(2, diagrams.Length);
                Assert.All(diagrams, diagram =>
                {
                    Assert.Equal("Rendered Mermaid diagram", AutomationProperties.GetName(diagram));
                    Assert.True(diagram.HasRenderedDiagram);
                    Assert.Contains("<svg", diagram.RenderedSvg, StringComparison.Ordinal);
                });
                Assert.DoesNotContain(
                    window.GetVisualDescendants().OfType<CodePreviewView>(),
                    preview => preview.Text?.Contains("flowchart LR", StringComparison.Ordinal) == true);
                Assert.Contains(
                    chatPreview.GetVisualDescendants().OfType<SelectableMarkdownDocument>(),
                    text => text.Text.Contains("Before the diagram", StringComparison.Ordinal));
                Assert.Contains(
                    chatPreview.GetVisualDescendants().OfType<SelectableMarkdownDocument>(),
                    text => text.Text.Contains("After the diagram", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Missing_agent_runtime_renders_only_the_setup_state() =>
        RunAgentComposerHeadlessAsync(() =>
        {
            var view = new AgentWorkspaceView
            {
                DataContext = new AgentComposerHost(null),
            };
            var window = new Window
            {
                Width = 700,
                Height = 900,
                Content = view,
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                var setup = Assert.IsType<StateOverlay>(
                    view.FindControl<StateOverlay>("AgentSetupRequiredState"));
                Assert.True(setup.IsEffectivelyVisible);
                Assert.Equal(StateOverlayKind.PermissionRequired, setup.Kind);
                Assert.False(Assert.IsType<ScrollViewer>(
                    view.FindControl<ScrollViewer>("AgentChatTranscript"))
                    .IsEffectivelyVisible);
                var noProvider = Assert.IsType<StateOverlay>(
                    view.FindControl<StateOverlay>("AgentNoProviderState"));
                Assert.False(noProvider.IsEffectivelyVisible);
                Assert.Equal(StateOverlayKind.Offline, noProvider.Kind);
                var failedTurn = Assert.IsType<StateOverlay>(
                    view.FindControl<StateOverlay>("AgentFailedTurnState"));
                Assert.False(failedTurn.IsEffectivelyVisible);
                Assert.Equal(StateOverlayKind.Retry, failedTurn.Kind);
                Assert.False(Assert.IsType<ItemsControl>(
                    view.FindControl<ItemsControl>("AgentQueuedFollowUps"))
                    .IsEffectivelyVisible);
                Assert.False(Assert.IsType<Border>(
                    view.FindControl<Border>("AgentComposer"))
                    .IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    private static async Task RunAgentComposerHeadlessAsync(Func<Task> assertion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(
            typeof(SqlEditorHeadlessApplication));
        try
        {
            var completed = await session.Dispatch(
                async () =>
                {
                    await assertion();
                    return true;
                },
                timeout.Token);
            Assert.True(completed);
        }
        finally
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (InvalidOperationException exception)
                when (IsCompletedHeadlessQueueRace(exception))
            {
                // Avalonia 12.0.5 can complete its empty dispatch queue after
                // the worker passed its cancellation check but before Take.
            }
        }
    }

    [Fact]
    public void Headless_teardown_filter_accepts_only_completed_queue_take()
    {
        using var queue = new BlockingCollection<int>();
        queue.CompleteAdding();
        var completedQueueTake = Assert.Throws<InvalidOperationException>(
            () => queue.Take());

        Assert.True(IsCompletedHeadlessQueueRace(completedQueueTake));
        Assert.False(IsCompletedHeadlessQueueRace(new InvalidOperationException()));
    }

    private static bool IsCompletedHeadlessQueueRace(
        InvalidOperationException exception)
    {
        var declaringType = exception.TargetSite?.DeclaringType;
        return exception.TargetSite?.Name == nameof(BlockingCollection<int>.Take)
            && declaringType?.IsGenericType == true
            && declaringType.GetGenericTypeDefinition() == typeof(BlockingCollection<>);
    }

    private static async Task<TControl> WaitForVisualAsync<TControl>(
        Control root,
        Window window,
        Func<TControl, bool> predicate,
        string description)
        where TControl : Control
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            window.UpdateLayout();
            var match = root.GetVisualDescendants()
                .OfType<TControl>()
                .FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(10);
        }

        var messages = root.FindControl<ItemsControl>("AgentChatMessages");
        var transcript = root.FindControl<ScrollViewer>("AgentChatTranscript");
        var realized = messages?.GetRealizedContainers().ToArray() ?? [];
        var realizedIndexes = messages is null
            ? string.Empty
            : string.Join(",", realized.Select(messages.IndexFromContainer));
        var previews = root.GetVisualDescendants()
            .OfType<MarkdownPreviewView>()
            .Prepend(root as MarkdownPreviewView)
            .OfType<MarkdownPreviewView>()
            .ToArray();
        Assert.Fail(
            $"Timed out waiting for {description}. "
            + $"messages={messages?.ItemCount}; "
            + $"realized={realized.Length} [{realizedIndexes}]; "
            + $"panel={messages?.ItemsPanelRoot?.Bounds}; "
            + $"offset={transcript?.Offset}; "
            + $"extent={transcript?.Extent}; "
            + $"viewport={transcript?.Viewport}; "
            + $"previews={previews.Length}; "
            + $"rendering={previews.Count(preview => preview.FindControl<TextBlock>("RenderingState")?.IsVisible == true)}; "
            + $"fallbacks={previews.Count(preview => preview.FindControl<SelectableTextBlock>("PlainTextFallback")?.IsVisible == true)}.");
        return null!;
    }

    private sealed record AgentComposerHost(AgentChatViewModel? AgentChat)
        : IAgentWorkspaceHost
    {
        public bool IsAgentPanelDocked => false;

        public string AgentPanelPinTip => string.Empty;
    }

    private static string RenderedText(SelectableTextBlock block) =>
        !string.IsNullOrEmpty(block.Text)
            ? block.Text
            : string.Concat(block.Inlines?.OfType<Run>().Select(run => run.Text) ?? []);

    private static string? RenderedMarkdownText(Control control) => control switch
    {
        SelectableMarkdownDocument document => document.Text,
        CodePreviewView document => document.Text,
        SelectableTextBlock selectable => RenderedText(selectable),
        TextBlock text => text.Text,
        _ => null,
    };

    private sealed class CountingUiThreadDispatcher : IUiThreadDispatcher
    {
        public int InvocationCount { get; private set; }

        public bool RequiresFramePacing => true;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            action();
            return Task.CompletedTask;
        }
    }
}

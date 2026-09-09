using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task Steering_replaces_the_visible_user_input_and_preserves_the_run_manifest()
    {
        var provider = new ControlledSteeringProvider();
        await using var fixture = new SteeringRuntimeFixture(provider);
        const string original = "Inspect production.";
        const string update = "Use staging and report only health.";
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt(original),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        await WaitUntilAsync(
            () => fixture.Runtime.Snapshot.ProvisionalAssistantText
                .Contains("obsolete", StringComparison.Ordinal));
        var runId = Assert.IsType<AgentRunId>(
            fixture.Runtime.Snapshot.RunId);
        var generation = fixture.Runtime.Snapshot.SteeringGeneration!.Value;
        var auditCount = fixture.Audit.Events.Count;
        var stale = await fixture.Runtime.SteerAsync(
            new GovernedAgentSteering(
                new AgentRunId("stale-run"),
                generation,
                "Do not apply."),
            CancellationToken.None);

        var accepted = await fixture.Runtime.SteerAsync(
            new GovernedAgentSteering(runId, generation, update),
            CancellationToken.None);
        await provider.ReplacementEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.False(stale.IsAccepted);
        Assert.Equal("agent_steering_run_changed", stale.Code);
        Assert.True(accepted.IsAccepted);
        Assert.Equal("agent_steering_applied", accepted.Code);
        Assert.False(fixture.Runtime.Snapshot.SteeringAvailable);
        Assert.False(fixture.Runtime.Snapshot.CanSteer);
        Assert.Equal(
            $"{original}\n\nSteering update:\n{update}",
            Assert.Single(
                fixture.Runtime.Snapshot.Messages,
                message => message.Role == AgentChatMessageRole.User).Content);
        Assert.DoesNotContain(
            "obsolete",
            fixture.Runtime.Snapshot.ProvisionalAssistantText,
            StringComparison.Ordinal);
        await WaitUntilAsync(
            () => fixture.Runtime.Snapshot.ProvisionalAssistantText
                .Contains("revised", StringComparison.Ordinal));

        var second = await fixture.Runtime.SteerAsync(
            new GovernedAgentSteering(
                runId,
                generation,
                "A second update."),
            CancellationToken.None);
        var requests = provider.Requests.ToArray();
        Assert.False(second.IsAccepted);
        Assert.Equal("agent_steering_not_available", second.Code);
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].RunId, requests[1].RunId);
        Assert.Equal(
            requests[0].Tools.Select(ToolFingerprint),
            requests[1].Tools.Select(ToolFingerprint), StringComparer.Ordinal);
        Assert.Equal(
            $"{original}\n\nSteering update:\n{update}",
            Assert.Single(
                requests[1].Messages,
                message => message.Role == AgentMessageRole.User).Content);
        Assert.Equal(auditCount, fixture.Audit.Events.Count);
        Assert.Empty(fixture.Terminal.Actions);

        provider.CompleteReplacement.TrySetResult();
        var completed = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(completed.IsSuccess);
        Assert.Equal(GovernedAgentState.Ready, fixture.Runtime.Snapshot.State);
        Assert.Equal(
            [
                $"{original}\n\nSteering update:\n{update}",
                "revised answer",
            ],
            fixture.Runtime.Snapshot.Messages.Select(message =>
                message.Content), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Steering_is_not_available_during_target_resolution_or_tool_continuation()
    {
        var initialProvider = new ControlledSteeringProvider();
        await using (var resolving = new SteeringRuntimeFixture(initialProvider))
        {
            resolving.Context.BlockInspectionNumber = 1;
            var sending = resolving.Runtime.SendAsync(
                resolving.Prompt("Inspect."),
                CancellationToken.None).AsTask();
            await resolving.Context.BlockedInspection.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            Assert.Equal(
                GovernedAgentState.StreamingProvider,
                resolving.Runtime.Snapshot.State);
            Assert.False(resolving.Runtime.Snapshot.SteeringAvailable);
            Assert.False(resolving.Runtime.Snapshot.CanSteer);

            resolving.Context.ReleaseInspection.TrySetResult();
            await WaitForSteeringAsync(resolving.Runtime);
            _ = await resolving.Runtime.StopAsync(CancellationToken.None);
            _ = await sending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var continuationProvider = new ControlledToolContinuationProvider();
        await using var continuation = new SteeringRuntimeFixture(
            continuationProvider);
        continuation.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(
                continuation.Context.Screen("ready", contentRevision: 1)));
        var continuing = continuation.Runtime.SendAsync(
            continuation.Prompt("Read and summarize."),
            CancellationToken.None).AsTask();
        await continuationProvider.ContinuationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => continuation.Runtime.Snapshot.State
                == GovernedAgentState.StreamingProvider);
        var runId = Assert.IsType<AgentRunId>(
            continuation.Runtime.Snapshot.RunId);

        var rejected = await continuation.Runtime.SteerAsync(
            new GovernedAgentSteering(
                runId,
                expectedGeneration: 1,
                "Change the summary."),
            CancellationToken.None);

        Assert.False(rejected.IsAccepted);
        Assert.Equal("agent_steering_not_available", rejected.Code);
        Assert.False(continuation.Runtime.Snapshot.SteeringAvailable);
        Assert.False(continuation.Runtime.Snapshot.CanSteer);
        continuationProvider.CompleteContinuation.TrySetResult();
        Assert.True(
            (await continuing.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task Concurrent_steering_attempt_is_rejected_without_consuming_the_first()
    {
        var provider = new ControlledSteeringProvider();
        await using var fixture = new SteeringRuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        fixture.Context.BlockInspectionNumber =
            fixture.Context.InspectionCount + 1;
        var runId = fixture.Runtime.Snapshot.RunId!.Value;
        var generation =
            fixture.Runtime.Snapshot.SteeringGeneration!.Value;
        var first = fixture.Runtime.SteerAsync(
            new GovernedAgentSteering(
                runId,
                generation,
                "Use staging."),
            CancellationToken.None).AsTask();
        await fixture.Context.BlockedInspection.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        var competing = await fixture.Runtime.SteerAsync(
            new GovernedAgentSteering(
                runId,
                generation,
                "Use development."),
            CancellationToken.None);

        Assert.False(competing.IsAccepted);
        Assert.Equal("agent_steering_in_progress", competing.Code);
        fixture.Context.ReleaseInspection.TrySetResult();
        Assert.True(
            (await first.WaitAsync(TimeSpan.FromSeconds(5))).IsAccepted);
        await provider.ReplacementEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        provider.CompleteReplacement.TrySetResult();
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task Delayed_steering_from_an_earlier_turn_cannot_steer_the_same_run()
    {
        var provider = new ControlledSteeringProvider();
        await using var fixture = new SteeringRuntimeFixture(provider);
        var firstSending = fixture.Runtime.SendAsync(
            fixture.Prompt("First turn."),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        var runId = fixture.Runtime.Snapshot.RunId!.Value;
        var staleGeneration =
            fixture.Runtime.Snapshot.SteeringGeneration!.Value;
        provider.CompleteOriginal.TrySetResult();
        Assert.True(
            (await firstSending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        var secondSending = fixture.Runtime.SendAsync(
            fixture.Prompt("Second turn."),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() =>
            provider.Requests.Count == 2
            && fixture.Runtime.Snapshot.SteeringAvailable
            && fixture.Runtime.Snapshot.CanSteer
            && fixture.Runtime.Snapshot.SteeringGeneration != staleGeneration);
        var currentGeneration =
            fixture.Runtime.Snapshot.SteeringGeneration!.Value;

        var stale = await fixture.Runtime.SteerAsync(
            new GovernedAgentSteering(
                runId,
                staleGeneration,
                "This belongs to the first turn."),
            CancellationToken.None);

        Assert.False(stale.IsAccepted);
        Assert.Equal("agent_steering_generation_changed", stale.Code);
        Assert.NotEqual(staleGeneration, currentGeneration);
        Assert.Equal(runId, fixture.Runtime.Snapshot.RunId);
        Assert.Equal(
            currentGeneration,
            fixture.Runtime.Snapshot.SteeringGeneration);
        Assert.True(fixture.Runtime.Snapshot.CanSteer);
        Assert.Equal(2, provider.Requests.Count);
        _ = await fixture.Runtime.StopAsync(CancellationToken.None);
        _ = await secondSending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Original_commit_wins_cleanly_while_steering_reinspection_is_blocked()
    {
        var provider = new ControlledSteeringProvider();
        await using var fixture = new SteeringRuntimeFixture(provider);
        const string original = "Inspect.";
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt(original),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        fixture.Context.BlockInspectionNumber =
            fixture.Context.InspectionCount + 1;
        var steering = fixture.Runtime.SteerAsync(
            CurrentSteering(fixture.Runtime, "Use staging."),
            CancellationToken.None).AsTask();
        await fixture.Context.BlockedInspection.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        provider.CompleteOriginal.TrySetResult();
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        fixture.Context.ReleaseInspection.TrySetResult();
        var result = await steering.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsAccepted);
        Assert.Equal("agent_steering_not_available", result.Code);
        Assert.Single(provider.Requests);
        Assert.Equal(
            [original, "obsolete draftoriginal answer"],
            fixture.Runtime.Snapshot.Messages.Select(message =>
                message.Content), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Target_provider_and_policy_drift_each_fail_before_kernel_steering()
    {
        await AssertSteeringDriftAsync(
            fixture =>
                fixture.Context.ReplaceSessionAfterInspection =
                    fixture.Context.InspectionCount,
            "agent_steering_target_changed");
        await AssertSteeringDriftAsync(
            fixture => fixture.ProviderResolver.Binding.IsCurrent = false,
            "agent_steering_provider_changed");
        await AssertSteeringDriftAsync(
            fixture => SetPrivateField(
                fixture.Runtime,
                "_effectivePolicy",
                AgentPolicy.Default),
            "agent_steering_policy_changed");
    }

    [Fact]
    public async Task Caller_cancellation_restores_availability_only_for_the_same_live_generation()
    {
        var provider = new ControlledSteeringProvider();
        await using var fixture = new SteeringRuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        fixture.Context.BlockInspectionNumber =
            fixture.Context.InspectionCount + 1;
        using var cancellation = new CancellationTokenSource();
        var steering = fixture.Runtime.SteerAsync(
            CurrentSteering(fixture.Runtime, "Use staging."),
            cancellation.Token).AsTask();
        await fixture.Context.BlockedInspection.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var result = await steering.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsAccepted);
        Assert.Equal("agent_steering_cancelled", result.Code);
        Assert.True(fixture.Runtime.Snapshot.SteeringAvailable);
        Assert.True(fixture.Runtime.Snapshot.CanSteer);
        _ = await fixture.Runtime.StopAsync(CancellationToken.None);
        _ = await sending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancelled_host_result_maps_caller_cancellation_without_consuming_steering()
    {
        var provider = new ControlledSteeringProvider();
        await using var fixture = new SteeringRuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        fixture.Context.ReturnCancelledResult = true;
        using var cancellation = new CancellationTokenSource();
        var cancellationTriggered = 0;
        fixture.Runtime.Changed += (_, _) =>
        {
            if (fixture.Runtime.Snapshot.Status.StartsWith(
                    "Checking the steering update",
                    StringComparison.Ordinal)
                && Interlocked.Exchange(ref cancellationTriggered, 1) == 0)
            {
                cancellation.Cancel();
            }
        };

        var result = await fixture.Runtime.SteerAsync(
            CurrentSteering(fixture.Runtime, "Use staging."),
            cancellation.Token);

        Assert.False(result.IsAccepted);
        Assert.Equal("agent_steering_cancelled", result.Code);
        Assert.True(fixture.Runtime.Snapshot.CanSteer);
        Assert.NotNull(fixture.Runtime.Snapshot.SteeringGeneration);
        _ = await fixture.Runtime.StopAsync(CancellationToken.None);
        _ = await sending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stop_wins_against_a_steering_attempt_blocked_in_target_reinspection()
    {
        var provider = new ControlledSteeringProvider();
        await using var fixture = new SteeringRuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        fixture.Context.BlockInspectionNumber =
            fixture.Context.InspectionCount + 1;
        var steering = fixture.Runtime.SteerAsync(
            CurrentSteering(fixture.Runtime, "Use staging."),
            CancellationToken.None).AsTask();
        await fixture.Context.BlockedInspection.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        var stopped = await fixture.Runtime.StopAsync(CancellationToken.None);
        var result = await steering.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Context.ReleaseInspection.TrySetResult();

        Assert.True(stopped.WasRunning);
        Assert.False(result.IsAccepted);
        Assert.Equal("agent_steering_not_available", result.Code);
        Assert.Equal(GovernedAgentState.Cancelled, fixture.Runtime.Snapshot.State);
        Assert.False(fixture.Runtime.Snapshot.SteeringAvailable);
        _ = await sending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_wins_against_a_steering_attempt_blocked_in_target_reinspection()
    {
        var provider = new ControlledSteeringProvider();
        await using var fixture = new SteeringRuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        fixture.Context.BlockInspectionNumber =
            fixture.Context.InspectionCount + 1;
        var steering = fixture.Runtime.SteerAsync(
            CurrentSteering(fixture.Runtime, "Use staging."),
            CancellationToken.None).AsTask();
        await fixture.Context.BlockedInspection.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        await fixture.Runtime.DisposeAsync();
        var result = await steering.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Context.ReleaseInspection.TrySetResult();

        Assert.False(result.IsAccepted);
        Assert.Equal("agent_steering_not_available", result.Code);
        Assert.Equal(GovernedAgentState.Cancelled, fixture.Runtime.Snapshot.State);
        Assert.False(fixture.Runtime.Snapshot.SteeringAvailable);
        _ = await sending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Provider_completion_during_changed_notification_uses_the_captured_turn_token()
    {
        using var provider = new SynchronouslyCompletingProvider();
        await using var fixture = new SteeringRuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        await provider.CompletionWaitStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var completionTriggered = 0;
        var completionCommittedInsideCallback = false;
        fixture.Runtime.Changed += (_, _) =>
        {
            if (!fixture.Runtime.Snapshot.Status.StartsWith(
                    "Checking the steering update",
                    StringComparison.Ordinal)
                || Interlocked.Exchange(ref completionTriggered, 1) != 0)
            {
                return;
            }

            provider.Complete();
            completionCommittedInsideCallback = SpinWait.SpinUntil(
                () => sending.IsCompleted,
                TimeSpan.FromSeconds(5));
        };

        var result = await fixture.Runtime.SteerAsync(
            CurrentSteering(fixture.Runtime, "Use staging."),
            CancellationToken.None);

        Assert.True(completionCommittedInsideCallback);
        Assert.False(result.IsAccepted);
        Assert.Equal("agent_steering_not_available", result.Code);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    private sealed class SynchronouslyCompletingProvider : IAgentProvider, IDisposable
    {
        private readonly ManualResetEventSlim _completion = new(false);

        public TaskCompletionSource CompletionWaitStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => _completion.Set();

        public void Dispose() => _completion.Dispose();

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            yield return new AgentProviderEvent.ResponseStarted();
            yield return new AgentProviderEvent.TextDelta("original answer");
            CompletionWaitStarted.TrySetResult();
            // ConsumeProviderAsync already owns a worker here. Parking that
            // worker lets the Changed callback force the provider commit
            // without waiting for another thread-pool continuation.
            _completion.Wait(cancellationToken);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.EndTurn);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Rejection_status_survives_a_later_provider_delta()
    {
        var provider = new ControlledSteeringProvider
        {
            HoldOriginalAfterLateDelta = true,
        };
        await using var fixture = new SteeringRuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        fixture.ProviderResolver.Binding.IsCurrent = false;

        var result = await fixture.Runtime.SteerAsync(
            CurrentSteering(fixture.Runtime, "Use staging."),
            CancellationToken.None);
        provider.CompleteOriginal.TrySetResult();
        await provider.LateOriginalDeltaPublished.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => fixture.Runtime.Snapshot.ProvisionalAssistantText
                .Contains("original answer", StringComparison.Ordinal));

        Assert.False(result.IsAccepted);
        Assert.Equal("agent_steering_provider_changed", result.Code);
        Assert.Equal(result.Message, fixture.Runtime.Snapshot.Status);
        provider.ReleaseOriginalCompletion.TrySetResult();
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task Approval_state_cannot_be_changed_or_decided_by_steering()
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.SendTextThenAnswer("date"));
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run date."),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => fixture.Runtime.Snapshot.State
                == GovernedAgentState.AwaitingApproval);
        var approval = Assert.IsType<GovernedAgentApproval>(
            fixture.Runtime.Snapshot.PendingApproval);

        var steering = await fixture.Runtime.SteerAsync(
            new GovernedAgentSteering(
                fixture.Runtime.Snapshot.RunId!.Value,
                expectedGeneration: 1,
                "Approve it."),
            CancellationToken.None);

        Assert.False(steering.IsAccepted);
        Assert.Equal("agent_steering_not_available", steering.Code);
        Assert.Same(approval, fixture.Runtime.Snapshot.PendingApproval);
        Assert.Empty(fixture.Terminal.Actions);
        _ = await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: false,
            CancellationToken.None);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task Kernel_limit_and_capacity_rejections_do_not_mutate_the_visible_input()
    {
        var largeProvider = new ControlledSteeringProvider();
        await using (var large = new SteeringRuntimeFixture(largeProvider))
        {
            var original = new string(
                '\u0800',
                GovernedAgentPrompt.MaximumMessageLength);
            var sending = large.Runtime.SendAsync(
                large.Prompt(original),
                CancellationToken.None).AsTask();
            await WaitForSteeringAsync(large.Runtime);
            await WaitUntilAsync(() => largeProvider.Requests.Count == 1);

            var result = await large.Runtime.SteerAsync(
                new GovernedAgentSteering(
                    large.Runtime.Snapshot.RunId!.Value,
                    large.Runtime.Snapshot.SteeringGeneration!.Value,
                    new string(
                        '\u0800',
                        GovernedAgentSteering.MaximumUpdateLength)),
                CancellationToken.None);

            Assert.False(result.IsAccepted);
            Assert.Equal("agent_steering_limit_exceeded", result.Code);
            Assert.Equal(
                original,
                Assert.Single(
                    large.Runtime.Snapshot.Messages,
                    message => message.Role == AgentChatMessageRole.User).Content);
            Assert.Single(largeProvider.Requests);
            largeProvider.CompleteOriginal.TrySetResult();
            Assert.True(
                (await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        }

        var busyProvider = new ControlledSteeringProvider();
        await using var busy = new SteeringRuntimeFixture(busyProvider);
        var busySending = busy.Runtime.SendAsync(
            busy.Prompt("Inspect."),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(busy.Runtime);
        await WaitUntilAsync(() => busyProvider.Requests.Count == 1);
        var session = GetPrivateField<NativeAgentSession>(
            busy.Runtime,
            "_session");
        SetPrivateField(session, "_providerOperationsInFlight", 2);

        var busyResult = await busy.Runtime.SteerAsync(
            CurrentSteering(busy.Runtime, "Use staging."),
            CancellationToken.None);

        Assert.False(busyResult.IsAccepted);
        Assert.Equal("agent_steering_provider_busy", busyResult.Code);
        Assert.Equal(
            "Inspect.",
            Assert.Single(
                busy.Runtime.Snapshot.Messages,
                message => message.Role == AgentChatMessageRole.User).Content);
        Assert.Single(busyProvider.Requests);
        busyProvider.CompleteOriginal.TrySetResult();
        Assert.True(
            (await busySending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    private static async Task AssertSteeringDriftAsync(
        Action<SteeringRuntimeFixture> introduceDrift,
        string expectedCode)
    {
        var provider = new ControlledSteeringProvider();
        await using var fixture = new SteeringRuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None).AsTask();
        await WaitForSteeringAsync(fixture.Runtime);
        await WaitUntilAsync(() => provider.Requests.Count == 1);
        introduceDrift(fixture);

        var result = await fixture.Runtime.SteerAsync(
            CurrentSteering(fixture.Runtime, "Use staging."),
            CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.Equal(expectedCode, result.Code);
        Assert.Single(provider.Requests);
        provider.CompleteOriginal.TrySetResult();
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    private static async Task WaitForSteeringAsync(
        GovernedAgentRuntime runtime) =>
        await WaitUntilAsync(
            () => runtime.Snapshot.SteeringAvailable
                && runtime.Snapshot.CanSteer);

    private static GovernedAgentSteering CurrentSteering(
        GovernedAgentRuntime runtime,
        string update) =>
        new(
            runtime.Snapshot.RunId!.Value,
            runtime.Snapshot.SteeringGeneration!.Value,
            update);

    private static string ToolFingerprint(AgentToolDefinition tool) =>
        $"{tool.Name}\n{tool.Description}\n{tool.InputSchema.GetRawText()}";

    private static T GetPrivateField<T>(object instance, string name)
        where T : class =>
        Assert.IsType<T>(
            instance.GetType()
                .GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(instance));

    private static void SetPrivateField(
        object instance,
        string name,
        object value) =>
        instance.GetType()
            .GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed class SteeringRuntimeFixture : IAsyncDisposable
    {
        public SteeringRuntimeFixture(IAgentProvider provider)
        {
            Context = new ContextClient();
            Audit = new RecordingAuditStore();
            Broker = new AgentCapabilityBroker(
                BuiltInAgentTools.Catalog,
                Audit,
                TimeProvider.System);
            Terminal = new ConsumingTerminalHost(
                Broker,
                new AgentTerminalActionComposer(),
                Context);
            ProviderResolver = new FixedProviderResolver(provider);
            Runtime = new GovernedAgentRuntime(
                Context,
                Broker,
                Terminal,
                agentBrowserHost: null,
                new AgentTerminalActionComposer(),
                browserComposer: null,
                BuiltInAgentTools.Catalog,
                ProviderResolver,
                new TestApprovalPrincipal(new ClientId("desktop-client")),
                TimeProvider.System,
                AgentPolicy.Default);
        }

        public ContextClient Context { get; }

        public RecordingAuditStore Audit { get; }

        public AgentCapabilityBroker Broker { get; }

        public ConsumingTerminalHost Terminal { get; }

        public FixedProviderResolver ProviderResolver { get; }

        public GovernedAgentRuntime Runtime { get; }

        public GovernedAgentPrompt Prompt(string message) =>
            new(
                new AiProviderProfileId("provider-1"),
                message,
                Context.Target,
                Runtime.Snapshot.EffectivePolicy!.SelectPrimaryModel(
                    "provider-1",
                    ProviderResolver.Binding.DefaultModel));

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Runtime.DisposeAsync();
                await Broker.DisposeAsync();
            }
            finally
            {
                Context.DisposeCancellationRegistration();
            }
        }
    }

    private sealed class ControlledSteeringProvider : IAgentProvider
    {
        private int _callCount;

        public ConcurrentQueue<AgentProviderRequest> Requests { get; } = [];

        public TaskCompletionSource CompleteOriginal { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReplacementEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CompleteReplacement { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LateOriginalDeltaPublished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseOriginalCompletion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HoldOriginalAfterLateDelta { get; init; }

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            var call = Interlocked.Increment(ref _callCount);
            yield return new AgentProviderEvent.ResponseStarted();
            if (call == 1)
            {
                yield return new AgentProviderEvent.TextDelta("obsolete draft");
                await CompleteOriginal.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                yield return new AgentProviderEvent.TextDelta("original answer");
                if (HoldOriginalAfterLateDelta)
                {
                    LateOriginalDeltaPublished.TrySetResult();
                    await ReleaseOriginalCompletion.Task
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                yield return new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn);
                yield break;
            }

            if (call != 2)
            {
                throw new InvalidOperationException(
                    "Steering may create only one replacement provider operation.");
            }

            ReplacementEntered.TrySetResult();
            yield return new AgentProviderEvent.TextDelta("revised answer");
            await CompleteReplacement.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.EndTurn);
        }
    }

    private sealed class ControlledToolContinuationProvider : IAgentProvider
    {
        private int _callCount;

        public TaskCompletionSource ContinuationEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CompleteContinuation { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            yield return new AgentProviderEvent.ResponseStarted();
            if (call == 1)
            {
                yield return new AgentProviderEvent.ToolCallStarted(
                    0,
                    "read-1",
                    ProviderToolName.FromInternal(BuiltInAgentTools.TerminalReadScreen));
                yield return new AgentProviderEvent.ToolCallArgumentsDelta(
                    0,
                    "{}");
                yield return new AgentProviderEvent.ToolCallCompleted(0);
                yield return new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.ToolUse);
                yield break;
            }

            if (call != 2
                || !request.Messages.Any(message =>
                    message.Role == AgentMessageRole.Tool))
            {
                throw new InvalidOperationException(
                    "The tool continuation received an unexpected request.");
            }

            ContinuationEntered.TrySetResult();
            yield return new AgentProviderEvent.TextDelta("summary");
            await CompleteContinuation.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.EndTurn);
        }
    }
}

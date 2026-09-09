using System.Collections.Concurrent;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task ReportProgressIsAlwaysAdvertisedWithAClosedBoundedSchema()
    {
        var provider = new ProviderRound((_, _) => Answer("Done."));
        await using var fixture = new RuntimeFixture(provider);

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Check the deployment."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var tool = Assert.Single(
            Assert.Single(provider.Requests).Tools,
            candidate => string.Equals(candidate.Name
, IntrinsicAgentTools.ReportProgress, StringComparison.Ordinal));
        Assert.Contains(
            "Never include credentials",
            tool.Description,
            StringComparison.Ordinal);
        var schema = tool.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["message"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);

        var properties = schema.GetProperty("properties");
        Assert.Equal(
            ["message", "percent"],
            properties.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        var message = properties.GetProperty("message");
        Assert.Equal("string", message.GetProperty("type").GetString());
        Assert.Equal(1, message.GetProperty("minLength").GetInt32());
        Assert.Equal(
            GovernedAgentProgress.MaximumMessageBytes,
            message.GetProperty("maxLength").GetInt32());
        var percent = properties.GetProperty("percent");
        Assert.Equal("integer", percent.GetProperty("type").GetString());
        Assert.Equal(0, percent.GetProperty("minimum").GetInt32());
        Assert.Equal(100, percent.GetProperty("maximum").GetInt32());
    }

    [Fact]
    public void ReportProgressParserDelegatesContentValidationToTheDomainType()
    {
        using var document = JsonDocument.Parse(
            """{"message":"Reviewed 12 of 20 hosts","percent":60}""");

        var parsed = Assert.IsType<AgentReportProgressParseResult.Parsed>(
            AgentReportProgressIntrinsic.Parse(document.RootElement));

        Assert.Equal("Reviewed 12 of 20 hosts", parsed.Progress.Message);
        Assert.Equal(60, parsed.Progress.Percent);
        Assert.Equal(
            GovernedAgentProgress.UntrustedModelContentOrigin,
            parsed.Progress.ContentOrigin);
    }

    [Fact]
    public void ReportProgressParserAcceptsAnOmittedPercent()
    {
        using var document = JsonDocument.Parse(
            """{"message":"Inspecting services"}""");

        var parsed = Assert.IsType<AgentReportProgressParseResult.Parsed>(
            AgentReportProgressIntrinsic.Parse(document.RootElement));

        Assert.Equal("Inspecting services", parsed.Progress.Message);
        Assert.Null(parsed.Progress.Percent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"message":42}""")]
    [InlineData("""{"message":"Working","extra":true}""")]
    [InlineData("""{"message":"one","message":"two"}""")]
    [InlineData("""{"message":"Working","percent":null}""")]
    [InlineData("""{"message":"Working","percent":1.5}""")]
    [InlineData("""{"message":"Working","percent":-1}""")]
    [InlineData("""{"message":"Working","percent":101}""")]
    [InlineData("""{"message":"first\nsecond"}""")]
    [InlineData("""{"message":"token=literal-progress-secret"}""")]
    public void ReportProgressParserRejectsUnknownDuplicateOrUnsafeInput(
        string json)
    {
        using var document = JsonDocument.Parse(json);

        var rejected =
            Assert.IsType<AgentReportProgressParseResult.Rejected>(
                AgentReportProgressIntrinsic.Parse(document.RootElement));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public void ReportProgressParserRejectsMoreThan512Utf8Bytes()
    {
        var message = string.Concat(new string('\u00E9', 256), "x");
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new { message }));

        var rejected =
            Assert.IsType<AgentReportProgressParseResult.Rejected>(
                AgentReportProgressIntrinsic.Parse(document.RootElement));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task ReportProgressReplacesTheVisibleValueAndReturnsOnlyAReceipt()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => Progress("progress-1", "Inspecting services", 20),
            2 => Progress("progress-2", "Reviewing logs", 80),
            3 => Answer("The deployment is healthy."),
            _ => throw new InvalidOperationException(
                "The progress provider received an unexpected round."),
        })
        {
            BlockOnCall = 3,
        };
        await using var fixture = new RuntimeFixture(provider);
        ConcurrentQueue<GovernedAgentProgress> observed = [];
        fixture.Runtime.Changed += (_, _) =>
        {
            if (fixture.Runtime.Snapshot.CurrentProgress is { } progress)
            {
                observed.Enqueue(progress);
            }
        };

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Check the deployment."),
            CancellationToken.None).AsTask();
        await provider.BlockedCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            new GovernedAgentProgress("Reviewing logs", 80),
            fixture.Runtime.Snapshot.CurrentProgress);
        provider.ReleaseBlockedCall.TrySetResult();
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
        Assert.Contains(
            observed,
            progress => progress is
            {
                Message: "Inspecting services",
                Percent: 20,
            });
        Assert.Contains(
            observed,
            progress => progress is
            {
                Message: "Reviewing logs",
                Percent: 80,
            });
        Assert.All(
            provider.Requests
                .SelectMany(request => request.Messages)
                .Where(message => message.Role == AgentMessageRole.Tool),
            message => Assert.Equal(
                """{"ok":true}""",
                message.ToolResult?.Value.Content));
        Assert.Equal(
            ["Check the deployment.", "The deployment is healthy."],
            fixture.Runtime.Snapshot.Messages.Select(message => message.Content), StringComparer.Ordinal);
        Assert.DoesNotContain(
            fixture.Runtime.Snapshot.Messages,
            message => message.Content.Contains(
                "Inspecting services",
                StringComparison.Ordinal)
                || message.Content.Contains(
                    "Reviewing logs",
                    StringComparison.Ordinal));
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Terminal.Permits);
        Assert.Empty(fixture.Audit.Events);
        Assert.Equal(3, fixture.Context.InspectionCount);
    }

    [Fact]
    public async Task InvalidProgressDoesNotReplaceThePreviousValueAndProviderContinues()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => Progress("progress-valid", "Inspecting services", 25),
            2 => ToolCall(
                "progress-invalid",
                IntrinsicAgentTools.ReportProgress,
                """{"message":"discard me","extra":true}"""),
            _ => throw new InvalidOperationException(
                "The blocked provider round must not execute."),
        })
        {
            BlockOnCall = 3,
        };
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Check the deployment."),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => provider.Requests.Count == 3);

        Assert.Equal(
            new GovernedAgentProgress("Inspecting services", 25),
            fixture.Runtime.Snapshot.CurrentProgress);
        var invalidResult = Assert.Single(
            provider.Requests.ToArray()[2].Messages,
            message => string.Equals(message.ToolResult?.ProviderCallId
, "progress-invalid", StringComparison.Ordinal)).ToolResult;
        Assert.NotNull(invalidResult);
        Assert.Equal(
            AgentToolResultStatus.Failed,
            invalidResult.Status);
        Assert.Equal(
            "invalid_tool_arguments",
            invalidResult.StableCode);
        Assert.Equal(
            """{"ok":false,"error":{"code":"invalid_tool_arguments","retryable":false}}""",
            invalidResult.Value.Content);
        Assert.Equal(2, fixture.Context.InspectionCount);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);

        Assert.True((await fixture.Runtime.StopAsync(
            CancellationToken.None)).WasRunning);
        Assert.False((await sending).IsSuccess);
        Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
        Assert.True(await fixture.Runtime.ClearAsync(CancellationToken.None));
        Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
    }

    [Fact]
    public async Task ReportProgressRejectsAStalePinnedTargetAndContinues()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => Progress("progress-stale", "Inspecting services", 40),
            2 => Answer("The target changed."),
            _ => throw new InvalidOperationException(
                "The stale-target provider received an unexpected round."),
        });
        await using var fixture = new RuntimeFixture(provider);
        fixture.Context.ReplaceSessionAfterInspection = 1;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Check the deployment."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
        var failure = Assert.Single(
            provider.Requests.ToArray()[1].Messages,
            message => message.Role == AgentMessageRole.Tool).ToolResult;
        Assert.NotNull(failure);
        Assert.Equal("target_changed", failure.StableCode);
        Assert.Equal(
            """{"ok":false,"error":{"code":"target_changed","retryable":false}}""",
            failure.Value.Content);
        Assert.Equal(2, fixture.Context.InspectionCount);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task CancellationDuringTargetReinspectionCannotPublishProgress()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => Progress("progress-cancel", "Inspecting services", 20),
            _ => throw new InvalidOperationException(
                "Cancellation must prevent provider continuation."),
        });
        await using var fixture = new RuntimeFixture(provider);
        fixture.Context.BlockInspectionNumber = 2;
        using var cancellation = new CancellationTokenSource();
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Check the deployment."),
            cancellation.Token).AsTask();
        await fixture.Context.BlockedInspection.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsSuccess);
        Assert.Equal("agent_cancelled", result.Code);
        Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
        Assert.Single(provider.Requests);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task ProviderFailureAfterProgressClearsTheVisibleValue()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => Progress("progress-failure", "Inspecting services", 20),
            2 => [new AgentProviderEvent.ResponseStarted()],
            _ => throw new InvalidOperationException(
                "The failing provider received an unexpected round."),
        });
        await using var fixture = new RuntimeFixture(provider);

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Check the deployment."),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(GovernedAgentState.Failed, fixture.Runtime.Snapshot.State);
        Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task FailedFullAccessDowngradeKeepsTheActiveTurnRecoverable()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => Answer("The governed run is bound."),
            2 => Progress("progress-before-policy-failure", "Inspecting services", 40),
            3 => Answer("This response must remain blocked until cleanup."),
            _ => throw new InvalidOperationException(
                "The policy-failure provider received an unexpected round."),
        })
        {
            BlockOnCall = 3,
        };
        await using var fixture = new RuntimeFixture(provider);
        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Bind this governed run."),
            CancellationToken.None)).IsSuccess);
        Assert.True((await fixture.Runtime.EnableYoloAsync(
            TimeSpan.FromMinutes(5),
            CancellationToken.None)).IsAccepted);
        fixture.Audit.FailurePredicate = auditEvent =>
            auditEvent.Details is AuditDetails.AgentRunPolicyTransitionDetails
            {
                Transition: AgentRunPolicyTransition.YoloDisabled,
            };

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Check the deployment."),
            CancellationToken.None).AsTask();
        await provider.BlockedCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            new GovernedAgentProgress("Inspecting services", 40),
            fixture.Runtime.Snapshot.CurrentProgress);

        var downgrade = await fixture.Runtime.DisableYoloAsync(
            CancellationToken.None);

        Assert.False(downgrade.IsAccepted);
        Assert.Equal(
            GovernedAgentState.StreamingProvider,
            fixture.Runtime.Snapshot.State);
        Assert.Equal(
            new GovernedAgentProgress("Inspecting services", 40),
            fixture.Runtime.Snapshot.CurrentProgress);
        Assert.False(sending.IsCompleted);

        fixture.Audit.FailurePredicate = null;
        var retried = await fixture.Runtime.DisableYoloAsync(
            CancellationToken.None);
        provider.ReleaseBlockedCall.TrySetResult();
        var sendResult = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(retried.IsAccepted);
        Assert.True(sendResult.IsSuccess);
        Assert.Equal(
            GovernedAgentState.Ready,
            fixture.Runtime.Snapshot.State);
        Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
    }

    [Fact]
    public async Task StopAndDisposeClearProgressWhileProviderContinuationIsBlocked()
    {
        var stopProvider = BlockingAfterOneProgress();
        await using (var fixture = new RuntimeFixture(stopProvider))
        {
            var sending = fixture.Runtime.SendAsync(
                fixture.Prompt("Check the deployment."),
                CancellationToken.None).AsTask();
            await WaitUntilAsync(
                () => fixture.Runtime.Snapshot.CurrentProgress is not null
                    && stopProvider.Requests.Count == 2);

            Assert.True((await fixture.Runtime.StopAsync(
                CancellationToken.None)).WasRunning);
            Assert.False((await sending).IsSuccess);
            Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
        }

        var disposeProvider = BlockingAfterOneProgress();
        await using var disposeFixture = new RuntimeFixture(disposeProvider);
        var disposeSending = disposeFixture.Runtime.SendAsync(
            disposeFixture.Prompt("Check the deployment."),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => disposeFixture.Runtime.Snapshot.CurrentProgress is not null
                && disposeProvider.Requests.Count == 2);

        await disposeFixture.Runtime.DisposeAsync();
        Assert.False((await disposeSending).IsSuccess);
        Assert.Null(disposeFixture.Runtime.Snapshot.CurrentProgress);
    }

    private static ProviderRound BlockingAfterOneProgress() =>
        new((call, _) => call switch
        {
            1 => Progress("progress-blocked", "Inspecting services", 20),
            _ => throw new InvalidOperationException(
                "The blocked provider round must not execute."),
        })
        {
            BlockOnCall = 2,
        };

    private static AgentProviderEvent[] Progress(
        string callId,
        string message,
        int? percent) =>
        ToolCall(
            callId,
            IntrinsicAgentTools.ReportProgress,
            percent is { } value
                ? JsonSerializer.Serialize(new
                {
                    message,
                    percent = value,
                })
                : JsonSerializer.Serialize(new
                {
                    message,
                }));

    private static AgentProviderEvent[] ToolCall(
        string callId,
        string toolName,
        string arguments) =>
    [
        new AgentProviderEvent.ResponseStarted(),
        new AgentProviderEvent.ToolCallStarted(
            0,
            callId,
            ProviderToolName.FromInternal(toolName)),
        new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments),
        new AgentProviderEvent.ToolCallCompleted(0),
        new AgentProviderEvent.ResponseCompleted(
            AgentProviderStopReason.ToolUse),
    ];

    private static AgentProviderEvent[] Answer(string text) =>
    [
        new AgentProviderEvent.ResponseStarted(),
        new AgentProviderEvent.TextDelta(text),
        new AgentProviderEvent.ResponseCompleted(
            AgentProviderStopReason.EndTurn),
    ];
}

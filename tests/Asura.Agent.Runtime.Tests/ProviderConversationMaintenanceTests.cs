using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Asura.Agent;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class ProviderConversationMaintenanceTests
{
    [Fact]
    public async Task TitleGeneratorUsesConfiguredRouteAndReturnsBoundedModelText()
    {
        var provider = new TextProvider("Roman Aqueduct Engineering");
        var resolver = new Resolver(provider);
        var generator = new ProviderConversationTitleGenerator(
            resolver,
            new AgentModelSelection("title-profile", "title-model"));

        var title = await generator.GenerateAsync(
            [
                new AgentMessage(AgentMessageRole.User, "Tell me about Roman aqueducts"),
                new AgentMessage(AgentMessageRole.Assistant, "They used gravity."),
            ],
            CancellationToken.None);

        Assert.Equal("Roman Aqueduct Engineering", title);
        Assert.Equal(new AiProviderProfileId("title-profile"), resolver.LastProfileId);
        Assert.Equal("title-model", resolver.Binding.LastModel);
        Assert.Contains("3–8 word title", Assert.Single(provider.Requests).Messages[^1].Content);
    }

    [Fact]
    public async Task CompactorUsesStructuredPiCheckpointPromptAndReturnsSummaryRole()
    {
        var provider = new TextProvider(
            "## Goal\nContinue the task.\n\n## Constraints & Preferences\n- Keep history.");
        var resolver = new Resolver(provider);
        var compactor = new ProviderConversationCompactor(
            resolver,
            new AgentModelSelection("compact-profile", "compact-model"));

        var summary = await compactor.CompactAsync(
            new AgentCompactionRequest(
                new AgentRunId("run-1"),
                1,
                [
                    new AgentMessage(AgentMessageRole.Summary, "Older summary"),
                    new AgentMessage(AgentMessageRole.User, "Continue"),
                    new AgentMessage(AgentMessageRole.Assistant, "Working"),
                ]),
            CancellationToken.None);

        Assert.Equal(AgentMessageRole.Summary, summary.Role);
        Assert.Contains("## Goal", summary.Content);
        var prompt = Assert.Single(provider.Requests).Messages[^1].Content;
        Assert.Contains("## Critical Context", prompt);
        Assert.Contains("Older summary", prompt);
        Assert.Contains("<conversation>", prompt);
    }

    [Fact]
    public async Task SplitTurnCompactionSummarizesHistoryAndTurnPrefixSeparately()
    {
        var provider = new SequenceTextProvider(
            "## Goal\nRetain prior work.",
            "## Original Request\nInspect the workspace.\n\n## Context for Suffix\nContinue the tool chain.");
        var resolver = new Resolver(provider);
        var compactor = new ProviderConversationCompactor(
            resolver,
            new AgentModelSelection("compact-profile", "compact-model"));

        var summary = await compactor.CompactAsync(
            new AgentCompactionRequest(
                new AgentRunId("run-split"),
                2,
                [new AgentMessage(AgentMessageRole.User, "Earlier work")],
                [new AgentMessage(AgentMessageRole.User, "Inspect everything")]),
            CancellationToken.None);

        Assert.Contains("## Goal", summary.Content);
        Assert.Contains("**Turn Context (split turn):**", summary.Content);
        Assert.Contains("## Original Request", summary.Content);
        Assert.Collection(
            provider.Requests,
            request => Assert.Contains(
                "## Critical Context",
                request.Messages[^1].Content),
            request => Assert.Contains(
                "PREFIX of a turn",
                request.Messages[^1].Content));
    }

    private sealed class Resolver(IAgentProvider provider) : IAgentProviderResolver
    {
        public Binding Binding { get; } = new(provider);

        public AiProviderProfileId? LastProfileId { get; private set; }

        public IAgentProviderBinding PinProvider(AiProviderProfileId profileId)
        {
            LastProfileId = profileId;
            return Binding;
        }
    }

    private sealed class Binding(IAgentProvider provider) : IAgentProviderBinding
    {
        public AiProviderProfileId ProfileId { get; } =
            new("maintenance-profile");

        public long Revision => 1;

        public string DefaultModel => "default-model";

        public bool IsCurrent => true;

        public string? LastModel { get; private set; }

        public IAgentProvider CreateProvider(string model)
        {
            LastModel = model;
            return provider;
        }
    }

    private sealed class TextProvider(string text) : IAgentProvider
    {
        public List<AgentProviderRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AgentProviderEvent.ResponseStarted();
            yield return new AgentProviderEvent.TextDelta(text);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.EndTurn);
            await Task.Yield();
        }
    }

    private sealed class SequenceTextProvider(params string[] texts) : IAgentProvider
    {
        private int _index;

        public List<AgentProviderRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AgentProviderEvent.ResponseStarted();
            yield return new AgentProviderEvent.TextDelta(texts[_index++]);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.EndTurn);
            await Task.Yield();
        }
    }
}

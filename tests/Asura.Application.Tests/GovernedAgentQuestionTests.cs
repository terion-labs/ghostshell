using System.Collections.Immutable;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class GovernedAgentQuestionTests
{
    private static readonly DateTimeOffset Expiry =
        new(2026, 7, 25, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Question_id_preserves_a_valid_runtime_identity()
    {
        var id = new AgentQuestionId("question-1");

        Assert.Equal("question-1", id.Value);
        Assert.Equal("question-1", id.ToString());
        Assert.NotEqual(AgentQuestionId.New(), AgentQuestionId.New());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\u00A0")]
    public void Question_id_rejects_a_missing_identity(string? value)
    {
        var error = Assert.ThrowsAny<ArgumentException>(
            () => new AgentQuestionId(value!));

        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void Question_preserves_bounded_untrusted_model_content()
    {
        var id = new AgentQuestionId("question-1");

        var question = new GovernedAgentQuestion(
            id,
            "Which deployment region should I inspect?",
            Expiry);

        Assert.Equal(id, question.Id);
        Assert.Equal(
            "Which deployment region should I inspect?",
            question.Question);
        Assert.Equal(Expiry, question.ExpiresAtUtc);
        Assert.Equal(
            GovernedAgentQuestion.UntrustedModelContentOrigin,
            question.ContentOrigin);
        Assert.Equal("untrusted_model_question", question.ContentOrigin);
    }

    [Fact]
    public void Question_requires_a_nondefault_identity_and_utc_expiry()
    {
        var missingId = Assert.Throws<ArgumentException>(
            () => new GovernedAgentQuestion(
                default,
                "Which region?",
                Expiry));
        var localExpiry = Assert.Throws<ArgumentException>(
            () => new GovernedAgentQuestion(
                new AgentQuestionId("question-1"),
                "Which region?",
                Expiry.ToOffset(TimeSpan.FromHours(2))));

        Assert.Equal("id", missingId.ParamName);
        Assert.Equal("expiresAtUtc", localExpiry.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\u00A0")]
    [InlineData("first\nsecond")]
    [InlineData("first\rsecond")]
    [InlineData("first\u2028second")]
    [InlineData("bell\u0007")]
    [InlineData("hidden\u200Bformat")]
    [InlineData("token=literal-question-secret")]
    [InlineData("authorization: bearer literal-question-secret")]
    [InlineData("https://user:password@example.test")]
    public void Question_rejects_blank_multiline_unsafe_or_secret_text(
        string? question)
    {
        var error = Assert.ThrowsAny<ArgumentException>(
            () => new GovernedAgentQuestion(
                new AgentQuestionId("question-1"),
                question!,
                Expiry));

        Assert.Equal("question", error.ParamName);
    }

    [Fact]
    public void Question_rejects_invalid_unicode()
    {
        foreach (var question in new[]
                 {
                     string.Concat("invalid", '\uD800'),
                     string.Concat("invalid", '\uDC00'),
                 })
        {
            var error = Assert.Throws<ArgumentException>(
                () => new GovernedAgentQuestion(
                    new AgentQuestionId("question-1"),
                    question,
                    Expiry));

            Assert.Equal("question", error.ParamName);
        }
    }

    [Fact]
    public void Question_enforces_its_utf8_byte_limit()
    {
        var exact = new string('\u00E9', 512);
        var accepted = new GovernedAgentQuestion(
            new AgentQuestionId("question-1"),
            exact,
            Expiry);

        Assert.Equal(exact, accepted.Question);
        var error = Assert.Throws<ArgumentException>(
            () => new GovernedAgentQuestion(
                new AgentQuestionId("question-2"),
                string.Concat(exact, "x"),
                Expiry));
        Assert.Equal("question", error.ParamName);
    }

    [Fact]
    public void Submitted_answer_preserves_bounded_user_content()
    {
        var response =
            new GovernedAgentQuestionResponse.Submitted(
                "Use the staging region.");

        Assert.Equal("Use the staging region.", response.Answer);
        Assert.Equal(
            "user_supplied_agent_answer",
            GovernedAgentQuestionResponse.UserContentOrigin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("first\nsecond")]
    [InlineData("bell\u0007")]
    [InlineData("hidden\u200Bformat")]
    [InlineData("api_key=literal-answer-secret")]
    [InlineData("-----BEGIN PRIVATE KEY----- literal")]
    public void Submitted_answer_rejects_blank_unsafe_or_secret_text(
        string? answer)
    {
        var error = Assert.ThrowsAny<ArgumentException>(
            () => new GovernedAgentQuestionResponse.Submitted(answer!));

        Assert.Equal("answer", error.ParamName);
    }

    [Fact]
    public void Submitted_answer_rejects_invalid_unicode()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new GovernedAgentQuestionResponse.Submitted(
                string.Concat("invalid", '\uD800')));

        Assert.Equal("answer", error.ParamName);
    }

    [Fact]
    public void Submitted_answer_enforces_its_utf8_byte_limit()
    {
        var exact = new string('\u00E9', 1024);
        var accepted =
            new GovernedAgentQuestionResponse.Submitted(exact);

        Assert.Equal(exact, accepted.Answer);
        var error = Assert.Throws<ArgumentException>(
            () => new GovernedAgentQuestionResponse.Submitted(
                string.Concat(exact, "x")));
        Assert.Equal("answer", error.ParamName);
    }

    [Fact]
    public void Declined_response_carries_no_answer_or_authority()
    {
        GovernedAgentQuestionResponse response =
            new GovernedAgentQuestionResponse.Declined();

        Assert.IsType<GovernedAgentQuestionResponse.Declined>(response);
        Assert.Equal(
            new GovernedAgentQuestionResponse.Declined(),
            response);
    }

    [Fact]
    public void Snapshot_exposes_one_pending_question_and_disables_send()
    {
        var question = new GovernedAgentQuestion(
            new AgentQuestionId("question-1"),
            "Which region?",
            Expiry);
        var snapshot = new GovernedAgentSnapshot(
            GovernedAgentState.AwaitingUserInput,
            RunId: null,
            ProviderId: null,
            Target: null,
            TargetTitle: "Target",
            ContextItems: [],
            Messages: [],
            EffectivePolicy: AgentPolicy.Default,
            ProvisionalAssistantText: string.Empty,
            Status: "Waiting",
            PendingQuestion: question);

        Assert.Same(question, snapshot.PendingQuestion);
        Assert.True(snapshot.IsBusy);
        Assert.False(snapshot.CanSend);
    }

    [Fact]
    public void Ask_user_is_intrinsic_and_not_capability_catalogued()
    {
        Assert.Equal("agent.ask_user", IntrinsicAgentTools.AskUser);
        Assert.False(BuiltInAgentTools.Catalog.TryGet(
            IntrinsicAgentTools.AskUser,
            out _));
        Assert.DoesNotContain(
            BuiltInAgentTools.Catalog.Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.AskUser, StringComparison.Ordinal));
    }
}

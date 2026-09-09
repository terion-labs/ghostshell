namespace Asura.Application;

/// <summary>
/// A local-human response to one model clarification. The response is intent
/// data for provider continuation, never approval or capability authority.
/// </summary>
public abstract record GovernedAgentQuestionResponse
{
    public const int MaximumAnswerBytes = 2048;
    public const string UserContentOrigin = "user_supplied_agent_answer";

    private GovernedAgentQuestionResponse()
    {
    }

    public sealed record Submitted : GovernedAgentQuestionResponse
    {
        public Submitted(string answer)
        {
            GovernedAgentQuestion.ValidateText(
                answer,
                MaximumAnswerBytes,
                "Agent question answer",
                nameof(answer));
            Answer = string.Concat(answer);
        }

        public string Answer { get; }
    }

    public sealed record Declined : GovernedAgentQuestionResponse;
}

namespace Asura.Application;

/// <summary>
/// A stable boundary after a complete assistant message. Forking retains the
/// conversation through this point and gives the branch a new run identity.
/// </summary>
public readonly record struct AgentConversationForkPoint
{
    public AgentConversationForkPoint(int messageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(messageCount);
        MessageCount = messageCount;
    }

    public int MessageCount { get; }
}

namespace Asura.Application;

public abstract record AgentMcpRunAuthorityResult
{
    private AgentMcpRunAuthorityResult()
    {
    }

    public sealed record Granted : AgentMcpRunAuthorityResult
    {
        internal Granted(AgentMcpRunAuthorityLease lease)
        {
            Lease = lease
                ?? throw new ArgumentNullException(nameof(lease));
        }

        public AgentMcpRunAuthorityLease Lease { get; }
    }

    public sealed record Denied(AgentAuthorizationError Error)
        : AgentMcpRunAuthorityResult;
}

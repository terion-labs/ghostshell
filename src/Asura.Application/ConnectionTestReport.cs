using Asura.Core;

namespace Asura.Application;

public sealed record ConnectionTestReport
{
    public ConnectionTestReport(
        ConnectionId connectionId,
        ConnectionKind kind,
        ConnectionTestVerification verification,
        bool endpointReached)
    {
        var expectedEndpointReached = verification is
            ConnectionTestVerification.EndpointAuthenticated or
            ConnectionTestVerification.ContainerReachable or
            ConnectionTestVerification.DistributionReachable;
        if (endpointReached != expectedEndpointReached)
        {
            throw new ArgumentException(
                "Endpoint reachability must agree with the test verification level.",
                nameof(endpointReached));
        }

        ConnectionId = connectionId;
        Kind = kind;
        Verification = verification;
        EndpointReached = endpointReached;
    }

    public ConnectionId ConnectionId { get; }

    public ConnectionKind Kind { get; }

    public ConnectionTestVerification Verification { get; }

    public bool EndpointReached { get; }
}

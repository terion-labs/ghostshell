namespace Asura.Application.Tests;

public sealed class SecurityCampaignLifecycleTests
{
    [Fact(DisplayName = "lifecycle.success")]
    [Trait("SecurityCampaignCase", "lifecycle.success")]
    public Task SuccessAsync() =>
        new AgentCapabilityBrokerTests()
            .HumanApprovalProducesOneExactConsumableAuthorizationAndCompleteAudit();

    [Fact(DisplayName = "lifecycle.denied")]
    [Trait("SecurityCampaignCase", "lifecycle.denied")]
    public Task DeniedAsync() =>
        new AgentCapabilityBrokerTests()
            .FailedDenialAuditCannotTurnTheSameApprovalIntoAuthority();

    [Fact(DisplayName = "lifecycle.expired")]
    [Trait("SecurityCampaignCase", "lifecycle.expired")]
    public Task ExpiredAsync() =>
        new AgentCapabilityBrokerTests().YoloPermitIsCancelledAtTheConfirmedWindowBoundary();

    [Fact(DisplayName = "lifecycle.cancel-before-dispatch")]
    [Trait("SecurityCampaignCase", "lifecycle.cancel-before-dispatch")]
    public Task CancelBeforeDispatchAsync() =>
        new AgentCapabilityBrokerTests().RunCancellationRevokesPendingAuthorityAndSignalsActiveWork();

    [Fact(DisplayName = "lifecycle.permit-replay")]
    [Trait("SecurityCampaignCase", "lifecycle.permit-replay")]
    public Task PermitReplayAsync() =>
        new AgentCapabilityBrokerTests()
            .HumanApprovalProducesOneExactConsumableAuthorizationAndCompleteAudit();

    [Fact(DisplayName = "lifecycle.target-drift")]
    [Trait("SecurityCampaignCase", "lifecycle.target-drift")]
    public Task TargetDriftAsync() =>
        new AgentCapabilityBrokerTests().ChangedTargetCannotReuseAnUnchangedRevisionFingerprint();

    [Fact(DisplayName = "lifecycle.policy-transition")]
    [Trait("SecurityCampaignCase", "lifecycle.policy-transition")]
    public Task PolicyTransitionAsync() =>
        new AgentCapabilityBrokerTests()
            .YoloPolicyTransitionsAreDurablyAuditedWithExactScopeAndWindow();
}

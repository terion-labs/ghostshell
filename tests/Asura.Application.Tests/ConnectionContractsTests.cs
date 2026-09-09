using Asura.Core;

namespace Asura.Application.Tests;

public sealed class ConnectionContractsTests
{
    [Fact]
    public void Open_plan_snapshots_secret_requirements_and_warnings()
    {
        var requirements = new List<ConnectionSecretRequirement>
        {
            new(ConnectionSecretRole.Password, new SecretRef("password-ref")),
        };
        var warnings = new List<ConnectionPlanWarning>
        {
            ConnectionPlanWarning.SecretBrokerRequired,
        };
        var plan = new ConnectionOpenPlan(
            new ConnectionId("ssh"),
            ConnectionKind.Ssh,
            new TerminalLaunchRequest(null, "/usr/bin/ssh", ["host.example"]),
            ConnectionAuthenticationMode.Password,
            SshHostKeyPolicy.Strict,
            ConnectionReconnectMode.Manual,
            requirements,
            warnings);

        requirements.Clear();
        warnings.Clear();

        Assert.Single(plan.SecretRequirements);
        Assert.Single(plan.Warnings);
        Assert.True(plan.RequiresSecretBroker);

        var prepared = plan.WithPreparedSecretBroker(
            new TerminalLaunchRequest(null, "/asura-helper", ["opaque-ticket"]));

        Assert.True(prepared.IsSecretBrokerPrepared);
        Assert.False(prepared.RequiresSecretBroker);
        Assert.Single(prepared.SecretRequirements);
        Assert.DoesNotContain(ConnectionPlanWarning.SecretBrokerRequired, prepared.Warnings);
    }

    [Fact]
    public void Test_report_rejects_reachability_that_disagrees_with_verification()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionTestReport(
            new ConnectionId("test"),
            ConnectionKind.Ssh,
            ConnectionTestVerification.ConfigurationValidated,
            true));
    }

    [Fact]
    public void Runtime_error_codes_have_unique_stable_codes_and_fixed_recovery()
    {
        var errors = Enum.GetValues<ConnectionRuntimeErrorCode>()
            .Select(ConnectionRuntimeError.Create)
            .ToArray();

        Assert.Equal(errors.Length, errors.Select(error => error.StableCode).Distinct(StringComparer.Ordinal).Count());
        Assert.All(errors, error =>
        {
            Assert.StartsWith("connection_", error.StableCode, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            Assert.True(Enum.IsDefined(error.RecoveryAction));
        });
    }

    [Fact]
    public void Host_key_review_preserves_unknown_and_changed_shapes()
    {
        var presented = new SshHostKeyIdentity("ssh-ed25519", $"SHA256:{new string('A', 43)}");
        var trusted = new SshHostKeyIdentity("ssh-ed25519", $"SHA256:{new string('B', 43)}");
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);

        var unknown = new SshHostKeyReview(
            SshHostKeyReviewId.New(),
            new ConnectionId("ssh"),
            "host.example:22",
            SshHostKeyDisposition.Unknown,
            presented,
            null,
            expires);
        var changed = new SshHostKeyReview(
            SshHostKeyReviewId.New(),
            new ConnectionId("ssh"),
            "host.example:22",
            SshHostKeyDisposition.Changed,
            presented,
            trusted,
            expires);

        Assert.False(unknown.RequiresExplicitReplacement);
        Assert.Null(unknown.Trusted);
        Assert.True(changed.RequiresExplicitReplacement);
        Assert.Equal(trusted, changed.Trusted);
    }

    [Fact]
    public void Reconnect_policy_uses_bounded_exponential_backoff()
    {
        var policy = new ConnectionReconnectPolicy(
            maximumAttempts: 5,
            initialDelay: TimeSpan.FromSeconds(1),
            maximumDelay: TimeSpan.FromSeconds(5),
            multiplier: 2);

        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayForAttempt(4));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayForAttempt(5));
    }
}

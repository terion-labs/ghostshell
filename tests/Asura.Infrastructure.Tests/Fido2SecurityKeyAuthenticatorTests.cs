using Asura.Application;

namespace Asura.Infrastructure.Tests;

/// <summary>
/// What can be asserted about security keys without one in hand: that the
/// library resolves, that an absent key is an answer rather than a hang,
/// and that a build without the library says so instead of throwing.
///
/// Deliberately never enrolls or derives. Both block until a human touches
/// the key, so a suite that called them would hang the moment someone
/// plugged one in — the gate must not depend on a finger.
/// </summary>
public sealed class Fido2SecurityKeyAuthenticatorTests
{
    [Fact]
    public async Task An_absent_key_is_an_answer_not_a_hang()
    {
        var authenticator = new Fido2SecurityKeyAuthenticator();
        if (!authenticator.IsSupported)
        {
            // No native library on this machine: the feature reports itself
            // unavailable, which is the contract, and there is nothing else
            // to ask.
            Assert.False(await authenticator.IsKeyPresentAsync(CancellationToken.None));
            return;
        }

        var present = await authenticator.IsKeyPresentAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));
        if (present)
        {
            // A key is attached — enrolling would wait for a touch, so this
            // test has learned all it may without asking a person for one.
            return;
        }

        var (enrollment, failure) = await authenticator.EnrollAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(enrollment);
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Fact]
    public async Task A_build_without_the_library_refuses_rather_than_throwing()
    {
        var authenticator = new Fido2SecurityKeyAuthenticator();
        if (authenticator.IsSupported)
        {
            return;
        }

        var (secret, failure) = await authenticator.DeriveSecretAsync(
            new SecurityKeyEnrollment(new byte[32], new byte[32]),
            CancellationToken.None);

        Assert.Null(secret);
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }
}

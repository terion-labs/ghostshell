using Asura.Application;

namespace Asura.Infrastructure.Tests;

/// <summary>
/// The Objective-C interop under Touch ID, exercised without ever showing
/// the sheet: availability is a pure query, and an invalid policy makes the
/// framework answer the reply block immediately with an error — the whole
/// hand-built block ABI runs, headless.
/// </summary>
public sealed class MacOsBiometricAuthenticatorTests
{
    [Fact]
    public void Availability_is_a_question_not_a_crash()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var authenticator = new MacOsBiometricAuthenticator();
        // True on a Touch ID Mac, false in a VM or CI box; both are answers.
        _ = authenticator.IsAvailable;
        Assert.Equal("Touch ID", authenticator.MethodName);
    }

    [Fact]
    public async Task The_reply_block_round_trips_without_ui()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var authenticator = new MacOsBiometricAuthenticator();
        if (!authenticator.IsAvailable)
        {
            return;
        }

        // An invalidated context: LAContext answers the reply immediately
        // with an error before any UI could exist. If the block layout were
        // wrong this is where the process would die, not a test fail.
        var answered = await MacOsBiometricAuthenticator
            .EvaluateInvalidatedContextForTesting()
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(answered);
    }
}

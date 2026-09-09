using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

public sealed class TerminalAutomationOperationTests
{
    [Fact]
    public async Task Explicit_enter_and_interrupt_require_the_current_input_lease()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var unknownLease = new InputLeaseId("unknown-lease");

        var deniedEnter = await harness.Client.EnterTerminalAsync(
            new TerminalEnterRequest(harness.SessionId, unknownLease),
            harness.HumanContext(),
            default);
        var deniedInterrupt = await harness.Client.InterruptTerminalAsync(
            new TerminalInterruptRequest(harness.SessionId, unknownLease),
            harness.HumanContext(),
            default);

        Assert.Equal(HostErrorCode.LeaseDenied, deniedEnter.Error().Code);
        Assert.Equal(HostErrorCode.LeaseDenied, deniedInterrupt.Error().Code);

        var lease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            default)).Value().Lease!;
        _ = (await harness.Client.EnterTerminalAsync(
            new TerminalEnterRequest(harness.SessionId, lease.Id),
            harness.HumanContext(),
            default)).Value();
        _ = (await harness.Client.InterruptTerminalAsync(
            new TerminalInterruptRequest(harness.SessionId, lease.Id),
            harness.HumanContext(),
            default)).Value();

        var terminal = harness.Factory[harness.SessionId];
        Assert.Equal(1, terminal.EnterCount);
        Assert.Equal(1, terminal.InterruptCount);
    }

    [Fact]
    public async Task Read_only_waits_return_their_typed_success_outcomes_without_a_lease()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var terminal = harness.Factory[harness.SessionId];
        terminal.ScreenText = "deployment ready";
        terminal.ScreenContentRevision = 4;

        var matched = (await harness.Client.WaitForTerminalTextAsync(
            new TerminalWaitForTextRequest(
                harness.SessionId,
                new TerminalWaitForTextInput("ready", TimeSpan.FromSeconds(1))),
            harness.HumanContext(),
            default)).Value();
        var changed = (await harness.Client.WaitForTerminalChangeAsync(
            new TerminalWaitForChangeRequest(
                harness.SessionId,
                new TerminalWaitForChangeInput(3, TimeSpan.FromSeconds(1))),
            harness.HumanContext(),
            default)).Value();
        var stable = (await harness.Client.WaitForTerminalStableAsync(
            new TerminalWaitForStableRequest(
                harness.SessionId,
                new TerminalWaitForStableInput(
                    TimeSpan.FromMilliseconds(20),
                    TimeSpan.FromSeconds(1))),
            harness.HumanContext(),
            default)).Value();

        Assert.Equal(TerminalWaitOutcomeKind.Matched, matched.Kind);
        Assert.Equal(TerminalWaitOutcomeKind.Changed, changed.Kind);
        Assert.Equal(3, changed.InitialContentRevision);
        Assert.Equal(4, changed.ObservedContentRevision);
        Assert.Equal(TerminalWaitOutcomeKind.Stable, stable.Kind);
    }

    [Fact]
    public async Task Wait_deadline_shortens_the_engine_timeout()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var terminal = harness.Factory[harness.SessionId];
        terminal.WaitOutcomeOverride = TerminalWaitOutcome.Timeout(null, null);

        var result = await harness.Client.WaitForTerminalTextAsync(
            new TerminalWaitForTextRequest(
                harness.SessionId,
                new TerminalWaitForTextInput("later", TimeSpan.FromSeconds(5))),
            harness.HumanContext(
                deadline: harness.Clock.GetUtcNow().AddMilliseconds(75)),
            default);

        Assert.Equal(TerminalWaitOutcomeKind.Timeout, result.Value().Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(75), terminal.LastTextWait!.Timeout);
    }

    [Fact]
    public async Task Elapsed_deadline_and_pre_cancelled_token_use_common_host_validation()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var request = new TerminalWaitForTextRequest(
            harness.SessionId,
            new TerminalWaitForTextInput("later", TimeSpan.FromSeconds(1)));

        var timedOut = await harness.Client.WaitForTerminalTextAsync(
            request,
            harness.HumanContext(deadline: harness.Clock.GetUtcNow()),
            default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await harness.Client.WaitForTerminalTextAsync(
            request,
            harness.HumanContext(),
            cancellation.Token);

        Assert.Equal(HostErrorCode.DeadlineExceeded, timedOut.Error().Code);
        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
    }

    [Fact]
    public async Task Cancellation_after_wait_start_returns_a_typed_cancelled_outcome()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var terminal = harness.Factory[harness.SessionId];
        terminal.BlockTextWaits = true;
        using var cancellation = new CancellationTokenSource();
        var wait = harness.Client.WaitForTerminalTextAsync(
            new TerminalWaitForTextRequest(
                harness.SessionId,
                new TerminalWaitForTextInput("later", TimeSpan.FromSeconds(1))),
            harness.HumanContext(),
            cancellation.Token).AsTask();
        await terminal.TextWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cancellation.Cancel();
        var result = await wait;

        Assert.Equal(TerminalWaitOutcomeKind.Cancelled, result.Value().Kind);
    }

    [Fact]
    public async Task Session_end_remains_a_typed_wait_outcome_at_the_client_boundary()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        harness.Factory[harness.SessionId].WaitOutcomeOverride =
            TerminalWaitOutcome.SessionEnded(null, null);

        var result = await harness.Client.WaitForTerminalChangeAsync(
            new TerminalWaitForChangeRequest(
                harness.SessionId,
                new TerminalWaitForChangeInput(0, TimeSpan.FromSeconds(1))),
            harness.HumanContext(),
            default);

        Assert.Equal(TerminalWaitOutcomeKind.SessionEnded, result.Value().Kind);
    }

    [Fact]
    public async Task Negotiation_advertises_explicit_terminal_automation()
    {
        await using var harness = new SessionHostTestHarness();

        var hello = (await harness.Client.NegotiateAsync(
            new ClientHello([1], SessionHostTestHarness.AllCapabilities()),
            harness.HumanContext(),
            default)).Value();

        Assert.True(hello.Capabilities.Contains(SessionCapabilities.TerminalEnter));
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.TerminalInterrupt));
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.TerminalWait));
    }
}

using Asura.App.Views;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class MainWindowCloseFlowTests
{
    [Fact]
    public async Task Completed_request_closes_without_confirmation()
    {
        var decisions = new List<CloseDecision>();

        var approved = await RunAsync(
            (decision, _) =>
            {
                decisions.Add(decision);
                return Success(Completed(SessionCloseOutcome.GracefullyClosed));
            });

        Assert.True(approved);
        Assert.Equal([CloseDecision.Request], decisions);
    }

    [Fact]
    public async Task Initial_host_failure_is_reported_without_confirmation()
    {
        var confirmations = 0;
        var errors = new List<string>();

        var approved = await RunAsync(
            (_, _) => Failure("The host is unavailable."),
            confirm: _ =>
            {
                confirmations++;
                return Task.FromResult(true);
            },
            showError: message =>
            {
                errors.Add(message);
                return Task.CompletedTask;
            });

        Assert.False(approved);
        Assert.Equal(0, confirmations);
        Assert.Equal(["The host is unavailable."], errors);
    }

    [Fact]
    public async Task Rejected_confirmation_restores_focus_before_cancel_round_trip()
    {
        var events = new List<string>();
        var cancelRoundTrip = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var run = RunAsync(
            (decision, _) =>
            {
                events.Add(decision.ToString());
                return decision switch
                {
                    CloseDecision.Request => Success(Confirmation()),
                    CloseDecision.Cancel => AwaitCancelAsync(cancelRoundTrip.Task),
                    _ => throw new InvalidOperationException(
                        $"Unexpected close decision {decision}."),
                };
            },
            confirm: _ =>
            {
                events.Add("Prompt");
                return Task.FromResult(false);
            },
            restoreFocus: () => events.Add("RestoreFocus"));

        await WaitForAsync(() => events.Contains(CloseDecision.Cancel.ToString()));

        Assert.False(run.IsCompleted);
        Assert.Equal(
            ["Request", "Prompt", "RestoreFocus", "Cancel"],
            events);

        cancelRoundTrip.SetResult();
        Assert.False(await run);
    }

    [Fact]
    public async Task Approved_confirmation_is_confirmed_with_the_host()
    {
        var decisions = new List<CloseDecision>();

        var approved = await RunAsync(
            (decision, _) =>
            {
                decisions.Add(decision);
                return decision switch
                {
                    CloseDecision.Request => Success(Confirmation()),
                    CloseDecision.Confirm => Success(
                        Completed(SessionCloseOutcome.ForceTerminated)),
                    _ => throw new InvalidOperationException(
                        $"Unexpected close decision {decision}."),
                };
            });

        Assert.True(approved);
        Assert.Equal(
            [CloseDecision.Request, CloseDecision.Confirm],
            decisions);
    }

    [Fact]
    public async Task Confirmation_host_failure_is_reported()
    {
        var errors = new List<string>();

        var approved = await RunAsync(
            (decision, _) => decision switch
            {
                CloseDecision.Request => Success(Confirmation()),
                CloseDecision.Confirm => Failure("Confirmation failed."),
                _ => throw new InvalidOperationException(
                    $"Unexpected close decision {decision}."),
            },
            showError: message =>
            {
                errors.Add(message);
                return Task.CompletedTask;
            });

        Assert.False(approved);
        Assert.Equal(["Confirmation failed."], errors);
    }

    [Fact]
    public async Task Repeated_confirmation_requirement_is_reported()
    {
        var errors = new List<string>();

        var approved = await RunAsync(
            (decision, _) => decision switch
            {
                CloseDecision.Request => Success(Confirmation()),
                CloseDecision.Confirm => Success(Confirmation()),
                _ => throw new InvalidOperationException(
                    $"Unexpected close decision {decision}."),
            },
            showError: message =>
            {
                errors.Add(message);
                return Task.CompletedTask;
            });

        Assert.False(approved);
        Assert.Equal(["The session still requires confirmation."], errors);
    }

    [Theory]
    [InlineData(SessionCloseOutcome.EngineFailed)]
    [InlineData(SessionCloseOutcome.ConfirmationRequired)]
    public async Task Failed_session_outcome_is_reported(
        SessionCloseOutcome outcome)
    {
        var errors = new List<string>();

        var approved = await RunAsync(
            (_, _) => Success(Completed(outcome, "Close detail.")),
            showError: message =>
            {
                errors.Add(message);
                return Task.CompletedTask;
            });

        Assert.False(approved);
        Assert.Equal(["Close detail."], errors);
    }

    [Fact]
    public async Task Cancelled_session_outcome_keeps_the_window_open_without_error()
    {
        var errors = new List<string>();

        var approved = await RunAsync(
            (_, _) => Success(Completed(SessionCloseOutcome.Cancelled)),
            showError: message =>
            {
                errors.Add(message);
                return Task.CompletedTask;
            });

        Assert.False(approved);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Lifetime_cancellation_keeps_the_window_open_without_error()
    {
        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        var errors = new List<string>();

        var approved = await RunAsync(
            (_, cancellationToken) =>
                ValueTask.FromCanceled<HostResult<CloseScopeResult>>(
                    cancellationToken),
            showError: message =>
            {
                errors.Add(message);
                return Task.CompletedTask;
            },
            cancellationToken: lifetime.Token);

        Assert.False(approved);
        Assert.Empty(errors);
    }

    private static Task<bool> RunAsync(
        Func<CloseDecision, CancellationToken, ValueTask<HostResult<CloseScopeResult>>> close,
        Func<CloseScopeResult.ConfirmationRequired, Task<bool>>? confirm = null,
        Func<string, Task>? showError = null,
        Action? restoreFocus = null,
        CancellationToken cancellationToken = default) =>
        MainWindowCloseFlow.RunAsync(
            close,
            confirm ?? (_ => Task.FromResult(true)),
            showError ?? (_ => Task.CompletedTask),
            restoreFocus ?? (() => { }),
            cancellationToken);

    private static ValueTask<HostResult<CloseScopeResult>> AwaitCancelAsync(
        Task cancellationRoundTrip) =>
        AwaitCancelCoreAsync(cancellationRoundTrip);

    private static async ValueTask<HostResult<CloseScopeResult>> AwaitCancelCoreAsync(
        Task cancellationRoundTrip)
    {
        await cancellationRoundTrip;
        return HostResult<CloseScopeResult>.Succeed(
            Completed(SessionCloseOutcome.Cancelled),
            5);
    }

    private static CloseScopeResult.ConfirmationRequired Confirmation() =>
        new(
            CloseScopeKind.Window,
            "window-1",
            [ActiveSession()]);

    private static CloseScopeResult.Completed Completed(
        SessionCloseOutcome outcome,
        string detail = "Closed.") =>
        new(
            CloseScopeKind.Window,
            "window-1",
            [new SessionCloseResult(
                new SessionId("session-1"),
                outcome,
                detail)]);

    private static ActiveSessionSummary ActiveSession() =>
        new(
            new SessionId("session-1"),
            new PanelInstanceId("panel-1"),
            "Terminal",
            "A process is active.",
            4);

    private static ValueTask<HostResult<CloseScopeResult>> Success(
        CloseScopeResult result) =>
        ValueTask.FromResult(HostResult<CloseScopeResult>.Succeed(result, 5));

    private static ValueTask<HostResult<CloseScopeResult>> Failure(
        string message) =>
        ValueTask.FromResult(
            HostResult<CloseScopeResult>.Fail(
                HostError.Create(HostErrorCode.EngineFailed, message),
                4));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Yield();
        }

        Assert.True(condition(), "Timed out waiting for the close flow event.");
    }
}

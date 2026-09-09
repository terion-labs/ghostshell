using Asura.Application;
using Asura.Core;

namespace Asura.App;

/// <summary>
/// Owns one terminal session owner's immutable startup-command batch and serializes every renderer
/// that can deliver it. The state deliberately outlives renderer replacement so policy, identity,
/// backoff, and terminal outcomes cannot reset during reattach or reconnect.
/// </summary>
public sealed class TerminalStartupCommandDispatchState : IDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StartupCommandDeliveryFailurePolicy _failurePolicy;
    private readonly PanelInstanceId _panelId;
    private readonly OperationContext _context;
    private readonly IReadOnlyList<string> _commands;
    private readonly TimeProvider _timeProvider;
    private TerminalStartupCommandDispatchResult? _lastResult;
    private DateTimeOffset? _nextAttemptUtc;
    private int _retryCount;
    private int _disposed;
    private bool _dispatchComplete;

    public TerminalStartupCommandDispatchState(
        PanelInstanceId panelId,
        IReadOnlyList<string> commands,
        OperationContext context,
        TimeProvider? timeProvider = null,
        StartupCommandDeliveryFailurePolicy failurePolicy =
            StartupCommandDeliveryFailurePolicy.RetryWhileLive)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(context);
        if (context.IdempotencyKey is null)
        {
            throw new ArgumentException(
                "A runtime-owned startup-command batch requires an idempotency key.",
                nameof(context));
        }

        if (!Enum.IsDefined(failurePolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failurePolicy),
                failurePolicy,
                "The startup-command delivery failure policy is not recognized.");
        }

        _panelId = panelId;
        _commands = Array.AsReadOnly(commands
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(command => command.TrimEnd('\r', '\n'))
            .ToArray());
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _failurePolicy = failurePolicy;
    }

    public event EventHandler<TerminalStartupCommandDispatchEventArgs>? DispatchCompleted;

    public PanelInstanceId PanelId => _panelId;

    public IReadOnlyList<string> Commands => _commands;

    public OperationContext Context => _context;

    public StartupCommandDeliveryFailurePolicy FailurePolicy => _failurePolicy;

    public TerminalStartupCommandDispatchResult? LastResult =>
        Volatile.Read(ref _lastResult);

    internal async ValueTask<TerminalStartupCommandDispatchResult?> DispatchIfNeededAsync(
        PanelInstanceId currentPanelId,
        Func<OperationContext, CancellationToken, ValueTask<TerminalStartupCommandDispatchResult>>
            dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (currentPanelId != _panelId
            || _commands.Count == 0
            || Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        TerminalStartupCommandDispatchResult result;
        try
        {
            await _gate.WaitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            if (_dispatchComplete)
            {
                return null;
            }

            if (_nextAttemptUtc is { } nextAttemptUtc
                && _timeProvider.GetUtcNow() < nextAttemptUtc)
            {
                return null;
            }

            result = await dispatch(_context, linkedCancellation.Token);
            var policyStopsAfterFailure = result.Error is not null
                && _failurePolicy
                    == StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure;
            if (result.CommandsDelivered
                || result.Error is { Retryable: false }
                || policyStopsAfterFailure)
            {
                _dispatchComplete = true;
            }
            else if (result.Error is { Retryable: true })
            {
                var delay = RetryDelays[Math.Min(_retryCount, RetryDelays.Length - 1)];
                _retryCount++;
                _nextAttemptUtc = _timeProvider.GetUtcNow() + delay;
            }

            Volatile.Write(ref _lastResult, result);
        }
        finally
        {
            _gate.Release();
        }

        DispatchCompleted?.Invoke(
            this,
            new TerminalStartupCommandDispatchEventArgs(_context, result));
        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Dispatch callbacks can still be unwinding on another thread. Cancellation is the
        // lifetime boundary; the CTS is left for collection so concurrent token readers stay safe.
        _lifetime.Cancel();
    }
}

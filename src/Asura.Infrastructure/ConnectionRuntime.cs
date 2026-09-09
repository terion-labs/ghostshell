using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

public sealed class ConnectionRuntime : IConnectionRuntime
{
    private readonly IReadOnlyDictionary<ConnectionKind, IConnectionRuntimeAdapter> _adapters;

    public ConnectionRuntime(IEnumerable<IConnectionRuntimeAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var byKind = new Dictionary<ConnectionKind, IConnectionRuntimeAdapter>();
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!byKind.TryAdd(adapter.Kind, adapter))
            {
                throw new ArgumentException(
                    $"More than one connection adapter was registered for {adapter.Kind}.",
                    nameof(adapters));
            }
        }

        _adapters = byKind;
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _adapters.TryGetValue(profile.ConnectionKind, out var adapter)
            ? adapter.PlanOpenAsync(profile, progress, cancellationToken)
            : ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.AdapterUnavailable)));
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        TerminalMultiplexerSession? multiplexerSession,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _adapters.TryGetValue(profile.ConnectionKind, out var adapter)
            ? adapter.PlanOpenAsync(profile, multiplexerSession, progress, cancellationToken)
            : ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.AdapterUnavailable)));
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _adapters.TryGetValue(profile.ConnectionKind, out var adapter)
            ? adapter.TestAsync(profile, progress, cancellationToken)
            : ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.AdapterUnavailable)));
    }
}

using Asura.Core;

namespace Asura.Application;

public sealed record ConnectionDiagnosticsReport
{
    public ConnectionDiagnosticsReport(
        ConnectionId connectionId,
        ConnectionKind kind,
        DateTimeOffset completedAtUtc,
        IReadOnlyList<ConnectionDiagnosticItem> items,
        ConnectionTestVerification? verification = null,
        ConnectionRuntimeError? failure = null,
        SshHostKeyReview? hostKeyReview = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId.Value);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("A diagnostics report must contain at least one item.", nameof(items));
        }

        var copiedItems = items.ToArray();
        if (failure is null && copiedItems.Any(item => item.Status == ConnectionDiagnosticStatus.Failed))
        {
            throw new ArgumentException("A failed diagnostics item requires a typed failure.", nameof(failure));
        }

        if (failure is not null && copiedItems.All(item => item.Status != ConnectionDiagnosticStatus.Failed))
        {
            throw new ArgumentException("A typed diagnostics failure requires a failed item.", nameof(items));
        }

        ConnectionId = connectionId;
        Kind = kind;
        CompletedAtUtc = completedAtUtc;
        Items = Array.AsReadOnly(copiedItems);
        Verification = verification;
        Failure = failure;
        HostKeyReview = hostKeyReview;
    }

    public ConnectionId ConnectionId { get; }

    public ConnectionKind Kind { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public IReadOnlyList<ConnectionDiagnosticItem> Items { get; }

    public ConnectionTestVerification? Verification { get; }

    public ConnectionRuntimeError? Failure { get; }

    public SshHostKeyReview? HostKeyReview { get; }

    public bool Succeeded => Failure is null;
}

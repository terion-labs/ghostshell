namespace Asura.SessionHost;

internal sealed record IdempotencyRecord(
    string Fingerprint,
    object Result,
    bool IsOutcomeUncertain = false);

namespace Asura.Monitoring;

internal sealed record RawNetworkObservation(
    string InterfaceId,
    long ReceivedBytes,
    long SentBytes);

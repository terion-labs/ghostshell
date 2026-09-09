namespace Asura.Files;

/// <summary>Non-secret result of FEAT negotiation and the completed transport handshake.</summary>
public sealed record FtpConnectionSnapshot(
    FtpTransportSecurity TransportSecurity,
    bool IsEncrypted,
    FtpServerFeature ServerFeatures,
    string EncodingWebName);

namespace Asura.Application;

public sealed record RecentSessionHistoryExportReceipt(
    int RecordCount,
    DateTimeOffset ExportedAt,
    long ByteLength,
    string Sha256);

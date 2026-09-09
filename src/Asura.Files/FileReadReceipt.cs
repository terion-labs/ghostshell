namespace Asura.Files;

public sealed record FileReadReceipt(
    FileLocation Source,
    long Offset,
    long BytesRead,
    bool IsTruncated);

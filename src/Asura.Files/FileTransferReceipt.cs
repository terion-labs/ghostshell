namespace Asura.Files;

public sealed record FileTransferReceipt(
    FileLocation Source,
    FileEntry Destination,
    FileTransferKind Kind,
    long BytesTransferred,
    bool ReplacedExisting,
    bool SourceDeleted);

namespace Asura.Files;

public sealed record FileDeleteReceipt(FileLocation DeletedLocation, bool WasDirectory);

namespace Asura.Packaging;

internal sealed record CefRuntimeReceiptCommand(
    string RuntimeRoot,
    string CatalogPath,
    string RuntimeIdentifier,
    string ArchiveSha1,
    string ArchiveSha256,
    string PatchSetSha256,
    string SourceSnapshotSha256,
    string OutputPath)
{
    private static readonly IReadOnlySet<string> Options = new HashSet<string>(
        [
            "--runtime-root",
            "--catalog",
            "--runtime-identifier",
            "--archive-sha1",
            "--archive-sha256",
            "--patch-set-sha256",
            "--source-snapshot-sha256",
            "--output",
        ],
        StringComparer.Ordinal);

    public static CefRuntimeReceiptCommand Parse(IReadOnlyList<string> arguments)
    {
        var values = PackagingCommandParser.Parse(arguments, Options);
        return new CefRuntimeReceiptCommand(
            PackagingCommandParser.Required(values, "--runtime-root"),
            PackagingCommandParser.Required(values, "--catalog"),
            PackagingCommandParser.Required(values, "--runtime-identifier"),
            PackagingCommandParser.Required(values, "--archive-sha1"),
            PackagingCommandParser.Required(values, "--archive-sha256"),
            PackagingCommandParser.Required(values, "--patch-set-sha256"),
            PackagingCommandParser.Required(values, "--source-snapshot-sha256"),
            PackagingCommandParser.Required(values, "--output"));
    }
}

internal sealed record CefRuntimeValidateCommand(
    string RuntimeRoot,
    string CatalogPath,
    string RuntimeIdentifier)
{
    private static readonly IReadOnlySet<string> Options = new HashSet<string>(
        [
            "--runtime-root",
            "--catalog",
            "--runtime-identifier",
        ],
        StringComparer.Ordinal);

    public static CefRuntimeValidateCommand Parse(IReadOnlyList<string> arguments)
    {
        var values = PackagingCommandParser.Parse(arguments, Options);
        return new CefRuntimeValidateCommand(
            PackagingCommandParser.Required(values, "--runtime-root"),
            PackagingCommandParser.Required(values, "--catalog"),
            PackagingCommandParser.Required(values, "--runtime-identifier"));
    }
}

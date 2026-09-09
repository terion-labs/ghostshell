namespace Asura.Packaging;

internal sealed record VelopackMacOsReleaseCommand(
    string ReleaseDirectory,
    string FullPackagePath,
    string ApplicationPath,
    string Version,
    string Channel)
{
    private static readonly IReadOnlySet<string> Options = new HashSet<string>(
        [
            "--release-directory",
            "--full-package",
            "--app",
            "--version",
            "--channel",
        ],
        StringComparer.Ordinal);

    public static VelopackMacOsReleaseCommand Parse(
        IReadOnlyList<string> arguments)
    {
        var values = PackagingCommandParser.Parse(arguments, Options);
        return new VelopackMacOsReleaseCommand(
            PackagingCommandParser.Required(values, "--release-directory"),
            PackagingCommandParser.Required(values, "--full-package"),
            PackagingCommandParser.Required(values, "--app"),
            PackagingCommandParser.Required(values, "--version"),
            PackagingCommandParser.Required(values, "--channel"));
    }
}

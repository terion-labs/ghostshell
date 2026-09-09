namespace Asura.Packaging;

internal static class MacOsAppBundlePublisher
{
    private const string PrivateParentPrefix = ".asura-package.";

    public static string Publish(string candidatePath, string destinationPath)
    {
        var candidate = MacOsPackagePaths.RequireExistingDirectory(
            candidatePath,
            nameof(candidatePath));
        if (!string.Equals(
                Path.GetFileName(candidate),
                MacOsPackagePaths.BundleName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The candidate must be named {MacOsPackagePaths.BundleName}.",
                nameof(candidatePath));
        }

        var destination = MacOsPackagePaths.RequireDestination(destinationPath);
        var candidateParent = Path.GetDirectoryName(candidate)
            ?? throw new ArgumentException(
                "The candidate must have a private parent directory.",
                nameof(candidatePath));
        var destinationParent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException(
                "The destination must have a parent directory.",
                nameof(destinationPath));
        var privateParentOwner = Path.GetDirectoryName(candidateParent)
            ?? throw new ArgumentException(
                "The candidate private parent must have an owner directory.",
                nameof(candidatePath));
        if (!Path.GetFileName(candidateParent).StartsWith(
                PrivateParentPrefix,
                StringComparison.Ordinal)
            || !MacOsPackagePaths.AreSameDirectory(
                privateParentOwner,
                destinationParent))
        {
            throw new ArgumentException(
                "The validated candidate must be inside a private sibling directory.",
                nameof(candidatePath));
        }

        ExclusiveDirectoryMover.Move(candidate, destination);
        return destination;
    }
}

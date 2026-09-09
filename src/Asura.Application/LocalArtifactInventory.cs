namespace Asura.Application;

public sealed record LocalArtifactInventory
{
    public LocalArtifactInventory(IReadOnlyList<LocalArtifactSummary> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count != Enum.GetValues<LocalArtifactKind>().Length)
        {
            throw new ArgumentException(
                "The inventory must contain one summary for every artifact category.",
                nameof(artifacts));
        }

        var actualKinds = artifacts.Select(item => item.Kind).ToHashSet();
        var expectedKinds = Enum.GetValues<LocalArtifactKind>().ToHashSet();
        if (!actualKinds.SetEquals(expectedKinds))
        {
            throw new ArgumentException(
                "The inventory must contain each defined artifact category exactly once.",
                nameof(artifacts));
        }

        Artifacts = [.. artifacts];
    }

    public IReadOnlyList<LocalArtifactSummary> Artifacts { get; }
}

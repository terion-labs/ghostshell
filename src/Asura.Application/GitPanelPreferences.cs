namespace Asura.Application;

/// <summary>
/// How the Git panel's working-set sections present themselves: each of the
/// two lists remembers whether it reads as a flat list or a directory tree.
/// The choice is the person's, not the panel's, so every Git panel shares it.
/// </summary>
public sealed record GitPanelPreferenceState(
    bool UnstagedViewIsTree,
    bool StagedViewIsTree)
{
    public static GitPanelPreferenceState Default { get; } = new(
        UnstagedViewIsTree: true,
        StagedViewIsTree: true);
}

/// <summary>
/// The live Git panel presentation preference, shared by every Git panel. A
/// change applies the moment it is made: it is persisted, published through
/// <see cref="Changed"/>, and read by the next panel — there is no save step
/// anywhere.
/// </summary>
public interface IGitPanelPreferences
{
    event EventHandler? Changed;

    ValueTask<GitPanelPreferenceState> ReadAsync(CancellationToken cancellationToken);

    ValueTask ApplyAsync(GitPanelPreferenceState state, CancellationToken cancellationToken);
}

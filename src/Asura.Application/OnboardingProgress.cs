namespace Asura.Application;

/// <summary>
/// Records the highest first-run experience completed for this local profile.
/// This state is deliberately separate from portable definitions so importing a
/// workspace cannot suppress or repeat local setup.
/// </summary>
public sealed record OnboardingProgress
{
    public OnboardingProgress(int completedVersion, long revision)
    {
        if (completedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedVersion),
                "The completed onboarding version cannot be negative.");
        }

        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "The onboarding progress revision must be positive.");
        }

        CompletedVersion = completedVersion;
        Revision = revision;
    }

    public int CompletedVersion { get; }

    public long Revision { get; }
}

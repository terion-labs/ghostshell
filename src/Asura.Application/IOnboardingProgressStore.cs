namespace Asura.Application;

public interface IOnboardingProgressStore
{
    ValueTask<OnboardingProgressResult<OnboardingProgress>> ReadAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Advances the highest completed onboarding version. Completing an already
    /// recorded version is idempotent; other stale revisions fail with a conflict.
    /// </summary>
    ValueTask<OnboardingProgressResult<OnboardingProgress>> CompleteAsync(
        int version,
        long expectedRevision,
        CancellationToken cancellationToken);
}

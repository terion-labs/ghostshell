using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Trusted presentation data for one run-local request to move a target-
/// advertised capability from Off to Ask. All titles must originate from
/// application-owned metadata; model prose must never be copied into this
/// contract.
/// </summary>
public sealed record GovernedAgentCapabilityRequest
{
    public static readonly TimeSpan DecisionLifetime =
        TimeSpan.FromMinutes(2);

    public const int MaximumAffectedToolCount = 64;
    public const int MaximumDisplayTitleBytes = 256;
    public const int MaximumToolTitleBytes = 256;
    public const int MaximumTargetTitleBytes = 512;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public GovernedAgentCapabilityRequest(
        AgentCapabilityRequestId id,
        AgentRunId runId,
        AgentCapability capability,
        string displayTitle,
        IEnumerable<string> affectedToolTitles,
        AgentTarget target,
        string targetTitle,
        long policyGeneration,
        DateTimeOffset expiresAtUtc)
    {
        RequireIdentifier(id.Value, nameof(id));
        AgentRunRegistration.ValidateRunId(runId);
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        DisplayTitle = RequireTitle(
            displayTitle,
            MaximumDisplayTitleBytes,
            nameof(displayTitle));
        AffectedToolTitles = CopyAffectedToolTitles(affectedToolTitles);
        Target = target ?? throw new ArgumentNullException(nameof(target));
        TargetTitle = RequireTitle(
            targetTitle,
            MaximumTargetTitleBytes,
            nameof(targetTitle));
        ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A capability-request expiry must be UTC.",
                nameof(expiresAtUtc));
        }

        Id = id;
        RunId = runId;
        Capability = capability;
        CapabilityToken = AgentCapabilityProtocol.GetToken(capability);
        PolicyGeneration = policyGeneration;
        ExpiresAtUtc = expiresAtUtc;
    }

    public AgentCapabilityRequestId Id { get; }

    public AgentRunId RunId { get; }

    public AgentCapability Capability { get; }

    public string CapabilityToken { get; }

    public string DisplayTitle { get; }

    public ImmutableArray<string> AffectedToolTitles { get; }

    public AgentTarget Target { get; }

    public string TargetTitle { get; }

    public long PolicyGeneration { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    private static void RequireIdentifier(
        string? value,
        string parameterName)
    {
        int byteCount;
        try
        {
            byteCount = value is null
                ? 0
                : StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A capability request identifier must contain valid Unicode.",
                parameterName,
                exception);
        }

        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || byteCount > 256)
        {
            throw new ArgumentException(
                "A capability request identifier must be printable and bounded.",
                parameterName);
        }
    }

    private static ImmutableArray<string> CopyAffectedToolTitles(
        IEnumerable<string> affectedToolTitles)
    {
        ArgumentNullException.ThrowIfNull(affectedToolTitles);
        var copies = affectedToolTitles
            .Select(title => RequireTitle(
                title,
                MaximumToolTitleBytes,
                nameof(affectedToolTitles)))
            .ToImmutableArray();
        if (copies.Length is 0 or > MaximumAffectedToolCount
            || copies.Distinct(StringComparer.Ordinal).Count() != copies.Length)
        {
            throw new ArgumentException(
                $"Affected tools must contain between 1 and {MaximumAffectedToolCount} distinct trusted titles.",
                nameof(affectedToolTitles));
        }

        return copies;
    }

    private static string RequireTitle(
        string value,
        int maximumBytes,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A capability-request title must contain valid Unicode.",
                parameterName,
                exception);
        }

        if (byteCount > maximumBytes
            || value.EnumerateRunes().Any(rune =>
                Rune.GetUnicodeCategory(rune) is
                    UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator))
        {
            throw new ArgumentException(
                "A capability-request title must be bounded, printable, and single-line.",
                parameterName);
        }

        return string.Concat(value);
    }
}

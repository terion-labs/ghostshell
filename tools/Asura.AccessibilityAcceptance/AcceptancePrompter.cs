namespace Asura.AccessibilityAcceptance;

internal sealed class AcceptancePrompter
{
    private const int MinimumEvidenceNoteLength = 12;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TimeProvider _timeProvider;

    public AcceptancePrompter(
        TextReader input,
        TextWriter output,
        TimeProvider? timeProvider = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CheckObservation Prompt(
        AcceptanceCheck check,
        TargetPlatform platform,
        IReadOnlyDictionary<string, AcceptanceStatus>? runnerAssertions = null)
    {
        ArgumentNullException.ThrowIfNull(check);
        _output.WriteLine();
        _output.WriteLine($"[{check.Id}] {check.Title}");
        _output.WriteLine(check.InstructionsFor(platform));
        _output.WriteLine(
            "Evidence is synthetic and summary-only. Never enter spoken transcripts, terminal or clipboard contents, usernames, addresses, credentials, or absolute paths.");

        var observations = new List<AssertionObservation>(check.Assertions.Count);
        foreach (var assertion in check.Assertions)
        {
            if (runnerAssertions?.TryGetValue(assertion.Id, out var known) == true)
            {
                _output.WriteLine($"  {assertion.Id}: {FormatStatus(known)} (runner boundary)");
                observations.Add(new AssertionObservation(assertion.Id, known));
                continue;
            }

            _output.WriteLine($"  {assertion.Id}: {assertion.Instructions}");
            var status = ReadStatus();
            observations.Add(new AssertionObservation(
                assertion.Id,
                status ?? AcceptanceStatus.Blocked));
            if (status is null)
            {
                FillRemainingBlocked(check, observations);
                return Create(
                    check,
                    observations,
                    "Operator input ended before all physical assertions were supplied.",
                    "operator-observed+runner-boundary");
            }
        }

        while (true)
        {
            _output.Write("Evidence note (summary or blocker, no verbatim speech): ");
            var rawNote = _input.ReadLine();
            if (rawNote is null)
            {
                var unsupported = observations
                    .Select(assertion => assertion.Result == AcceptanceStatus.Pass
                        ? assertion with { Result = AcceptanceStatus.Blocked }
                        : assertion)
                    .ToArray();
                return Create(
                    check,
                    unsupported,
                    "Operator input ended before a physical evidence note was supplied.",
                    "operator-observed+runner-boundary");
            }

            var note = EvidenceSanitizer.SanitizeNote(rawNote);
            if (note.Value.Length >= MinimumEvidenceNoteLength)
            {
                if (note.RedactionsApplied > 0)
                {
                    _output.WriteLine(
                        $"Sanitized {note.RedactionsApplied} sensitive or unsafe field(s) before evidence output.");
                }

                return new CheckObservation(
                    check.Id,
                    check.Title,
                    CheckObservation.ResolveResult(observations),
                    runnerAssertions is null ? "operator-observed" : "operator-observed+runner-boundary",
                    observations,
                    note.Value,
                    note.RedactionsApplied,
                    _timeProvider.GetUtcNow());
            }

            _output.WriteLine(
                $"Enter at least {MinimumEvidenceNoteLength} characters of concrete, non-sensitive evidence.");
        }
    }

    public static CheckObservation CreateAutomatic(
        AcceptanceCheck check,
        AcceptanceStatus status,
        string note,
        TimeProvider? timeProvider = null) =>
        CreateAutomatic(
            check,
            check.Assertions.ToDictionary(assertion => assertion.Id, _ => status, StringComparer.Ordinal),
            note,
            timeProvider);

    public static CheckObservation CreateAutomatic(
        AcceptanceCheck check,
        IReadOnlyDictionary<string, AcceptanceStatus> statuses,
        string note,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(statuses);
        var assertions = check.Assertions.Select(assertion => new AssertionObservation(
            assertion.Id,
            statuses.TryGetValue(assertion.Id, out var status)
                ? status
                : AcceptanceStatus.Blocked)).ToArray();
        var sanitized = EvidenceSanitizer.SanitizeNote(note);
        return new CheckObservation(
            check.Id,
            check.Title,
            CheckObservation.ResolveResult(assertions),
            "runner-observed-boundary",
            assertions,
            sanitized.Value,
            sanitized.RedactionsApplied,
            (timeProvider ?? TimeProvider.System).GetUtcNow());
    }

    private static CheckObservation Create(
        AcceptanceCheck check,
        IReadOnlyList<AssertionObservation> observations,
        string note,
        string mode)
    {
        var sanitized = EvidenceSanitizer.SanitizeNote(note);
        return new CheckObservation(
            check.Id,
            check.Title,
            CheckObservation.ResolveResult(observations),
            mode,
            observations,
            sanitized.Value,
            sanitized.RedactionsApplied,
            DateTimeOffset.UtcNow);
    }

    private AcceptanceStatus? ReadStatus()
    {
        while (true)
        {
            _output.Write("Observed assertion [PASS/FAIL/BLOCKED]: ");
            var answer = _input.ReadLine();
            if (answer is null)
            {
                return null;
            }

            switch (answer.Trim().ToUpperInvariant())
            {
                case "PASS":
                    return AcceptanceStatus.Pass;
                case "FAIL":
                    return AcceptanceStatus.Fail;
                case "BLOCKED":
                    return AcceptanceStatus.Blocked;
                default:
                    _output.WriteLine("Use exactly PASS, FAIL, or BLOCKED. There is no SKIP state.");
                    break;
            }
        }
    }

    private static void FillRemainingBlocked(
        AcceptanceCheck check,
        List<AssertionObservation> observations)
    {
        foreach (var assertion in check.Assertions.Skip(observations.Count))
        {
            observations.Add(new AssertionObservation(assertion.Id, AcceptanceStatus.Blocked));
        }
    }

    private static string FormatStatus(AcceptanceStatus status) =>
        status.ToString().ToUpperInvariant();
}

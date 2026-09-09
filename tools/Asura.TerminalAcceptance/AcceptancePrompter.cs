namespace Asura.TerminalAcceptance;

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

    public CheckObservation Prompt(AcceptanceCheck check, TargetPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(check);

        _output.WriteLine();
        _output.WriteLine($"[{check.Id}] {check.Title}");
        _output.WriteLine(check.InstructionsFor(platform));
        _output.WriteLine(
            "Evidence notes must be synthetic and summary-only. Never enter secrets, clipboard contents, usernames, remote host/IP addresses, shell history, or absolute paths.");

        var status = ReadStatus();
        if (status is null)
        {
            return BlockedForEndedInput(check);
        }

        while (true)
        {
            _output.Write("Evidence note (commands/versions and observed behavior or blocker): ");
            var rawNote = _input.ReadLine();
            if (rawNote is null)
            {
                return BlockedForEndedInput(check);
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
                    status.Value,
                    "operator-observed",
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
        TimeProvider? timeProvider = null)
    {
        var sanitized = EvidenceSanitizer.SanitizeNote(note);
        return new CheckObservation(
            check.Id,
            check.Title,
            status,
            "runner-observed-boundary",
            sanitized.Value,
            sanitized.RedactionsApplied,
            (timeProvider ?? TimeProvider.System).GetUtcNow());
    }

    private AcceptanceStatus? ReadStatus()
    {
        while (true)
        {
            _output.Write("Observed result [PASS/FAIL/BLOCKED]: ");
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

    private CheckObservation BlockedForEndedInput(AcceptanceCheck check) =>
        new(
            check.Id,
            check.Title,
            AcceptanceStatus.Blocked,
            "runner-observed-boundary",
            "Operator input ended before a physical observation was supplied.",
            0,
            _timeProvider.GetUtcNow());
}

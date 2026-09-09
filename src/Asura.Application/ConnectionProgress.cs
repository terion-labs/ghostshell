namespace Asura.Application;

/// <summary>
/// Fixed progress text deliberately excludes profile values and process output.
/// </summary>
public sealed record ConnectionProgress(
    ConnectionProgressStage Stage,
    string StableCode,
    string Message);

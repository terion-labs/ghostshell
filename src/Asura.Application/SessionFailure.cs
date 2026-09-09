namespace Asura.Application;

public sealed record SessionFailure(string StableCode, string Message, bool Retryable);

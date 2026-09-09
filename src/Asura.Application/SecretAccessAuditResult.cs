namespace Asura.Application;

public readonly record struct SecretAccessAuditResult(bool IsSuccess)
{
    public static SecretAccessAuditResult Succeeded => new(true);

    public static SecretAccessAuditResult Failed => new(false);
}

namespace Asura.Application;

public sealed record SecretUsePurpose
{
    public const string GlobalTargetId = "global";
    public const string AllSecretsTargetId = "*";

    public SecretUsePurpose(SecretUseKind kind, string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        Kind = kind;
        TargetId = targetId;
    }

    public SecretUseKind Kind { get; }

    public string TargetId { get; }

    public static SecretUsePurpose ManageGlobal() =>
        new(SecretUseKind.UserManagement, GlobalTargetId);

    public static SecretUsePurpose ManageAll() =>
        new(SecretUseKind.UserManagement, AllSecretsTargetId);
}

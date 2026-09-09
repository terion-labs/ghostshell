namespace Asura.Application;

public abstract record SecretVaultResult<T>
{
    private SecretVaultResult()
    {
    }

    public sealed record Success(T Value) : SecretVaultResult<T>;

    public sealed record Failure(SecretVaultError Error) : SecretVaultResult<T>;

    public static SecretVaultResult<T> Succeed(T value) => new Success(value);

    public static SecretVaultResult<T> Fail(SecretVaultError error) => new Failure(error);
}

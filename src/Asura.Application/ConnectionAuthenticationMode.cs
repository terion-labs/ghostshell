namespace Asura.Application;

public enum ConnectionAuthenticationMode
{
    None,
    SshAgent,
    Password,
    PrivateKey,
    PrivateKeyWithPassphrase,
}

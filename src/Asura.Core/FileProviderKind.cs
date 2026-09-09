namespace Asura.Core;

public enum FileProviderKind
{
    Local,
    S3,
    Sftp,
    Ftp,
    Smb,
    WebDav,
}

public enum FtpSecurityMode
{
    Plaintext,
    ExplicitTls,
    ImplicitTls,
}

public enum FtpConnectionMode
{
    AutoPassive,
    Passive,
    Active,
}

public enum SmbCredentialMode
{
    Guest,
    UsernamePassword,
}

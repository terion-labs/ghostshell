namespace Asura.Files;

[Flags]
public enum FtpServerFeature
{
    None = 0,
    MachineListing = 1 << 0,
    Size = 1 << 1,
    ModifiedTime = 1 << 2,
    RestartDownload = 1 << 3,
    Utf8 = 1 << 4,
    Checksum = 1 << 5,
}

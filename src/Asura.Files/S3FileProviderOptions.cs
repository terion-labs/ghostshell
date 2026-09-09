using System.Text;

namespace Asura.Files;

/// <summary>Identifies one S3 or S3-compatible bucket exposed by a provider profile.</summary>
public sealed record S3FileProviderOptions
{
    public S3FileProviderOptions(
        FileProviderProfileId profileId,
        FileAuthority authority,
        string bucketName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        int bucketNameBytes;
        try
        {
            bucketNameBytes = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetByteCount(bucketName);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "The S3 bucket name must have an exact UTF-8 representation.",
                nameof(bucketName),
                exception);
        }

        if (bucketNameBytes > 255 || bucketName.Any(char.IsControl))
        {
            throw new ArgumentException("The S3 bucket name is not a bounded protocol name.", nameof(bucketName));
        }

        ProfileId = profileId;
        Authority = authority;
        BucketName = bucketName;
    }

    public FileProviderProfileId ProfileId { get; }

    public FileAuthority Authority { get; }

    public string BucketName { get; }
}

using Asura.Core;

namespace Asura.Core.Tests;

public sealed class AgentImageAttachmentTests
{
    [Fact]
    public void SupportedImageCopiesVerifiedBytes()
    {
        var bytes = new byte[]
        {
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01,
        };

        var image = new AgentImageAttachment("sample.png", "IMAGE/PNG", bytes);
        bytes[^1] = 0xff;

        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(0x01, image.Content[^1]);
    }

    [Fact]
    public void MediaTypeMustMatchTheImageSignature()
    {
        Assert.Throws<ArgumentException>(() => new AgentImageAttachment(
            "not-an-image.png",
            "image/png",
            "not png"u8));
    }

    [Fact]
    public void FileNameCannotCarryAPath()
    {
        Assert.Throws<ArgumentException>(() => new AgentImageAttachment(
            "../sample.png",
            "image/png",
            [
                0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            ]));
    }
}

using System.Text;
using Asura.Docking;

namespace Asura.App.Tests;

public sealed class DockLayoutPayloadCodecTests
{
    [Fact]
    public void Legacy_payloads_are_limited_by_utf8_bytes_before_deserialization()
    {
        var payload = new string(
            '\u00e9',
            (DockLayoutPayloadCodec.MaximumDecodedBytes / 2) + 1);

        var error = Assert.Throws<InvalidDataException>(
            () => DockLayoutPayloadCodec.Decode(payload));

        Assert.Contains("size", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            Encoding.UTF8.GetByteCount(payload)
            > DockLayoutPayloadCodec.MaximumDecodedBytes);
    }

    [Fact]
    public void A_legacy_payload_at_the_utf8_byte_limit_remains_readable()
    {
        var payload = new string('x', DockLayoutPayloadCodec.MaximumDecodedBytes);

        Assert.Same(payload, DockLayoutPayloadCodec.Decode(payload));
    }
}

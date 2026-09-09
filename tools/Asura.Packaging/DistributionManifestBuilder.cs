using System.Text;
using System.Text.Json;

namespace Asura.Packaging;

internal static class DistributionManifestBuilder
{
    public static byte[] BuildGitHubVelopack(string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("source", "github-release");
            writer.WriteString("updateStrategy", "velopack");
            writer.WriteString("packageId", "sh.asura");
            writer.WriteString("channel", runtimeIdentifier + "-stable");
            writer.WriteString("runtimeIdentifier", runtimeIdentifier);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }
}

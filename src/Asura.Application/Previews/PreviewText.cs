using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Asura.Application.Previews;

/// <summary>
/// Turning bytes into something readable. Shared by the previewers so that
/// "truncated" and "not valid" read the same whatever the format.
/// </summary>
public static class PreviewText
{
    /// <summary>The most bytes laid out as hex before the dump is cut.</summary>
    public const int MaximumHexBytes = 64 * 1024;

    public const string TruncationNotice = "[preview truncated]";

    public static string Utf8(ReadOnlySpan<byte> content, bool truncated) =>
        Encoding.UTF8.GetString(content)
        + (truncated ? "\n\n" + TruncationNotice : string.Empty);

    /// <summary>
    /// JSON laid out one value to a line. A partial file cannot be parsed, so a
    /// truncated preview is shown as it arrived rather than as an error.
    /// </summary>
    public static string Json(ReadOnlySpan<byte> content, bool truncated)
    {
        if (truncated)
        {
            return Utf8(content, truncated: true);
        }

        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            var buffer = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(
                buffer,
                new JsonWriterOptions { Indented = true });
            document.RootElement.WriteTo(writer);
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return Encoding.UTF8.GetString(content);
        }
    }

    /// <summary>XML indented by element depth, on the same terms as JSON.</summary>
    public static string Xml(ReadOnlySpan<byte> content, bool truncated)
    {
        if (truncated)
        {
            return Utf8(content, truncated: true);
        }

        try
        {
            var document = XDocument.Parse(
                Encoding.UTF8.GetString(content),
                LoadOptions.PreserveWhitespace);
            var builder = new StringBuilder();
            using var writer = XmlWriter.Create(
                builder,
                new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    OmitXmlDeclaration = document.Declaration is null,
                });
            document.Save(writer);
            writer.Flush();
            return builder.ToString();
        }
        catch (XmlException)
        {
            return Encoding.UTF8.GetString(content);
        }
    }

    /// <summary>
    /// The classic offset / bytes / characters dump. Rows are fixed width by
    /// construction, so the view must not wrap them — a wrapped hex dump stops
    /// being a grid and stops being readable.
    /// </summary>
    public static string Hex(ReadOnlySpan<byte> content, bool providerTruncated)
    {
        var shown = content[..Math.Min(content.Length, MaximumHexBytes)];
        var builder = new StringBuilder((shown.Length / 16 + 1) * 72);
        for (var offset = 0; offset < shown.Length; offset += 16)
        {
            var row = shown.Slice(offset, Math.Min(16, shown.Length - offset));
            builder.Append(offset.ToString("X8", CultureInfo.InvariantCulture));
            builder.Append("  ");
            for (var index = 0; index < 16; index++)
            {
                if (index < row.Length)
                {
                    builder.Append(row[index].ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append("  ");
                }

                builder.Append(index == 7 ? "  " : " ");
            }

            builder.Append(" | ");
            foreach (var value in row)
            {
                builder.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }

            builder.AppendLine();
        }

        if (providerTruncated || shown.Length < content.Length)
        {
            builder.AppendLine(TruncationNotice);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The same dump as <see cref="Hex"/>, split into rows so a list can draw
    /// the handful on screen instead of a text view measuring every one.
    /// </summary>
    public static HexPreviewRendering HexRows(
        ReadOnlySpan<byte> content,
        bool providerTruncated)
    {
        var shown = content[..Math.Min(content.Length, MaximumHexBytes)];
        var rows = new List<HexPreviewRow>((shown.Length / 16) + 1);
        var bytes = new StringBuilder(50);
        var characters = new StringBuilder(16);
        for (var offset = 0; offset < shown.Length; offset += 16)
        {
            var row = shown.Slice(offset, Math.Min(16, shown.Length - offset));
            bytes.Clear();
            characters.Clear();
            for (var index = 0; index < 16; index++)
            {
                if (index < row.Length)
                {
                    bytes.Append(row[index].ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    bytes.Append("  ");
                }

                bytes.Append(index == 7 ? "  " : ' ');
            }

            foreach (var value in row)
            {
                characters.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }

            rows.Add(new HexPreviewRow(
                offset.ToString("X8", CultureInfo.InvariantCulture),
                bytes.ToString().TrimEnd(),
                characters.ToString()));
        }

        var truncated = providerTruncated || shown.Length < content.Length;
        var size = ByteSize.Format(shown.Length);
        return new HexPreviewRendering(
            rows,
            truncated ? $"First {size} of the file" : size);
    }

    /// <summary>The file's extension in lower case, without the dot.</summary>
    public static string Extension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 1
            ? extension[1..].ToLowerInvariant()
            : string.Empty;
    }

    /// <summary>
    /// Whether a switch is on, taking the previewer's own default when the
    /// reader has not touched it.
    /// </summary>
    public static bool IsOn(
        IReadOnlyDictionary<string, bool> toggles,
        string id,
        bool byDefault)
    {
        ArgumentNullException.ThrowIfNull(toggles);
        return toggles.TryGetValue(id, out var value) ? value : byDefault;
    }
}

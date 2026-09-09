using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// A copied, bounded raster image supplied by the local user. The media type
/// is verified from the file signature so an attachment never carries path or
/// executable-content semantics into the agent transcript.
/// </summary>
[JsonConverter(typeof(AgentImageAttachmentJsonConverter))]
public sealed class AgentImageAttachment
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const int MaximumPerMessage = 4;
    public const int MaximumTotalBytesPerMessage = 8 * 1024 * 1024;
    public const int MaximumFileNameLength = 255;

    private readonly byte[] _content;

    public AgentImageAttachment(
        string fileName,
        string mediaType,
        ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Length > MaximumFileNameLength
            || fileName.Any(character =>
                char.IsControl(character)
                || character is '/' or '\\')
            || !IsWellFormedUtf16(fileName))
        {
            throw new ArgumentException(
                "The image file name must be a bounded plain name.",
                nameof(fileName));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        var normalizedMediaType = mediaType.Trim().ToLowerInvariant();
        if (content.IsEmpty || content.Length > MaximumBytes)
        {
            throw new ArgumentException(
                "The image attachment exceeds its byte limit.",
                nameof(content));
        }

        if (!SignatureMatches(normalizedMediaType, content))
        {
            throw new ArgumentException(
                "The image bytes do not match a supported raster media type.",
                nameof(mediaType));
        }

        FileName = string.Concat(fileName);
        MediaType = normalizedMediaType;
        _content = content.ToArray();
    }

    public string FileName { get; }

    public string MediaType { get; }

    [JsonIgnore]
    public ReadOnlySpan<byte> Content => _content;

    private static bool SignatureMatches(
        string mediaType,
        ReadOnlySpan<byte> content) =>
        mediaType switch
        {
            "image/png" => content.StartsWith(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "image/jpeg" => content.StartsWith(
                new byte[] { 0xff, 0xd8, 0xff }),
            "image/gif" => content.StartsWith("GIF87a"u8)
                || content.StartsWith("GIF89a"u8),
            "image/webp" => content.Length >= 12
                && content[..4].SequenceEqual("RIFF"u8)
                && content.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 == value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class AgentImageAttachmentJsonConverter
    : JsonConverter<AgentImageAttachment>
{
    public override AgentImageAttachment Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        throw new JsonException(
            "Agent image bytes require the bounded checkpoint restore path.");

    public override void Write(
        Utf8JsonWriter writer,
        AgentImageAttachment value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("fileName", value.FileName);
        writer.WriteString("mediaType", value.MediaType);
        writer.WriteNumber("byteLength", value.Content.Length);
        writer.WriteEndObject();
    }
}

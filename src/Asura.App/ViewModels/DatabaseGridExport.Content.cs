using System.Text;
using Asura.Application;

namespace Asura.App.ViewModels;

internal static partial class DatabaseGridExport
{
    private static void WriteContent(TextWriter writer, DatabaseValueContent content)
    {
        using var source = content.OpenRead();
        switch (content.Kind)
        {
            case DatabaseValueKind.Binary:
                writer.Write("0x");
                WriteBinaryContent(writer, source, asBase64: false);
                break;
            case DatabaseValueKind.Collection:
                WriteArrayContent(writer, source, json: false, depth: 0);
                break;
            default:
                CopyContentText(writer, source);
                break;
        }
    }

    private static void WriteJsonContent(TextWriter writer, DatabaseValueContent content, int depth)
    {
        using var source = content.OpenRead();
        switch (content.Kind)
        {
            case DatabaseValueKind.Binary:
                writer.Write('"');
                WriteBinaryContent(writer, source, asBase64: true);
                writer.Write('"');
                break;
            case DatabaseValueKind.Collection:
                WriteArrayContent(writer, source, json: true, depth);
                break;
            case DatabaseValueKind.Json:
            case DatabaseValueKind.SignedInteger:
            case DatabaseValueKind.UnsignedInteger:
            case DatabaseValueKind.Decimal:
                // Only worker-validated JSON receives this semantic kind.
                // Keep complete tokens out of the desktop's JSON DOM.
                CopyContentText(writer, source);
                break;
            default:
                WriteJsonContentString(writer, source);
                break;
        }
    }

    private static void WriteArrayContent(TextWriter writer, Stream source, bool json, int depth)
    {
        var shape = DatabaseArrayContent.ReadShape(source);
        writer.Write('[');
        for (long index = 0; index < shape.Count; index++)
        {
            if (index > 0)
            {
                writer.Write(", ");
            }

            var textConsumed = false;
            var value = DatabaseArrayContent.ReadScalar(source, shape, (text, _) =>
            {
                if (json && shape.ElementType == DatabaseArrayScalarType.Text)
                {
                    WriteJsonContentString(writer, text);
                }
                else
                {
                    CopyContentText(writer, text);
                }

                textConsumed = true;
                return null;
            });
            if (!textConsumed)
            {
                if (json)
                {
                    WriteJsonValue(writer, value, depth + 1);
                }
                else if (value is null)
                {
                    writer.Write("NULL");
                }
                else
                {
                    WriteFullValue(writer, value);
                }
            }
        }

        if (source.ReadByte() != -1)
        {
            throw new InvalidDataException("The detached database array has trailing content.");
        }

        writer.Write(']');
    }

    private static StreamReader OpenContentText(Stream source) =>
        new(source, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);

    private static void CopyContentText(TextWriter writer, Stream source)
    {
        using var text = OpenContentText(source);
        var buffer = new char[4096];
        int count;
        while ((count = text.Read(buffer)) != 0)
        {
            writer.Write(buffer.AsSpan(0, count));
        }
    }

    private static void WriteJsonContentString(TextWriter writer, Stream source)
    {
        using var text = OpenContentText(source);
        writer.Write('"');
        var buffer = new char[4096];
        int count;
        while ((count = text.Read(buffer)) != 0)
        {
            foreach (var character in buffer.AsSpan(0, count))
            {
                switch (character)
                {
                    case '"': writer.Write("\\\""); break;
                    case '\\': writer.Write("\\\\"); break;
                    default:
                        if (character < ' ' || char.IsSurrogate(character))
                        {
                            // Escape code units, including a pair split across
                            // read chunks. Never replace a surrogate with FFFD.
                            writer.Write($"\\u{(int)character:X4}");
                        }
                        else
                        {
                            writer.Write(character);
                        }
                        break;
                }
            }
        }

        writer.Write('"');
    }

    private static void WriteBinaryContent(TextWriter writer, Stream source, bool asBase64)
    {
        var buffer = new byte[Base64ChunkBytes];
        int count;
        while ((count = source.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) != 0)
        {
            writer.Write(asBase64
                ? Convert.ToBase64String(buffer, 0, count)
                : Convert.ToHexString(buffer.AsSpan(0, count)));
        }
    }
}

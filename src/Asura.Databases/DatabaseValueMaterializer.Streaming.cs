using System.Data.Common;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Microsoft.Data.Sqlite;

namespace Asura.Databases;

internal static partial class DatabaseValueMaterializer
{
    public static async Task<DatabaseValue> MaterializeAsync(
        DbDataReader reader,
        int ordinal,
        DatabaseColumnDescriptor column,
        Func<DatabaseValueContentStore> getStore,
        CancellationToken cancellationToken)
    {
        if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
        {
            return new DatabaseValue(null, column.ValueKind, "NULL");
        }

        if (column.ValueKind is DatabaseValueKind.Text or DatabaseValueKind.Json)
        {
            // SQLite's blob-stream implementation probes table/rowid metadata.
            // Arbitrary expression results need not have that metadata; its
            // native probe can fault. Keep the supported scalar access path
            // until SQLite execution is isolated, without a second full copy.
            using var text = reader is SqliteDataReader
                ? new StringReader(reader.GetString(ordinal))
                : reader.GetTextReader(ordinal);
            return await ReadTextAsync(text, column.ValueKind, getStore, cancellationToken).ConfigureAwait(false);
        }

        if (column.ValueKind == DatabaseValueKind.Binary)
        {
            await using var binary = reader is SqliteDataReader
                ? new MemoryStream(reader.GetFieldValue<byte[]>(ordinal), writable: false)
                : reader.GetStream(ordinal);
            return await ReadBinaryAsync(binary, getStore, cancellationToken).ConfigureAwait(false);
        }

        var value = reader.GetValue(ordinal);
        if (value is Stream stream)
        {
            await using (stream.ConfigureAwait(false))
            {
                if (stream.CanSeek) { stream.Position = 0; }
                return await ReadBinaryAsync(stream, getStore, cancellationToken).ConfigureAwait(false);
            }
        }
        if (value is TextReader textReader)
        {
            using (textReader)
            {
                return await ReadTextAsync(textReader, DatabaseValueKind.Text, getStore, cancellationToken).ConfigureAwait(false);
            }
        }
        return FromProviderValue(value, column.ValueKind, column.DataTypeName);
    }

    internal static async Task<DatabaseValue> ReadTextAsync(
        TextReader reader,
        DatabaseValueKind kind,
        Func<DatabaseValueContentStore> getStore,
        CancellationToken cancellationToken)
    {
        var prefix = new char[DefaultMaxDisplayCharacters + 1];
        var count = await reader.ReadBlockAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (count < prefix.Length)
        {
            var text = new string(prefix, 0, count);
            return new DatabaseValue(text, kind, text);
        }

        var content = await getStore().StoreAsync(
            DatabaseValueKind.Text,
            async (destination, token) =>
            {
                await using var writer = new StreamWriter(destination, new UTF8Encoding(false, true), 8192, leaveOpen: true);
                await writer.WriteAsync(prefix, token).ConfigureAwait(false);
                var buffer = new char[8192];
                int read;
                while ((read = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    await writer.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }

                await writer.FlushAsync(token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        if (kind == DatabaseValueKind.Json)
        {
            // This validation belongs in the owned provider worker. The UI
            // streams a validated document without constructing its own DOM.
            // Invalid JSON retains its exact text and exports as a JSON string.
            try
            {
                await using var source = content.OpenRead();
                using var document = await JsonDocument.ParseAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
                content = new ValidatedJsonContent(content);
            }
            catch (JsonException)
            {
                // Same semantics as the scalar JSON export path.
            }
        }

        var displayLength = DefaultMaxDisplayCharacters - 64;
        if (char.IsHighSurrogate(prefix[displayLength - 1]))
        {
            displayLength--;
        }
        return new DatabaseValue(content, kind,
            new string(prefix, 0, displayLength) + "… [display preview; full value available]", IsTruncated: true);
    }

    private sealed class ValidatedJsonContent(DatabaseValueContent source) : DatabaseValueContent
    {
        public override long Length => source.Length;
        public override DatabaseValueKind Kind => DatabaseValueKind.Json;
        public override Stream OpenRead() => source.OpenRead();
    }

    internal static async Task<DatabaseValue> ReadBinaryAsync(
        Stream source,
        Func<DatabaseValueContentStore> getStore,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[DefaultMaxDisplayCharacters + 1];
        var count = 0;
        while (count < prefix.Length)
        {
            var read = await source.ReadAsync(prefix.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return FromProviderValue(prefix.AsSpan(0, count).ToArray(), DatabaseValueKind.Binary);
            }

            count += read;
        }

        var content = await getStore().StoreAsync(
            DatabaseValueKind.Binary,
            async (destination, token) =>
            {
                await destination.WriteAsync(prefix, token).ConfigureAwait(false);
                var buffer = new byte[8192];
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
        return new DatabaseValue(content, DatabaseValueKind.Binary,
            $"0x{Convert.ToHexString(prefix.AsSpan(0, BinaryPreviewByteCount))}… ({content.Length} bytes; full value available)",
            IsTruncated: true);
    }
}

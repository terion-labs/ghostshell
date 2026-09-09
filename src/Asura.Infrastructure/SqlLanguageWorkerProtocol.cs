using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;

namespace Asura.Infrastructure;

internal static class SqlLanguageWorkerProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 8 * 1024 * 1024;

    public static WorkerCatalog Catalog(SqlCatalogSnapshot snapshot) => new(
        snapshot.DriverId,
        snapshot.DefaultCatalog,
        snapshot.DefaultSchema,
        [.. snapshot.Objects.Select(Object)],
        [.. snapshot.Routines.Select(Routine)],
        Coverage(snapshot.RoutineCoverage),
        [.. snapshot.IntrinsicSymbols.Select(IntrinsicSymbol)],
        Coverage(snapshot.IntrinsicCoverage));

    public static byte[] Serialize(WorkerRequestEnvelope request)
    {
        using var stream = new BoundedWriteStream(MaximumMessageBytes);
        JsonSerializer.Serialize(
            stream,
            request,
            SqlLanguageWorkerJsonContext.Default.WorkerRequestEnvelope);
        return stream.ToArray();
    }

    public static WorkerResponseEnvelope Deserialize(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(
                    bytes,
                    SqlLanguageWorkerJsonContext.Default.WorkerResponseEnvelope)
                ?? throw new SqlLanguageProtocolException(
                    "SQL language worker returned an empty JSON value.");
        }
        catch (JsonException exception)
        {
            throw new SqlLanguageProtocolException(
                "SQL language worker returned malformed JSON.",
                exception);
        }
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > MaximumMessageBytes)
        {
            throw new SqlLanguageProtocolException(
                $"SQL language response length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    internal static WorkerCatalogObjectId ObjectId(DatabaseObjectId value) => new(
        value.Catalog,
        value.Schema,
        value.Name);

    private static WorkerCatalogObject Object(SqlCatalogObject value) => new(
        ObjectId(value.Id),
        value.Kind == DatabaseTableKind.View ? "view" : "table",
        [.. value.Columns.Select(Column)]);

    private static WorkerCatalogColumn Column(SqlCatalogColumn value) => new(
        value.Name,
        value.DataTypeName,
        value.ValueKind.ToString().ToLowerInvariant(),
        value.IsNullable);

    private static WorkerCatalogRoutine Routine(SqlCatalogRoutine value) => new(
        ObjectId(value.Id),
        value.Kind.ToString().ToLowerInvariant(),
        value.Signature,
        [.. value.Parameters.Select(RoutineParameter)],
        value.ReturnTypeName,
        ValueKind(value.ReturnValueKind),
        value.MinimumArgumentCount,
        value.MaximumArgumentCount);

    private static WorkerCatalogRoutineParameter RoutineParameter(
        SqlCatalogRoutineParameter value) => new(
            value.Name,
            value.DataTypeName,
            ValueKind(value.ValueKind),
            value.Mode.ToString().ToLowerInvariant(),
            value.IsOptional,
            value.IsVariadic);

    private static WorkerCatalogIntrinsicSymbol IntrinsicSymbol(
        SqlCatalogIntrinsicSymbol value) => new(
            value.Name,
            value.Kind.ToString().ToLowerInvariant());

    private static string Coverage(SqlCatalogCoverage value) => value switch
    {
        SqlCatalogCoverage.UserDefinedOnly => "userDefinedOnly",
        SqlCatalogCoverage.Complete => "complete",
        SqlCatalogCoverage.Partial => "partial",
        _ => "none",
    };

    private static string? ValueKind(DatabaseValueKind? value) =>
        value?.ToString().ToLowerInvariant();

    private sealed class BoundedWriteStream(int maximumBytes) : Stream
    {
        private readonly MemoryStream _inner = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _inner.ToArray();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            _inner.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _inner.WriteByte(value);
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes < 0 || _inner.Length > maximumBytes - additionalBytes)
            {
                throw new SqlLanguageProtocolException(
                    "SQL language request exceeds 8 MiB.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

internal sealed class SqlLanguageProtocolException : Exception
{
    public SqlLanguageProtocolException(string message)
        : base(message)
    {
    }

    public SqlLanguageProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

}

internal sealed record WorkerRequestEnvelope(
    int Version,
    long Id,
    string Method,
    WorkerRequestParameters? Params = null);

internal sealed record WorkerRequestParameters(
    WorkerCatalog? Catalog = null,
    string? Sql = null,
    int? CursorOffset = null,
    WorkerCatalogObjectId? PreferredObject = null);

internal sealed record WorkerCatalog(
    string DriverId,
    string? DefaultCatalog,
    string? DefaultSchema,
    IReadOnlyList<WorkerCatalogObject> Objects,
    IReadOnlyList<WorkerCatalogRoutine> Routines,
    string RoutineCoverage,
    IReadOnlyList<WorkerCatalogIntrinsicSymbol> IntrinsicSymbols,
    string IntrinsicCoverage);

internal sealed record WorkerCatalogObject(
    WorkerCatalogObjectId Id,
    string Kind,
    IReadOnlyList<WorkerCatalogColumn> Columns);

internal sealed record WorkerCatalogObjectId(
    string? Catalog,
    string? Schema,
    string Name);

internal sealed record WorkerCatalogColumn(
    string Name,
    string DataTypeName,
    string ValueKind,
    bool? IsNullable);

internal sealed record WorkerCatalogRoutine(
    WorkerCatalogObjectId Id,
    string Kind,
    string Signature,
    IReadOnlyList<WorkerCatalogRoutineParameter> Parameters,
    string? ReturnTypeName,
    string? ReturnValueKind,
    int MinimumArgumentCount,
    int? MaximumArgumentCount);

internal sealed record WorkerCatalogRoutineParameter(
    string? Name,
    string DataTypeName,
    string? ValueKind,
    string Mode,
    bool IsOptional,
    bool IsVariadic);

internal sealed record WorkerCatalogIntrinsicSymbol(
    string Name,
    string Kind);

internal sealed record WorkerResponseEnvelope(
    int Version,
    long Id,
    JsonElement? Result,
    WorkerResponseError? Error);

internal sealed record WorkerCompletionResult(
    int? ReplacementStart,
    int? ReplacementLength,
    IReadOnlyList<WorkerCompletionItem>? Items);

internal sealed record WorkerDiagnosticResult(
    IReadOnlyList<WorkerDiagnostic>? Items);

internal sealed record WorkerCompletionItem(
    string? Label,
    string? Kind,
    string? Detail,
    string? InsertText);

internal sealed record WorkerDiagnostic(
    string? Message,
    string? Severity,
    int Start,
    int Length,
    string? Code);

internal sealed record WorkerResponseError(string? Code, string? Message);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkerRequestEnvelope))]
[JsonSerializable(typeof(WorkerResponseEnvelope))]
[JsonSerializable(typeof(WorkerCompletionResult))]
[JsonSerializable(typeof(WorkerDiagnosticResult))]
internal sealed partial class SqlLanguageWorkerJsonContext : JsonSerializerContext;

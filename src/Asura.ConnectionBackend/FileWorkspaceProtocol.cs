using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Core;
using Asura.Files;
using ProviderId = Asura.Files.FileProviderProfileId;

namespace Asura.ConnectionBackend;

internal enum FileWorkspaceOperation { List, Stat, Read, Write, CreateDirectory, Rename, Transfer, Delete, GetAccessControl, SetAccessControl }
internal enum FileWorkspaceMessageKind { Ready, Execute, Result, ResolveSecret, VerifyHostKey, Identities, Sign, Upload, Download, Progress }

internal sealed record FileWorkspaceRequest(FileProviderProfile Profile, ConnectionProfile? Connection, FileWorkspaceOperation Operation,
    FileListRequest? List = null, FileStatRequest? Stat = null, FileReadRequest? Read = null, FileWriteRequest? Write = null,
    FileCreateDirectoryRequest? CreateDirectory = null, FileRenameRequest? Rename = null, FileTransferRequest? Transfer = null,
    FileDeleteRequest? Delete = null, FileAccessControlRequest? GetAccessControl = null, FileSetAccessControlRequest? SetAccessControl = null);

internal sealed record FileWorkspacePage(FileEntry[] Items, FilePageToken? ContinuationToken);
internal sealed record FileWorkspaceIdentity(int Id, string Algorithm, byte[] PublicKey);
internal sealed record FileWorkspaceHostKey(string Algorithm, string PublicKeyBase64);

internal sealed record FileWorkspaceMessage(FileWorkspaceMessageKind Kind,
    FileProviderError? Error = null, FileWorkspacePage? Page = null, FileEntry? Entry = null,
    FileReadReceipt? Read = null, FileWriteReceipt? Write = null, FileTransferReceipt? Transfer = null,
    FileDeleteReceipt? Delete = null, FileAccessControl? AccessControl = null,
    ResolveSecretRequest? Secret = null, FileWorkspaceHostKey? HostKey = null,
    byte[]? Bytes = null, int Identity = 0, int Count = 0, FileTransferProgress? Progress = null);

internal sealed record FileWorkspaceReply(FileWorkspaceMessageKind Kind, byte[]? Bytes = null,
    SecretVaultErrorCode? SecretFailure = null, SshHostKeyVerification? Verification = null,
    FileWorkspaceIdentity[]? Identities = null);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    Converters = [typeof(FileWorkspaceLocationConverter), typeof(FileWorkspacePreconditionConverter),
        typeof(FileWorkspaceVersionConverter), typeof(FileWorkspacePageTokenConverter), typeof(FileWorkspaceProviderIdConverter), typeof(FileWorkspaceAuthorityConverter)])]
[JsonSerializable(typeof(FileWorkspaceRequest))]
[JsonSerializable(typeof(FileWorkspaceMessage))]
[JsonSerializable(typeof(FileWorkspaceReply))]
[JsonSerializable(typeof(FileWorkspaceLocation))]
[JsonSerializable(typeof(FileWorkspacePrecondition))]
[JsonSerializable(typeof(FileProviderConfiguration.Local), TypeInfoPropertyName = "LocalFileProviderConfiguration")]
[JsonSerializable(typeof(ProviderId), TypeInfoPropertyName = "RuntimeFileProviderProfileId")]
internal sealed partial class FileWorkspaceJsonContext : JsonSerializerContext;

internal sealed record FileWorkspaceLocation(string Provider, string? Authority, string[]? Segments, string? ObjectKey, bool ContainerRoot, string? Version)
{
    internal static FileWorkspaceLocation From(FileLocation location) => new(location.ProviderProfileId.Value,
        location.Authority?.Value, location.Address is FileLocationAddress.Hierarchical hierarchical
            ? [.. hierarchical.Path.Segments.Select(segment => segment.Value)] : null,
        location.ObjectKey?.Value, location.IsContainerRoot, location.Version?.Value);

    internal FileLocation ToLocation()
    {
        var id = new ProviderId(Provider);
        var authority = Authority is null ? (FileAuthority?)null : new FileAuthority(Authority);
        var version = Version is null ? (FileVersion?)null : new FileVersion(Version);
        if ((Segments is not null ? 1 : 0) + (ObjectKey is not null ? 1 : 0) + (ContainerRoot ? 1 : 0) != 1)
        {
            throw new JsonException("The file location must contain exactly one address kind.");
        }
        if (Segments is not null) { return new(id, authority, FilePath.FromSegments(Segments.Select(value => new FilePathSegment(value))), version); }
        if (authority is null) { throw new JsonException("An object location requires an authority."); }
        return ObjectKey is not null ? FileLocation.ForObjectKey(id, authority.Value, new(ObjectKey), version)
            : FileLocation.ForContainerRoot(id, authority.Value, version);
    }
}

internal sealed class FileWorkspaceLocationConverter : JsonConverter<FileLocation>
{
    public override FileLocation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (JsonSerializer.Deserialize(ref reader, FileWorkspaceJsonContext.Default.FileWorkspaceLocation)
            ?? throw new JsonException("The file location is missing.")).ToLocation();

    public override void Write(Utf8JsonWriter writer, FileLocation value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, FileWorkspaceLocation.From(value), FileWorkspaceJsonContext.Default.FileWorkspaceLocation);
}

internal sealed record FileWorkspacePrecondition(string Kind, string? Version);

internal sealed class FileWorkspacePreconditionConverter : JsonConverter<FileMutationPrecondition>
{
    public override FileMutationPrecondition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = JsonSerializer.Deserialize(ref reader, FileWorkspaceJsonContext.Default.FileWorkspacePrecondition)
            ?? throw new JsonException("The file precondition is missing.");
        return value switch
        {
            { Kind: "any", Version: null } => new FileMutationPrecondition.Any(),
            { Kind: "absent", Version: null } => new FileMutationPrecondition.MustNotExist(),
            { Kind: "present", Version: null } => new FileMutationPrecondition.MustExist(),
            { Kind: "version", Version: not null } => new FileMutationPrecondition.VersionMatches(new(value.Version)),
            _ => throw new JsonException("The file precondition is invalid."),
        };
    }

    public override void Write(Utf8JsonWriter writer, FileMutationPrecondition value, JsonSerializerOptions options)
    {
        var shape = value switch
        {
            FileMutationPrecondition.Any => new FileWorkspacePrecondition("any", null),
            FileMutationPrecondition.MustNotExist => new FileWorkspacePrecondition("absent", null),
            FileMutationPrecondition.MustExist => new FileWorkspacePrecondition("present", null),
            FileMutationPrecondition.VersionMatches version => new FileWorkspacePrecondition("version", version.Version.Value),
            _ => throw new JsonException("The file precondition is invalid."),
        };
        JsonSerializer.Serialize(writer, shape, FileWorkspaceJsonContext.Default.FileWorkspacePrecondition);
    }
}

internal abstract class FileWorkspaceStringConverter<T>(Func<string, T> create, Func<T, string> text) : JsonConverter<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        create(reader.GetString() ?? throw new JsonException("The opaque file token is missing."));
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(text(value));
}

internal sealed class FileWorkspaceVersionConverter() : FileWorkspaceStringConverter<FileVersion>(value => new(value), value => value.Value);
internal sealed class FileWorkspacePageTokenConverter() : FileWorkspaceStringConverter<FilePageToken>(value => new(value), value => value.Value);
internal sealed class FileWorkspaceProviderIdConverter() : FileWorkspaceStringConverter<ProviderId>(value => new(value), value => value.Value);
internal sealed class FileWorkspaceAuthorityConverter() : FileWorkspaceStringConverter<FileAuthority>(value => new(value), value => value.Value);

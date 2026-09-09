using System.Globalization;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Desktop;

internal sealed class IsolatedPosixFilePanelClient : IFilePanelClient
{
    private const string ProfileId = "builtin.files.isolate";
    private const string EncodedSegmentPrefix = "asura-posix:";
    private const int MaximumPageSize = 1000;
    private const long MaximumPreviewBytes = 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly FilePanelLocation Root = Location("/");
    private readonly IConnectionCommandExecutor _executor;

    public IsolatedPosixFilePanelClient(IConnectionCommandExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        Profiles =
        [
            new FileProviderProfileDescriptor(
                ProfileId,
                "Workspace",
                FileProviderFamily.Posix,
                Root,
                FilePanelCapability.List
                    | FilePanelCapability.Stat
                    | FilePanelCapability.RangedRead
                    | FilePanelCapability.CreateDirectory
                    | FilePanelCapability.Rename
                    | FilePanelCapability.Copy
                    | FilePanelCapability.Delete
                    | FilePanelCapability.StreamingWrite,
                MaximumPageSize,
                MaximumPreviewBytes,
                StartLocation: Location("/home/asura")),
        ];
    }

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; }

    public async ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryPath(request.Location, out var path, out var error))
        {
            return FilePanelResult<FilePanelPage>.Failure(error!);
        }

        const string script = """
            p=$(printf %s "$1" | base64 -d) || exit 2
            export SHOW_HIDDEN="$2"
            find "$p" -mindepth 1 -maxdepth 1 -exec sh -c '
              for p do
                n=${p##*/}
                if [ "$SHOW_HIDDEN" != 1 ]; then case "$n" in .*) continue;; esac; fi
                b=$(printf %s "$n" | base64 | tr -d "\n")
                if [ -L "$p" ]; then k=l; elif [ -d "$p" ]; then k=d; elif [ -f "$p" ]; then k=f; else k=o; fi
                s=$(stat -c %s "$p" 2>/dev/null || printf 0)
                m=$(stat -c %Y "$p" 2>/dev/null || printf 0)
                printf "%s\t%s\t%s\t%s\n" "$b" "$k" "$s" "$m"
              done
            ' sh {} +
            """;
        var result = await ExecuteAsync(
            "/bin/sh",
            ["-c", script, "asura-files", path, request.ShowHidden ? "1" : "0"],
            16 * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return FilePanelResult<FilePanelPage>.Failure(result.Error!);
        }

        var entries = result.Value!
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => TryParseEntry(request.Location, line))
            .Where(entry => entry is not null)
            .Cast<FilePanelEntry>()
            .OrderByDescending(entry => entry.Kind == FilePanelEntryKind.Directory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var offset = ParseOffset(request.ContinuationToken);
        var page = entries.Skip(offset).Take(request.PageSize).ToArray();
        var next = offset + page.Length < entries.Length
            ? (offset + page.Length).ToString(CultureInfo.InvariantCulture)
            : null;
        return FilePanelResult<FilePanelPage>.Success(new FilePanelPage(page, next));
    }

    public async ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken)
    {
        if (!TryPath(location, out var path, out var error))
        {
            return FilePanelResult<FilePanelEntry>.Failure(error!);
        }

        const string script = "p=$(printf %s \"$1\" | base64 -d) || exit 2; n=${p##*/}; [ -n \"$n\" ] || n=/; b=$(printf %s \"$n\" | base64 | tr -d '\\n'); if [ -L \"$p\" ]; then k=l; elif [ -d \"$p\" ]; then k=d; elif [ -f \"$p\" ]; then k=f; else k=o; fi; s=$(stat -c %s \"$p\" 2>/dev/null || printf 0); m=$(stat -c %Y \"$p\" 2>/dev/null || printf 0); printf '%s\\t%s\\t%s\\t%s\\n' \"$b\" \"$k\" \"$s\" \"$m\"";
        var result = await ExecuteAsync(
            "/bin/sh",
            ["-c", script, "asura-files", path],
            16 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return FilePanelResult<FilePanelEntry>.Failure(result.Error!);
        }

        var entry = TryParseEntry(location.Parent, result.Value!.TrimEnd('\n'));
        return entry is null
            ? Failure<FilePanelEntry>("file_stat_invalid", "The environment returned invalid file metadata.")
            : FilePanelResult<FilePanelEntry>.Success(entry);
    }

    public async ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryPath(request.Location, out var path, out var error))
        {
            return FilePanelResult<FilePanelPreview>.Failure(error!);
        }

        var maximum = Math.Min(request.MaximumBytes, MaximumPreviewBytes);
        var command = new ConnectionBinaryCommand(
            BuiltInConnections.Local,
            "/bin/sh",
            ["-c", "p=$(printf %s \"$1\" | base64 -d) || exit 2; head -c \"$2\" -- \"$p\"", "asura-files", path, maximum.ToString(CultureInfo.InvariantCulture)],
            CommandTimeout,
            checked((int)maximum));
        var result = await _executor.ExecuteBinaryAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (result.Outcome != ConnectionCommandOutcome.Exited || result.ExitCode != 0)
        {
            return Failure<FilePanelPreview>("file_preview_failed", "The file could not be read from the workspace environment.");
        }

        var (kind, mediaType) = FilePanelPreviewClassifier.Classify(
            request.Location,
            result.StandardOutput.Span);
        return FilePanelResult<FilePanelPreview>.Success(new FilePanelPreview(
            request.Location,
            kind,
            mediaType,
            result.StandardOutput.Span,
            result.OutputTruncated));
    }

    public async ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        if (IsRoot(request.Location))
        {
            return RootMutationFailure<FilePanelEntry>();
        }

        var result = await MutateAsync("mkdir -- \"$p\"", request.Location, cancellationToken)
            .ConfigureAwait(false);
        return !result.IsSuccess
            ? FilePanelResult<FilePanelEntry>.Failure(result.Error!)
            : await StatAsync(request.Location, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken)
    {
        if (IsRoot(request.Source) || IsRoot(request.Destination))
        {
            return RootMutationFailure<FilePanelEntry>();
        }

        if (!TryPath(request.Source, out var source, out var error)
            || !TryPath(request.Destination, out var destination, out error))
        {
            return FilePanelResult<FilePanelEntry>.Failure(error!);
        }

        var result = await ExecuteAsync(
            "/bin/sh",
            ["-c", "a=$(printf %s \"$1\" | base64 -d) || exit 2; b=$(printf %s \"$2\" | base64 -d) || exit 2; mv -- \"$a\" \"$b\"", "asura-files", source, destination],
            16 * 1024,
            cancellationToken).ConfigureAwait(false);
        return !result.IsSuccess
            ? FilePanelResult<FilePanelEntry>.Failure(result.Error!)
            : await StatAsync(request.Destination, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken)
    {
        if (IsRoot(request.Location))
        {
            return RootMutationFailure<FilePanelDeleteReceipt>();
        }

        var stat = await StatAsync(request.Location, cancellationToken).ConfigureAwait(false);
        if (!stat.IsSuccess)
        {
            return FilePanelResult<FilePanelDeleteReceipt>.Failure(stat.Error!);
        }

        if (!TryPath(request.Location, out var path, out var error))
        {
            return FilePanelResult<FilePanelDeleteReceipt>.Failure(error!);
        }

        var result = await ExecuteAsync(
            "/bin/sh",
            ["-c", request.Recursive ? "p=$(printf %s \"$1\" | base64 -d) || exit 2; rm -rf -- \"$p\"" : "p=$(printf %s \"$1\" | base64 -d) || exit 2; rm -f -- \"$p\"", "asura-files", path],
            16 * 1024,
            cancellationToken).ConfigureAwait(false);
        return !result.IsSuccess
            ? FilePanelResult<FilePanelDeleteReceipt>.Failure(result.Error!)
            : FilePanelResult<FilePanelDeleteReceipt>.Success(new FilePanelDeleteReceipt(
                request.Location,
                stat.Value!.Kind == FilePanelEntryKind.Directory));
    }

    public async ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (IsRoot(request.Location))
        {
            return RootMutationFailure<FilePanelTextWriteReceipt>();
        }

        if (!TryPath(request.Location, out var path, out var error))
        {
            return FilePanelResult<FilePanelTextWriteReceipt>.Failure(error!);
        }

        var existed = (await StatAsync(request.Location, cancellationToken).ConfigureAwait(false)).IsSuccess;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Content));
        var result = await ExecuteAsync(
            "/bin/sh",
            ["-c", "p=$(printf %s \"$1\" | base64 -d) || exit 2; printf %s \"$2\" | base64 -d > \"$p\"", "asura-files", path, encoded],
            16 * 1024,
            cancellationToken).ConfigureAwait(false);
        return !result.IsSuccess
            ? FilePanelResult<FilePanelTextWriteReceipt>.Failure(result.Error!)
            : FilePanelResult<FilePanelTextWriteReceipt>.Success(new FilePanelTextWriteReceipt(
                request.Location,
                Encoding.UTF8.GetByteCount(request.Content),
                existed));
    }

    public async ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken)
    {
        if (IsRoot(request.Source) || IsRoot(request.Destination))
        {
            return RootMutationFailure<FilePanelCopyReceipt>();
        }

        if (!TryPath(request.Source, out var source, out var error)
            || !TryPath(request.Destination, out var destination, out error))
        {
            return FilePanelResult<FilePanelCopyReceipt>.Failure(error!);
        }

        var result = await ExecuteAsync(
            "/bin/sh",
            ["-c", "a=$(printf %s \"$1\" | base64 -d) || exit 2; b=$(printf %s \"$2\" | base64 -d) || exit 2; cp -R -- \"$a\" \"$b\"", "asura-files", source, destination],
            16 * 1024,
            cancellationToken).ConfigureAwait(false);
        var stat = await StatAsync(request.Destination, cancellationToken).ConfigureAwait(false);
        return !result.IsSuccess || !stat.IsSuccess
            ? FilePanelResult<FilePanelCopyReceipt>.Failure(result.Error ?? stat.Error!)
            : FilePanelResult<FilePanelCopyReceipt>.Success(new FilePanelCopyReceipt(
                request.Source,
                request.Destination,
                stat.Value!.Size ?? 0));
    }

    private async ValueTask<FilePanelResult<Unit>> MutateAsync(
        string script,
        FilePanelLocation location,
        CancellationToken cancellationToken)
    {
        if (!TryPath(location, out var path, out var error))
        {
            return FilePanelResult<Unit>.Failure(error!);
        }

        var result = await ExecuteAsync(
            "/bin/sh",
            ["-c", "p=$(printf %s \"$1\" | base64 -d) || exit 2; " + script, "asura-files", path],
            16 * 1024,
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? FilePanelResult<Unit>.Success(Unit.Value)
            : FilePanelResult<Unit>.Failure(result.Error!);
    }

    private async ValueTask<FilePanelResult<string>> ExecuteAsync(
        string executable,
        IReadOnlyList<string> arguments,
        int outputLimit,
        CancellationToken cancellationToken)
    {
        var result = await _executor.ExecuteAsync(
            new ConnectionCommand(
                BuiltInConnections.Local,
                executable,
                arguments,
                CommandTimeout,
                outputLimit),
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ConnectionCommandOutcome.Cancelled)
        {
            return FilePanelResult<string>.Failure(new FilePanelError(
                FilePanelErrorCode.Cancelled,
                "file_operation_cancelled",
                "The file operation was cancelled.",
                Retryable: true));
        }

        return result.Outcome == ConnectionCommandOutcome.Exited && result.ExitCode == 0
            ? FilePanelResult<string>.Success(result.StandardOutput)
            : Failure<string>("file_environment_command_failed", "The workspace environment could not complete the file operation.");
    }

    private static FilePanelEntry? TryParseEntry(FilePanelLocation parent, string line)
    {
        var fields = line.Split('\t');
        if (fields.Length != 4)
        {
            return null;
        }

        byte[] nameBytes;
        try
        {
            nameBytes = Convert.FromBase64String(fields[0]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (nameBytes.Length == 0
            || !long.TryParse(fields[2], CultureInfo.InvariantCulture, out var size)
            || !long.TryParse(fields[3], CultureInfo.InvariantCulture, out var modified))
        {
            return null;
        }

        var kind = fields[1] switch
        {
            "d" => FilePanelEntryKind.Directory,
            "f" => FilePanelEntryKind.File,
            "l" => FilePanelEntryKind.Link,
            _ => FilePanelEntryKind.Other,
        };
        var name = DisplayName(nameBytes);
        return new FilePanelEntry(
            parent.Child(new FilePanelPathSegment(EncodeSegment(nameBytes))),
            name,
            kind,
            kind == FilePanelEntryKind.File ? size : null,
            modified > 0 ? DateTimeOffset.FromUnixTimeSeconds(modified) : null,
            nameBytes[0] == '.');
    }

    private static int ParseOffset(string? token) =>
        int.TryParse(token, CultureInfo.InvariantCulture, out var offset) && offset >= 0
            ? offset
            : 0;

    private static bool TryPath(
        FilePanelLocation location,
        out string path,
        out FilePanelError? error)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!string.Equals(location.ProviderProfileId, ProfileId, StringComparison.Ordinal)
            || location.Address is not FilePanelAddress.Hierarchical hierarchical)
        {
            path = string.Empty;
            error = new FilePanelError(
                FilePanelErrorCode.InvalidLocation,
                "file_environment_location_invalid",
                "This location does not belong to the workspace environment.",
                Retryable: false);
            return false;
        }

        using var bytes = new MemoryStream();
        bytes.WriteByte((byte)'/');
        for (var index = 0; index < hierarchical.Path.Segments.Length; index++)
        {
            if (index > 0)
            {
                bytes.WriteByte((byte)'/');
            }

            if (!TryDecodeSegment(hierarchical.Path.Segments[index].Value, out var segmentBytes))
            {
                path = string.Empty;
                error = InvalidLocationError();
                return false;
            }

            bytes.Write(segmentBytes);
        }

        path = Convert.ToBase64String(bytes.ToArray());
        error = null;
        return true;
    }

    private static FilePanelLocation Location(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => new FilePanelPathSegment(
                EncodeSegment(Encoding.UTF8.GetBytes(segment))));
        return new FilePanelLocation(
            ProfileId,
            authority: null,
            new FilePanelAddress.Hierarchical(FilePanelPath.FromSegments(segments)));
    }

    private static string EncodeSegment(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var decoded = new UTF8Encoding(false, true).GetString(bytes);
            if (decoded is not ("." or "..")
                && !decoded.Any(char.IsControl)
                && !decoded.StartsWith(EncodedSegmentPrefix, StringComparison.Ordinal))
            {
                return decoded;
            }
        }
        catch (DecoderFallbackException)
        {
        }

        return EncodedSegmentPrefix + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeSegment(string segment, out byte[] bytes)
    {
        if (!segment.StartsWith(EncodedSegmentPrefix, StringComparison.Ordinal))
        {
            bytes = Encoding.UTF8.GetBytes(segment);
            return true;
        }

        var encoded = segment[EncodedSegmentPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }

        return bytes.Length > 0
            && !bytes.Contains((byte)'/')
            && !bytes.Contains((byte)'\0')
            && !bytes.AsSpan().SequenceEqual("."u8)
            && !bytes.AsSpan().SequenceEqual(".."u8);
    }

    private static bool IsRoot(FilePanelLocation location) =>
        location.Address is FilePanelAddress.Hierarchical { Path.IsRoot: true };

    private static FilePanelError InvalidLocationError() => new(
        FilePanelErrorCode.InvalidLocation,
        "file_environment_location_invalid",
        "This location does not belong to the workspace environment.",
        Retryable: false);

    private static FilePanelResult<T> RootMutationFailure<T>() =>
        FilePanelResult<T>.Failure(new FilePanelError(
            FilePanelErrorCode.RootMutationNotAllowed,
            "file_root_mutation_not_allowed",
            "The workspace environment root cannot be changed.",
            Retryable: false));

    private static string DisplayName(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var display = new StringBuilder(bytes.Length * 4);
            foreach (var value in bytes)
            {
                if (value is >= 0x20 and <= 0x7e && value != '\\')
                {
                    display.Append((char)value);
                }
                else
                {
                    display.Append(CultureInfo.InvariantCulture, $"\\x{value:X2}");
                }
            }

            return display.ToString();
        }
    }

    private static FilePanelResult<T> Failure<T>(string code, string message) =>
        FilePanelResult<T>.Failure(new FilePanelError(
            FilePanelErrorCode.IoFailure,
            code,
            message,
            Retryable: true));
}

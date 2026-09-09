using System.Globalization;
using Asura.Application;

namespace Asura.Docker;

/// <summary>
/// The shallow filesystem protocol spoken over <c>docker exec</c>. Paths are
/// argv, not interpolated shell text, and entry fields are NUL-delimited so a
/// legal Unix filename cannot corrupt the record boundary.
/// </summary>
internal static class DockerFileExecProtocol
{
    public const int MissingExitCode = 40;
    public const int NotDirectoryExitCode = 41;

    public static IReadOnlyList<string> ShellPaths { get; } = Array.AsReadOnly(
    [
        "/bin/sh",
        "/bin/bash",
        "/bin/ash",
        "/bin/dash",
        "/bin/zsh",
        "/bin/ksh",
    ]);

    public static string ListScript { get; } = $$"""
        {{EmitEntryFunction}}
        directory=$1
        if [ ! -d "$directory" ]; then
          if [ -e "$directory" ] || [ -L "$directory" ]; then exit {{NotDirectoryExitCode}}; fi
          exit {{MissingExitCode}}
        fi
        for entry in "$directory"/.[!.]* "$directory"/..?* "$directory"/*; do
          emit_entry "$entry"
        done
        """;

    public static string StatScript { get; } = $$"""
        {{EmitEntryFunction}}
        target=$1
        if [ ! -e "$target" ] && [ ! -L "$target" ]; then exit {{MissingExitCode}}; fi
        emit_entry "$target"
        """;

    public static DockerResult<DockerFileListing> ParseListing(
        DockerResourceReference resource,
        string parentPath,
        string output)
    {
        var entries = ParseEntries(parentPath, output);
        return entries is null
            ? Invalid<DockerFileListing>()
            : new DockerResult<DockerFileListing>.Success(new DockerFileListing(
                resource,
                parentPath,
                Array.AsReadOnly(entries
                    .OrderByDescending(entry => entry.Kind == DockerFileKind.Directory)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray())));
    }

    public static DockerResult<DockerFileEntry> ParseStat(string path, string output)
    {
        var parentPath = ParentPath(path);
        var entries = ParseEntries(parentPath, output);
        return entries is { Count: 1 }
            ? new DockerResult<DockerFileEntry>.Success(entries[0])
            : Invalid<DockerFileEntry>();
    }

    private static List<DockerFileEntry>? ParseEntries(string parentPath, string output)
    {
        if (output.Length == 0)
        {
            return [];
        }

        var fields = output.Split('\0');
        if (fields[^1].Length != 0 || (fields.Length - 1) % 4 != 0)
        {
            return null;
        }

        var entries = new List<DockerFileEntry>((fields.Length - 1) / 4);
        for (var index = 0; index < fields.Length - 1; index += 4)
        {
            var name = fields[index];
            if (!IsValidName(name) || !TryKind(fields[index + 1], out var kind))
            {
                return null;
            }

            long? size = null;
            if (kind == DockerFileKind.File
                && long.TryParse(
                    fields[index + 2],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedSize)
                && parsedSize >= 0)
            {
                size = parsedSize;
            }

            DateTimeOffset? modifiedAt = null;
            if (long.TryParse(
                    fields[index + 3],
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var epochSeconds))
            {
                try
                {
                    modifiedAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }

            entries.Add(new DockerFileEntry(
                name,
                ChildPath(parentPath, name),
                kind,
                size,
                modifiedAt));
        }

        return entries;
    }

    private static bool TryKind(string value, out DockerFileKind kind)
    {
        kind = value switch
        {
            "f" => DockerFileKind.File,
            "d" => DockerFileKind.Directory,
            "l" => DockerFileKind.Link,
            "o" => DockerFileKind.Other,
            _ => (DockerFileKind)(-1),
        };
        return Enum.IsDefined(kind);
    }

    private static bool IsValidName(string name) =>
        !string.IsNullOrEmpty(name)
        && name is not "." and not ".."
        && !name.Contains('/', StringComparison.Ordinal)
        && !name.Contains('\0', StringComparison.Ordinal);

    private static string ChildPath(string parentPath, string name) => string.Equals(parentPath, "/", StringComparison.Ordinal) ? $"/{name}" : $"{parentPath}/{name}";

    private static string ParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static DockerResult<T> Invalid<T>() =>
        new DockerResult<T>.Failure(new DockerError(
            DockerErrorCode.InvalidResponse,
            "The container filesystem probe returned an invalid response.",
            true));

    private const string EmitEntryFunction = """
        emit_entry() {
          entry=$1
          if [ ! -e "$entry" ] && [ ! -L "$entry" ]; then return; fi
          name=${entry##*/}
          if [ -L "$entry" ]; then kind=l
          elif [ -d "$entry" ]; then kind=d
          elif [ -f "$entry" ]; then kind=f
          else kind=o
          fi
          metadata=$(stat -c '%s %Y' "$entry" 2>/dev/null) || metadata=
          if [ -n "$metadata" ]; then
            size=${metadata%% *}
            modified=${metadata#* }
          else
            size=
            modified=
          fi
          printf '%s\0%s\0%s\0%s\0' "$name" "$kind" "$size" "$modified"
        }
        """;
}

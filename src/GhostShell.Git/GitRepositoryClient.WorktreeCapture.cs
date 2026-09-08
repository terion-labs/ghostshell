namespace GhostShell.Git;

public sealed partial class GitRepositoryClient
{
    // Paths are positional arguments, never shell source. Unapproved bytes stay
    // in an owner-private capture, never the shared Git object store. Only the
    // built-in normalization inputs are copied into shadow metadata: mutable
    // repository filters/includes cannot turn hashing into command execution.
    private const string WorktreeCaptureScript = """
        set -eu
        umask 077
        root=$1
        path=$2
        write=$3
        expected_raw=$4
        expected_mode=$5
        expected_normalization=$6
        # GIT_CONFIG conflicts with an explicit --local read. Global/system
        # sources remain disabled; selected local keys never follow includes.
        unset GIT_CONFIG
        run_git() { command git --no-replace-objects --literal-pathspecs -c core.fsmonitor=false -c core.hooksPath=/dev/null -C "$root" "$@"; }
        temporary=$7
        if [ -z "$temporary" ]; then temporary=$(mktemp -d "${TMPDIR:-/tmp}/ghostshell-git-capture.XXXXXX"); fi
        trap 'rm -rf -- "$temporary"' EXIT
        trap 'exit 1' HUP INT TERM
        mkdir "$temporary/worktree"
        : > "$temporary/normalization"
        record_attribute() {
            if [ -f "$2" ]; then
                identity=$(run_git hash-object --no-filters -- "$2")
            else
                identity=missing
            fi
            printf '%s\0%s\0' "$1" "$identity" >> "$temporary/normalization"
        }
        capture_attribute() {
            if [ -f "$1" ]; then cp -- "$1" "$2"; fi
            record_attribute "$3" "$2"
        }
        normalization=none
        if [ -L "$root/$path" ]; then
            readlink -n "$root/$path" > "$temporary/candidate"
            raw=$(run_git hash-object --no-filters -- "$temporary/candidate")
            mode=120000
            indexed=$raw
        elif [ -f "$root/$path" ]; then
            if [ "$write" = write ]; then
                cp -- "$root/$path" "$temporary/candidate"
                raw=$(run_git hash-object --no-filters -- "$temporary/candidate")
            else
                raw=$(run_git hash-object --no-filters -- "$path")
            fi
            mode=100644
            if [ -x "$root/$path" ]; then mode=100755; fi
            indexed=$raw
            for key in core.autocrlf core.eol core.safecrlf core.checkRoundtripEncoding; do
                if value=$(run_git config --local --no-includes --get "$key"); then
                    command git config --file "$temporary/config" --add "$key" "$value"
                    printf '%s\0%s\0' "$key" "$value" >> "$temporary/normalization"
                fi
            done
            if file_mode=$(run_git config --local --no-includes --type=bool --get core.filemode); then
                printf '%s\0%s\0' core.filemode "$file_mode" >> "$temporary/normalization"
                if [ "$file_mode" = false ]; then
                    mode=100644
                    index_entry=$(run_git ls-files --stage -- "$path")
                    case "$index_entry" in '100755 '*) mode=100755;; esac
                fi
            fi
            if global_attributes=$(run_git config --local --no-includes --path --get core.attributesFile); then :; else
                global_attributes="${XDG_CONFIG_HOME:-$HOME/.config}/git/attributes"
            fi
            case "$global_attributes" in ''|/*) ;; *) global_attributes="$root/$global_attributes";; esac
            capture_attribute "$global_attributes" "$temporary/global-attributes" global
            info_attributes=$(run_git rev-parse --git-path info/attributes)
            case "$info_attributes" in /*) ;; *) info_attributes="$root/$info_attributes";; esac
            capture_attribute "$info_attributes" "$temporary/info-attributes" info
            case "$path" in */*) directory=${path%/*};; *) directory=.;; esac
            while :; do
                mkdir -p "$temporary/worktree/$directory"
                attribute_path="$directory/.gitattributes"
                if [ "$directory" = . ]; then attribute_path=.gitattributes; fi
                attribute_copy="$temporary/worktree/$attribute_path"
                if [ -f "$root/$attribute_path" ] && [ ! -L "$root/$attribute_path" ]; then
                    cp -- "$root/$attribute_path" "$attribute_copy"
                elif [ ! -e "$root/$attribute_path" ] && [ ! -L "$root/$attribute_path" ]; then
                    index_entry=$(run_git ls-files --stage -- "$attribute_path")
                    case "$index_entry" in
                        '100644 '*|'100755 '*) run_git cat-file blob ":$attribute_path" > "$attribute_copy";;
                    esac
                fi
                record_attribute "worktree:$attribute_path" "$attribute_copy"
                [ "$directory" != . ] || break
                case "$directory" in */*) directory=${directory%/*};; *) directory=.;; esac
            done
            normalization=$(run_git hash-object --no-filters -- "$temporary/normalization")
        elif [ -d "$root/$path" ]; then
            prefix=$(run_git -C "$root/$path" rev-parse --show-prefix)
            [ -z "$prefix" ] || exit 1
            raw=$(run_git -C "$root/$path" rev-parse --verify HEAD)
            mode=160000
            indexed=$raw
        else
            exit 1
        fi
        if [ "$write" = write ]; then
            [ "$raw" = "$expected_raw" ] && [ "$mode" = "$expected_mode" ] \
                && [ "$normalization" = "$expected_normalization" ] || exit 1
            if [ "$mode" = 100644 ] || [ "$mode" = 100755 ]; then
                format=$(run_git rev-parse --show-object-format)
                command git init -q --bare --template= --object-format="$format" "$temporary/metadata"
                command git config --file "$temporary/metadata/config" core.bare false
                if [ -f "$temporary/config" ]; then cat "$temporary/config" >> "$temporary/metadata/config"; fi
                command git config --file "$temporary/metadata/config" core.attributesFile "$temporary/global-attributes"
                if [ -f "$temporary/info-attributes" ]; then
                    mkdir -p "$temporary/metadata/info"
                    cp "$temporary/info-attributes" "$temporary/metadata/info/attributes"
                fi
                shadow_git() { command git --no-replace-objects -C "$temporary/worktree" --git-dir="$temporary/metadata" --work-tree="$temporary/worktree" "$@"; }
                # Normalize the approved capture using only its bound snapshot.
                normalized=$(shadow_git hash-object -w --path="$path" --stdin < "$temporary/candidate")
                indexed=$(
                    (shadow_git cat-file blob "$normalized" || { : > "$temporary/read-failed"; exit 1; }) \
                        | run_git hash-object --no-filters -w --stdin
                )
                [ ! -e "$temporary/read-failed" ] && [ "$indexed" = "$normalized" ] || exit 1
            elif [ "$mode" = 120000 ]; then
                indexed=$(run_git hash-object --no-filters -w -- "$temporary/candidate")
                [ "$indexed" = "$raw" ] || exit 1
            fi
        fi
        printf '%s\0%s\0%s\0%s' "$mode" "$raw" "$indexed" "$normalization"
        """;

    private async ValueTask<WorktreeCapture?> CaptureWorktreeAsync(
        GitRepositoryHandle repository,
        string path,
        bool writeObjects,
        CancellationToken cancellationToken,
        WorktreeCapture? expected = null)
    {
        // The command runner can terminate a cancelled process with SIGKILL,
        // which cannot run a shell trap. Keep the write capture's exact path so
        // a separate bounded command can remove it after that process exits.
        var temporary = string.Empty;
        if (writeObjects)
        {
            var allocation = await ExecuteIsolatedCommandAsync(repository, SealedGitEnvironment,
                "/bin/sh", ["-c", "umask 077; mktemp -d /tmp/ghostshell-git-capture.XXXXXXXXXXXX"],
                ReadTimeout, GovernedStateOutputLimit, cancellationToken).ConfigureAwait(false);
            if (allocation is not GitResult<CommandOutput>.Success allocated)
            {
                return null;
            }
            temporary = allocated.Value.Text.TrimEnd('\r', '\n');
            const string prefix = "/tmp/ghostshell-git-capture.";
            if (!temporary.StartsWith(prefix, StringComparison.Ordinal)
                || temporary.Length != prefix.Length + 12
                || !temporary.Skip(prefix.Length).All(char.IsAsciiLetterOrDigit))
            {
                return null;
            }
        }

        try
        {
            var result = await ExecuteIsolatedCommandAsync(
                repository,
                SealedGitEnvironment,
                "/bin/sh",
                ["-c", WorktreeCaptureScript, "ghostshell-worktree-capture", repository.WorkingTreeRoot, path,
                    writeObjects ? "write" : "read", expected?.RawObjectId ?? string.Empty,
                    expected?.Mode ?? string.Empty, expected?.NormalizationIdentity ?? string.Empty, temporary],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
            if (result is not GitResult<CommandOutput>.Success success)
            {
                return null;
            }

            var fields = success.Value.Text.Split('\0');
            return fields.Length == 4
                && fields[0] is "100644" or "100755" or "120000" or "160000"
                && IsObjectId(fields[1]) && IsObjectId(fields[2])
                && (string.Equals(fields[3], "none", StringComparison.Ordinal) || IsObjectId(fields[3]))
                    ? new WorktreeCapture(fields[0], fields[1], fields[2], fields[3])
                    : null;
        }
        finally
        {
            if (temporary.Length > 0)
            {
                _ = await ExecuteIsolatedCommandAsync(repository, SealedGitEnvironment,
                    "/bin/sh", ["-c", "rm -rf -- \"$1\"", "ghostshell-capture-cleanup", temporary],
                    TimeSpan.FromSeconds(5), GovernedStateOutputLimit, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private sealed record WorktreeCapture(string Mode, string RawObjectId, string IndexObjectId, string NormalizationIdentity);

    private static WorktreeCapture? ObservedWorktreeCapture(GitRepositoryGuard guard, string path)
    {
        var fields = guard.WorktreeContentIdentity.Split('\0');
        for (var index = 0; index + 3 < fields.Length; index += 4)
        {
            if (string.Equals(fields[index], path, StringComparison.Ordinal))
            {
                return new WorktreeCapture(fields[index + 1], fields[index + 2], fields[index + 2], fields[index + 3]);
            }
        }
        return null;
    }
}

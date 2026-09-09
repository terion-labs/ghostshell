#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C

readonly SCRIPT_DIRECTORY="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly LEGAL_DIRECTORY="$SCRIPT_DIRECTORY/src/legal"
readonly POLICY_FILE="$LEGAL_DIRECTORY/runtime-license-map.tsv"
readonly SOURCES_FILE="$LEGAL_DIRECTORY/sources.tsv"
readonly LEGAL_REVIEW_FILE="$LEGAL_DIRECTORY/legal-review.tsv"
readonly FORMAT_VERSION=1

dependency_list=""
jar_directory=""
manifest=""
output=""
metadata=""

usage() {
    echo "Usage: $0 --dependency-list FILE --jar-directory DIR --manifest FILE --output FILE --metadata FILE"
}

while (($# > 0)); do
    case "$1" in
        --dependency-list)
            [[ $# -ge 2 ]] || { usage >&2; exit 64; }
            dependency_list="$2"
            shift 2
            ;;
        --jar-directory)
            [[ $# -ge 2 ]] || { usage >&2; exit 64; }
            jar_directory="$2"
            shift 2
            ;;
        --manifest)
            [[ $# -ge 2 ]] || { usage >&2; exit 64; }
            manifest="$2"
            shift 2
            ;;
        --output)
            [[ $# -ge 2 ]] || { usage >&2; exit 64; }
            output="$2"
            shift 2
            ;;
        --metadata)
            [[ $# -ge 2 ]] || { usage >&2; exit 64; }
            metadata="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 64
            ;;
    esac
done

[[ -n "$dependency_list" && -n "$jar_directory" && -n "$manifest" && -n "$output" && -n "$metadata" ]] || {
    usage >&2
    exit 64
}
[[ -f "$dependency_list" ]] || { echo "Dependency list does not exist: $dependency_list" >&2; exit 66; }
[[ -d "$jar_directory" ]] || { echo "Runtime JAR directory does not exist: $jar_directory" >&2; exit 66; }
[[ -f "$POLICY_FILE" ]] || { echo "License policy does not exist: $POLICY_FILE" >&2; exit 66; }
[[ -f "$SOURCES_FILE" ]] || { echo "Legal source manifest does not exist: $SOURCES_FILE" >&2; exit 66; }
[[ -f "$LEGAL_REVIEW_FILE" ]] || { echo "Legal review manifest does not exist: $LEGAL_REVIEW_FILE" >&2; exit 66; }

for command_name in awk cmp cut diff find grep mktemp od sed sort tr uniq wc; do
    command -v "$command_name" >/dev/null || {
        echo "$command_name is required to generate third-party notices." >&2
        exit 69
    }
done

jar_command="$(command -v jar || true)"
if [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/jar" ]]; then
    jar_command="$JAVA_HOME/bin/jar"
fi
[[ -n "$jar_command" && -x "$jar_command" ]] || {
    echo "A JDK jar command is required to generate third-party notices." >&2
    exit 69
}
"$jar_command" --version >/dev/null 2>&1 || {
    echo "The configured JDK jar command is not executable: $jar_command" >&2
    exit 69
}

sha256_file() {
    if command -v sha256sum >/dev/null; then
        sha256sum "$1" | awk '{print $1}'
    elif command -v shasum >/dev/null; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        echo "sha256sum or shasum is required to generate third-party notices." >&2
        exit 69
    fi
}

absolute_path() {
    local path="$1"
    local directory base
    directory="$(cd "$(dirname "$path")" && pwd)"
    base="$(basename "$path")"
    echo "$directory/$base"
}

work_directory="$(mktemp -d "${TMPDIR:-/tmp}/asura-sql-legal.XXXXXX")"
cleanup() {
    rm -rf -- "$work_directory"
}
trap cleanup EXIT

mkdir -p "$(dirname "$manifest")" "$(dirname "$output")" "$(dirname "$metadata")"
normalized_unsorted="$work_directory/runtime-dependencies.unsorted.txt"
normalized_manifest="$work_directory/runtime-dependencies.txt"

awk '
    /^[[:space:]]+[A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+:jar:/ {
        count = split($1, field, ":")
        if (count == 5) {
            print field[1] ":" field[2] ":" field[3] ":" field[4] ":" field[5]
        } else if (count == 6) {
            print field[1] ":" field[2] ":" field[3] ":" field[4] ":" field[5] ":" field[6]
        } else {
            invalid = 1
        }
    }
    END { if (invalid) exit 2 }
' "$dependency_list" > "$normalized_unsorted" || {
    echo "Maven emitted an unsupported runtime dependency coordinate." >&2
    exit 65
}

[[ -s "$normalized_unsorted" ]] || {
    echo "Maven did not resolve any runtime dependencies." >&2
    exit 65
}
if [[ -n "$(sort "$normalized_unsorted" | uniq -d)" ]]; then
    echo "Maven emitted duplicate runtime dependency coordinates." >&2
    exit 65
fi
sort "$normalized_unsorted" > "$normalized_manifest"

policy_entries="$work_directory/policy-entries.tsv"
source_entries="$work_directory/source-entries.tsv"
legal_review_entries="$work_directory/legal-review-entries.tsv"
grep -v '^[[:space:]]*#' "$POLICY_FILE" | grep -v '^[[:space:]]*$' > "$policy_entries"
grep -v '^[[:space:]]*#' "$SOURCES_FILE" | grep -v '^[[:space:]]*$' > "$source_entries"
grep -v '^[[:space:]]*#' "$LEGAL_REVIEW_FILE" | grep -v '^[[:space:]]*$' > "$legal_review_entries"

if ! cmp -s "$policy_entries" <(sort "$policy_entries"); then
    echo "Runtime license policy must be sorted by Maven coordinate." >&2
    exit 65
fi
if ! cmp -s "$source_entries" <(sort "$source_entries"); then
    echo "Legal source manifest must be sorted by relative path." >&2
    exit 65
fi
if ! cmp -s "$legal_review_entries" <(sort "$legal_review_entries"); then
    echo "Legal review manifest must be sorted by Maven coordinate." >&2
    exit 65
fi
if [[ -n "$(cut -f1 "$policy_entries" | uniq -d)" ]]; then
    echo "Runtime license policy contains duplicate coordinates." >&2
    exit 65
fi
if [[ -n "$(cut -f1 "$source_entries" | uniq -d)" ]]; then
    echo "Legal source manifest contains duplicate paths." >&2
    exit 65
fi
if [[ -n "$(cut -f1 "$legal_review_entries" | uniq -d)" ]]; then
    echo "Legal review manifest contains duplicate coordinates." >&2
    exit 65
fi

resolved_keys="$work_directory/resolved-keys.txt"
awk -F: '
    NF == 5 { print $1 ":" $2 ":" $4; next }
    NF == 6 { print $1 ":" $2 ":" $5; next }
    { exit 2 }
' "$normalized_manifest" > "$resolved_keys"
policy_keys="$work_directory/policy-keys.txt"
cut -f1 "$policy_entries" > "$policy_keys"
if ! cmp -s "$resolved_keys" "$policy_keys"; then
    echo "Runtime dependency graph and license policy differ:" >&2
    diff -u "$policy_keys" "$resolved_keys" >&2 || true
    exit 65
fi
while IFS=$'\t' read -r review_coordinate review_reason; do
    [[ -n "$review_coordinate" && -n "$review_reason" ]] || {
        echo "Malformed legal review row: $review_coordinate" >&2
        exit 65
    }
    grep -Fxq "$review_coordinate" "$policy_keys" || {
        echo "Legal review references an unresolved runtime coordinate: $review_coordinate" >&2
        exit 65
    }
done < "$legal_review_entries"

declared_legal_files="$work_directory/declared-legal-files.txt"
actual_legal_files="$work_directory/actual-legal-files.txt"
cut -f1 "$source_entries" > "$declared_legal_files"
find "$LEGAL_DIRECTORY/licenses" -type f -print \
    | sed "s#^$LEGAL_DIRECTORY/##" \
    | sort > "$actual_legal_files"
if ! cmp -s "$declared_legal_files" "$actual_legal_files"; then
    echo "Vendored legal files and their source manifest differ:" >&2
    diff -u "$declared_legal_files" "$actual_legal_files" >&2 || true
    exit 65
fi

while IFS=$'\t' read -r relative_path expected_sha256 upstream_source; do
    [[ -n "$relative_path" && -n "$expected_sha256" && -n "$upstream_source" ]] || {
        echo "Malformed legal source manifest row: $relative_path" >&2
        exit 65
    }
    case "$relative_path" in
        /*|*..*)
            echo "Unsafe legal source path: $relative_path" >&2
            exit 65
            ;;
    esac
    legal_file="$LEGAL_DIRECTORY/$relative_path"
    [[ -s "$legal_file" ]] || { echo "Vendored legal file is missing or empty: $relative_path" >&2; exit 66; }
    actual_sha256="$(sha256_file "$legal_file")"
    [[ "$actual_sha256" == "$expected_sha256" ]] || {
        echo "Vendored legal file hash mismatch: $relative_path" >&2
        exit 65
    }
done < "$source_entries"

expected_jars="$work_directory/expected-jars.txt"
while IFS= read -r dependency; do
    field_count="$(awk -F: '{print NF}' <<< "$dependency")"
    if [[ "$field_count" == 5 ]]; then
        IFS=: read -r group artifact packaging version scope <<< "$dependency"
        echo "$artifact-$version.jar"
    else
        IFS=: read -r group artifact packaging classifier version scope <<< "$dependency"
        echo "$artifact-$version-$classifier.jar"
    fi
done < "$normalized_manifest" | sort > "$expected_jars"
actual_jars="$work_directory/actual-jars.txt"
find "$jar_directory" -maxdepth 1 -type f -name '*.jar' -print \
    | sed 's#^.*/##' \
    | sort > "$actual_jars"
if ! cmp -s "$expected_jars" "$actual_jars"; then
    echo "Copied runtime JARs and runtime dependency manifest differ:" >&2
    diff -u "$expected_jars" "$actual_jars" >&2 || true
    exit 65
fi

documents_directory="$work_directory/documents"
extraction_directory="$work_directory/extracted"
mkdir -p "$documents_directory" "$extraction_directory"
dependency_index="$work_directory/dependency-index.txt"
: > "$dependency_index"
registered_hash=""

register_document() {
    local source_file="$1"
    local reference="$2"
    local document_name="$3"
    local document_sha256 destination
    [[ -s "$source_file" ]] || { echo "Legal document is missing or empty: $reference" >&2; exit 65; }
    document_sha256="$(sha256_file "$source_file")"
    destination="$documents_directory/$document_sha256.payload"
    if [[ -f "$destination" ]]; then
        cmp -s "$source_file" "$destination" || {
            echo "SHA-256 collision while indexing legal documents." >&2
            exit 70
        }
    else
        cp "$source_file" "$destination"
    fi
    printf '%s\t%s\n' "$reference" "$document_name" >> "$documents_directory/$document_sha256.refs"
    registered_hash="$document_sha256"
}

register_vendored_list() {
    local coordinate="$1"
    local list="$2"
    local role="$3"
    local relative_path source_row
    [[ "$list" != "-" ]] || return 0
    old_ifs="$IFS"
    IFS=,
    for relative_path in $list; do
        IFS="$old_ifs"
        source_row="$(awk -F '\t' -v path="$relative_path" '$1 == path { print; matches++ } END { if (matches != 1) exit 2 }' "$source_entries")" || {
            echo "License policy references an undeclared legal file: $relative_path" >&2
            exit 65
        }
        register_document "$LEGAL_DIRECTORY/$relative_path" \
            "$coordinate — vendored $role from $(printf '%s' "$source_row" | cut -f3)" \
            "$relative_path"
        printf -- '- `%s` — `%s` (%s)\n' "$registered_hash" "$relative_path" "$role" >> "$dependency_index"
        IFS=,
    done
    IFS="$old_ifs"
}

dependency_count=0
while IFS= read -r dependency; do
    dependency_count=$((dependency_count + 1))
    field_count="$(awk -F: '{print NF}' <<< "$dependency")"
    classifier=""
    if [[ "$field_count" == 5 ]]; then
        IFS=: read -r group artifact packaging version scope <<< "$dependency"
        jar_name="$artifact-$version.jar"
    else
        IFS=: read -r group artifact packaging classifier version scope <<< "$dependency"
        jar_name="$artifact-$version-$classifier.jar"
    fi
    coordinate="$group:$artifact:$version"
    jar_path="$(absolute_path "$jar_directory/$jar_name")"
    policy_row="$(awk -F '\t' -v coordinate="$coordinate" '$1 == coordinate { print; matches++ } END { if (matches != 1) exit 2 }' "$policy_entries")" || {
        echo "License policy does not contain exactly one row for $coordinate." >&2
        exit 65
    }
    IFS=$'\t' read -r policy_coordinate license_expression fallback_licenses supplemental_notices <<< "$policy_row"
    [[ -n "$license_expression" && -n "$fallback_licenses" && -n "$supplemental_notices" ]] || {
        echo "Malformed license policy row for $coordinate." >&2
        exit 65
    }

    jar_sha256="$(sha256_file "$jar_path")"
    {
        printf '\n### `%s`\n\n' "$coordinate"
        printf -- '- Runtime coordinate: `%s`\n' "$dependency"
        printf -- '- JAR: `%s`\n' "$jar_name"
        printf -- '- JAR SHA-256: `%s`\n' "$jar_sha256"
        printf -- '- Distribution license: `%s`\n' "$license_expression"
        legal_review_reason="$(awk -F '\t' -v coordinate="$coordinate" '$1 == coordinate { print $2; matches++ } END { if (matches > 1) exit 2 }' "$legal_review_entries")" || {
            echo "Legal review manifest contains duplicate rows for $coordinate." >&2
            exit 65
        }
        if [[ -n "$legal_review_reason" ]]; then
            printf -- '- Legal review required: **yes** — %s\n' "$legal_review_reason"
        else
            printf -- '- Legal review required: no recorded exception\n'
        fi
        printf -- '- Legal documents:\n'
    } >> "$dependency_index"

    resources_file="$work_directory/resources-$dependency_count.txt"
    all_resources_file="$work_directory/all-resources-$dependency_count.txt"
    "$jar_command" tf "$jar_path" > "$all_resources_file" || {
        echo "Cannot read runtime JAR: $jar_name" >&2
        exit 65
    }
    grep -Ei '^META-INF/[^/]*(LICENSE|NOTICE|COPYING|DEPENDENCIES)[^/]*$' "$all_resources_file" \
        | sort -u > "$resources_file" || true
    embedded_license=false
    extract_root="$extraction_directory/$dependency_count"
    mkdir -p "$extract_root"
    while IFS= read -r resource; do
        [[ -n "$resource" ]] || continue
        case "$(basename "$resource" | tr '[:lower:]' '[:upper:]')" in
            LICENSE*|COPYING*) embedded_license=true ;;
        esac
        (
            cd "$extract_root"
            "$jar_command" xf "$jar_path" "$resource"
        )
        register_document "$extract_root/$resource" \
            "$coordinate — embedded $resource in $jar_name" \
            "$resource"
        printf -- '- `%s` — `%s` (embedded in JAR)\n' "$registered_hash" "$resource" >> "$dependency_index"
    done < "$resources_file"

    if [[ "$embedded_license" == true ]]; then
        [[ "$fallback_licenses" == "-" ]] || {
            echo "Redundant fallback license policy for $coordinate; its JAR already contains a license." >&2
            exit 65
        }
    else
        [[ "$fallback_licenses" != "-" ]] || {
            echo "No embedded or vendored license text for $coordinate." >&2
            exit 65
        }
        register_vendored_list "$coordinate" "$fallback_licenses" "license"
    fi
    register_vendored_list "$coordinate" "$supplemental_notices" "supplemental legal document"
done < "$normalized_manifest"

document_count="$(find "$documents_directory" -type f -name '*.payload' | wc -l | tr -d ' ')"
legal_review_required_count="$(wc -l < "$legal_review_entries" | tr -d ' ')"
[[ "$document_count" -gt 0 ]] || { echo "No legal documents were collected." >&2; exit 65; }

generated_output="$work_directory/THIRD-PARTY-NOTICES.md"
{
    echo '# Asura SQL language worker — third-party notices'
    echo
    echo "Legal closure format: $FORMAT_VERSION"
    echo
    echo "Runtime dependency count: $dependency_count"
    echo
    echo "Unique legal document count: $document_count"
    echo
    echo "Dependencies requiring legal review: $legal_review_required_count"
    echo
    echo 'This file is generated deterministically from the pinned Maven runtime graph. Every runtime JAR is SHA-256 indexed below. LICENSE, NOTICE, COPYING, and DEPENDENCIES resources embedded in those JARs are reproduced, and pinned upstream legal texts are supplied when a JAR omits its license. Document hashes cover the original payload bytes; the generator may add one separator newline after a payload solely to delimit the next marker.'
    echo
    echo '## Runtime dependency index'
    cat "$dependency_index"
    echo
    echo '## Legal document registry'
    while IFS= read -r payload; do
        document_sha256="$(basename "$payload" .payload)"
        echo
        printf '### SHA-256 `%s`\n\n' "$document_sha256"
        echo 'Referenced by:'
        echo
        sort -u "$documents_directory/$document_sha256.refs" | while IFS=$'\t' read -r reference document_name; do
            printf -- '- `%s`: %s\n' "$document_name" "$reference"
        done
        echo
        printf '%s\n' "----- BEGIN LEGAL DOCUMENT $document_sha256 -----"
        cat "$payload"
        printf '\n%s\n' "----- END LEGAL DOCUMENT $document_sha256 -----"
    done < <(find "$documents_directory" -type f -name '*.payload' | sort)
} > "$generated_output"

mv "$normalized_manifest" "$manifest"
mv "$generated_output" "$output"
runtime_dependencies_sha256="$(sha256_file "$manifest")"
third_party_notices_sha256="$(sha256_file "$output")"
generated_metadata="$work_directory/legal-closure.properties"
{
    echo "formatVersion=$FORMAT_VERSION"
    echo "legalDocumentCount=$document_count"
    echo "legalReviewRequiredCount=$legal_review_required_count"
    echo "runtimeDependencyCount=$dependency_count"
    echo "runtimeDependenciesSha256=$runtime_dependencies_sha256"
    echo "thirdPartyNoticesSha256=$third_party_notices_sha256"
} > "$generated_metadata"
mv "$generated_metadata" "$metadata"

echo "Validated $dependency_count runtime dependencies and $document_count unique legal documents."

#!/usr/bin/env bash
set -euo pipefail

readonly SCRIPT_DIRECTORY="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "$SCRIPT_DIRECTORY/.." && pwd)"
readonly WORKER_DIRECTORY="$REPOSITORY_ROOT/native/sql-language-worker"
readonly LEGAL_CLOSURE_GENERATOR="$WORKER_DIRECTORY/generate-third-party-notices.sh"
readonly MAVEN_CONTENT_LOCK="$WORKER_DIRECTORY/maven-content-lock.json"
readonly MAVEN_REPOSITORY_PREPARER="$SCRIPT_DIRECTORY/prepare-locked-maven-repository.py"
readonly ARTIFACTS_DIRECTORY="$REPOSITORY_ROOT/native/artifacts"
readonly MAVEN_IMAGE_VERSION="3.9.11-eclipse-temurin-21"
readonly MAVEN_IMAGE_LINUX_AMD64="maven:$MAVEN_IMAGE_VERSION@sha256:463a1849665463254b2dd56e3a5b316f1596bc93d0571065c06ea05bb48ab8f4"
readonly MAVEN_IMAGE_LINUX_ARM64="maven:$MAVEN_IMAGE_VERSION@sha256:d1d89ba5f782ba5dd52272e7da5aed592abb192dff6435523b852ffab3ee8484"
readonly NATIVE_IMAGE_VERSION="25.0.4"
readonly NATIVE_IMAGE_LINUX_AMD64="container-registry.oracle.com/graalvm/native-image:$NATIVE_IMAGE_VERSION@sha256:1e1e083cb7cbf4d8ca0b9809ec0028a26e8022743415531c0c0a75842e1b5b61"
readonly NATIVE_IMAGE_LINUX_ARM64="container-registry.oracle.com/graalvm/native-image:$NATIVE_IMAGE_VERSION@sha256:320ef30a226f66bf0ee34a80aaa1d40bc42e0c96277a634e0b30b32907da957b"
readonly EXPECTED_NATIVE_IMAGE_VERSION="25.0.4"
readonly MAXIMUM_RUNTIME_HEAP="256m"
readonly MINIMUM_MACOS_VERSION="13.0"
readonly MAXIMUM_GLIBC_VERSION="2.34"

rid=""
mode="auto"
skip_tests=false
native_image_command=""
maven_image=""
native_image=""

if [[ -n "${GRAALVM_HOME:-}" && -z "${JAVA_HOME:-}" ]]; then
    export JAVA_HOME="$GRAALVM_HOME"
fi

resolve_native_image() {
    if [[ -n "${GRAALVM_HOME:-}" && -x "$GRAALVM_HOME/bin/native-image" ]]; then
        echo "$GRAALVM_HOME/bin/native-image"
    elif [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/native-image" ]]; then
        echo "$JAVA_HOME/bin/native-image"
    elif command -v native-image >/dev/null; then
        command -v native-image
    fi
    return 0
}

usage() {
    echo "Usage: $0 [--rid RID] [--docker|--local] [--skip-tests]"
    echo ""
    echo "RIDs: linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64"
}

while (($# > 0)); do
    case "$1" in
        --rid)
            [[ $# -ge 2 ]] || { usage >&2; exit 64; }
            rid="$2"
            shift 2
            ;;
        --docker)
            mode="docker"
            shift
            ;;
        --local)
            mode="local"
            shift
            ;;
        --skip-tests)
            skip_tests=true
            shift
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

host_rid() {
    local operating_system architecture
    operating_system="$(uname -s)"
    architecture="$(uname -m)"
    case "$operating_system:$architecture" in
        Linux:x86_64) echo "linux-x64" ;;
        Linux:aarch64|Linux:arm64) echo "linux-arm64" ;;
        Darwin:x86_64) echo "osx-x64" ;;
        Darwin:arm64) echo "osx-arm64" ;;
        MINGW*:x86_64|MSYS*:x86_64|CYGWIN*:x86_64) echo "win-x64" ;;
        *)
            echo "Cannot infer a supported RID from $operating_system/$architecture." >&2
            exit 65
            ;;
    esac
}

if [[ -z "$rid" ]]; then
    rid="$(host_rid)"
fi

binary_name="asura-sql-language"
docker_platform=""
abi=""
case "$rid" in
    linux-x64)
        docker_platform="linux/amd64"
        maven_image="$MAVEN_IMAGE_LINUX_AMD64"
        native_image="$NATIVE_IMAGE_LINUX_AMD64"
        abi="linux-gnu-x64"
        ;;
    linux-arm64)
        docker_platform="linux/arm64"
        maven_image="$MAVEN_IMAGE_LINUX_ARM64"
        native_image="$NATIVE_IMAGE_LINUX_ARM64"
        abi="linux-gnu-arm64"
        ;;
    osx-x64) abi="darwin-x64" ;;
    osx-arm64) abi="darwin-arm64" ;;
    win-x64)
        binary_name="asura-sql-language.exe"
        abi="windows-x64"
        ;;
    win-arm64)
        echo "Windows ARM64 is not supported by GraalVM Native Image 25.0.4." >&2
        exit 65
        ;;
    *)
        echo "Unsupported RID: $rid" >&2
        exit 65
        ;;
esac

if [[ "$mode" == "auto" ]]; then
    native_image_command="$(resolve_native_image)"
    if [[ "$rid" == "$(host_rid)" ]] && command -v mvn >/dev/null && [[ -n "$native_image_command" ]]; then
        mode="local"
    elif [[ -n "$docker_platform" ]] && command -v docker >/dev/null; then
        mode="docker"
    else
        echo "No local GraalVM toolchain is available, and Docker can only build Linux RIDs." >&2
        exit 69
    fi
fi

if [[ "$mode" == "local" ]]; then
    native_image_command="$(resolve_native_image)"
    [[ "$rid" == "$(host_rid)" ]] || {
        echo "Local Native Image cannot cross-compile $rid from $(host_rid)." >&2
        exit 69
    }
    command -v mvn >/dev/null || { echo "mvn is required for --local." >&2; exit 69; }
    [[ -n "$native_image_command" ]] || {
        echo "native-image is required for --local (PATH, GRAALVM_HOME, or JAVA_HOME)." >&2
        exit 69
    }
fi

if [[ "$mode" == "docker" ]]; then
    [[ -n "$docker_platform" ]] || {
        echo "Docker builds are supported only for Linux RIDs." >&2
        exit 69
    }
    command -v docker >/dev/null || { echo "docker is required for --docker." >&2; exit 69; }
    [[ "$maven_image" =~ @sha256:[0-9a-f]{64}$ ]] || {
        echo "The Maven builder must be pinned to a platform manifest digest." >&2
        exit 65
    }
    [[ "$native_image" =~ @sha256:[0-9a-f]{64}$ ]] || {
        echo "The Native Image builder must be pinned to a platform manifest digest." >&2
        exit 65
    }
fi

verify_native_image_version() {
    local version_line="$1"
    if [[ ! "$version_line" =~ ^native-image[[:space:]]+$EXPECTED_NATIVE_IMAGE_VERSION([[:space:]]|$) ]]; then
        echo "Native Image $EXPECTED_NATIVE_IMAGE_VERSION is required; found: $version_line" >&2
        exit 69
    fi
}

sha256_file() {
    if command -v sha256sum >/dev/null; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

legal_property() {
    local key="$1"
    local properties_file="$worker_build_directory/legal-closure.properties"
    awk -F= -v key="$key" '
        $1 == key { value = $2; matches++ }
        END { if (matches != 1 || value == "") exit 2; print value }
    ' "$properties_file"
}

version_is_at_most() {
    local actual="$1"
    local maximum="$2"
    local actual_major actual_minor maximum_major maximum_minor
    IFS=. read -r actual_major actual_minor <<< "$actual"
    IFS=. read -r maximum_major maximum_minor <<< "$maximum"
    ((actual_major < maximum_major
        || (actual_major == maximum_major && actual_minor <= maximum_minor)))
}

read_macos_minimum_version() {
    otool -l "$1" | awk '
        $1 == "cmd" && $2 == "LC_BUILD_VERSION" { in_build_version = 1; next }
        in_build_version && $1 == "minos" { print $2; exit }
        $1 == "cmd" && $2 == "LC_VERSION_MIN_MACOSX" { in_version_min = 1; next }
        in_version_min && $1 == "version" { print $2; exit }
    '
}

if [[ "$mode" == "local" ]]; then
    native_image_output="$("$native_image_command" --version 2>&1)"
else
    native_image_output="$(docker run --rm --network none --platform "$docker_platform" \
        "$native_image" --version 2>&1)"
fi
native_image_version="$(printf '%s\n' "$native_image_output" \
    | awk '/^native-image / { print; exit }')"
verify_native_image_version "$native_image_version"

artifact_directory="$ARTIFACTS_DIRECTORY/$rid"
artifact_path="$artifact_directory/$binary_name"
worker_build_directory="$WORKER_DIRECTORY/target"
if [[ -n "${ASURA_BUILD_ARTIFACTS_ROOT:-}" ]]; then
    worker_build_directory="$ASURA_BUILD_ARTIFACTS_ROOT/sql-language-worker/$rid"
fi
mkdir -p "$ARTIFACTS_DIRECTORY"
staging_directory="$(mktemp -d "$ARTIFACTS_DIRECTORY/.sql-language-$rid.XXXXXX")"
staged_artifact_path="$staging_directory/$binary_name"
locked_maven_repository="$staging_directory/maven-repository"
cleanup_staging() {
    if [[ -d "$locked_maven_repository" ]]; then
        # The verified repository is sealed before Maven starts. Restore owner
        # write permission only so the private staging tree can be removed.
        "$MAVEN_REPOSITORY_PREPARER" --unseal "$locked_maven_repository"
    fi
    rm -rf -- "$staging_directory"
}
trap cleanup_staging EXIT

command -v python3 >/dev/null || { echo "python3 is required to verify the Maven content lock." >&2; exit 69; }
maven_content_lock_sha256="$(sha256_file "$MAVEN_CONTENT_LOCK")"
"$MAVEN_REPOSITORY_PREPARER" "$MAVEN_CONTENT_LOCK" "$locked_maven_repository"
[[ "$(sha256_file "$MAVEN_CONTENT_LOCK")" == "$maven_content_lock_sha256" ]] || {
    echo "The Maven content lock changed while its repository was prepared." >&2
    exit 65
}

maven_goal="verify"
if [[ "$skip_tests" == true ]]; then
    maven_goal="package"
fi
native_image_common_arguments=(
    --no-fallback
    -O2
    "-R:MaxHeapSize=$MAXIMUM_RUNTIME_HEAP"
    -H:+ReportExceptionStackTraces
)

if [[ "$mode" == "local" ]]; then
    maven_local_arguments=(
        "-Dmaven.repo.local=$locked_maven_repository"
        "-Dasura.build.directory=$worker_build_directory"
    )
    (
        cd "$WORKER_DIRECTORY"
        if [[ "$skip_tests" == true ]]; then
            mvn -B --offline "${maven_local_arguments[@]}" clean package -DskipTests
        else
            mvn -B --offline "${maven_local_arguments[@]}" clean verify
        fi
        mvn -B --offline "${maven_local_arguments[@]}" \
            org.apache.maven.plugins:maven-dependency-plugin:3.8.1:list \
            -DincludeScope=runtime \
            "-DoutputFile=$worker_build_directory/runtime-dependencies.raw.txt" \
            -DappendOutput=false \
            org.apache.maven.plugins:maven-dependency-plugin:3.8.1:copy-dependencies \
            -DincludeScope=runtime \
            "-DoutputDirectory=$worker_build_directory/runtime-jars" \
            -DoverWriteReleases=true \
            -DoverWriteSnapshots=true \
            -DoverWriteIfNewer=true
        "$LEGAL_CLOSURE_GENERATOR" \
            --dependency-list "$worker_build_directory/runtime-dependencies.raw.txt" \
            --jar-directory "$worker_build_directory/runtime-jars" \
            --manifest "$worker_build_directory/runtime-dependencies.txt" \
            --output "$worker_build_directory/THIRD-PARTY-NOTICES.md" \
            --metadata "$worker_build_directory/legal-closure.properties"
        mvn -B --offline "${maven_local_arguments[@]}" \
            -Dtest=LegalClosurePolicyTest \
            "-Dlegal.runtimeDependencies=$worker_build_directory/runtime-dependencies.txt" \
            "-Dlegal.thirdPartyNotices=$worker_build_directory/THIRD-PARTY-NOTICES.md" \
            "-Dlegal.metadata=$worker_build_directory/legal-closure.properties" \
            test
    )
    native_image_arguments=(
        "${native_image_common_arguments[@]}"
        -jar "$worker_build_directory/asura-sql-language-worker.jar"
        -o "$staged_artifact_path"
    )
    if [[ "$rid" == osx-* ]]; then
        native_image_arguments=(
            "${native_image_common_arguments[@]}"
            "--native-compiler-options=-mmacosx-version-min=$MINIMUM_MACOS_VERSION"
            "-H:NativeLinkerOption=-mmacosx-version-min=$MINIMUM_MACOS_VERSION"
            -jar "$worker_build_directory/asura-sql-language-worker.jar"
            -o "$staged_artifact_path"
        )
        MACOSX_DEPLOYMENT_TARGET="$MINIMUM_MACOS_VERSION" \
            "$native_image_command" "${native_image_arguments[@]}"
    else
        "$native_image_command" "${native_image_arguments[@]}"
    fi
else
    maven_arguments=(mvn -B --offline -Dmaven.repo.local=/locked-m2 clean "$maven_goal")
    if [[ "$skip_tests" == true ]]; then
        maven_arguments+=(-DskipTests)
    fi
    docker run --rm \
        --network none \
        --platform "$docker_platform" \
        -v "$locked_maven_repository:/locked-m2:ro" \
        -v "$WORKER_DIRECTORY:/workspace" \
        -w /workspace \
        "$maven_image" \
        "${maven_arguments[@]}"
    docker run --rm \
        --network none \
        --platform "$docker_platform" \
        -v "$locked_maven_repository:/locked-m2:ro" \
        -v "$WORKER_DIRECTORY:/workspace" \
        -w /workspace \
        "$maven_image" \
        mvn -B --offline -Dmaven.repo.local=/locked-m2 \
            org.apache.maven.plugins:maven-dependency-plugin:3.8.1:list \
            -DincludeScope=runtime \
            -DoutputFile=target/runtime-dependencies.raw.txt \
            -DappendOutput=false \
            org.apache.maven.plugins:maven-dependency-plugin:3.8.1:copy-dependencies \
            -DincludeScope=runtime \
            -DoutputDirectory=target/runtime-jars \
            -DoverWriteReleases=true \
            -DoverWriteSnapshots=true \
            -DoverWriteIfNewer=true
    docker run --rm \
        --network none \
        --platform "$docker_platform" \
        -v "$WORKER_DIRECTORY:/workspace" \
        -w /workspace \
        "$maven_image" \
        bash ./generate-third-party-notices.sh \
            --dependency-list target/runtime-dependencies.raw.txt \
            --jar-directory target/runtime-jars \
            --manifest target/runtime-dependencies.txt \
            --output target/THIRD-PARTY-NOTICES.md \
            --metadata target/legal-closure.properties
    docker run --rm \
        --network none \
        --platform "$docker_platform" \
        -v "$locked_maven_repository:/locked-m2:ro" \
        -v "$WORKER_DIRECTORY:/workspace" \
        -w /workspace \
        "$maven_image" \
        mvn -B --offline -Dmaven.repo.local=/locked-m2 \
            -Dtest=LegalClosurePolicyTest \
            -Dlegal.runtimeDependencies=/workspace/target/runtime-dependencies.txt \
            -Dlegal.thirdPartyNotices=/workspace/target/THIRD-PARTY-NOTICES.md \
            -Dlegal.metadata=/workspace/target/legal-closure.properties \
            test
    docker run --rm \
        --network none \
        --platform "$docker_platform" \
        --entrypoint native-image \
        -v "$WORKER_DIRECTORY:/workspace" \
        -v "$staging_directory:/out" \
        -w /workspace \
        "$native_image" \
        "${native_image_common_arguments[@]}" \
        -jar target/asura-sql-language-worker.jar \
        -o "/out/$binary_name"
fi

if [[ "$rid" != win-* ]]; then
    chmod +x "$staged_artifact_path"
fi

minimum_os_version=""
minimum_glibc_version=""
if [[ "$rid" == osx-* ]]; then
    command -v otool >/dev/null || { echo "otool is required for macOS ABI validation." >&2; exit 69; }
    minimum_os_version="$(read_macos_minimum_version "$staged_artifact_path")"
    [[ "$minimum_os_version" == "$MINIMUM_MACOS_VERSION" ]] || {
        echo "macOS artifact minimum version is $minimum_os_version; expected $MINIMUM_MACOS_VERSION." >&2
        exit 70
    }
elif [[ "$rid" == linux-* ]]; then
    minimum_glibc_version="$(docker run --rm \
        --network none \
        --platform "$docker_platform" \
        --entrypoint /bin/bash \
        -v "$staging_directory:/out:ro" \
        "$native_image" \
        -lc "readelf --version-info '/out/$binary_name' \
            | grep -o 'GLIBC_[0-9.]*' \
            | sed 's/GLIBC_//' \
            | sort -Vu \
            | tail -1")"
    [[ -n "$minimum_glibc_version" ]] || {
        echo "Could not determine the Linux artifact's glibc floor." >&2
        exit 70
    }
    version_is_at_most "$minimum_glibc_version" "$MAXIMUM_GLIBC_VERSION" || {
        echo "Linux artifact requires glibc $minimum_glibc_version; maximum supported is $MAXIMUM_GLIBC_VERSION." >&2
        exit 70
    }
fi

if [[ "$mode" == "local" ]]; then
    (
        cd "$WORKER_DIRECTORY"
        mvn -B --offline "${maven_local_arguments[@]}" \
            -Dtest=NativeExecutableSmokeTest \
            "-Dnative.executable=$staged_artifact_path" \
            test
    )
else
    docker run --rm \
        --network none \
        --platform "$docker_platform" \
        -v "$locked_maven_repository:/locked-m2:ro" \
        -v "$WORKER_DIRECTORY:/workspace" \
        -v "$staging_directory:/out:ro" \
        -w /workspace \
        "$maven_image" \
        mvn -B --offline -Dmaven.repo.local=/locked-m2 \
            -Dtest=NativeExecutableSmokeTest \
            "-Dnative.executable=/out/$binary_name" \
            test
fi

cp "$worker_build_directory/THIRD-PARTY-NOTICES.md" "$staging_directory/THIRD-PARTY-NOTICES.md"
cp "$worker_build_directory/runtime-dependencies.txt" "$staging_directory/runtime-dependencies.txt"

artifact_sha256="$(sha256_file "$staged_artifact_path")"
legal_closure_format_version="$(legal_property formatVersion)"
legal_document_count="$(legal_property legalDocumentCount)"
legal_review_required_count="$(legal_property legalReviewRequiredCount)"
runtime_dependency_count="$(legal_property runtimeDependencyCount)"
runtime_dependencies_sha256="$(legal_property runtimeDependenciesSha256)"
third_party_notices_sha256="$(legal_property thirdPartyNoticesSha256)"
[[ "$legal_closure_format_version" =~ ^[0-9]+$ \
    && "$legal_document_count" =~ ^[0-9]+$ \
    && "$legal_review_required_count" =~ ^[0-9]+$ \
    && "$runtime_dependency_count" =~ ^[0-9]+$ ]] || {
    echo "Legal closure metadata contains a non-numeric count or format version." >&2
    exit 65
}
[[ "$(sha256_file "$staging_directory/runtime-dependencies.txt")" == "$runtime_dependencies_sha256" ]] || {
    echo "Published runtime dependency manifest differs from the validated legal closure." >&2
    exit 65
}
[[ "$(sha256_file "$staging_directory/THIRD-PARTY-NOTICES.md")" == "$third_party_notices_sha256" ]] || {
    echo "Published third-party notices differ from the validated legal closure." >&2
    exit 65
}

escaped_builder="${native_image_version//\\/\\\\}"
escaped_builder="${escaped_builder//\"/\\\"}"
maven_builder_source="$maven_image"
native_image_builder_source="$native_image"
builder_source="$native_image_builder_source"
if [[ "$mode" == "local" ]]; then
    maven_builder_source="local"
    native_image_builder_source="local"
    builder_source="local"
fi
escaped_builder_source="${builder_source//\\/\\\\}"
escaped_builder_source="${escaped_builder_source//\"/\\\"}"
escaped_maven_builder_source="${maven_builder_source//\\/\\\\}"
escaped_maven_builder_source="${escaped_maven_builder_source//\"/\\\"}"
escaped_native_image_builder_source="${native_image_builder_source//\\/\\\\}"
escaped_native_image_builder_source="${escaped_native_image_builder_source//\"/\\\"}"
built_at_utc="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
minimum_version_field=""
if [[ -n "$minimum_os_version" ]]; then
    minimum_version_field="  \"minimumOsVersion\": \"$minimum_os_version\","
elif [[ -n "$minimum_glibc_version" ]]; then
    minimum_version_field="  \"minimumGlibcVersion\": \"$minimum_glibc_version\","
fi
cat > "$staging_directory/build-receipt.json" <<EOF
{
  "protocolVersion": 1,
  "rid": "$rid",
  "artifact": "$binary_name",
  "abi": "$abi",
$minimum_version_field
  "sha256": "$artifact_sha256",
  "calciteVersion": "1.42.0",
  "javaRelease": 21,
  "legalClosureFormatVersion": $legal_closure_format_version,
  "legalDocumentCount": $legal_document_count,
  "legalReviewRequiredCount": $legal_review_required_count,
  "runtimeDependencyCount": $runtime_dependency_count,
  "runtimeDependenciesSha256": "$runtime_dependencies_sha256",
  "thirdPartyNoticesSha256": "$third_party_notices_sha256",
  "mavenContentLockSha256": "$maven_content_lock_sha256",
  "maximumRuntimeHeap": "$MAXIMUM_RUNTIME_HEAP",
  "buildMode": "$mode",
  "builder": "$escaped_builder",
  "builderSource": "$escaped_builder_source",
  "mavenBuilderSource": "$escaped_maven_builder_source",
  "nativeImageBuilderSource": "$escaped_native_image_builder_source",
  "builtAtUtc": "$built_at_utc"
}
EOF

mkdir -p "$artifact_directory"
previous_receipt="$artifact_directory/.build-receipt.json.previous.$$"
if [[ -f "$artifact_directory/build-receipt.json" ]]; then
    mv "$artifact_directory/build-receipt.json" "$previous_receipt"
fi
mv -f "$staged_artifact_path" "$artifact_path"
mv -f \
    "$staging_directory/THIRD-PARTY-NOTICES.md" \
    "$artifact_directory/THIRD-PARTY-NOTICES.md"
mv -f \
    "$staging_directory/runtime-dependencies.txt" \
    "$artifact_directory/runtime-dependencies.txt"
# The receipt is the commit marker: packaging fails closed while it is absent,
# and can only observe it after the binary and metadata have been published.
mv -f "$staging_directory/build-receipt.json" "$artifact_directory/build-receipt.json"
if [[ -f "$previous_receipt" ]]; then
    rm -f -- "$previous_receipt"
fi

echo "Built $artifact_path"
echo "SHA-256 $artifact_sha256"

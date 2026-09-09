"""Exercise the real bundling script with disposable engines, never the user's app."""

import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).resolve().with_name("build-macos-connection-engines.sh")
CODE = ("asura-openvpn-engine", "tailscale", "tailscaled", "openconnect", "libopenconnect.5.dylib")
LEGAL = (
    "GO-LICENSE.txt", "OPENCONNECT-LGPL-2.1.txt", "OPENCONNECT-SOURCE-AND-RELINKING.md",
    "OPENSSL-LICENSE.txt", "OPENVPN-MPL-2.0.txt", "OPENVPN-LICENSE.md", "ASIO-LICENSE.txt",
    "LZ4-LICENSE.txt", "OPENVPN-VERSIONS.txt", "OPENVPN-THIRD-PARTY-NOTICES.txt", "THIRD-PARTY-NOTICES.md",
)


class DevelopmentEngineTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="asura-engine-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        scripts = self.root / "scripts"
        scripts.mkdir()
        self.script = scripts / SCRIPT.name
        shutil.copy2(SCRIPT, self.script)
        self.payload = self.root / "native/artifacts/osx-arm64/connection-engines"
        self.payload.mkdir(parents=True)
        for name in CODE:
            self.write_executable(self.payload / name, "exit 64" if name == CODE[0] else "exit 0")
        for name in (*LEGAL, "sources/openconnect-9.21.tar.gz"):
            path = self.payload / name
            path.parent.mkdir(exist_ok=True)
            path.write_text(name)
        self.manifest()
        commands = self.root / "commands"
        commands.mkdir()
        self.write_executable(commands / "file", "echo 'Mach-O 64-bit executable arm64'")
        self.write_executable(commands / "otool", "printf 'fixture:\n /usr/lib/libSystem.B.dylib\n'")
        # Rebuilding is deliberately unavailable in these offline failure tests.
        self.write_executable(commands / "go", "exit 1")
        self.environment = dict(os.environ, PATH=f"{commands}:{os.environ['PATH']}")
        self.destination = self.root / "candidate/Contents/MacOS"
        self.destination.mkdir(parents=True)

    @staticmethod
    def write_executable(path, body):
        path.write_text(f"#!/bin/sh\n{body}\n")
        path.chmod(0o755)

    def manifest(self):
        entries = []
        for name in (*CODE, *LEGAL, "sources/openconnect-9.21.tar.gz"):
            digest = hashlib.sha256((self.payload / name).read_bytes()).hexdigest()
            entries.append(f"{digest}  {name}\n")
        (self.payload / "MANIFEST.sha256").write_text("".join(entries))

    def run_script(self, *arguments):
        return subprocess.run(
            ["/bin/bash", str(self.script), *map(str, arguments)],
            env=self.environment, capture_output=True, text=True, timeout=20, check=False,
        )

    def test_stages_complete_payload_into_empty_managed_output_without_sources(self):
        result = self.run_script("--stage", self.destination, "--rid", "osx-arm64")
        self.assertEqual(0, result.returncode, result.stderr)
        for name in CODE:
            copied = self.destination / "runtimes/osx-arm64/connection-engines" / name
            self.assertEqual((self.payload / name).read_bytes(), copied.read_bytes())
            self.assertTrue(os.access(copied, os.X_OK))
        self.assertTrue((self.destination / "connection-engine-legal/THIRD-PARTY-NOTICES.md").is_file())
        self.assertFalse((self.destination / "connection-engine-legal/sources").exists())

    def test_intel_target_skips_arm_cache_even_on_arm_host(self):
        for cache in ("present", "missing"):
            with self.subTest(cache=cache):
                if cache == "missing":
                    shutil.rmtree(self.payload)
                result = self.run_script("--stage", self.destination, "--rid", "osx-x64")
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn("Skipping unsupported", result.stderr)
                self.assertEqual([], list(self.destination.iterdir()))

    def test_unknown_target_is_rejected_without_staging(self):
        result = self.run_script("--stage", self.destination, "--rid", "linux-x64")
        self.assertEqual(64, result.returncode)
        self.assertEqual([], list(self.destination.iterdir()))

    def test_intel_host_with_no_cache_does_not_invoke_arm_builder(self):
        shutil.rmtree(self.payload)
        self.write_executable(self.root / "commands/uname",
                              'case "$1" in -s) echo Darwin;; -m) echo x86_64;; esac')
        result = self.run_script("--stage", self.destination, "--rid", "osx-x64")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn("Restoring", result.stderr)
        self.assertFalse(self.payload.exists())

    def test_msbuild_passes_effective_target_through_runner_to_staging(self):
        root = SCRIPT.parent.parent
        project = ET.parse(root / "src/Asura.Desktop/Asura.Desktop.csproj")
        arguments = project.find(".//Target[@Name='ConfigureMacOsDevelopmentRun']/PropertyGroup/RunArguments").text
        self.assertIn('--runtime-identifier "$(AsuraEffectiveRuntimeIdentifier)"', arguments)
        runner = (SCRIPT.parent / "run-macos-development.sh").read_text()
        self.assertIn('runtime_identifier="$2"', runner)
        self.assertIn('--stage "${macos_directory}" --rid "${runtime_identifier}"', runner)

    def test_missing_cache_and_failed_rebuild_leave_destination_untouched(self):
        shutil.rmtree(self.payload)
        sentinel = self.destination / "existing-app"
        sentinel.write_text("preserve")
        result = self.run_script("--stage", self.destination)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([sentinel], list(self.destination.iterdir()))

    def test_missing_cache_is_rebuilt_then_verified_before_staging(self):
        seed = self.root / "seed"
        shutil.move(self.payload, seed)
        # Replace only the expensive compiler body. The production --stage and
        # --verify paths still run, including recursion into this build command.
        script = self.script.read_text()
        dispatch, separator, _ = script.partition('if [[ "$(uname -s):$(uname -m)" != "Darwin:arm64" ]]; then')
        self.assertTrue(separator)
        self.script.write_text(dispatch + '\ncp -R "${repository_dir}/seed" "${artifact_directory}"\n')
        result = self.run_script("--stage", self.destination)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue((self.payload / "openconnect").exists())
        self.assertTrue((self.destination / "runtimes/osx-arm64/connection-engines/openconnect").exists())

    def test_rejects_missing_corrupt_nonexecutable_and_unloadable_engines(self):
        executable = self.payload / "openconnect"
        for fault in ("missing", "corrupt", "permission", "unloadable", "manifest"):
            with self.subTest(fault=fault):
                self.write_executable(executable, "exit 0")
                self.manifest()
                if fault == "missing":
                    executable.unlink()
                elif fault == "corrupt":
                    executable.write_text("corrupt")
                elif fault == "permission":
                    executable.chmod(0o644)
                elif fault == "unloadable":
                    self.write_executable(executable, "exit 126")
                    self.manifest()
                else:
                    manifest = self.payload / "MANIFEST.sha256"
                    manifest.write_text("".join(line for line in manifest.read_text().splitlines(True)
                                                if not line.endswith("  openconnect\n")))
                self.assertNotEqual(0, self.run_script("--verify").returncode)


if __name__ == "__main__":
    unittest.main()

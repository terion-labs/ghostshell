#!/usr/bin/env python3
"""Check the pinned-framework transformation without accessing any Keychain."""

import importlib.util
from pathlib import Path
import tempfile
import unittest
import sys

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("scope_cef", Path(__file__).with_name("scope-cef-keychain.py"))
scope = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scope)


class KeychainScopeTests(unittest.TestCase):
    def test_release_and_development_are_distinct_and_preserve_offsets(self):
        with tempfile.TemporaryDirectory() as directory:
            binary = Path(directory) / "framework"
            before = b"\xcf\xfa\xed\xfe" + b"prefix" + scope.SERVICES["upstream"] + b"suffix"
            binary.write_bytes(before)
            binary.chmod(0o755)
            self.assertTrue(scope.scope_framework(binary, "upstream", "release"))
            self.assertEqual(before.replace(scope.SERVICES["upstream"], scope.SERVICES["release"]), binary.read_bytes())
            self.assertFalse(scope.scope_framework(binary, "upstream", "release"))
            self.assertTrue(scope.scope_framework(binary, "release", "development"))
            self.assertEqual(before.replace(scope.SERVICES["upstream"], scope.SERVICES["development"]), binary.read_bytes())
            self.assertEqual(0o755, binary.stat().st_mode & 0o777)

    def test_unknown_or_ambiguous_payload_is_never_changed(self):
        for body in (b"unknown", scope.SERVICES["upstream"] * 2,
                     scope.SERVICES["upstream"] + scope.SERVICES["release"]):
            with self.subTest(body=body), tempfile.TemporaryDirectory() as directory:
                binary = Path(directory) / "framework"
                data = b"\xcf\xfa\xed\xfe" + body
                binary.write_bytes(data)
                with self.assertRaises(ValueError):
                    scope.scope_framework(binary, "upstream", "release")
                self.assertEqual(data, binary.read_bytes())

    def test_link_or_non_macho_is_never_changed(self):
        with tempfile.TemporaryDirectory() as directory:
            binary = Path(directory) / "framework"
            binary.write_bytes(scope.SERVICES["upstream"])
            link = Path(directory) / "link"
            link.symlink_to(binary)
            for candidate in (binary, link):
                with self.assertRaises(ValueError):
                    scope.scope_framework(candidate, "upstream", "release")
            self.assertEqual(scope.SERVICES["upstream"], binary.read_bytes())


if __name__ == "__main__":
    unittest.main()

"""Exercise the published NativeAOT binary, including offline edit preconditions."""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXE = Path(os.environ.get("UCL_TEST_EXE", str(ROOT / "src/unity_compat/bin/win-x64/ucl.exe")))


@unittest.skipUnless(os.name == "nt" and EXE.exists(), "Requires the Windows native build")
class NativeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.project = Path(self.tmp.name) / "中文 Project"
        (self.project / "Assets").mkdir(parents=True)
        (self.project / "ProjectSettings").mkdir()
        (self.project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 2022.3.62f3c1\n")
        (self.project / "Packages").mkdir()
        (self.project / "Packages/manifest.json").write_text(json.dumps({"dependencies": {"com.unity.test-framework": "1.1.33"}}))
        self.file = self.project / "Assets/中文.txt"
        self.file.write_bytes(b'\xef\xbb\xbf' + "旧文本\r\nNext\r\n".encode())

    def call(self, *args, exit=0):
        result = subprocess.run([str(EXE), *args, "--project", str(self.project), "--json"], capture_output=True, encoding="utf-8")
        self.assertEqual(result.returncode, exit, result.stdout + result.stderr)
        return json.loads(result.stdout if result.returncode == 0 else result.stderr)

    def test_unicode_find_and_bom_preserving_replace(self):
        found = self.call("file", "find", "Assets/中文.txt", "--search", "旧文本")
        self.assertEqual(found["matches"][0]["line"], 1)
        sha = found["sha256"]
        self.call("file", "replace", "Assets/中文.txt", "--from", "旧文本", "--to", "新文本", "--sha", sha, "--dry-run")
        self.assertIn("旧文本".encode(), self.file.read_bytes())
        self.call("file", "replace", "Assets/中文.txt", "--from", "旧文本", "--to", "新文本", "--sha", sha)
        self.assertEqual(self.file.read_bytes(), b'\xef\xbb\xbf' + "新文本\r\nNext\r\n".encode())
        self.call("file", "replace", "Assets/中文.txt", "--from", "新文本", "--to", "oops", "--sha", sha, exit=2)

    def test_edit_requires_unique_match_and_sha(self):
        self.file.write_text("same same", encoding="utf-8")
        sha = self.call("file", "sha", "Assets/中文.txt")["sha256"]
        self.call("file", "replace", "Assets/中文.txt", "--from", "same", "--to", "x", "--sha", sha, exit=2)
        self.call("file", "replace", "Assets/中文.txt", "--from", "same", "--to", "x", exit=2)
        self.assertEqual(self.file.read_text(), "same same")

    def test_traversal_and_bad_arguments_fail_locally(self):
        self.call("file", "sha", "Assets/../../secret", exit=2)
        self.call("create", "cube", exit=2)
        self.call("hierarchy", "--limti", "1", exit=2)
        self.call("set", "X", "Transform", "m_LocalPosition", "1,2", "--format", "unsupported", exit=2)
        self.call("hierarchy", "--timeout", "NaN", exit=2)
        self.call("job", "status", "../endpoint", exit=2)

    def test_native_install_preserves_versions_and_archives(self):
        self.call("install")
        self.call("install")
        manifest = json.loads((self.project / "Packages/manifest.json").read_text())
        self.assertEqual(manifest["dependencies"]["com.unity.test-framework"], "1.1.33")
        self.assertEqual(manifest["dependencies"]["com.unitycompat.cli"], "0.3.0")
        self.assertEqual(len(list((self.project / ".unity-compat-backups").iterdir())), 2)
        self.assertTrue((self.project / "Packages/com.unitycompat.cli/Editor/Compat/PipeBridge.cs").is_file())

    def test_native_install_version_floor_and_unrelated_package(self):
        path = self.project / "ProjectSettings/ProjectVersion.txt"
        path.write_text("m_EditorVersion: 2021.2.1f1")
        self.call("install", exit=4)
        path.write_text("m_EditorVersion: 2022.3.62f3c1")
        (self.project / "Packages/com.unitycompat.cli").mkdir()
        self.call("install", exit=4)

    def test_offline_discovery_no_editor(self):
        entries = self.call("list", "--offline", "--group", "agent")
        self.assertEqual(len(entries), 5)
        self.assertTrue(any(x["name"] == "agent_inspect" for x in entries))

    def test_detached_daemon_returns_closes_and_recovers_dead_worker(self):
        # Capture pipes are the Agent's normal invocation. The worker must not hold
        # them open after the short-lived parent returns.
        def daemon(action):
            child = subprocess.run([str(EXE), "daemon", action, "--project", str(self.project), "--json"],
                capture_output=True, encoding="utf-8", timeout=8)
            self.assertEqual(child.returncode, 0, child.stderr)
            return json.loads(child.stdout)
        try:
            first = daemon("start")
            self.assertTrue(first["running"])
            self.assertEqual(daemon("status")["pid"], first["pid"])
            subprocess.run(["taskkill", "/PID", str(first["pid"]), "/F"], capture_output=True, timeout=5, check=True)
            self.assertFalse(daemon("status")["running"])
            self.assertTrue(daemon("start")["running"])
        finally:
            daemon("stop")
        self.assertFalse(daemon("status")["running"])


if __name__ == "__main__": unittest.main()

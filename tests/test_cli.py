import json
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch
from unity_compat.cli import load_params, parser
from unity_compat.protocol import CliError, Mailbox, atomic_json, project_root, read_json
from unity_compat import projects


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.project = Path(self.temp.name) / "测试 Project"
        (self.project / "Assets").mkdir(parents=True)
        (self.project / "ProjectSettings").mkdir()
        (self.project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 2022.3.62f3c1\n")
        (self.project / "Packages").mkdir()
        atomic_json(self.project / "Packages/manifest.json", {"dependencies": {"com.unity.test-framework": "1.1.33", "example": "2.0.0"}})
        self.mailbox = Mailbox(self.project)
        atomic_json(self.mailbox.root / "status.json", {"protocol": 1, "session": "session-a", "project": str(self.project), "heartbeat": time.time(), "state": "ready"})

    def test_global_flags_before_and_after_subcommand(self):
        for values in (["--json", "--project", "test", "command", "get_project_info"],
                       ["command", "get_project_info", "--json", "--project", "test"]):
            args = parser().parse_args(values)
            self.assertTrue(args.json)
            self.assertEqual(args.project, "test")

    def test_typed_unicode_and_file_params(self):
        path = Path(self.temp.name) / "参数.json"
        atomic_json(path, {"name": "坦克"})
        self.assertEqual(load_params("@" + str(path), ["count=3", "flag=true", "point=[1,2,3]", "text=hello"]),
                         {"name": "坦克", "count": 3, "flag": True, "point": [1, 2, 3], "text": "hello"})
        with self.assertRaises(CliError): load_params("[]", [])
        with self.assertRaises(CliError): load_params('{"x": NaN}', [])

    def test_atomic_read_retries_transient_windows_permission(self):
        with patch.object(Path, "read_text", side_effect=[PermissionError("replace"), PermissionError("replace"), '{"ok":true}']) as read, patch("unity_compat.protocol.time.sleep") as sleep:
            self.assertEqual(read_json(Path("status.json")), {"ok": True})
            self.assertEqual(read.call_count, 3)
            self.assertEqual(sleep.call_count, 2)

    def test_atomic_read_has_bounded_retry_and_preserves_invalid_json(self):
        with patch.object(Path, "read_text", side_effect=PermissionError("denied")) as read, patch("unity_compat.protocol.time.sleep"):
            with self.assertRaises(PermissionError): read_json(Path("status.json"))
            self.assertEqual(read.call_count, 9)
        with patch.object(Path, "read_text", return_value="broken") as read:
            with self.assertRaises(json.JSONDecodeError): read_json(Path("status.json"))
            self.assertEqual(read.call_count, 1)

    def test_atomic_read_retries_replace_gap(self):
        with patch.object(Path, "read_text", side_effect=[FileNotFoundError("replace gap"), '{"ok":true}']), patch("unity_compat.protocol.time.sleep"):
            self.assertEqual(read_json(Path("status.json")), {"ok": True})

    def test_stale_status_and_project_mismatch(self):
        data = read_json(self.mailbox.root / "status.json")
        data["heartbeat"] -= 30
        atomic_json(self.mailbox.root / "status.json", data)
        with self.assertRaises(CliError) as result: self.mailbox.status()
        self.assertEqual(result.exception.code, "EDITOR_UNAVAILABLE")
        data.update(heartbeat=time.time(), project=str(Path(self.temp.name)))
        atomic_json(self.mailbox.root / "status.json", data)
        with self.assertRaises(CliError) as result: self.mailbox.status()
        self.assertEqual(result.exception.code, "PROJECT_MISMATCH")

    def test_request_response_and_concurrent_unique_ids(self):
        ids = []
        threads = [threading.Thread(target=lambda: ids.append(self.mailbox.submit("get_project_info", {}, 10))) for _ in range(10)]
        for thread in threads: thread.start()
        for thread in threads: thread.join()
        self.assertEqual(len(set(ids)), 10)
        for job in ids:
            req = read_json(self.mailbox.root / "requests" / (job + ".json"))
            self.assertEqual(req["id"], job)
            self.assertEqual(req["session"], "session-a")
        atomic_json(self.mailbox.root / "responses" / (ids[0] + ".json"), {"protocol": 1, "id": ids[0], "success": True, "data": "ok"})
        self.assertEqual(self.mailbox.wait(ids[0], 1)["data"], "ok")

    def test_timeout_never_replays_or_loses_job(self):
        job = self.mailbox.submit("manage_gameobject", {"action": "create", "name": "once"}, 2)
        with self.assertRaises(CliError) as result: self.mailbox.wait(job, .01)
        self.assertEqual(result.exception.code, "TIMEOUT")
        self.assertEqual(result.exception.data["jobId"], job)
        self.assertEqual(len(list((self.mailbox.root / "requests").glob("*.json"))), 1)
        self.mailbox.cancel(job)
        self.assertTrue((self.mailbox.root / "cancellations" / (job + ".json")).exists())

    def test_response_identity_is_checked(self):
        job = self.mailbox.submit("anything", {})
        atomic_json(self.mailbox.root / "responses" / (job + ".json"), {"protocol": 1, "id": "0" * 32, "success": True})
        with self.assertRaises(CliError): self.mailbox.wait(job, 1)

    def test_job_path_traversal_and_request_limit(self):
        with self.assertRaises(CliError): self.mailbox.job("../status")
        with self.assertRaises(CliError): self.mailbox.submit("eval", {"code": "x" * (5 * 1024 * 1024)})

    def test_install_preserves_versions_and_uninstall_archives(self):
        projects.install(self.project)
        manifest = read_json(self.project / "Packages/manifest.json")
        self.assertEqual(manifest["dependencies"]["com.unity.test-framework"], "1.1.33")
        self.assertEqual(manifest["dependencies"]["example"], "2.0.0")
        self.assertTrue((self.project / "Packages/com.unitycompat.cli/Editor/Compat/MailboxBridge.cs").is_file())
        removed = projects.uninstall(self.project)
        self.assertTrue((Path(removed["backup"]) / "com.unitycompat.cli/package.json").exists())
        self.assertNotIn("com.unitycompat.cli", read_json(self.project / "Packages/manifest.json")["dependencies"])

    def test_install_failure_preserves_previous_package(self):
        projects.install(self.project)
        marker = self.project / "Packages/com.unitycompat.cli/marker.txt"
        marker.write_text("keep")
        with patch("unity_compat.projects.shutil.copytree", side_effect=OSError("copy failed")):
            with self.assertRaises(OSError): projects.install(self.project)
        self.assertEqual(marker.read_text(), "keep")

    def test_project_detection_and_version_floor(self):
        self.assertEqual(project_root(self.project), self.project.resolve())
        projects.check_version("2021.3.1f1")
        with self.assertRaises(CliError): projects.check_version("2020.3.1f1")


if __name__ == "__main__": unittest.main()

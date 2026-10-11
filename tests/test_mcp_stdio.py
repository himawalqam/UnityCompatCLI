"""Check native MCP framing/lifecycle while no Unity Editor is required."""
import json
import os
import queue
import subprocess
import tempfile
import threading
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXE = Path(os.environ.get("UCL_TEST_EXE", str(ROOT / "src/unity_compat/bin/win-x64/ucl.exe")))


@unittest.skipUnless(os.name == "nt" and EXE.is_file(), "Needs native Windows binary")
class StdioTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.project = Path(self.temp.name)
        (self.project / "Assets").mkdir()
        (self.project / "ProjectSettings").mkdir()
        (self.project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 2022.3.62f3c1")
        self.env = dict(os.environ, UCL_HOME=str(ROOT / "src/unity_compat"))

    def start(self, profile="compact"):
        self.process = subprocess.Popen([str(EXE), "mcp", "--project", str(self.project), "--profile", profile],
                                        env=self.env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding="utf-8")
        self.addCleanup(self.close)
        self.messages = queue.Queue()

        def read():
            for line in self.process.stdout:
                self.messages.put(json.loads(line))
        threading.Thread(target=read, daemon=True).start()
        self.seq = 0

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
        self.process.stdout.close()
        self.process.stderr.close()

    def rpc(self, method, params=None, notify=False):
        self.seq += 1
        request = {"jsonrpc": "2.0", "method": method}
        if not notify:
            request["id"] = self.seq
        if params is not None:
            request["params"] = params
        self.process.stdin.write(json.dumps(request) + "\n")
        self.process.stdin.flush()
        if not notify:
            response = self.messages.get(timeout=8)
            self.assertEqual(response["id"], self.seq)
            return response

    def initialize(self):
        result = self.rpc("initialize", {"protocolVersion": "2025-06-18", "clientInfo": {"name": "test", "version": "1"}, "capabilities": {}})["result"]
        self.assertEqual(result["serverInfo"]["version"], "0.3.0")
        self.rpc("notifications/initialized", notify=True)

    def test_lifecycle_and_compact_discovery_offline(self):
        self.start()
        self.assertEqual(self.rpc("tools/list")["error"]["code"], -32002)
        self.initialize()
        tools = self.rpc("tools/list")["result"]["tools"]
        self.assertEqual(len(tools), 9)
        self.assertIn("ucl_command", [x["name"] for x in tools])
        inspector = next(x for x in tools if x["name"] == "agent_inspect")
        self.assertIn("fields", inspector["inputSchema"]["properties"])
        self.assertEqual(self.rpc("ping")["result"], {})
        self.assertEqual(self.rpc("unsupported/method")["error"]["code"], -32601)

    def test_full_profile_resources_and_error_results(self):
        self.start("full")
        self.initialize()
        tools = self.rpc("tools/list")["result"]["tools"]
        self.assertEqual(len(tools), len(json.loads((ROOT / "src/unity_compat/catalog.json").read_text(encoding="utf-8"))) + 3)
        resources = self.rpc("resources/list")["result"]["resources"]
        self.assertEqual(len(resources), 19)
        templates = self.rpc("resources/templates/list")["result"]["resourceTemplates"]
        self.assertIn("{params}", templates[0]["uriTemplate"])
        result = self.rpc("tools/call", {"name": "ucl_command", "arguments": {"command": "does_not_exist"}})["result"]
        self.assertTrue(result["isError"])
        self.assertEqual(result["structuredContent"]["error"]["code"], "UNKNOWN_COMMAND")
        result = self.rpc("tools/call", {"name": "ucl_status", "arguments": {}})["result"]
        self.assertTrue(result["isError"])

    def test_parse_error_does_not_break_next_request(self):
        self.start()
        self.process.stdin.write('{"invalid"\n')
        self.process.stdin.flush()
        self.assertEqual(self.messages.get(timeout=8)["error"]["code"], -32700)
        self.initialize()
        self.assertEqual(self.rpc("ping")["result"], {})

    def test_cli_errors_are_structured_stderr(self):
        result = subprocess.run([str(EXE), "hierarchy", "--limti", "3", "--json"], env=self.env,
                                capture_output=True, encoding="utf-8")
        self.assertEqual(result.returncode, 2)
        self.assertEqual(result.stdout, "")
        self.assertEqual(json.loads(result.stderr)["error"]["code"], "BAD_ARGUMENTS")

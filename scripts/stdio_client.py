"""A reusable, correlated native MCP stdio client for benchmark instrumentation."""
import json
import queue
import subprocess
import threading

class StdioClient:
    def __init__(self, exe, project, profile="compact"):
        self.process = subprocess.Popen([str(exe), "mcp", "--project", str(project), "--profile", profile],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
        self.lock = threading.Lock(); self.sequence = 0; self.pending = {}
        def read():
            for line in self.process.stdout:
                value = json.loads(line)
                if value.get("id") in self.pending: self.pending[value["id"]].put(value)
        self.reader = threading.Thread(target=read, daemon=True); self.reader.start()
        self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "benchmark", "version": "0.3"}})
        self.rpc("notifications/initialized", notify=True)
    def rpc(self, method, params=None, notify=False):
        with self.lock:
            self.sequence += 1; id = self.sequence; responses = queue.Queue()
            request = {"jsonrpc": "2.0", "method": method}
            if params is not None: request["params"] = params
            if not notify: request["id"] = id; self.pending[id] = responses
            self.process.stdin.write(json.dumps(request, ensure_ascii=False) + "\n"); self.process.stdin.flush()
        if notify: return {}
        try:
            value = responses.get(timeout=40)
            if "error" in value: raise RuntimeError(value["error"])
            return value["result"]
        finally: self.pending.pop(id, None)
    def command(self, command, params):
        result = self.rpc("tools/call", {"name": "ucl_command", "arguments": {"command": command, "params": params}})
        if result.get("isError"): raise RuntimeError(result)
        return result
    def close(self):
        self.process.stdin.close(); self.process.wait(timeout=5)
        self.process.stdout.close(); self.process.stderr.close()

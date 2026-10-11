"""Concurrency, idle keepalive, cancellation and frontend compatibility in the actual Editor."""
import asyncio
import json
import queue
import subprocess
import threading
from pathlib import Path
from mcp import Client, StdioServerParameters

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "validation/Unity2022"
EXE = ROOT / "src/unity_compat/bin/win-x64/ucl.exe"

async def sdk_checks():
    async with Client(StdioServerParameters(command=str(EXE), args=["mcp", "--project", str(PROJECT)])) as client:
        assert len((await client.list_tools()).tools) == 9
        result = await client.call_tool("ucl_command", {"command": "manage_tools", "params": {"action": "activate", "group": "scripting"}})
        assert not result.is_error
        tools = (await client.list_tools()).tools
        assert any(t.name == "apply_text_edits" for t in tools)
        edits = next(t for t in tools if t.name == "apply_text_edits").input_schema
        assert edits["properties"]["edits"]["type"] == "array"
        rows = await asyncio.gather(*(client.call_tool("ucl_command", {"command": "fixture_method", "params": {"message": str(i)}}) for i in range(24)))
        assert [r.structured_content["text"] for r in rows] == [str(i) for i in range(24)]
        await asyncio.sleep(35)  # Server idle limit is 30 s; the native session pings every 5 s.
        result = await client.call_tool("ucl_command", {"command": "fixture_method", "params": {"message": "after idle"}})
        assert result.structured_content["text"] == "after idle"
        result = await client.call_tool("ucl_command", {"command": "debug_request_context"})
        assert result.structured_content["transport"] == "mcp-stdio"
        result = await client.call_tool("ucl_command", {"command": "unity_docs", "params": {"action": "get_doc", "class_name": "Transform", "member_name": "position"}})
        assert not result.is_error
        assert any(p["found"] and "position" in p["text"].lower() for p in result.structured_content["pages"]), result
        result = await client.call_tool("ucl_command", {"command": "set_active_instance", "params": {"instance": str(PROJECT)}})
        assert not result.is_error and result.structured_content["scope"] == "this-mcp-session"
        result = await client.call_tool("ucl_command", {"command": "manage_tools", "params": {"action": "reset"}})
        assert not result.is_error and len((await client.list_tools()).tools) == 9
    return {"concurrentCalls": 24, "idleSeconds": 35, "groupVisibility": "passed", "typedArraySchema": "passed", "docs": "passed", "projectSelection": "passed"}

def cancellation():
    child = subprocess.Popen([str(EXE), "mcp", "--project", str(PROJECT)], stdin=subprocess.PIPE,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    messages = queue.Queue()
    def read():
        for line in child.stdout: messages.put(json.loads(line))
    threading.Thread(target=read, daemon=True).start()
    def send(method, params=None, id=None):
        request = {"jsonrpc": "2.0", "method": method}
        if params is not None: request["params"] = params
        if id is not None: request["id"] = id
        child.stdin.write(json.dumps(request) + "\n"); child.stdin.flush()
    try:
        send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "cancel", "version": "1"}}, 1)
        messages.get(timeout=5)
        send("notifications/initialized")
        send("tools/call", {"name": "ucl_command", "arguments": {"command": "fixture_async", "params": {"delay": 1500}}}, 2)
        send("notifications/cancelled", {"requestId": 2, "reason": "fixture"})
        result = messages.get(timeout=10)["result"]
        assert result["isError"] and result["structuredContent"]["exitCode"] == 9, result
        send("ping", {}, 3)
        assert messages.get(timeout=5)["result"] == {}
        return {"cooperativeCancellation": "passed", "pingAfterCancellation": "passed"}
    finally:
        child.stdin.close(); child.wait(timeout=5)
        child.stdout.close(); child.stderr.close()

result = asyncio.run(sdk_checks())
result.update(cancellation())
(ROOT / "artifacts/mcp-advanced-v3.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result))

"""Version 0.3 features/lifecycle verification. Only the independent Unity2022 fixture."""
import json
import subprocess
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "validation/Unity2022"
EXE = ROOT / "src/unity_compat/bin/win-x64/ucl.exe"
checks = []


def call(*args, expected=0):
    child = subprocess.run([str(EXE), *map(str, args), "--project", str(PROJECT), "--json"],
                           capture_output=True, encoding="utf-8", timeout=100)
    assert child.returncode == expected, (args, child.returncode, child.stdout, child.stderr)
    if child.returncode:
        assert child.stdout == "", "CLI failures must use stderr"
    data = json.loads(child.stdout if not child.returncode else child.stderr)
    checks.append({"args": args, "exit": child.returncode})
    (ROOT / "artifacts/live-v3-progress.json").write_text(json.dumps(checks, ensure_ascii=False), encoding="utf-8")
    return data


def settled(predicate=lambda s: True, timeout=50):
    until = time.monotonic() + timeout
    while time.monotonic() < until:
        child = subprocess.run([str(EXE), "status", "--project", str(PROJECT), "--json"],
                               capture_output=True, encoding="utf-8", timeout=5)
        if child.returncode == 0:
            state = json.loads(child.stdout)
            if state["state"] == "ready" and predicate(state):
                return state
        time.sleep(.2)
    raise AssertionError("Editor failed to settle")


session = settled()["session"]
assert call("command", "fixture_method", "--message", "中文")["count"] == 2
assert call("command", "fixture_method", expected=6)["error"]["code"] == "MISSING_ARGUMENT"
assert call("command", "fixture_method", "--message", "ok", "--other", "1", expected=6)["error"]["code"] == "INVALID_ARGUMENT"
assert call("command", "fixture_method", "--message", "ok", "--count", "bad", expected=6)["error"]["code"] == "INVALID_ARGUMENT"
assert call("command", "fixture_async", "--delay", "20")["completed"]
assert call("describe", "fixture_method")["inputSchema"]["required"] == ["message"]
assert call("eval", "return 9 * 9;") == 81
assert call("eval", "return 9 * 9;", "--metrics")["cacheHit"]
code = 'return (int)args["value"];'
assert call("eval", code, "--arg", 'args={"value":21}') == 21
assert call("eval", code, "--arg", 'args={"value":42}', "--metrics")["cacheHit"]
assert call("status")["session"] == session, "Eval must not reload the project"
diagnostics = call("eval", "invalid C# code", expected=6)
assert diagnostics["error"]["code"] == "COMPILATION_FAILED"
assert diagnostics["data"]["data"]["error"]["diagnostics"][0]["line"] == 1
assert call("eval", 'throw new Exception("fixture runtime");', expected=6)["error"]["code"] == "EVAL_RUNTIME_ERROR"

cube = call("create", "cube", "--name", "V3_Fixture", "--position", "1,2,3")["instanceID"]
try:
    projected = call("hierarchy", "--root", cube, "--fields", "id,name", "--depth", "0")
    assert projected["nodes"] == [{"id": cube, "name": "V3_Fixture"}]
    fields = call("inspect", cube, "--component", "Transform", "--fields", "m_LocalPosition.*")["fields"]
    assert fields == {"m_LocalPosition.x": 1, "m_LocalPosition.y": 2, "m_LocalPosition.z": 3}
    assert call("inspect", cube, "--component", "Transform", "--fields", "m_LocalPosition.*", "--limit", "2")["truncated"]
    assert call("inspect", cube, "--component", "Unavailable", expected=6)["error"]["code"] == "COMPONENT_NOT_FOUND"
    call("set", cube, "Transform", "m_LocalPosition", "0,0,0", "--expect", "[9,9,9]", expected=10)
finally:
    call("delete", cube)
assert call("inspect", cube, expected=6)["error"]["code"] == "TARGET_NOT_FOUND"
call("build-targets", "--installed")
assert call("screenshot", expected=11)["error"]["code"] == "UNSUPPORTED"
call("daemon", "start")
try:
    assert call("daemon", "status")["running"]
    call("windows")
    time.sleep(1)
    call("command", "fixture_method", "--message", "daemon")
finally:
    call("daemon", "stop")

# Persist only the fixture scene before unattended tests/Play Mode.
call("scene", "save", "--name", "V3Fixture", "--path", "Assets")
call("test", "--mode", "EditMode", "--timeout", "90")
settled()
assert call("pause", expected=6)["error"]["code"] == "INVALID_STATE"
assert call("play")["state"] == "transition_requested"
settled(lambda s: s["playing"])
call("pause")
call("step")
call("stop")
settled(lambda s: not s["playing"])
prior = settled()["session"]
assert call("recompile")["requested"] == "recompile"
settled(lambda s: s["session"] != prior)
assert call("command", "fixture_method", "--message", "after reload")["text"] == "after reload"

path = ROOT / "artifacts/live-v3.json"
path.write_text(json.dumps({"checks": checks, "count": len(checks)}, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"{len(checks)} v3 real Editor feature/lifecycle checks passed; {path}")

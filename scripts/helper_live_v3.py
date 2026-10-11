"""Script helper equivalence and failing tests in the disposable project only."""
import json
import subprocess
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "validation/Unity2022"
EXE = ROOT / "src/unity_compat/bin/win-x64/ucl.exe"
checks = []
def call(*args, expected=0):
    print("CHECK", args[:2], flush=True)
    p = subprocess.run([str(EXE), *args, "--project", str(PROJECT), "--json"], capture_output=True, encoding="utf-8", timeout=100)
    assert p.returncode == expected, (args, p.returncode, p.stdout, p.stderr)
    checks.append({"args": args[:2], "exit": p.returncode})
    return json.loads(p.stdout if p.returncode == 0 else p.stderr)
def wait_reload(prior=None):
    until = time.monotonic() + 50
    while time.monotonic() < until:
        p = subprocess.run([str(EXE), "status", "--project", str(PROJECT), "--json"], capture_output=True, encoding="utf-8", timeout=5)
        if p.returncode == 0:
            state = json.loads(p.stdout)
            if state["state"] == "ready" and (prior is None or state["session"] != prior): return state["session"]
        time.sleep(.2)
    raise AssertionError("Fixture did not finish reloading")
def cmd(name, params=None, expected=0):
    return call("command", name, "--params", json.dumps(params or {}, ensure_ascii=False), expected=expected)
path = "Assets/Editor/V3HelperFixture.cs"
file = PROJECT / path
tests = PROJECT / "Assets/Tests/Editor/FixtureTests.cs"
original = tests.read_bytes()
assert not file.exists()
try:
    prior = wait_reload()
    cmd("create_script", {"path": path, "contents": "public class V3HelperFixture { public int Value = 1; }\n"})
    wait_reload(prior)
    cmd("create_script", {"path": path, "contents": "overwrite"}, expected=10)
    assert cmd("validate_script", {"uri": path})["valid"]
    assert cmd("validate_script", {"uri": path, "level": "basic"})["valid"]
    assert len(cmd("find_in_file", {"uri": path, "pattern": "Value"})["matches"]) == 1
    sha = cmd("get_sha", {"uri": path})
    assert sha["sha256"]
    prior = wait_reload()
    # Insert an empty comment at a known 1-indexed position, then validate the result.
    cmd("apply_text_edits", {"uri": path, "edits": [{"startLine": 1, "startCol": 1, "endLine": 1, "endCol": 1, "newText": "// helper\n"}], "precondition_sha256": sha["sha256"], "strict": True})
    wait_reload(prior)
    assert file.read_text().startswith("// helper\n")
    cmd("apply_text_edits", {"uri": path, "edits": [{"startLine": 1, "startCol": 1, "endLine": 1, "endCol": 1, "newText": "// stale\n"}], "precondition_sha256": sha["sha256"]}, expected=10)
    cmd("apply_text_edits", {"uri": path, "edits": [], "strict": True}, expected=10)
    assert cmd("manage_script_capabilities")["compiler"] == "roslyn-3.8-relocated"
    assert cmd("execute_custom_tool", {"tool_name": "fixture_method", "parameters": {"message": "custom"}})["text"] == "custom"
    cmd("find_in_file", {"uri": "Assets/../../outside.txt", "pattern": "x"}, expected=6)
    prior = wait_reload()
    cmd("delete_script", {"uri": path})
    wait_reload(prior)
    assert not file.exists()
    prior = wait_reload()
    tests.write_bytes(original.replace(b'Assert.That(2 + 2, Is.EqualTo(4));', b'Assert.Fail("Deliberate v3 fixture failure");'))
    call("refresh")
    wait_reload(prior)
    failed = call("test", "--mode", "EditMode", "--timeout", "90", expected=8)
    assert failed["error"]["code"] == "TESTS_FAILED"
finally:
    prior = wait_reload()
    changed = tests.read_bytes() != original or file.exists()
    if tests.read_bytes() != original: tests.write_bytes(original)
    if file.exists(): cmd("delete_script", {"uri": path})
    if changed:
        call("refresh")
        wait_reload(prior)
(ROOT / "artifacts/helpers-v3.json").write_text(json.dumps({"count": len(checks), "checks": checks}, indent=2), encoding="utf-8")
print(f"{len(checks)} script helper / failure checks passed")

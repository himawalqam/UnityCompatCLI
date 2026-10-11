"""Exercise a real, isolated Editor. Requires create_validation_project.py + install + open."""
import concurrent.futures
import json
import subprocess
import sys
import time
from pathlib import Path
from unity_compat.protocol import Mailbox, atomic_json, read_json

root = Path(__file__).resolve().parents[1]
project = root / "validation/Unity2022"
mailbox = Mailbox(project)
checks = []

def call(command, params=None, expected=True, timeout=40):
    job = mailbox.submit(command, params or {}, timeout)
    result = mailbox.wait(job, timeout)
    assert result["success"] == expected, json.dumps(result, ensure_ascii=False)
    checks.append({"command": command, "success": result["success"], "expectedSuccess": expected})
    return result.get("data")

def data(value):
    return value.get("data", value)

inventory = data(call("cli_list"))
assert len(inventory) >= 74
assert any(x["name"] == "fixture_echo" for x in inventory)
echo = call("fixture_echo", {"message": "中文参数"})
assert echo["echoed"]["message"] == "中文参数"
call("not_a_command", expected=False)
call("manage_gameobject", {"action": "create"}, expected=False)
call("execute_code", {"action": "execute", "code": "return UnityEngine.Application.unityVersion;"})
suffix = str(int(time.time()))
cube_name = "CompatCube_" + suffix
call("manage_gameobject", {"action": "create", "name": cube_name, "primitiveType": "Cube", "position": [1, 2, 3]})
snapshot = data(call("scene_snapshot"))
assert any(x["item"]["path"] == cube_name and x["item"]["position"] == [1.0, 2.0, 3.0] for x in snapshot["objects"])
call("serialized_inspect", {"target": cube_name, "component": "Transform"})
call("manage_selection", {"action": "set", "target": cube_name})
call("project_settings", {"action": "get", "section": "physics", "property": "gravity"})
call("project_settings", {"action": "set", "section": "player", "property": "companyName", "value": "Compat fixture"})
assert data(call("project_settings", {"action": "get", "property": "companyName"})) == "Compat fixture"
call("project_audit", {"action": "scene"})
call("project_audit", {"action": "assets"})
call("type_query", {"query": "AudioSource", "components_only": True})
call("manage_audio", {"action": "list"})
call("manage_lighting", {"action": "status"})
call("manage_navigation", {"action": "status"})
terrain_name = "CompatTerrain_" + suffix
terrain_path = "Assets/CompatTerrain_" + suffix + ".asset"
call("manage_terrain", {"action": "create", "path": terrain_path, "name": terrain_name, "resolution": 33})
call("manage_terrain", {"action": "set_heights", "target": terrain_name, "width": 2, "height": 2, "heights": [[.1, .2], [.3, .4]]})
heights = data(call("manage_terrain", {"action": "get_heights", "target": terrain_name, "width": 2, "height": 2}))
assert abs(heights[0][0] - .1) < .001
call("asset_references", {"action": "dependencies", "path": terrain_path})
call("asset_references", {"action": "referencers", "path": terrain_path})
call("asset_importer", {"action": "get", "path": terrain_path})
call("asset_bundles", {"action": "list"})
batch = call("cli_batch", {"operations": [
    {"command": "fixture_echo", "params": {"step": 1}},
    {"command": "manage_gameobject", "params": {"action": "create"}},
    {"command": "manage_gameobject", "params": {"action": "create", "name": "MustNotRun"}}
]}, expected=False)
assert batch["completed"] == 2
batch = call("cli_batch", {"operations": [{"command": "fixture_echo"}, {"command": "unknown"}]}, expected=False)
assert "Unknown" in (batch or {}).get("error", "") if batch else True

# A request that belongs to a previous editor session is rejected before mutation.
import uuid
job = uuid.uuid4().hex
atomic_json(mailbox.root / "requests" / (job + ".json"), {"protocol": 1, "id": job,
    "session": "invalid-session", "command": "manage_gameobject", "params": {"action": "create", "name": "MustNotRunOldSession"},
    "created": time.time(), "deadline": time.time() + 30})
assert not mailbox.wait(job, 10)["success"]
checks.append({"check": "stale-session rejected", "success": True})

# Multiple CLI invocations queue independently and their IDs/results stay distinct.
def echo_parallel(n):
    job = mailbox.submit("fixture_echo", {"n": n})
    result = mailbox.wait(job, 20)
    assert result["data"]["echoed"]["n"] == n
    return job
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    ids = list(pool.map(echo_parallel, range(8)))
assert len(set(ids)) == 8
checks.append({"check": "8 concurrent commands", "success": True})

# Queued cancellation must never create an object. Publish cancellation before request.
import uuid
job = uuid.uuid4().hex
atomic_json(mailbox.root / "cancellations" / (job + ".json"), {"id": job})
atomic_json(mailbox.root / "requests" / (job + ".json"), {"protocol": 1, "id": job,
    "session": mailbox.status()["session"], "command": "manage_gameobject", "params": {"action": "create", "name": "MustNotRunCancelled"},
    "created": time.time(), "deadline": time.time() + 30})
assert not mailbox.wait(job, 10)["success"]
checks.append({"check": "queued cancellation", "success": True})
job = uuid.uuid4().hex
atomic_json(mailbox.root / "requests" / (job + ".json"), {"protocol": 1, "id": job,
    "session": mailbox.status()["session"], "command": "manage_gameobject", "params": {"action": "create", "name": "MustNotRunExpired"},
    "created": time.time() - 10, "deadline": time.time() - 5})
assert not mailbox.wait(job, 10)["success"]
checks.append({"check": "expired request", "success": True})
snapshot = data(call("scene_snapshot"))
assert not any(x["item"]["path"].startswith("MustNotRun") for x in snapshot["objects"])

call("read_console", {"action": "get", "count": 10})
call("execute_code", {"action": "execute", "code": 'return UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), "Assets/ValidationScene.unity");'})
call("cli_poll", {"command": "run_tests", "params": {"mode": "EditMode"}}, timeout=90)
report = {"unity": mailbox.status()["unity"], "commandCount": len(inventory), "checks": checks,
          "testProject": str(project), "externalServer": False}
(root / "docs/live-validation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"checks": len(checks), "commands": len(inventory), "unity": report["unity"]}))

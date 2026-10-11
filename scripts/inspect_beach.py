"""Read-only CLI inspection. Saves the exact replies; never changes selection or scenes."""
import json
import subprocess
import sys
from pathlib import Path

PROJECT = Path("D:/unityProject/Beach-Tank")
OUT = Path("D:/unityTools/UnityCompatCLI/artifacts/Beach-Tank-20261011")

def call(label, *args):
    p = subprocess.run([sys.executable, "-m", "unity_compat", *args,
                        "--project-path", str(PROJECT), "--json", "--timeout", "30"],
                       capture_output=True, encoding="utf-8")
    try:
        result = json.loads(p.stdout)
    except ValueError:
        result = {"success": False, "stdout": p.stdout, "stderr": p.stderr}
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / (label + ".json")).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"label": label, "exit": p.returncode, "result": result}, ensure_ascii=False))
    return result

if __name__ == "__main__":
    if len(sys.argv) > 1:
        call(sys.argv[1], *sys.argv[2:])
    else:
        result = call("status", "status")
        if not result.get("success"):
            sys.exit(1)
        for name in ("get_windows", "get_selection", "get_editor_state"):
            call(name, "command", name)
        call("hierarchy", "scene", "get_hierarchy")
        call("scene_snapshot", "command", "scene_snapshot", "--arg", "limit=3000")

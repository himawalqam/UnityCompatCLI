"""Validate the installed wheel's embedded package without importing the editable checkout."""
import json
import sys
import tempfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
installed = root / "artifacts/wheel-install-v3"
sys.path.insert(0, str(installed))
import unity_compat
from unity_compat import projects
from unity_compat.cli import offline_catalog
from unity_compat.protocol import atomic_json, read_json

assert Path(unity_compat.__file__).is_relative_to(installed), unity_compat.__file__
count = len(json.loads((root / "src/unity_compat/catalog.json").read_text(encoding="utf-8")))
assert len(offline_catalog()) == count
assert (installed / "unity_compat/bin/win-x64/ucl.exe").is_file()
with tempfile.TemporaryDirectory() as temp:
    project = Path(temp) / "IndependentWheelProject"
    (project / "Assets").mkdir(parents=True)
    (project / "ProjectSettings").mkdir()
    (project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 2022.3.62f3c1\n")
    atomic_json(project / "Packages/manifest.json", {"dependencies": {}})
    result = projects.install(project)
    bridge = project / "Packages/com.unitycompat.cli"
    assert (bridge / "Editor/Compat/MailboxBridge.cs").is_file()
    assert (bridge / "LICENSE-Coplay.txt").is_file()
    assert len(list((bridge / "Editor/Compiler").glob("*.dll"))) == 8
    assert read_json(bridge / "package.json")["name"] == "com.unitycompat.cli"
print(json.dumps({"installedWheel": str(installed), "commands": count, "compilerDlls": 8, "embeddedBridgeInstall": "passed", "nativeBinary": "present"}))

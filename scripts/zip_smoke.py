"""Exercise the standalone Windows ZIP without the editable package or pip launcher."""
import hashlib
import json
import os
import subprocess
import tempfile
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
version = json.loads((root / "src/unity_compat/bridge/package.json").read_text(encoding="utf-8"))["version"]
count = len(json.loads((root / "src/unity_compat/catalog.json").read_text(encoding="utf-8")))
release = root / f"dist/UnityCompatCLI-{version}-Windows.zip"
with tempfile.TemporaryDirectory(prefix="zip-smoke-", dir=root / "artifacts") as temp:
    folder = Path(temp)
    with zipfile.ZipFile(release) as archive:
        for name in archive.namelist():
            assert (folder / name).resolve().is_relative_to(folder)
        archive.extractall(folder)
    exe = folder / "ucl.exe"
    env = dict(os.environ, PATH=str(Path(os.environ["SystemRoot"]) / "System32"))

    def run(*args):
        result = subprocess.run([str(exe), *args], cwd=folder, env=env,
                                capture_output=True, text=True, encoding="utf-8", timeout=20)
        assert result.returncode == 0, (args, result.stdout, result.stderr)
        return result.stdout

    assert run("--version").strip() == version + " Windows x64 NativeAOT"
    catalog = json.loads(run("list", "--offline", "--json"))
    assert len(catalog) == count
    project = folder / "IndependentZipProject"
    (project / "Assets").mkdir(parents=True)
    (project / "ProjectSettings").mkdir()
    (project / "Packages").mkdir()
    (project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 2022.3.62f3c1\n")
    (project / "Packages/manifest.json").write_text('{"dependencies":{}}')
    run("install", "--project", str(project), "--json")
    bridge = project / "Packages/com.unitycompat.cli"
    assert (bridge / "Editor/Compat/PipeBridge.cs").is_file()
    assert json.loads((bridge / "package.json").read_text())["version"] == version
    assert len(list((bridge / "Editor/Compiler").glob("*.dll"))) == 8
    assert (folder / "frontend-catalog.json").is_file()
    assert (folder / "LICENSE-dotnet.txt").is_file()
    assert (folder / "THIRD-PARTY-NOTICES-dotnet.txt").is_file()

result = {"release": str(release), "version": version, "offlineCommands": count,
          "standaloneBridgeInstall": "passed", "pythonOnChildPath": False,
          "sha256": hashlib.sha256(release.read_bytes()).hexdigest()}
(root / "artifacts/zip-smoke-v3.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result))

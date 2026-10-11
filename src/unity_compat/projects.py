from __future__ import annotations
import json
import os
import re
import shutil
import subprocess
import time
import uuid
from pathlib import Path
from .protocol import CliError, atomic_json, read_json

PACKAGE_NAME = "com.unitycompat.cli"


def version(project: Path) -> str:
    text = (project / "ProjectSettings/ProjectVersion.txt").read_text(encoding="utf-8-sig")
    match = re.search(r"m_EditorVersion:\s*(\S+)", text)
    if not match:
        raise CliError("INVALID_PROJECT_VERSION", "ProjectVersion.txt contains no Unity version.", exit_code=4)
    return match[1]


def check_version(value: str) -> None:
    match = re.match(r"(\d+)\.(\d+)", value)
    if not match or tuple(map(int, match.groups())) < (2021, 3):
        raise CliError("UNSUPPORTED_UNITY", "The initial compatibility floor is Unity 2021.3 LTS.", exit_code=4)


def install(project: Path) -> dict:
    check_version(version(project))
    source = Path(__file__).parent / "bridge"
    manifest_path = project / "Packages/manifest.json"
    manifest = read_json(manifest_path) if manifest_path.exists() else {"dependencies": {}}
    if not isinstance(manifest.get("dependencies"), dict):
        raise CliError("INVALID_MANIFEST", "Packages manifest dependencies must be an object.", exit_code=4)
    deps = manifest["dependencies"]
    package = read_json(source / "package.json")
    for key, value in package["dependencies"].items():
        deps.setdefault(key, value)  # Preserve the project's already-selected package versions.
    deps[PACKAGE_NAME] = package["version"]
    target = project / "Packages" / PACKAGE_NAME
    if target.exists():
        if not (target / "package.json").is_file() or read_json(target / "package.json").get("name") != PACKAGE_NAME:
            raise CliError("PACKAGE_CONFLICT", "Refusing to overwrite an unrelated package directory.", exit_code=4)
    backup_dir = project / ".unity-compat-backups" / (time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8])
    backup_dir.mkdir(parents=True)
    if manifest_path.exists():
        shutil.copy2(manifest_path, backup_dir / "manifest.json")
    staging = project / "Packages" / (".ucl-stage-" + uuid.uuid4().hex)
    replaced = False
    installed_new = False
    try:
        shutil.copytree(source, staging)
        if target.exists():
            os.replace(target, backup_dir / PACKAGE_NAME)
            replaced = True
        os.replace(staging, target)
        installed_new = True
        atomic_json(manifest_path, manifest)
    except Exception:
        if installed_new and target.exists() and (target / "package.json").is_file():
            shutil.rmtree(target)
        if replaced:
            os.replace(backup_dir / PACKAGE_NAME, target)
        if (backup_dir / "manifest.json").exists():
            shutil.copy2(backup_dir / "manifest.json", manifest_path)
        raise
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    return {"installed": str(target), "version": package["version"], "backup": str(backup_dir),
            "message": "Open this project in Unity and wait for compilation, then run unity-compat status."}


def uninstall(project: Path) -> dict:
    target = project / "Packages" / PACKAGE_NAME
    if not target.is_dir() or read_json(target / "package.json").get("name") != PACKAGE_NAME:
        raise CliError("BRIDGE_NOT_INSTALLED", "No Unity Compat CLI package is installed.", exit_code=4)
    manifest_path = project / "Packages/manifest.json"
    manifest = read_json(manifest_path)
    manifest.get("dependencies", {}).pop(PACKAGE_NAME, None)
    backup_dir = project / ".unity-compat-backups" / ("uninstall-" + uuid.uuid4().hex)
    backup_dir.mkdir(parents=True)
    shutil.copy2(manifest_path, backup_dir / "manifest.json")
    os.replace(target, backup_dir / PACKAGE_NAME)
    try:
        atomic_json(manifest_path, manifest)
    except Exception:
        os.replace(backup_dir / PACKAGE_NAME, target)
        raise
    return {"uninstalled": str(target), "backup": str(backup_dir), "message": "Package files are archived, not deleted."}


def editors() -> list[dict]:
    roots = [Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Unity/Hub/Editor"]
    roots += [Path(p) for p in os.environ.get("UNITY_COMPAT_EDITOR_ROOTS", "").split(os.pathsep) if p]
    found = {}
    for root in roots:
        if root.is_dir():
            for path in root.glob("*/Editor/Unity.exe"):
                found[str(path.resolve())] = {"version": path.parents[1].name, "path": str(path.resolve())}
    return sorted(found.values(), key=lambda x: x["version"])


def open_editor(project: Path, executable: str | None, headless: bool = False) -> dict:
    expected = version(project)
    if executable:
        path = Path(executable).resolve()
    else:
        options = [e for e in editors() if e["version"] == expected]
        if not options:
            raise CliError("EDITOR_NOT_FOUND", f"No Unity {expected} installed; pass --editor.", editors(), 4)
        path = Path(options[0]["path"])
    if not path.is_file():
        raise CliError("EDITOR_NOT_FOUND", f"No executable at {path}", exit_code=4)
    log = project / "Library/UnityCompat/editor.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    args = [str(path), "-projectPath", str(project), "-logFile", str(log)]
    if headless:
        args += ["-batchmode", "-nographics"]  # No -quit: mailbox needs the update loop.
    child = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" and headless else 0)
    return {"pid": child.pid, "editor": str(path), "log": str(log), "headless": headless}

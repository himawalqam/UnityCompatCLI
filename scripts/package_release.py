"""Package an already-built wheel and docs without network publishing."""
import json
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
dist = root / "dist"
version = json.loads((root / "src/unity_compat/bridge/package.json").read_text(encoding="utf-8"))["version"]
wheel = dist / f"unity_compat_cli-{version}-py3-none-any.whl"
if not wheel.is_file(): raise SystemExit("First run: python -m pip wheel . --no-deps -w dist")
with zipfile.ZipFile(wheel) as archive:
    names = archive.namelist()
    required = ["unity_compat/bridge/Editor/Compat/MailboxBridge.cs", "unity_compat/bridge/LICENSE-Coplay.txt",
                "unity_compat/bridge/package.json", "unity_compat/catalog.json",
                "unity_compat/bin/win-x64/ucl.exe", "unity_compat/bin/win-x64/LICENSE-dotnet.txt",
                "unity_compat/bin/win-x64/THIRD-PARTY-NOTICES-dotnet.txt"]
    required += ["unity_compat/frontend-catalog.json", "unity_compat/bridge/agent-schemas.json",
        "unity_compat/bridge/Editor/Compiler/UnityCompat.Microsoft.CodeAnalysis.CSharp.dll",
        "unity_compat/bridge/Editor/Compiler/LICENSE-Microsoft.txt", "unity_compat/bridge/Editor/Compiler/provenance.json"]
    for name in required:
        if name not in names: raise SystemExit("Wheel is missing: " + name)
    package = json.loads(archive.read("unity_compat/bridge/package.json"))
    if package["name"] != "com.unitycompat.cli": raise SystemExit("Wrong package in wheel")
release = dist / f"UnityCompatCLI-{version}-Windows.zip"
with zipfile.ZipFile(release, "w", zipfile.ZIP_DEFLATED) as archive:
    for file in [wheel, root / "README.md", root / "LICENSE", root / "THIRD_PARTY_NOTICES.md"]:
        archive.write(file, file.name)
    for folder in ("docs", "examples"):
        for file in (root / folder).rglob("*"):
            if file.is_file(): archive.write(file, file.relative_to(root))
    package_root = root / "src/unity_compat"
    for file in (package_root / "bin/win-x64").iterdir():
        if file.is_file(): archive.write(file, file.name)
    archive.write(package_root / "catalog.json", "catalog.json")
    archive.write(package_root / "frontend-catalog.json", "frontend-catalog.json")
    for file in (package_root / "bridge").rglob("*"):
        if file.is_file(): archive.write(file, file.relative_to(package_root))
print(json.dumps({"wheel": str(wheel), "release": str(release), "size": release.stat().st_size}))

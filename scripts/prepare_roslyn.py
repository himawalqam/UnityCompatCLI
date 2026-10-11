"""Fetch pinned MIT Roslyn assemblies, then relocate identities/namespaces with Cecil.

No binaries or source from Unity Pipeline are redistributed. Download hashes are recorded.
"""
import concurrent.futures
import hashlib
import json
import subprocess
import time
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / "research/nuget"
PACKAGES = {
    "microsoft.codeanalysis.common": ("3.8.0", "netstandard2.0", "Microsoft.CodeAnalysis.dll"),
    "microsoft.codeanalysis.csharp": ("3.8.0", "netstandard2.0", "Microsoft.CodeAnalysis.CSharp.dll"),
    "system.collections.immutable": ("5.0.0", "netstandard2.0", "System.Collections.Immutable.dll"),
    "system.reflection.metadata": ("5.0.0", "netstandard2.0", "System.Reflection.Metadata.dll"),
    "system.memory": ("4.5.4", "netstandard2.0", "System.Memory.dll"),
    "system.buffers": ("4.5.1", "netstandard2.0", "System.Buffers.dll"),
    "system.runtime.compilerservices.unsafe": ("4.7.1", "netstandard2.0", "System.Runtime.CompilerServices.Unsafe.dll"),
    "system.text.encoding.codepages": ("4.5.1", "netstandard2.0", "System.Text.Encoding.CodePages.dll"),
    "mono.cecil": ("0.11.6", "netstandard2.0", "Mono.Cecil.dll"),
}


def fetch(item):
    name, (version, framework, dll) = item
    folder = CACHE / name
    folder.mkdir(parents=True, exist_ok=True)
    url = f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg"
    archive = folder / "package.nupkg"
    if not archive.exists():
        for attempt in range(4):
            try:
                archive.write_bytes(urllib.request.urlopen(url, timeout=30).read())
                break
            except OSError:
                if attempt == 3:
                    raise
                time.sleep(1)
    with zipfile.ZipFile(archive) as z:
        for member in z.namelist():
            assert (folder / member).resolve().is_relative_to(folder.resolve())
        z.extractall(folder)
    assert (folder / f"lib/{framework}/{dll}").is_file()
    return {"name": name, "version": version, "url": url,
            "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "license": "MIT"}


with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    records = list(pool.map(fetch, PACKAGES.items()))
out = ROOT / "src/unity_compat/bridge/Editor/Compiler"
out.mkdir(exist_ok=True)
subprocess.run(["dotnet", "run", "--project", str(ROOT / "native/RelocateRoslyn"), "-c", "Release", "--", str(CACHE), str(out)], check=True)
(out / "provenance.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
license_text = (ROOT / "src/unity_compat/bin/win-x64/LICENSE-dotnet.txt").read_text(encoding="utf-8")
(out / "LICENSE-Microsoft.txt").write_text(license_text, encoding="utf-8")
for record in records:
    folder = CACHE / record["name"]
    for name in ("ThirdPartyNotices.rtf", "THIRD-PARTY-NOTICES.TXT", "ThirdPartyNotices.txt", "LICENSE.txt"):
        if (folder / name).is_file():
            (out / (record["name"] + "-" + name)).write_bytes((folder / name).read_bytes())
print(json.dumps({"assemblies": 8, "destination": str(out)}))

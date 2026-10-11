"""Compile both bridge assemblies against installed Unity assemblies without editing a project."""
import argparse
import subprocess
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument("--editor", required=True, help="Unity installation's Editor directory")
p.add_argument("--reference-project", required=True, help="Read-only project with TestRunner/package assemblies")
args = p.parse_args()
root = Path(__file__).resolve().parents[1]
editor = Path(args.editor)
data = editor / "Data"
out = root / "artifacts/compile"
out.mkdir(parents=True, exist_ok=True)
compiler = data / "DotNetSdkRoslyn/csc.dll"
framework = data / "UnityReferenceAssemblies/unity-4.8-api"
references = list(framework.glob("*.dll")) + list((framework / "Facades").glob("*.dll"))
references += list((data / "Managed/UnityEngine").glob("*.dll")) + list((data / "Managed/UnityEditor").glob("*.dll"))
references += [data / "Managed/Newtonsoft.Json.dll"]
assemblies = Path(args.reference_project) / "Library/ScriptAssemblies"
references += [assemblies / "UnityEditor.TestRunner.dll", assemblies / "UnityEngine.TestRunner.dll"]
references += list((Path(args.reference_project) / "Library/PackageCache").glob("com.unity.ext.nunit*/net35/unity-custom/nunit.framework.dll"))
references = list(dict.fromkeys(x.resolve() for x in references if x.is_file()))
bridge = root / "src/unity_compat/bridge"
references += list((bridge / "Editor/Compiler").glob("*.dll"))
for name, files in [
    ("UnityCompatVendor.Runtime", list((bridge / "Runtime").rglob("*.cs"))),
    ("UnityCompatVendor.Editor", [x for x in (bridge / "Editor").rglob("*.cs") if "Compat" not in x.relative_to(bridge / "Editor").parts]),
    ("UnityCompat.Editor", list((bridge / "Editor/Compat").glob("*.cs"))),
]:
    output = out / (name + ".dll")
    response = out / (name + ".rsp")
    flags = ["-nologo", "-utf8output", "-target:library", "-langversion:9.0", "-nostdlib+", "-define:UNITY_EDITOR;UNITY_EDITOR_WIN;UNITY_2021_3_OR_NEWER;UNITY_2022_1_OR_NEWER;UNITY_2022_2_OR_NEWER;UNITY_2022_3_OR_NEWER", f'-out:"{output}"']
    flags += [f'-r:"{x}"' for x in references]
    flags += [f'"{x}"' for x in files]
    response.write_text("\n".join(flags), encoding="utf-8")
    result = subprocess.run([str(data / "NetCoreRuntime/dotnet.exe"), str(compiler), "@" + str(response)], text=True, encoding="utf-8", errors="replace", capture_output=True)
    (out / (name + ".log")).write_text(result.stdout + result.stderr, encoding="utf-8")
    errors = [x for x in (result.stdout + result.stderr).splitlines() if "error " in x]
    print(f"{name}: {len(files)} sources; exit {result.returncode}; {len(errors)} errors")
    print("\n".join(errors[:40]))
    if result.returncode:
        raise SystemExit(result.returncode)
    references.append(output)

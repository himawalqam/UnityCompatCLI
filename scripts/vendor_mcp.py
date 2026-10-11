"""Reproducible, explicit vendoring from a local Coplay v10 package; never edits it."""
import hashlib
import json
import re
import sys
from pathlib import Path
from urllib.request import urlopen

root = Path(__file__).resolve().parents[1]
source = Path(sys.argv[1]).resolve()
dest = root / "src/unity_compat/bridge"
if dest.exists():
    raise SystemExit("Refusing to overwrite an existing bridge; use a clean checkout")
records = []
catalog = []
keep_initializers = {
    "EditorStateCache.cs", "TestRunnerService.cs", "TestRunnerNoThrottle.cs",
    "AssetGenJobManager.cs", "ProjectIdentityUtility.cs", "McpLogRecord.cs",
    "ExecuteCode.cs", "UnityReflect.cs", "GetMenuItems.cs",
}
for path in sorted(source.rglob("*")):
    if not path.is_file() or path.suffix == ".meta":
        continue
    rel = path.relative_to(source)
    if path.suffix not in {".cs", ".asmdef", ".uxml", ".uss", ".json", ".md"}:
        continue
    original = path.read_bytes()
    text = original.decode("utf-8-sig")
    if path.suffix == ".cs":
        for match in re.finditer(r'\[McpForUnity(Tool|Resource)\("([^"]+)"([^\]]*)\)\]', text):
            group = re.search(r'Group\s*=\s*"([^"]+)"', match[3])
            catalog.append({"name": match[2], "kind": match[1].lower(),
                            "group": group[1] if group else "core",
                            "source": str(rel).replace("\\", "/"),
                            "requiresPolling": "RequiresPolling = true" in match[3],
                            "actions": sorted(set(re.findall(r'case\s+"([^"\n]+)"\s*:', text)))})
        if path.name not in keep_initializers:
            text = re.sub(r'\[(?:UnityEditor\.)?InitializeOnLoad(?:Method)?\]',
                          '/* UnityCompat: upstream auto-start disabled */', text)
        text = re.sub(r'\[MenuItem\([^\n]*\)\]',
                      '/* UnityCompat: upstream menus disabled */', text)
        # Never permit legacy transport auto-start, even if another handler references it.
        if path.name == "StdioBridgeHost.cs":
            text = re.sub(r'(private static bool ShouldAutoStartBridge\(\)\s*\{)',
                          r'\1\n            return false;\n#pragma warning disable CS0162', text)
    text = text.replace("MCPForUnity", "UnityCompatVendor")
    text = text.replace("com.coplaydev.unity-mcp", "com.unitycompat.cli")
    target = dest / rel
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding="utf-8", newline="\n")
    records.append({"path": str(rel).replace("\\", "/"), "sha256": hashlib.sha256(original).hexdigest()})

package = json.loads((dest / "package.json").read_text(encoding="utf-8"))
package.update(name="com.unitycompat.cli", version="0.1.0", displayName="Unity Compat CLI",
               unity="2021.3", description="Local file mailbox CLI automation; no MCP server required.",
               author={"name": "Unity Compat CLI contributors"})
package.pop("documentationUrl", None)
package.pop("licensesUrl", None)
(dest / "package.json").write_text(json.dumps(package, indent=2) + "\n", encoding="utf-8")
license_text = urlopen("https://raw.githubusercontent.com/CoplayDev/unity-mcp/v10.0.0/LICENSE", timeout=20).read().decode()
(dest / "LICENSE-Coplay.txt").write_text(license_text, encoding="utf-8")
(root / "THIRD_PARTY_NOTICES.md").write_text(
    "# Third-party notices\n\nThe bridge vendors CoplayDev/unity-mcp v10.0.0 (MIT).\n"
    "Source: https://github.com/CoplayDev/unity-mcp/tree/v10.0.0/MCPForUnity\n\n"
    "Imported from the user's embedded package, including its documented WebSocket cancellation patch.\n"
    "Changes: renamed namespaces/assemblies/package; disabled upstream transport, setup, migrations,\n"
    "client-config rewriting and UI auto-start/menu hooks; added an independent file mailbox and commands.\n"
    "Legacy network code remains dormant to satisfy dependencies of upstream tool helpers;\n"
    "the CLI never calls it. Online asset generation still needs its provider credentials.\n\n"
    "Upstream license: `src/unity_compat/bridge/LICENSE-Coplay.txt`.\n"
    "Original file hashes: `docs/vendor-provenance.json`.\n", encoding="utf-8")
(root / "docs").mkdir(exist_ok=True)
(root / "docs/vendor-provenance.json").write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
(root / "src/unity_compat/catalog.json").write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"files": len(records), "tools": sum(x["kind"] == "tool" for x in catalog),
                  "resources": sum(x["kind"] == "resource" for x in catalog)}))

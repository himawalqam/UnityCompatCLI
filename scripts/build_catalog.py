"""Rebuild the offline command inventory. Source-derived argument/action hints are not schemas."""
import json
import re
from pathlib import Path

root = Path(__file__).resolve().parents[1]
bridge = root / "src/unity_compat/bridge"
entries = []
for path in sorted(bridge.rglob("*.cs")):
    text = path.read_text(encoding="utf-8-sig")
    matches = list(re.finditer(r'\[(McpForUnityTool|McpForUnityResource|CliCommand)\("([^"]+)"([^\]]*)\)\]', text))
    for index, match in enumerate(matches):
        section = text[match.end():matches[index + 1].start() if index + 1 < len(matches) else len(text)]
        group = re.search(r'Group\s*=\s*"([^"]+)"', match[3])
        description = re.match(r',\s*"([^"]+)"', match[3])
        kind = {"McpForUnityTool": "tool", "McpForUnityResource": "resource", "CliCommand": "extension"}[match[1]]
        entries.append({"name": match[2], "kind": kind, "group": group[1] if group else ("resources" if kind == "resource" else "core"),
            "description": description[1] if description else match[2],
            "source": str(path.relative_to(bridge)).replace("\\", "/"),
            "requiresPolling": "RequiresPolling = true" in match[3],
            "actionHints": sorted(set(re.findall(r'case\s+"([^"\n]+)"\s*:', section) +
                re.findall(r'action\s*[!=]=\s*"([^"\n]+)"', section) +
                re.findall(r'const string Action\w*\s*=\s*"([^"\n]+)"', section))),
            "argumentHints": sorted(set(re.findall(r'(?:@?params|p|cmd)\["([^"]+)"\]|(?:p\.Get(?:Required|Bool|Int|Float|StringArray)?\(|Args\.Required\(p,\s*)"([^"]+)"', section)))})
        entries[-1]["argumentHints"] = sorted(set(a or b for a, b in entries[-1]["argumentHints"]))
entries += json.loads((root / "src/unity_compat/frontend-catalog.json").read_text(encoding="utf-8"))
schemas = json.loads((bridge / "agent-schemas.json").read_text(encoding="utf-8"))
for entry in entries:
    if entry["name"] in schemas: entry["inputSchema"] = schemas[entry["name"]]
entries.sort(key=lambda x: x["name"])
assert len({x["name"] for x in entries}) == len(entries), "Duplicate command names in source catalog"
text = json.dumps(entries, ensure_ascii=False, indent=2) + "\n"
(root / "src/unity_compat/catalog.json").write_text(text, encoding="utf-8")
(bridge / "catalog.json").write_text(text, encoding="utf-8")
print(json.dumps({"commands": len(entries), "tools": sum(x["kind"] == "tool" for x in entries),
                  "resources": sum(x["kind"] == "resource" for x in entries), "extensions": sum(x["kind"] == "extension" for x in entries)}))

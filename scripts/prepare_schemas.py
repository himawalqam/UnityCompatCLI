"""Maintain shared compact query schemas and native frontend metadata."""
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
package = root / "src/unity_compat"
string = {"type": "string"}
target = {"type": ["integer", "string"]}
fields = {"oneOf": [string, {"type": "array", "items": string}], "description": "Comma-separated serialized names or an array; nested.path and parent.* are supported"}
def schema(properties, required=()):
    return {"type": "object", "properties": properties, "required": list(required), "additionalProperties": False}
hierarchy = schema({"root": target, "depth": {"type": "integer", "minimum": 0, "maximum": 64, "default": 2},
    "limit": {"type": "integer", "minimum": 1, "maximum": 10000, "default": 80}, "components": {"type": "boolean"}, "fields": fields})
inspect = schema({"target": target, "path": string, "component": string, "fields": fields,
    "all": {"type": "boolean"}, "types": {"type": "boolean"}, "max_depth": {"type": "integer", "minimum": 0, "maximum": 32},
    "limit": {"type": "integer", "minimum": 1, "maximum": 2000, "default": 80}})
inspect["anyOf"] = [{"required": ["target"]}, {"required": ["path"]}]
setter = schema({"target": target, "path": string, "component": string, "property": string, "value": {}, "expect": {}}, ["property", "value"])
setter["anyOf"] = inspect["anyOf"]
schemas = {"agent_hierarchy": hierarchy, "agent_snapshot": hierarchy, "agent_inspect": inspect, "agent_set": setter,
    "agent_find": schema({"query": string, "type": string, "limit": {"type": "integer", "minimum": 1, "maximum": 10000, "default": 30}}),
    "eval": schema({"code": string, "args": {"type": "object"}, "metrics": {"type": "boolean"}}, ["code"])}
(package / "bridge/agent-schemas.json").write_text(json.dumps(schemas, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
old = json.loads((root / "artifacts/benchmark-20261011/tools-list.json").read_text(encoding="utf-8"))["tools"]
front = []
descriptions = {
    "debug_request_context": "Read native project, transport, profile and version; no secrets",
    "manage_tools": "Manage this native MCP session's visible command groups; call tools/list afterward",
    "set_active_instance": "Select an absolute Unity project path for this MCP session; ports and legacy hashes are unsupported",
    "unity_docs": "Fetch official ScriptReference, Manual or package documentation; get_doc/get_manual/get_package_doc/lookup; bounded text and exact candidate URLs",
}
for item in old:
    if item["name"] not in descriptions: continue
    front.append({"name": item["name"], "description": descriptions[item["name"]], "kind": "frontend",
        "group": "docs" if item["name"] == "unity_docs" else "cli", "source": "native/Ucl/Frontend.cs", "inputSchema": item["inputSchema"]})
    for value in front[-1]["inputSchema"]["properties"].values(): value.pop("description", None)
    if item["name"] == "set_active_instance": front[-1]["inputSchema"]["properties"]["instance"]["description"] = "Absolute project path"
(package / "frontend-catalog.json").write_text(json.dumps(front, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

"""Interoperability with the official MCP Python SDK, not a hand-built protocol client."""
import asyncio
import importlib.metadata
import json
from pathlib import Path
from mcp import Client, StdioServerParameters

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "validation/Unity2022"
EXE = ROOT / "src/unity_compat/bin/win-x64/ucl.exe"


async def main():
    params = StdioServerParameters(command=str(EXE), args=["mcp", "--project", str(PROJECT), "--profile", "full"])
    async with Client(params) as client:
        tools = (await client.list_tools()).tools
        names = [t.name for t in tools]
        assert "fixture_method" in names and "eval" in names
        typed = next(t for t in tools if t.name == "fixture_method")
        schema = typed.input_schema
        assert schema["properties"]["count"]["type"] == "integer"
        assert schema["required"] == ["message"]
        result = await client.call_tool("fixture_method", {"message": "中文 SDK", "count": 7})
        assert result.structured_content == {"text": "中文 SDK", "count": 7}
        result = await client.call_tool("eval", {"code": "return 6 * 7;"})
        assert result.structured_content == {"data": 42}
        result = await client.call_tool("agent_inspect", {"target": 2147483647})
        assert result.is_error and result.structured_content["error"]["code"] == "TARGET_NOT_FOUND"
        resources = (await client.list_resources()).resources
        assert len(resources) == 19
        templates = (await client.list_resource_templates()).resource_templates
        assert len(templates) == 19
        result = await client.read_resource("unitycompat://resource/get_selection")
        assert result.contents
    record = {"sdk": importlib.metadata.version("mcp"), "tools": len(names),
              "typedSchema": "passed", "unicodeCall": "passed", "eval": "passed", "errors": "passed",
              "resources": 19, "templates": 19, "resourceRead": "passed"}
    (ROOT / "artifacts/mcp-sdk-v3.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
    print(json.dumps(record))


asyncio.run(main())

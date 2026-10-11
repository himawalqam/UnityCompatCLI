"""Identical 500-child hierarchy responses, serial and concurrent, in the owned fixture."""
import concurrent.futures
import json
import math
import statistics
import threading
import time
from pathlib import Path
from probe_mcp import McpClient
from benchmark_reads import unwrap_mcp
from stdio_client import StdioClient

ROOT=Path(__file__).resolve().parents[1]
PROJECT=ROOT/"validation/Unity2022"
OUT=ROOT/"artifacts/benchmark-v3-20261011/Unity2022/large.json"
old=McpClient();new=StdioClient(ROOT/"src/unity_compat/bin/win-x64/ucl.exe",PROJECT)
def payload(x):
    while isinstance(x,dict) and "success" in x and "data" in x:x=x["data"]
    return x
def p95(x):return sorted(x)[math.ceil(.95*len(x))-1]
records=[];bursts=[];root=None
try:
    instances=unwrap_mcp(old.rpc("resources/read",{"uri":"mcpforunity://instances"}))
    instance=next(x for x in instances["instances"] if x["name"]==PROJECT.name)
    old.rpc("tools/call",{"name":"set_active_instance","arguments":{"instance":instance["id"]}})
    root=new.command("eval",{"code":"var root = new UnityEngine.GameObject(\"V3LargeFixture\"); for (int i=0;i<500;i++) {var child=new UnityEngine.GameObject(\"Node\"+i); child.transform.SetParent(root.transform);} return root.GetInstanceID();"})["structuredContent"]["data"]
    cp={"action":"get_hierarchy","parent":root,"pageSize":500,"maxNodes":500}
    mp={"name":"manage_scene","arguments":{"action":"get_hierarchy","parent":root,"page_size":500,"max_nodes":500}}
    a=payload(unwrap_mcp(old.rpc("tools/call",mp)));b=payload(unwrap_mcp(new.command("manage_scene",cp)))
    assert a==b and len(a["items"])==500
    for n in range(20):
        for route in ["old_mcp","stdio"] if n%2 else ["stdio","old_mcp"]:
            start=time.perf_counter()
            result=old.rpc("tools/call",mp) if route=="old_mcp" else new.command("manage_scene",cp)
            assert len(payload(unwrap_mcp(result))["items"])==500
            records.append({"route":route,"round":n,"ms":(time.perf_counter()-start)*1000})
    lock=threading.Lock()
    def legacy():
        with lock:old.seq+=1;id=old.seq
        response=old.s.post("http://127.0.0.1:8080/mcp",json={"jsonrpc":"2.0","id":id,"method":"tools/call","params":mp},timeout=40)
        response.raise_for_status();response.encoding="utf-8"
        body=next(json.loads(x[5:].strip()) for x in response.text.splitlines() if x.startswith("data:")) if "text/event-stream" in response.headers.get("content-type","") else response.json()
        assert body["id"]==id and len(payload(unwrap_mcp(body["result"]))["items"])==500
    for n in range(3):
        for route in ["old_mcp","stdio"] if n%2 else ["stdio","old_mcp"]:
            def query(_):
                start=time.perf_counter()
                if route=="old_mcp":legacy()
                else:assert len(payload(unwrap_mcp(new.command("manage_scene",cp)))["items"])==500
                return (time.perf_counter()-start)*1000
            start=time.perf_counter()
            with concurrent.futures.ThreadPoolExecutor(max_workers=24) as pool:times=list(pool.map(query,range(24)))
            bursts.append({"route":route,"round":n,"totalMs":(time.perf_counter()-start)*1000,"p50":statistics.median(times),"p95":p95(times),"individualMs":times})
    summary=[{"route":route,"n":20,"p50":statistics.median([r["ms"] for r in records if r["route"]==route]),"p95":p95([r["ms"] for r in records if r["route"]==route])} for route in ["old_mcp","stdio"]]
    OUT.write_text(json.dumps({"nodes":500,"sameSemantic":True,"records":records,"summary":summary,"bursts":bursts},indent=2),encoding="utf-8")
    print(json.dumps({"summary":summary,"bursts":[{k:v for k,v in r.items() if k!="individualMs"} for r in bursts]}))
finally:
    if root is not None:new.command("manage_gameobject",{"action":"delete","target":root})
    new.close();old.close()

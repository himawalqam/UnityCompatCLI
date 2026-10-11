"""Alternating identical read tasks plus compact workflows; no LLM inference is measured."""
import concurrent.futures
import argparse
import json
import math
import statistics
import subprocess
import threading
import time
from pathlib import Path
import requests
import tiktoken
from probe_mcp import McpClient
from benchmark_reads import unwrap_mcp
from stdio_client import StdioClient

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument("--project",default="D:/unityProject/Beach-Tank")
parser.add_argument("--resume",action="store_true")
args=parser.parse_args()
PROJECT=Path(args.project)
EXE=ROOT/"src/unity_compat/bin/win-x64/ucl.exe"
OUT=ROOT/"artifacts/benchmark-v3-20261011"/PROJECT.name
OUT.mkdir(parents=True,exist_ok=True)
ENC=tiktoken.get_encoding("o200k_base")
ROUNDS=20
records=[];samples={};summaries=[]
if args.resume:
    records=json.loads((OUT/"records.json").read_text(encoding="utf-8"))
    samples=json.loads((OUT/"samples.json").read_text(encoding="utf-8"))
    summaries=json.loads((OUT/"summary.json").read_text(encoding="utf-8"))
def compact(value): return json.dumps(value,ensure_ascii=False,separators=(',',':'))
def payload(value):
    for _ in range(5):
        if not isinstance(value,dict) or value.get("success") is False or "data" not in value: break
        if "success" not in value: break
        value=value["data"]
    return value
def stable(value):
    if isinstance(value,dict):return {k:stable(v) for k,v in value.items() if k not in {"focused","isFocused","heartbeat","observed_at_unix_ms","sequence","timestamp","time","ageMs","cacheAgeMs"}}
    if isinstance(value,list):return [stable(x) for x in value]
    return value
def native(command,params=None,direct=False):
    p=subprocess.run([str(EXE),"command",command,"--params",compact(params or {}),"--project",str(PROJECT),"--json"]+(["--direct"] if direct else []),
        capture_output=True,encoding="utf-8",timeout=40)
    if p.returncode:raise RuntimeError(p.stderr)
    return json.loads(p.stdout),p.stdout
def daemon(action):
    p=subprocess.run([str(EXE),"daemon",action,"--project",str(PROJECT),"--json"],capture_output=True,encoding="utf-8",timeout=8)
    if p.returncode:raise RuntimeError(p.stderr)
def p95(values):return sorted(values)[max(0,math.ceil(.95*len(values))-1)]
def save():
    for name,data in [("records",records),("samples",samples),("summary",summaries)]:
        (OUT/(name+".json")).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding="utf-8")
def summary(case,route):
    rows=[r["ms"] for r in records if r["case"]==case and r["route"]==route]
    return {"case":case,"route":route,"n":len(rows),"p50":round(statistics.median(rows),2),"p95":round(p95(rows),2)}

old=McpClient();stdio=StdioClient(EXE,PROJECT)
shell=subprocess.Popen([str(EXE),"shell","--project",str(PROJECT),"--json"],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding="utf-8",bufsize=1)
try:
    instances=unwrap_mcp(old.rpc("resources/read",{"uri":"mcpforunity://instances"}))
    target=next(x for x in instances["instances"] if x["name"]==PROJECT.name)
    old.rpc("tools/call",{"name":"set_active_instance","arguments":{"instance":target["id"]}})
    before,_=native("manage_scene",{"action":"get_active"},True)
    component="BeachMainMenu" if PROJECT.name=="Beach-Tank" else "Camera"
    found,_=native("agent_find",{"type":component},True)
    id=found["objects"][0]["id"]
    daemon("start")
    cases=[
        ("windows","get_windows",{},"resources/read",{"uri":"mcpforunity://editor/windows"}),
        ("hierarchy","manage_scene",{"action":"get_hierarchy","pageSize":50},"tools/call",{"name":"manage_scene","arguments":{"action":"get_hierarchy","page_size":50}}),
        ("full_inspector","get_gameobject_component",{"instanceID":id,"componentName":component},"resources/read",{"uri":f"mcpforunity://scene/gameobject/{id}/component/{component}"}),
        ("console","read_console",{"action":"get","count":10,"types":["error"],"format":"json"},"tools/call",{"name":"read_console","arguments":{"action":"get","count":10,"types":["error"],"format":"json"}}),
    ]
    routes=["old_mcp","oneshot","daemon","shell","stdio"]
    for label,command,params,method,mp in cases:
        if args.resume and len([r for r in summaries if r["case"]==label])==5: continue
        for n in range(-1,ROUNDS):
            for route in routes if n%2 else list(reversed(routes)):
                start=time.perf_counter()
                if route=="old_mcp":raw=old.rpc(method,mp);value=payload(unwrap_mcp(raw));rendered="\n".join(x.get("text","") for x in raw.get("content",raw.get("contents",[])))
                elif route=="stdio":raw=stdio.command(command,params);value=payload(unwrap_mcp(raw));rendered=raw["content"][0]["text"]
                elif route=="shell":
                    shell.stdin.write("command "+command+" --params '"+compact(params)+"'\n");shell.stdin.flush()
                    rendered=shell.stdout.readline();raw=json.loads(rendered);value=payload(raw)
                else:raw,rendered=native(command,params,route=="oneshot");value=payload(raw)
                ms=(time.perf_counter()-start)*1000
                if n<0:samples[label+"-"+route]={"response":raw,"semantic":value,"rendered":rendered,"outputTokens":len(ENC.encode(rendered))}
                else:records.append({"case":label,"route":route,"round":n,"ms":ms})
        for route in routes:
            assert stable(samples[label+"-"+route]["semantic"])==stable(samples[label+"-old_mcp"]["semantic"]),(label,route)
            summaries.append(summary(label,route))
        print(label,compact(summaries[-5:]),flush=True);save()
    # A task asking only four references: verify these equal the full old resource.
    fields=["characters","startButton","chapterMenu","resumePanel"] if component=="BeachMainMenu" else ["m_ClearFlags","m_BackGroundColor","near clip plane","far clip plane"]
    for n in range(-1,ROUNDS):
        for route in ["old_mcp","projected_stdio"] if n%2 else ["projected_stdio","old_mcp"]:
            start=time.perf_counter()
            if route=="old_mcp":raw=old.rpc(cases[2][3],cases[2][4]);rendered="\n".join(x["text"] for x in raw["contents"]);value=payload(unwrap_mcp(raw))
            else:raw=stdio.command("agent_inspect",{"target":id,"component":component,"fields":fields});rendered=raw["content"][0]["text"];value=json.loads(rendered)
            if n<0:samples["four_fields-"+route]={"semantic":value,"rendered":rendered,"outputTokens":len(ENC.encode(rendered))}
            else:records.append({"case":"four_fields","route":route,"round":n,"ms":(time.perf_counter()-start)*1000})
    source=samples["four_fields-old_mcp"]["semantic"]["component"]["properties"]
    for field,value in (samples["four_fields-projected_stdio"]["semantic"]["fields"] if component=="BeachMainMenu" else {}).items():
        other=source[field]
        if value is None:assert other is None
        elif isinstance(other,str):assert value["asset"]==other
        else:assert value["id"]==other.get("instanceID",other.get("id")) and value["name"]==other["name"]
    if component=="Camera":
        projected=samples["four_fields-projected_stdio"]["semantic"]["fields"]
        clear=source["clearFlags"]
        assert projected["m_ClearFlags"]==({1:"Skybox",2:"SolidColor",3:"Depth",4:"Nothing"}.get(clear,clear))
        assert math.isclose(projected["near clip plane"],source["nearClipPlane"],rel_tol=1e-6)
        assert math.isclose(projected["far clip plane"],source["farClipPlane"],rel_tol=1e-6)
        assert all(math.isclose(projected["m_BackGroundColor"][i],source["backgroundColor"][k],rel_tol=1e-6) for i,k in enumerate("rgba"))
    summaries += [summary("four_fields",r) for r in ["old_mcp","projected_stdio"]];save()
    # Identical code result, explicit cold/new/cache distinctions.
    code="return new { version = UnityEngine.Application.unityVersion, roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().rootCount };"
    cold=time.perf_counter();result=stdio.command("eval",{"code":code,"metrics":True});cold=(time.perf_counter()-cold)*1000
    cold_metrics=result["structuredContent"]
    for n in range(10):
        for route in ["old_mcp","roslyn_cached"] if n%2 else ["roslyn_cached","old_mcp"]:
            start=time.perf_counter()
            result=old.rpc("tools/call",{"name":"execute_code","arguments":{"action":"execute","code":code}}) if route=="old_mcp" else stdio.command("eval",{"code":code})
            if route=="old_mcp":assert payload(unwrap_mcp(result))["result"]==cold_metrics["result"]
            else:assert result["structuredContent"]==cold_metrics["result"]
            records.append({"case":"eval_cached","route":route,"round":n,"ms":(time.perf_counter()-start)*1000})
    unique=[]
    for n in range(5):
        start=time.perf_counter();metrics=stdio.command("eval",{"code":code+f" // unique {n}","metrics":True})["structuredContent"]
        unique.append({"wallMs":(time.perf_counter()-start)*1000,"compileMs":metrics["compileMs"],"cacheHit":metrics["cacheHit"]})
    summaries += [summary("eval_cached",r) for r in ["old_mcp","roslyn_cached"]]
    (OUT/"eval-cold.json").write_text(json.dumps({"firstWallMs":cold,"first":cold_metrics,"newSnippets":unique},indent=2),encoding="utf-8");save()
    # Correlated JSON-RPC IDs let a single warm HTTP session receive concurrent reads.
    def old_concurrent(_):
        with old_lock:old.seq+=1;seq=old.seq
        request={"jsonrpc":"2.0","id":seq,"method":"resources/read","params":{"uri":"mcpforunity://editor/windows"}}
        start=time.perf_counter();response=old.s.post("http://127.0.0.1:8080/mcp",json=request,timeout=40);response.raise_for_status()
        response.encoding="utf-8";body=next((json.loads(x[5:].strip()) for x in response.text.splitlines() if x.startswith("data:")),None) if "text/event-stream" in response.headers.get("content-type","") else response.json()
        assert body["id"]==seq and "error" not in body
        return (time.perf_counter()-start)*1000
    old_lock=threading.Lock();bursts=[]
    for round in range(3):
        for route in ["old_mcp","stdio"] if round%2 else ["stdio","old_mcp"]:
            def call(_):
                if route=="old_mcp":return old_concurrent(_)
                start=time.perf_counter();stdio.command("get_windows",{});return (time.perf_counter()-start)*1000
            start=time.perf_counter()
            with concurrent.futures.ThreadPoolExecutor(max_workers=24) as pool:times=list(pool.map(call,range(24)))
            bursts.append({"route":route,"round":round,"n":24,"totalMs":(time.perf_counter()-start)*1000,"p50":statistics.median(times),"p95":p95(times),"individualMs":times})
    (OUT/"bursts.json").write_text(json.dumps(bursts,indent=2),encoding="utf-8")
    schemas_old=old.rpc("tools/list");schemas_new=stdio.rpc("tools/list")
    (OUT/"discovery.json").write_text(json.dumps({"oldTools":len(schemas_old["tools"]),"compactTools":len(schemas_new["tools"]),
        "oldTokens":len(ENC.encode(compact(schemas_old))),"compactTokens":len(ENC.encode(compact(schemas_new)))},indent=2),encoding="utf-8")
    after,_=native("manage_scene",{"action":"get_active"},True)
    assert stable(before)==stable(after),(before,after)
    (OUT/"environment.json").write_text(json.dumps({"project":str(PROJECT),"unity":"2022.3.62f3c1","rounds":ROUNDS,
        "evalRounds":10,"tokenizer":"o200k_base, text content only","nativeBytes":EXE.stat().st_size,"sceneBefore":before,"sceneAfter":after,
        "editorMode":"GUI" if PROJECT.name=="Beach-Tank" else "batchmode with graphics",
        "projectedFieldEquivalence":True,
        "note":"Warm Editor; alternating routes, 1 warmup excluded. Python subprocess wall time includes process startup, no PowerShell and no Agent/LLM inference/billing. New eval cache is reported separately from new snippets. GUI requests are affected by Editor tick scheduling."},indent=2),encoding="utf-8")
finally:
    daemon("stop");shell.stdin.close();shell.wait(timeout=5);shell.stdout.close();shell.stderr.close();stdio.close();old.close()
print(OUT,flush=True)

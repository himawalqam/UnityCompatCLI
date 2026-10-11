"""Alternating live MCP / Python mailbox / native pipe. Same command and semantic payload."""
import argparse
import json
import math
import statistics
import subprocess
import sys
import time
from pathlib import Path
from probe_mcp import McpClient
from benchmark_reads import unwrap_mcp

ROOT=Path(__file__).resolve().parents[1]
EXE=ROOT/'src/unity_compat/bin/win-x64/ucl.exe'
OUT=ROOT/'artifacts/benchmark-v2-20261011'
p=argparse.ArgumentParser()
p.add_argument('--project',default='D:/unityProject/Beach-Tank')
p.add_argument('--rounds',type=int,default=20)
args=p.parse_args()
PROJECT=Path(args.project)
OUT=OUT/PROJECT.name
OUT.mkdir(parents=True,exist_ok=True)

def native(command,params=None,envelope=False):
    argv=[str(EXE),'command',command,'--params',json.dumps(params or {},ensure_ascii=False),'--project',str(PROJECT),'--json','--timeout','30']
    if envelope:argv.append('--envelope')
    result=subprocess.run(argv,capture_output=True,encoding='utf-8',timeout=35)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    return json.loads(result.stdout),result.stdout,argv

def payload(raw):
    for _ in range(5):
        if not isinstance(raw,dict) or raw.get('success') is False or 'data' not in raw:break
        if 'protocol' not in raw and 'message' not in raw and len(raw)>3:break
        raw=raw['data']
    return raw

def stable(value):
    if isinstance(value,dict):return {k:stable(v) for k,v in value.items() if k not in {'focused','isFocused','heartbeat','observed_at_unix_ms','sequence','timestamp','time','ageMs','cacheAgeMs'}}
    if isinstance(value,list):return [stable(v) for v in value]
    return value

def quantile(values,q):return sorted(values)[max(0,math.ceil(len(values)*q)-1)]

client=McpClient()
records=[];samples={};summary=[]
shell=subprocess.Popen([str(EXE),'shell','--project',str(PROJECT),'--json','--timeout','30'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',bufsize=1)
try:
    instances=unwrap_mcp(client.rpc('resources/read',{'uri':'mcpforunity://instances'}))
    if PROJECT.name=='Unity2022' and not any(x['name']==PROJECT.name for x in instances['instances']):
        native('execute_code',{'action':'execute','code':'var task = MCPForUnity.Editor.Services.MCPServiceLocator.TransportManager.StartAsync(MCPForUnity.Editor.Services.Transport.TransportMode.Http); return task.Status.ToString();'})
        for _ in range(20):
            instances=unwrap_mcp(client.rpc('resources/read',{'uri':'mcpforunity://instances'}))
            if any(x['name']==PROJECT.name for x in instances['instances']):break
            time.sleep(.25)
    target=next(x for x in instances['instances'] if x['name']==PROJECT.name)
    client.rpc('tools/call',{'name':'set_active_instance','arguments':{'instance':target['id']}})
    tree,_,_=native('agent_hierarchy',{'depth':0})
    id=tree['nodes'][0]['id']
    inspector_id=id; component='Transform'
    if PROJECT.name=='Beach-Tank':
        found,_,_=native('agent_find',{'type':'BeachMainMenu'})
        if found['objects']:inspector_id=found['objects'][0]['id'];component='BeachMainMenu'
    state,_,_=native('manage_scene',{'action':'get_active'})
    (OUT/'scene-before.json').write_text(json.dumps(state,ensure_ascii=False,indent=2),encoding='utf-8')
    cases=[
        ('windows','get_windows',{},'resources/read',{'uri':'mcpforunity://editor/windows'}),
        ('selection','get_selection',{},'resources/read',{'uri':'mcpforunity://editor/selection'}),
        ('hierarchy','manage_scene',{'action':'get_hierarchy','pageSize':50},'tools/call',{'name':'manage_scene','arguments':{'action':'get_hierarchy','page_size':50}}),
        ('inspector','get_gameobject_component',{'instanceID':inspector_id,'componentName':component},'resources/read',{'uri':f'mcpforunity://scene/gameobject/{inspector_id}/component/{component}'}),
        ('find_object','find_gameobjects',{'searchTerm':tree['nodes'][0]['name'],'includeInactive':True},'tools/call',{'name':'find_gameobjects','arguments':{'search_term':tree['nodes'][0]['name'],'include_inactive':True}}),
        ('project_info','get_project_info',{},'resources/read',{'uri':'mcpforunity://project/info'}),
        ('console','read_console',{'action':'get','count':10,'types':['error'],'format':'json'},'tools/call',{'name':'read_console','arguments':{'action':'get','count':10,'types':['error'],'format':'json'}}),
        ('execute_code','execute_code',{'action':'execute','code':'return new { version = UnityEngine.Application.unityVersion, roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().rootCount };'},'tools/call',{'name':'execute_code','arguments':{'action':'execute','code':'return new { version = UnityEngine.Application.unityVersion, roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().rootCount };'}}),
    ]
    for label,command,cp,method,mp in cases:
        values={};print('START',label,flush=True)
        for n in range(-1,args.rounds):
            routes=['shell','native','python','mcp'] if n%2 else ['mcp','python','native','shell']
            for route in routes:
                started=time.perf_counter()
                try:
                    if route=='mcp':
                        raw=client.rpc(method,mp);semantic=payload(unwrap_mcp(raw));rendered=json.dumps(raw,ensure_ascii=False,separators=(',',':'));request={'method':method,'params':mp}
                    elif route=='python':
                        argv=[sys.executable,'-m','unity_compat','command',command,'--params',json.dumps(cp),'--project',str(PROJECT),'--json','--timeout','30']
                        run=subprocess.run(argv,capture_output=True,encoding='utf-8',timeout=35)
                        if run.returncode:raise RuntimeError(run.stdout+run.stderr)
                        raw=json.loads(run.stdout);semantic=payload(raw);rendered=run.stdout;request={'argv':argv}
                    elif route=='shell':
                        line='command '+command+" --params '"+json.dumps(cp,ensure_ascii=False,separators=(',',':'))+"'"
                        shell.stdin.write(line+'\n');shell.stdin.flush();rendered=shell.stdout.readline()
                        raw=json.loads(rendered);semantic=payload(raw);request={'shellLine':line}
                    else:
                        raw,rendered,argv=native(command,cp);semantic=payload(raw);request={'argv':argv}
                    ms=(time.perf_counter()-started)*1000
                    successful=not(isinstance(semantic,dict) and semantic.get('success') is False)
                    if n<0:
                        samples[f'{label}-{route}']={'request':request,'response':raw,'semantic':semantic,'rendered':rendered}
                        values[route]=semantic
                    else:records.append({'case':label,'route':route,'round':n,'ms':ms,'success':successful,'bytes':len(rendered.encode('utf-8'))})
                except Exception as e:
                    records.append({'case':label,'route':route,'round':n,'ms':(time.perf_counter()-started)*1000,'success':False,'error':str(e)})
                    print('FAIL',route,str(e)[:350],flush=True)
        comparable=stable(values.get('native'))==stable(values.get('mcp'))
        for route in ['mcp','python','native','shell']:
            rows=[r for r in records if r['case']==label and r['route']==route and r['round']>=0];times=[r['ms'] for r in rows]
            summary.append({'case':label,'route':route,'success':sum(r['success'] for r in rows),'n':len(rows),'p50':round(statistics.median(times),2),'p95':round(quantile(times,.95),2),'sameSemanticMcp':comparable})
        print('DONE',label,json.dumps(summary[-4:]),flush=True)
        for name,value in [('records',records),('samples',samples),('summary',summary)]:
            (OUT/f'{name}.json').write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
    state_after,_,_=native('manage_scene',{'action':'get_active'})
    (OUT/'scene-after.json').write_text(json.dumps(state_after,ensure_ascii=False,indent=2),encoding='utf-8')
    (OUT/'environment.json').write_text(json.dumps({'project':str(PROJECT),'rounds':args.rounds,'unity':'2022.3.62f3c1','inspectorTarget':inspector_id,'inspectorComponent':component,'scene':state,'python':sys.version,'nativeBytes':EXE.stat().st_size,'transport':{'mcp':'warm HTTP + Editor WebSocket','python':'new Python process + file mailbox','native':'new NativeAOT process + named pipe','shell':'warm NativeAOT process + named pipe'},'note':'No LLM inference/billing measured; responses captured for offline token accounting'},ensure_ascii=False,indent=2),encoding='utf-8')
finally:
    shell.stdin.close();shell.wait(timeout=5)
    # Restore this client's target to the game even when benchmarking a fixture.
    try:
        beach=next(x for x in instances['instances'] if x['name']=='Beach-Tank')
        client.rpc('tools/call',{'name':'set_active_instance','arguments':{'instance':beach['id']}})
    except Exception:pass
    client.close()
print(OUT,flush=True)

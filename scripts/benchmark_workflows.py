import json
import statistics
import subprocess
import sys
import time
from pathlib import Path
from benchmark_reads import unwrap_mcp, ok, p95
from probe_mcp import McpClient, OUT
from unity_compat.protocol import Mailbox

PROJECT = Path('D:/unityTools/UnityCompatCLI/validation/Unity2022')

def cli(command, params):
    args = [sys.executable, '-m', 'unity_compat', 'command', command, '--params', json.dumps(params),
            '--project-path', str(PROJECT), '--format', 'ndjson', '--timeout', '30']
    p = subprocess.run(args, capture_output=True, encoding='utf-8', timeout=35)
    raw = json.loads(p.stdout)
    return raw, raw.get('data', raw), p.returncode

def mcp(client, command, params):
    raw = client.rpc('tools/call', {'name': command, 'arguments': params})
    return raw, unwrap_mcp(raw), 0

def translate(p):
    mapping = {'primitiveType': 'primitive_type', 'componentType': 'component_type', 'pageSize': 'page_size',
               'searchTerm': 'search_term', 'includeInactive': 'include_inactive'}
    return {mapping.get(k, k): v for k, v in p.items()}

def main():
    client = McpClient()
    records, samples = [], {}
    def request(route, command, params):
        t = time.perf_counter()
        raw, semantic, exit_ = cli(command, params) if route == 'cli' else mcp(client, command, translate(params))
        return raw, semantic, exit_, (time.perf_counter()-t)*1000
    try:
        instances = unwrap_mcp(client.rpc('resources/read', {'uri':'mcpforunity://instances'}))
        fixture = next(x for x in instances['instances'] if x['name']=='Unity2022')
        print('SELECT', client.rpc('tools/call', {'name':'set_active_instance','arguments':{'instance':fixture['id']}}), flush=True)
        for round_ in range(10):
            for route in (['mcp','cli'] if round_%2 else ['cli','mcp']):
                name = f'AgentBench_{route}_{round_}_{int(time.time())}'
                code = 'var g=UnityEngine.GameObject.Find("' + name + '"); return new { passed=g!=null && g.GetComponent<UnityEngine.BoxCollider>()!=null && g.GetComponent<UnityEngine.AudioSource>()!=null && Math.Abs(g.GetComponent<UnityEngine.AudioSource>().volume-.25f)<.001f && g.transform.position==new UnityEngine.Vector3(3,1,4), name=g==null?null:g.name };'
                steps = [
                    ('manage_gameobject', {'action':'create','name':name,'primitiveType':'Cube','position':[1,2,3]}),
                    ('manage_components', {'action':'add','target':name,'componentType':'AudioSource'}),
                    ('manage_components', {'action':'set_property','target':name,'componentType':'AudioSource','property':'volume','value':.25}),
                    ('manage_gameobject', {'action':'modify','target':name,'position':[3,1,4]}),
                    ('execute_code', {'action':'execute','code':code}),
                    ('manage_gameobject', {'action':'delete','target':name}),
                ]
                started = time.perf_counter()
                step_records=[]
                passed=True
                try:
                    for command, params in steps:
                        raw, value, exit_, ms = request(route, command, params)
                        successful = ok(value) and exit_ == 0
                        if command=='execute_code':
                            successful = successful and value.get('data',{}).get('result',{}).get('passed') is True
                        step_records.append({'command':command,'params':params,'ms':ms,'success':successful,'response':raw})
                        passed = passed and successful
                finally:
                    # Idempotent cleanup by the independent read/execute path even if a step failed.
                    mb = Mailbox(PROJECT)
                    cleanup = 'var g=UnityEngine.GameObject.Find("'+name+'"); if(g!=null) UnityEngine.Object.DestroyImmediate(g); return true;'
                    if not passed:
                        j=mb.submit('execute_code',{'action':'execute','code':cleanup},30);mb.wait(j,30)
                elapsed=(time.perf_counter()-started)*1000
                records.append({'case':'create_modify_verify_delete','route':route,'round':round_,'ms':elapsed,'success':passed,'steps':step_records})
                if round_==0: samples[f'workflow-{route}']=step_records
                print('WORKFLOW',round_,route,round(elapsed,1),passed,flush=True)
                (OUT/'workflow-records.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')

        # Batch equal tools; both in sequential mode. This does not assume Unity main-thread parallelism.
        operations=[('manage_scene',{'action':'get_hierarchy','pageSize':50}),
                    ('find_gameobjects',{'searchTerm':'Camera','includeInactive':True}),
                    ('read_console',{'action':'get','count':5,'types':['error'],'format':'json'})]
        for round_ in range(20):
            for route in (['mcp','cli'] if round_%2 else ['cli','mcp']):
                start=time.perf_counter()
                if route=='cli':
                    raw,value,exit_=cli('cli_batch',{'operations':[{'command':c,'params':p} for c,p in operations]})
                else:
                    raw=client.rpc('tools/call',{'name':'batch_execute','arguments':{'commands':[{'tool':c,'params':translate(p)} for c,p in operations],'parallel':False,'fail_fast':True}})
                    value=unwrap_mcp(raw);exit_=0
                records.append({'case':'batch_three_reads','route':route,'round':round_,'ms':(time.perf_counter()-start)*1000,'success':ok(value) and exit_==0})
                if round_==0:samples[f'batch-{route}']={'response':raw,'semantic':value}
        failures=[('missing_parameter','find_gameobjects',{}),
                  ('unknown_action','manage_scene',{'action':'bench_invalid_action'}),
                  ('missing_target','manage_components',{'action':'add','target':'BenchMissingNeverExists','componentType':'AudioSource'}),
                  ('invalid_code','execute_code',{'action':'execute','code':'return THIS IS NOT VALID CSHARP;'}),
                  ('unknown_command','bench_unknown_command',{})]
        for label,command,params in failures:
            for route in ['cli','mcp']:
                raw,value,exit_,ms=request(route,command,params)
                correctly_rejected=not ok(value) or exit_!=0 or raw.get('isError') is True
                records.append({'case':label,'route':route,'ms':ms,'success':correctly_rejected,'expected':'rejection','response':raw,'exit':exit_})
                print('FAILURE',label,route,round(ms,1),correctly_rejected,flush=True)

        # Deliberate stress of the corrected heartbeat read: no game state mutation.
        mb=Mailbox(Path('D:/unityProject/Beach-Tank'))
        status_errors=[]
        for i in range(2000):
            try:mb.status()
            except Exception as e:status_errors.append({'i':i,'error':str(e)})
        samples['status-stress-after-fix']={'reads':2000,'errors':status_errors}
        for case in ['create_modify_verify_delete','batch_three_reads']:
            print('SUMMARY',case,[{'route':route,'n':len(rows:=[r for r in records if r['case']==case and r['route']==route]),'success':sum(r['success'] for r in rows),'p50':statistics.median(r['ms'] for r in rows),'p95':p95([r['ms'] for r in rows])} for route in ['cli','mcp']],flush=True)
        (OUT/'workflow-records.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
        (OUT/'workflow-samples.json').write_text(json.dumps(samples,ensure_ascii=False,indent=2),encoding='utf-8')
        # Leave this client's active routing back on the user's project before closing.
        beach=next(x for x in instances['instances'] if x['name']=='Beach-Tank')
        client.rpc('tools/call',{'name':'set_active_instance','arguments':{'instance':beach['id']}})
    finally:client.close()

if __name__=='__main__':main()

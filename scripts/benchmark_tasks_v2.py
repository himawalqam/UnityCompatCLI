"""Task-oriented comparison. MCP full-resource and compact eval alternatives are both measured."""
import json
import statistics
import subprocess
import time
from pathlib import Path
from probe_mcp import McpClient
from benchmark_reads import unwrap_mcp, p95

ROOT=Path(__file__).resolve().parents[1]
EXE=ROOT/'src/unity_compat/bin/win-x64/ucl.exe'
PROJECT='D:/unityProject/Beach-Tank'
OUT=ROOT/'artifacts/benchmark-v2-20261011/Beach-Tank'
ROUNDS=10
records=[];samples={}

def cli(*args):
    p=subprocess.run([str(EXE),*args,'--project',PROJECT,'--json'],capture_output=True,encoding='utf-8',timeout=35)
    if p.returncode:raise RuntimeError(p.stdout)
    return json.loads(p.stdout),p.stdout,'ucl '+' '.join(args)+' --json'

def payload(raw):
    while isinstance(raw,dict) and 'success' in raw and 'data' in raw and raw.get('success') is not False:raw=raw['data']
    return raw

def texts(responses):
    return '\n'.join(c.get('text','') for r in responses for c in r.get('content',r.get('contents',[])))

client=McpClient()
try:
    instances=unwrap_mcp(client.rpc('resources/read',{'uri':'mcpforunity://instances'}))
    beach=next(x for x in instances['instances'] if x['name']=='Beach-Tank')
    client.rpc('tools/call',{'name':'set_active_instance','arguments':{'instance':beach['id']}})
    info,_,_=cli('find','BeachLobbyUI','--type','BeachMainMenu');id=info['objects'][0]['id']
    fields='characters,startButton,chapterMenu,resumePanel'
    code='var go=UnityEngine.GameObject.Find("BeachLobbyUI"); var c=go.GetComponent("BeachMainMenu"); var s=new UnityEditor.SerializedObject(c); System.Func<string,object> r=k=>{ var p=s.FindProperty(k); var o=p.objectReferenceValue; return o==null?null:new { id=o.GetInstanceID(), name=o.name, asset=UnityEditor.AssetDatabase.GetAssetPath(o) }; }; return new { characters=r("characters"), startButton=r("startButton"), chapterMenu=r("chapterMenu"), resumePanel=r("resumePanel") };'
    for task,routes in [('snapshot',['native','mcp']),('four_fields',['native','mcp_resource','mcp_eval'])]:
        for n in range(-1,ROUNDS):
            for route in routes if n%2 else list(reversed(routes)):
                start=time.perf_counter();responses=[];request=[]
                if route=='native':
                    raw,rendered,command=cli('snapshot','--depth','0','--components') if task=='snapshot' else cli('inspect',str(id),'--component','BeachMainMenu','--fields',fields)
                    semantic=raw if task=='snapshot' else raw['fields'];request=command
                elif task=='snapshot':
                    for method,params in [('tools/call',{'name':'manage_scene','arguments':{'action':'get_hierarchy','page_size':50}}),('resources/read',{'uri':'mcpforunity://editor/windows'}),('resources/read',{'uri':'mcpforunity://editor/selection'})]:
                        responses.append(client.rpc(method,params));request.append({'method':method,'params':params})
                    raw=responses;rendered=texts(responses);semantic={'hierarchy':payload(unwrap_mcp(responses[0])),'windows':payload(unwrap_mcp(responses[1])),'selection':payload(unwrap_mcp(responses[2]))}
                else:
                    method,params=('resources/read',{'uri':f'mcpforunity://scene/gameobject/{id}/component/BeachMainMenu'}) if route=='mcp_resource' else ('tools/call',{'name':'execute_code','arguments':{'action':'execute','code':code}})
                    response=client.rpc(method,params);responses=[response];request={'method':method,'params':params};raw=response;rendered=texts(responses)
                    value=payload(unwrap_mcp(response))
                    if route=='mcp_eval':semantic=value['result']
                    else:
                        properties=value['component'].get('properties',value['component'])
                        semantic={key:properties[key] for key in fields.split(',')}
                ms=(time.perf_counter()-start)*1000
                if n<0:samples[task+'-'+route]={'request':request,'response':raw,'rendered':rendered,'semantic':semantic}
                else:records.append({'task':task,'route':route,'round':n,'ms':ms})
        print(task,[{'route':route,'p50':round(statistics.median(r['ms'] for r in records if r['task']==task and r['route']==route),2),'p95':round(p95([r['ms'] for r in records if r['task']==task and r['route']==route]),2)} for route in routes],flush=True)
    # Four references must agree across all three alternatives.
    assert samples['four_fields-native']['semantic']==samples['four_fields-mcp_eval']['semantic'],(samples['four_fields-native']['semantic'],samples['four_fields-mcp_eval']['semantic'])
    # Full Inspector uses richer reference objects; compare their matching ID/name values.
    native=samples['four_fields-native']['semantic'];resource=samples['four_fields-mcp_resource']['semantic']
    for key in fields.split(','):
        if native[key] is None:assert resource[key] is None
        elif isinstance(resource[key],str):assert native[key]['asset']==resource[key]
        else:
            assert native[key]['name']==resource[key]['name']
            assert native[key]['id']==resource[key].get('instanceID',resource[key].get('id'))
    a=samples['snapshot-native']['semantic'];b=samples['snapshot-mcp']['semantic']
    na=[(n['id'],n['name'],n['active'],n['children'],[x.split('.')[-1] for x in n['components']]) for n in a['hierarchy']['nodes']]
    nb=[(n['instanceID'],n['name'],n['activeSelf'],n['childCount'],n['componentTypes']) for n in b['hierarchy']['items']]
    assert na==nb,(na,nb)
    assert a['windows']==b['windows'] and a['selection']==b['selection']
    (OUT/'task-records.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
    (OUT/'task-samples.json').write_text(json.dumps(samples,ensure_ascii=False,indent=2),encoding='utf-8')
finally:client.close()

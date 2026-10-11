import json
import statistics
import subprocess
import sys
import time
from pathlib import Path
from benchmark_reads import unwrap_mcp, ok, p95
from probe_mcp import McpClient, OUT
from unity_compat.protocol import Mailbox

PROJECT = Path('D:/unityProject/Beach-Tank')
ops=[('manage_scene',{'action':'get_hierarchy','pageSize':50}),
     ('find_gameobjects',{'searchTerm':'BeachLobbyUI','includeInactive':True}),
     ('read_console',{'action':'get','count':5,'types':['error'],'format':'json'})]
client=McpClient()
records=[]
try:
    instances=unwrap_mcp(client.rpc('resources/read',{'uri':'mcpforunity://instances'}))
    beach=next(x for x in instances['instances'] if x['name']=='Beach-Tank')
    client.rpc('tools/call',{'name':'set_active_instance','arguments':{'instance':beach['id']}})
    for round_ in range(20):
        for route in (['mcp','cli'] if round_%2 else ['cli','mcp']):
            start=time.perf_counter()
            if route=='cli':
                params={'operations':[{'command':c,'params':p} for c,p in ops]}
                args=[sys.executable,'-m','unity_compat','command','cli_batch','--params',json.dumps(params),'--project-path',str(PROJECT),'--format','ndjson']
                r=subprocess.run(args,capture_output=True,encoding='utf-8',timeout=35)
                raw=json.loads(r.stdout);value=raw.get('data',raw);exit_=r.returncode
                outputs=value.get('results',[])
            else:
                from benchmark_workflows import translate
                params={'commands':[{'tool':c,'params':translate(p)} for c,p in ops],'parallel':False,'fail_fast':True}
                raw=client.rpc('tools/call',{'name':'batch_execute','arguments':params})
                value=unwrap_mcp(raw);exit_=0;outputs=value.get('data',{}).get('results',[])
            ms=(time.perf_counter()-start)*1000
            # Validate actual results, not just the outer envelope.
            successful=ok(value) and exit_==0 and len(outputs)==3 and all(ok(x['result']) for x in outputs)
            successful=successful and outputs[0]['result']['data']['total']==5 and outputs[1]['result']['data']['instanceIDs']==[30040]
            records.append({'round':round_,'route':route,'ms':ms,'success':successful,'response':raw if round_==0 else None,'request':params if round_==0 else None})
    print([{'route':route,'n':len(rows:=[x for x in records if x['route']==route]),'success':sum(x['success'] for x in rows),'p50':statistics.median(x['ms'] for x in rows),'p95':p95([x['ms'] for x in rows])} for route in ['cli','mcp']],flush=True)
    (OUT/'beach-batch-records.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
    mb=Mailbox(PROJECT);issues=[];heartbeats=set();started=time.perf_counter()
    for i in range(2000):
        try:heartbeats.add(mb.status()['heartbeat'])
        except Exception as e:issues.append({'i':i,'error':str(e)})
        time.sleep(.005)
    result={'reads':2000,'errors':issues,'different_heartbeats':len(heartbeats),'ms':(time.perf_counter()-started)*1000}
    print('STATUS STRESS',result,flush=True)
    (OUT/'status-stress-after-fix.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
finally:client.close()

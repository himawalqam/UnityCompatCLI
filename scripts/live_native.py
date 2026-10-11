"""Real Editor verification of NativeAOT commands, mutations and concurrency. Fixture only."""
import concurrent.futures
import json
import subprocess
import time
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
EXE=ROOT/'src/unity_compat/bin/win-x64/ucl.exe'
PROJECT=ROOT/'validation/Unity2022'
checks=[]

def call(*args, expected=0, stdin=None):
    p=subprocess.run([str(EXE),*map(str,args),'--project',str(PROJECT),'--json'],input=stdin,capture_output=True,encoding='utf-8',timeout=65)
    assert p.returncode==expected,(args,p.returncode,p.stdout,p.stderr)
    checks.append({'args':list(args),'exit':p.returncode})
    return json.loads(p.stdout if p.returncode == 0 else p.stderr)

call('status')
call('hierarchy','--components')
call('windows')
call('snapshot','--depth','0')
name='Native_中文_'+str(int(time.time()))
created=call('create','cube','--name',name,'--position','1,2,3')
id=created['instanceID']
try:
    call('add',id,'AudioSource')
    result=call('set',id,'AudioSource','m_Volume','0.25','--expect','1')
    assert result['after']==.25
    call('set',id,'AudioSource','m_Volume','0.5','--expect','1',expected=10)
    inspected=call('inspect',id,'--component','AudioSource','--fields','m_Volume')
    assert inspected['fields']=={'m_Volume':.25}
    call('set',id,'Transform','m_LocalPosition','3,1,4')
    pos=call('inspect',id,'--component','Transform','--fields','m_LocalPosition')['fields']['m_LocalPosition']
    assert pos==[3.,1.,4.],pos
    assert call('find',name,'--type','AudioSource')['matches']==1
    assert 'UnityEngine.AudioSource' in call('inspect',id)['components']
    batch=f'inspect {id} --component AudioSource --fields m_Volume\nfind "{name}"\nhierarchy --depth 0\n'
    result=call('batch','-',stdin=batch)
    assert result['completed']==3,result
    call('batch','-',stdin='create cube --name BatchOnce\ncommand not_a_command\n',expected=6)
    assert call('find','BatchOnce')['matches']==0,'Unknown command batch must fail validation before first mutation'
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        rows=list(pool.map(lambda _:call('inspect',id,'--component','AudioSource','--fields','m_Volume'),range(24)))
    assert all(x['fields']['m_Volume']==.25 for x in rows)
    call('command','not_a_command',expected=6)
    call('inspect',id,'--component','AudioSource','--fields','does_not_exist',expected=6)
    call('eval','this is invalid C sharp',expected=6)
    detached=call('command','fixture_echo','--arg','中文=值','--detach')
    echoed=call('job','wait',detached['id'])
    assert echoed['echoed']['中文']=='值'
finally:
    call('delete',id)
assert call('find',name)['matches']==0
out=ROOT/'artifacts/native-live-v3.json'
out.write_text(json.dumps({'checks':checks,'count':len(checks)},ensure_ascii=False,indent=2),encoding='utf-8')
print(f'{len(checks)} real Editor native checks passed; {out}')

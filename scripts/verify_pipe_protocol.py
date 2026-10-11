"""Frame authentication, stale sessions, deadlines and duplicate ID recovery in a real fixture."""
import json
import struct
import subprocess
import time
import uuid
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
PROJECT=ROOT/'validation/Unity2022'
EXE=ROOT/'src/unity_compat/bin/win-x64/ucl.exe'
ROOT_IPC=PROJECT/'Library/UnityCompat'
ep=json.loads((ROOT_IPC/'endpoint.json').read_text())
results=[]

def request(**overrides):
    req={'protocol':1,'id':uuid.uuid4().hex,'session':ep['session'],'token':ep['token'],'command':'fixture_echo','params':{'message':'once'},'created':time.time(),'deadline':time.time()+30}
    req.update(overrides)
    return req

def exchange(req):
    data=json.dumps(req).encode()
    with open('\\\\.\\pipe\\'+ep['pipe'],'r+b',buffering=0) as pipe:
        pipe.write(struct.pack('<i',len(data))+data)
        def read(n):
            data=b''
            while len(data)<n:
                part=pipe.read(n-len(data))
                if not part:raise EOFError('Connection closed')
                data+=part
            return data
        return json.loads(read(struct.unpack('<i',read(4))[0]))

first=request()
assert exchange(first)['success']
file=ROOT_IPC/'responses'/f"{first['id']}.json"
before=file.read_bytes()
duplicate=exchange(first)
assert not duplicate['success'] and 'Duplicate' in duplicate['error']['message']
assert file.read_bytes()==before,'Duplicate must preserve the original job result'
results.append('duplicate rejected, original result preserved')
for label,req in [('stale session',request(session='old')),('expired deadline',request(deadline=time.time()-1))]:
    response=exchange(req)
    assert not response['success'],response
    results.append(label+' rejected')
try:
    exchange(request(token='wrong'))
    raise AssertionError('Unauthenticated request accepted')
except (EOFError,OSError):results.append('invalid token disconnected')

# A deliberately slow evaluation may finish after the caller times out. Inspect the durable
# job instead of reissuing the request. Only the disposable fixture is blocked briefly.
run=subprocess.run([str(EXE),'eval','System.Threading.Thread.Sleep(200); return "timeout-finished-once";','--project',str(PROJECT),'--timeout','0.1','--json'],capture_output=True,encoding='utf-8')
assert run.returncode!=0,run.stdout
response=json.loads(run.stderr)
job=response['data']['jobId']
until=time.monotonic()+8
path=ROOT_IPC/'responses'/f'{job}.json'
while not path.exists() and time.monotonic()<until:time.sleep(.05)
assert path.exists(),job
terminal=json.loads(path.read_text())
assert terminal['id']==job
results.append('timeout retains inspectable terminal job without replay')
(ROOT/'artifacts/pipe-protocol-v3.json').write_text(json.dumps({'checks':results,'timeoutJob':terminal},ensure_ascii=False,indent=2),encoding='utf-8')
print('\n'.join(results))

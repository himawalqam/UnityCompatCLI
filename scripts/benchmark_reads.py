"""Same Editor, alternating read-only MCP and CLI requests, with raw evidence."""
import json
import platform
import statistics
import subprocess
import sys
import time
from pathlib import Path

from probe_mcp import McpClient, OUT
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src'))
from unity_compat.protocol import Mailbox

PROJECT = Path('D:/unityProject/Beach-Tank')
ROUNDS = 20
CASES = [
    ('windows', 'get_windows', {}, 'resources/read', {'uri': 'mcpforunity://editor/windows'}),
    ('selection', 'get_selection', {}, 'resources/read', {'uri': 'mcpforunity://editor/selection'}),
    ('hierarchy', 'manage_scene', {'action': 'get_hierarchy', 'pageSize': 50}, 'tools/call',
        {'name': 'manage_scene', 'arguments': {'action': 'get_hierarchy', 'page_size': 50}}),
    ('inspector', 'get_gameobject_component', {'instanceID': 30040, 'componentName': 'BeachMainMenu'}, 'resources/read',
        {'uri': 'mcpforunity://scene/gameobject/30040/component/BeachMainMenu'}),
    ('find_object', 'find_gameobjects', {'searchTerm': 'BeachLobbyUI', 'includeInactive': True}, 'tools/call',
        {'name': 'find_gameobjects', 'arguments': {'search_term': 'BeachLobbyUI', 'include_inactive': True}}),
    ('project_info', 'get_project_info', {}, 'resources/read', {'uri': 'mcpforunity://project/info'}),
    ('console', 'read_console', {'action': 'get', 'count': 10, 'types': ['error'], 'format': 'json'}, 'tools/call',
        {'name': 'read_console', 'arguments': {'action': 'get', 'count': 10, 'types': ['error'], 'format': 'json'}}),
    ('execute_code', 'execute_code', {'action': 'execute', 'code': 'return new { version = UnityEngine.Application.unityVersion, roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().rootCount };'}, 'tools/call',
        {'name': 'execute_code', 'arguments': {'action': 'execute', 'code': 'return new { version = UnityEngine.Application.unityVersion, roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().rootCount };'}}),
]

def unwrap_mcp(raw):
    parts = raw.get('content', raw.get('contents', []))
    values = []
    for p in parts:
        if 'text' in p:
            try:
                values.append(json.loads(p['text']))
            except ValueError:
                values.append(p['text'])
    if raw.get('isError'):
        return {'success': False, 'error': values}
    return values[0] if len(values) == 1 else values

def ok(value):
    return not (isinstance(value, dict) and value.get('success') is False)

def p95(values):
    return sorted(values)[max(0, __import__('math').ceil(len(values) * .95) - 1)]

def main():
    client = McpClient()
    mailbox = Mailbox(PROJECT)
    status = mailbox.status(require_ready=True)
    records = []
    samples = {}
    try:
        instances = unwrap_mcp(client.rpc('resources/read', {'uri': 'mcpforunity://instances'}))
        beach = next(x for x in instances['instances'] if x['name'] == 'Beach-Tank')
        selection = client.rpc('tools/call', {'name': 'set_active_instance', 'arguments': {'instance': beach['id']}})
        for case in CASES:
            label, command, cp, method, mp = case
            print('START', label, flush=True)
            for round_ in range(-1, ROUNDS):
                for route in (['mcp', 'cli', 'api'] if round_ % 2 else ['api', 'cli', 'mcp']):
                    start = time.perf_counter()
                    try:
                        if route == 'mcp':
                            raw = client.rpc(method, mp)
                            semantic = unwrap_mcp(raw)
                            rendered = json.dumps(raw, ensure_ascii=False, separators=(',', ':'))
                            request = {'method': method, 'params': mp}
                            successful = ok(semantic)
                        elif route == 'cli':
                            args = [sys.executable, '-m', 'unity_compat', 'command', command, '--params',
                                    json.dumps(cp, ensure_ascii=False), '--project-path', str(PROJECT), '--json', '--timeout', '30']
                            process = subprocess.run(args, capture_output=True, encoding='utf-8', timeout=35)
                            rendered = process.stdout
                            raw = json.loads(rendered)
                            semantic = raw.get('data', raw)
                            successful = process.returncode == 0 and ok(semantic)
                            request = {'cmd': subprocess.list2cmdline(args)}
                        else:
                            job = mailbox.submit(command, cp, 30)
                            raw = mailbox.wait(job, 30)
                            semantic = raw.get('data', raw)
                            rendered = json.dumps(raw, ensure_ascii=False, separators=(',', ':'))
                            successful = raw.get('success') is True and ok(semantic)
                            request = {'command': command, 'params': cp}
                        elapsed = (time.perf_counter() - start) * 1000
                        if round_ < 0:
                            print('WARMUP', route, label, round(elapsed, 1), successful, flush=True)
                            if not successful:
                                print(json.dumps(raw, ensure_ascii=True)[:2000], flush=True)
                            samples[f'{label}-{route}'] = {'request': request, 'response': raw, 'rendered': rendered, 'semantic': semantic}
                        else:
                            records.append({'case': label, 'route': route, 'round': round_, 'ms': elapsed,
                                            'success': successful, 'bytes': len(rendered.encode('utf-8')),
                                            'characters': len(rendered)})
                    except Exception as e:
                        records.append({'case': label, 'route': route, 'round': round_, 'ms': (time.perf_counter()-start)*1000,
                                        'success': False, 'exception': str(e)})
                        print('ERROR', route, label, str(e), flush=True)
            summary = []
            for route in ['mcp', 'cli', 'api']:
                rows = [x for x in records if x['case'] == label and x['route'] == route and x['round'] >= 0]
                times = [x['ms'] for x in rows]
                summary.append({'route': route, 'success': sum(x['success'] for x in rows), 'n': len(rows),
                                'p50': round(statistics.median(times), 2), 'p95': round(p95(times), 2)})
            print('DONE', label, json.dumps(summary), flush=True)
            (OUT / 'read-records.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
            (OUT / 'read-samples.json').write_text(json.dumps(samples, ensure_ascii=False, indent=2), encoding='utf-8')
        env = {'python': sys.version, 'platform': platform.platform(), 'unity': status.get('unity'), 'project': str(PROJECT),
               'rounds': ROUNDS, 'time_utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'instances': instances,
               'cli_transport': 'atomic-file-mailbox', 'mcp_transport': 'Streamable HTTP + editor WebSocket',
               'note': 'api uses persistent Python Mailbox, cli starts one Python process per call; no LLM billing measurement'}
        (OUT / 'environment.json').write_text(json.dumps(env, ensure_ascii=False, indent=2), encoding='utf-8')
    finally:
        client.close()

if __name__ == '__main__':
    main()

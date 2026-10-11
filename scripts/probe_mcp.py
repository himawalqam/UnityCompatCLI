import json
from pathlib import Path
import requests

OUT = Path('D:/unityTools/UnityCompatCLI/artifacts/benchmark-20261011')
OUT.mkdir(parents=True, exist_ok=True)

class McpClient:
    def __init__(self):
        self.s = requests.Session()
        self.s.headers.update({'Accept': 'application/json, text/event-stream'})
        self.seq = 0
        self.rpc('initialize', {'protocolVersion': '2025-03-26', 'capabilities': {},
                                'clientInfo': {'name': 'unity-compat-benchmark', 'version': '0.1.0'}})
        self.rpc('notifications/initialized', notification=True)

    def rpc(self, method, params=None, notification=False):
        self.seq += 1
        req = {'jsonrpc': '2.0', 'method': method}
        if not notification:
            req['id'] = self.seq
        if params is not None:
            req['params'] = params
        r = self.s.post('http://127.0.0.1:8080/mcp', json=req, timeout=40)
        r.raise_for_status()
        if r.headers.get('Mcp-Session-Id'):
            self.s.headers['Mcp-Session-Id'] = r.headers['Mcp-Session-Id']
        if notification or not r.content:
            return {}
        if 'text/event-stream' in r.headers.get('content-type', ''):
            r.encoding = 'utf-8'  # SSE is UTF-8; requests otherwise guesses Latin-1 without a charset.
            messages = [json.loads(x[5:].strip()) for x in r.text.splitlines() if x.startswith('data:')]
            body = next((x for x in messages if x.get('id') == self.seq), {})
        else:
            body = r.json()
        if 'error' in body:
            raise RuntimeError(json.dumps(body['error']))
        return body.get('result', body)

    def close(self):
        if self.s.headers.get('Mcp-Session-Id'):
            self.s.delete('http://127.0.0.1:8080/mcp', timeout=10)
        self.s.close()

if __name__ == '__main__':
    client = McpClient()
    try:
        for method in ['tools/list', 'resources/list']:
            result = client.rpc(method)
            (OUT / (method.replace('/', '-')+'.json')).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
            if method == 'tools/list':
                print('TOOLS', [(x['name'], list(x.get('inputSchema', {}).get('properties', {}))) for x in result.get('tools', [])])
            else:
                print('RESOURCES', [(x['uri'], x.get('name')) for x in result.get('resources', [])])
        result = client.rpc('resources/templates/list')
        (OUT / 'resource-templates.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
        print('TEMPLATES', result)
    finally:
        client.close()

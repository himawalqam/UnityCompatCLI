import json
from pathlib import Path

p = Path('D:/unityTools/UnityCompatCLI/artifacts/Beach-Tank-20261011')
for name in ['scene_snapshot', 'inspector-main-menu', 'inspector-camera', 'inspector-characters']:
    d = json.loads((p / (name + '.json')).read_text(encoding='utf-8'))
    data = d.get('data', {}).get('data', {})
    print(name, 'success:', d.get('success'))
    if name == 'scene_snapshot':
        print('total:', data.get('total'), 'truncated:', data.get('truncated'))
        print(json.dumps([x for x in data.get('objects', []) if any(c.startswith('Beach') for c in x['components'])], ensure_ascii=False))
    elif name == 'inspector-characters':
        print('property count:', len(data.get('properties', [])), 'truncated:', data.get('truncated'))
        print(json.dumps([x for x in data.get('properties', []) if x['path'].endswith('.title') or x['path'].endswith('.playerPrefab')], ensure_ascii=False))
    elif name == 'inspector-camera':
        print(json.dumps([x for x in data.get('properties', []) if any(t in x['path'] for t in ['Field', 'Clip', 'Orthographic', 'TargetTexture'])], ensure_ascii=False))
    else:
        print(json.dumps(data, ensure_ascii=False))

import json
import subprocess
import sys
from pathlib import Path
import tiktoken

from benchmark_reads import CASES, PROJECT
from probe_mcp import OUT

ENCODERS = {name: tiktoken.get_encoding(name) for name in ['o200k_base', 'cl100k_base']}
def counts(s):
    return {name: len(e.encode(s)) for name, e in ENCODERS.items()}

def compact(obj):
    return json.dumps(obj, ensure_ascii=False, separators=(',', ':'))

samples = json.loads((OUT / 'read-samples.json').read_text(encoding='utf-8'))
rows = []
for label, command, cp, method, mp in CASES:
    cli = samples[f'{label}-cli']
    mcp = samples[f'{label}-mcp']
    args = [sys.executable, '-m', 'unity_compat', 'command', command, '--params', compact(cp),
            '--project-path', str(PROJECT), '--format', 'ndjson', '--timeout', '30']
    # Running from the benchmark venv needs the local src on PYTHONPATH.
    import os
    env = dict(os.environ, PYTHONPATH=str(Path(__file__).resolve().parents[1] / 'src'))
    process = subprocess.run(args, capture_output=True, encoding='utf-8', env=env, timeout=35)
    if process.returncode:
        raise RuntimeError(process.stdout)
    texts = '\n'.join(x.get('text', '') for x in mcp['response'].get('content', mcp['response'].get('contents', [])))
    row = {'case': label, 'cli_pretty': counts(cli['rendered']), 'cli_compact_actual': counts(process.stdout),
           'mcp_full_result': counts(mcp['rendered']), 'mcp_text_only': counts(texts),
           'cli_semantic': counts(compact(cli['semantic'])), 'mcp_semantic': counts(compact(mcp['semantic'])),
           'cli_input': counts(compact(cli['request'])), 'mcp_input': counts(compact(mp)),
           'cli_bytes': len(cli['rendered'].encode('utf-8')), 'mcp_bytes': len(mcp['rendered'].encode('utf-8'))}
    rows.append(row)
    print(label, json.dumps(row), flush=True)
tools = json.loads((OUT / 'tools-list.json').read_text(encoding='utf-8'))
resources = json.loads((OUT / 'resources-list.json').read_text(encoding='utf-8'))
root = Path(__file__).resolve().parents[1]
process = subprocess.run([sys.executable, '-m', 'unity_compat', 'list', '--offline', '--json'], capture_output=True, encoding='utf-8', env=env)
docs = (root / 'README.md').read_text(encoding='utf-8') + (root / 'docs/commands.md').read_text(encoding='utf-8')
result = {'encoders': list(ENCODERS), 'rows': rows, 'discovery': {
    'mcp_tool_count': len(tools['tools']), 'mcp_tool_schemas': counts(compact(tools)),
    'mcp_resources': counts(compact(resources)), 'cli_command_catalog': counts(process.stdout),
    'cli_readme_plus_commands': counts(docs),
    'note': 'Catalog lacks typed argument schemas. Discovery is not equal information or guaranteed model context billing.'},
    'warning': 'Tokenizer proxy counts, not actual model usage/billing. MCP client rendering may deduplicate structuredContent. LLM reasoning and tool execution headers excluded.'}
(OUT / 'token-counts.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')

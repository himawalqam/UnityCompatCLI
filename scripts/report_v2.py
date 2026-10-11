"""Generate benchmark report from raw measurements; tiktoken is a benchmark-only dependency."""
import json
import statistics
from pathlib import Path
import tiktoken

ROOT=Path(__file__).resolve().parents[1]
BASE=ROOT/'artifacts/benchmark-v2-20261011'
P=BASE/'Beach-Tank'
read=lambda path:json.loads(path.read_text(encoding='utf-8'))
summary=read(P/'summary.json');samples=read(P/'samples.json');tasks=read(P/'task-records.json');task_samples=read(P/'task-samples.json');agent=read(P/'agent-tool-records.json')
encs={name:tiktoken.get_encoding(name) for name in ['o200k_base','cl100k_base']}
def count(text,name='o200k_base'):return len(encs[name].encode(text,disallowed_special=()))
def texts(raw):return '\n'.join(x.get('text','') for x in raw.get('content',raw.get('contents',[])))
labels={'windows':'窗口','selection':'选择','hierarchy':'完整 Hierarchy','inspector':'完整 BeachMainMenu Inspector','find_object':'搜索物体','project_info':'项目信息*','console':'错误日志','execute_code':'执行同一段 C#'}
lines=['# Unity Compat CLI 0.2 实测，2026-10-11','',
'0.2 显著改善了旧 CLI 的启动、轮询、输出和交互方式。对 Beach-Tank 的 Hierarchy/搜索/日志及组合任务，新版更快；窗口、选择、完整 Inspector 等原 MCP 资源查询仍稍快。选择 CLI 的实际收益是普通命令、按字段读取、批处理和无需外部 server，不能概括为所有操作都胜过 MCP。','',
'## 方法与环境','',
'Windows 11 / Intel i5-1155G7 / 约 7.79 GiB 内存，Unity 2022.3.62f3c1。GUI 项目 Beach-Tank；另外测了独立 headless fixture Unity2022。原 MCP 是项目现有 Coplay v10，通过本机 HTTP + Editor WebSocket；CLI 复用同一套 C# handler，以命名管道直接调用。新原生程序约 3.35 MB，无额外服务进程。','',
'每项一次预热，再交替顺序执行 20 次，记录 p50 与 nearest-rank p95。MCP/Python/native/shell 每条路线、每个项目各 160/160 成功，共 1,280 次计入统计的读取。GUI 测试前后 active scene 数据完全相同。独立 fixture 后台实例在 GUI 正式测量前已关闭。桌面机器仍有正常系统负载，headless 测量末段受到 GUI 重开/加载影响，尾延迟须按原始记录理解。','',
'下表计时从已启动的 Python 测试进程发起请求开始。MCP 连接已复用；native 每次启动新原生进程；shell 复用一个原生进程；Python 每次启动解释器并走文件邮箱。**此表不包含为 CLI 再启动 PowerShell 的时间，也不包含模型推理或计费。** 实际 Agent 工具调用另测，见后文。','',
'## 同一查询 / 同一语义结果，GUI','',
'| 查询 | 原 MCP p50/p95 ms | 旧 Python 邮箱 p50/p95 | 新 native p50/p95 | 新 shell p50/p95 |',
'| --- | ---: | ---: | ---: | ---: |']
for case,label in labels.items():
 row={x['route']:x for x in summary if x['case']==case}
 lines.append('| '+label+' | '+' | '.join(f"{row[k]['p50']:.1f}/{row[k]['p95']:.1f}" for k in ['mcp','python','native','shell'])+' |')
lines+=['',
'*项目信息的 CLI 还返回 renderPipeline/input/packages，信息量比原 MCP 多；此项不标为完全等量。其他 7 项预热样本的业务数据通过规范化后逐值相等。当前 Inspector 目标通过实际组件查询确定，未沿用旧 session 的 instance ID。','',
'GUI 的 Editor 主线程在闲置时约每 100ms 处理一次队列，所以 shell 去掉 CLI 启动之后仍可能等待下一次 Editor tick。不能用进程复用承诺每条 GUI 指令都是几毫秒。完整 C# eval 的编译成本仍存在，管道没有消除它。','',
'## 实际 Agent 工具调用：shell 选择也影响结果','',
'对同样的完整 Hierarchy 查询，直接通过 Codex 的 MCP 工具与 exec_command 各交替调用 5 次：','',
'| 实验 | MCP p50 ms | CLI p50 ms | CLI 范围 ms |',
'| --- | ---: | ---: | ---: |']
for name,label in [('cli','默认 PowerShell（login=false）'),('cmd-native','cmd.exe（login=false）')]:
 rows=[x['ms'] for x in agent if x['route']==name]
 # First ten records are PS comparison; remaining ten are cmd comparison.
 block=agent[:10] if name=='cli' else agent[10:]
 m=[x['ms'] for x in block if x['route']=='mcp']
 lines.append(f'| {label} | {statistics.median(m):.1f} | {statistics.median(rows):.1f} | {min(rows)}–{max(rows)} |')
lines+=['',
'这些实际调用全部成功，但只有每组 5 次，属于小样本。PowerShell 的启动开销会抵消原生 CLI 的收益；Agent 的普通 ucl 命令应指定 shell="C:/Windows/System32/cmd.exe", login=false。这里明确记录了不利于 CLI 的默认 PowerShell 结果，没有只报告程序本体耗时。','',
'## 按实际任务比较','',
'组合查看任务：读取 5 个场景根节点的名字/活动状态/子节点数/组件，并读取窗口列表与当前选择。CLI 用 snapshot --depth 0 --components 一次请求；MCP 使用现有 Hierarchy 工具 + 两个资源读取。根节点摘要、窗口和选择均核对一致；CLI 另外带场景状态。','',
'字段任务：检查 BeachMainMenu 的 characters/startButton/chapterMenu/resumePanel 四个引用。列出 MCP 完整 Inspector 和 MCP 自写 C# 只返回四个字段的两种合理方案；CLI 使用 inspect --fields。三个方案的引用信息均核对一致。','',
'每项预热一次后交替测 10 次。输入/输出使用固定 o200k_base 代理计数；输入是可读 CLI 指令或 MCP method/params，不包含内部管道 token、GUID、长度帧。','',
'| 任务 / 路线 | p50 ms | p95 ms | 输入 token | 输出 token | 合计 |',
'| --- | ---: | ---: | ---: | ---: | ---: |']
token_data={}
for task in ['snapshot','four_fields']:
 for route in ['native','mcp'] if task=='snapshot' else ['native','mcp_resource','mcp_eval']:
  sample=task_samples[task+'-'+route];rows=[x['ms'] for x in tasks if x['task']==task and x['route']==route]
  request=sample['request'] if isinstance(sample['request'],str) else json.dumps(sample['request'],ensure_ascii=False,separators=(',',':'))
  input_=count(request);output=count(sample['rendered'])
  token_data[task+'-'+route]={'input':input_,'output':output,'total':input_+output}
  lines.append(f'| {task} / {route} | {statistics.median(rows):.1f} | {max(rows):.1f} | {input_} | {output} | {input_+output} |')
lines+=['',
'字段任务的节省来自直接命令与明确字段选择，不是 JSON 编码本身。MCP 也可以使用 C# 裁剪输出，表格已包含此方案。一般的完整 Inspector，CLI 与 MCP 返回基本相同的数据，因此不会出现同样大的 Token 降幅。','',
'## 完整结果的输出 Token','',
'下面比较实际 Python pretty stdout、MCP content/contents 中的 text、native compact payload。MCP JSON-RPC 外层和重复 structuredContent 不计入这一列，避免夸大 MCP 消耗。','',
'| 查询 | 旧 Python | 原 MCP text | 新 native |',
'| --- | ---: | ---: | ---: |']
totals={k:0 for k in ['python','mcp','native']};counts={}
for case,label in labels.items():
 values={'python':count(samples[case+'-python']['rendered']),'mcp':count(texts(samples[case+'-mcp']['response'])),'native':count(samples[case+'-native']['rendered'])}
 counts[case]=values
 for key in totals:totals[key]+=values[key]
 lines.append(f"| {label} | {values['python']} | {values['mcp']} | {values['native']} |")
lines+=[f"| 八项合计* | {totals['python']} | {totals['mcp']} | {totals['native']} |",'',
f"这组输出比旧 Python 少约 {100*(1-totals['native']/totals['python']):.1f}%，比 MCP text 少约 {100*(1-totals['native']/totals['mcp']):.1f}%。*项目信息含 CLI 额外数据，因此合计不是严格逐字信息等量；其余七项信息一致。实际计费、系统提示、工具 schema 是否加载、模型推理 Token、用户总等待时间均未测量，不能将这些代理计数当作真实账单。",'',
'两套编码 o200k_base / cl100k_base 的完整计数见原始 token-counts.json。SSE 读取已明确使用 UTF-8，修复的是基准客户端 requests 默认字符集，不是 MCP 服务；旧样本未用来推导新版结论。','',
'## Headless 与能力范围','',
'| 查询 | MCP p50 ms | native p50 ms | shell p50 ms |',
'| --- | ---: | ---: | ---: |']
h=read(BASE/'Unity2022/summary.json')
for case in ['windows','hierarchy','inspector','console']:
 row={x['route']:x for x in h if x['case']==case}
 lines.append(f"| {labels[case]} | {row['mcp']['p50']:.1f} | {row['native']['p50']:.1f} | {row['shell']['p50']:.1f} |")
lines+=['',
'在低负载 headless 中，已有 MCP 连接通常比每次新启动 native 更快；shell 约 6ms，与 MCP 相近或更快。GUI 和 headless 不应混在一张总体“提速倍数”里。','',
'19 个离线/协议单元检查通过；47 项原生真实 Editor 检查通过（包括中文、创建/组件/属性、旧值拒绝、向量、批处理、24 个并发读取、错误与任务）；5 项 pipe 协议检查通过（重复请求、旧会话、过期、认证、超时恢复）。三个桥接程序集在 Unity 2022 参考程序集下编译零错误；NativeAOT 发布零警告。','',
'78 个静态 Editor 入口及 CLI 本地文件/批处理/shell 能力已提供；入口数不等于独立功能数，也不表示覆盖 MCP 的全部 server/云端工具。2021.3 是兼容目标，尚未真实运行；macOS/Linux 原生通道未完成。构建、导入、C# 编译、PlayMode 和模型思考时间仍需分别处理。','',
'## 复现','',
'代码：scripts/benchmark_v2.py、benchmark_tasks_v2.py、report_v2.py、probe_mcp.py。','',
'原始记录：artifacts/benchmark-v2-20261011/{Beach-Tank,Unity2022}/ 下的 records、samples、summary、environment、task-records、task-samples、agent-tool-records、token-counts JSON。该目录为本地生成文件，默认被 Git 忽略。不要将 fixtures 中的修改脚本用于游戏项目。']
for name,enc in encs.items():
 result={}
 for case in labels:
  result[case]={route:count(samples[case+'-'+route]['rendered'],name) for route in ['python','native']}
  result[case]['mcp_text']=count(texts(samples[case+'-mcp']['response']),name)
 (P/f'token-counts-{name}.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
(P/'token-counts.json').write_text(json.dumps({'o200k_base':counts,'task':token_data},indent=2),encoding='utf-8')
report=ROOT/'docs/benchmark-v2-20261011.md'
report.write_text('\n'.join(lines)+'\n',encoding='utf-8')
print(report)
print('output totals',totals)

import json
import statistics
from pathlib import Path
from benchmark_reads import p95
from probe_mcp import OUT

root=Path(__file__).resolve().parents[1]
reads=json.loads((OUT/'read-records.json').read_text())
work=json.loads((OUT/'workflow-records.json').read_text(encoding='utf-8'))
batch=json.loads((OUT/'beach-batch-records.json').read_text(encoding='utf-8'))
tokens=json.loads((OUT/'token-counts.json').read_text())
labels={'windows':'打开窗口','selection':'当前选择','hierarchy':'Hierarchy 根节点','inspector':'主菜单组件 Inspector','find_object':'查找 BeachLobbyUI','project_info':'项目资料','console':'错误日志','execute_code':'执行只读 C# 查询'}
lines=['# UnityCompat CLI 与 Beach-Tank 原 MCP 实测对比（2026-10-11）','',
'结论：在当前实测版本和当前工作方式下，日常 Agent 开发优先使用原 MCP；UnityCompat CLI 适合无需额外 server 的终端自动化、明确指定项目和新增的专用检查命令。CLI 目前没有实现速度或 Token 开销的全面优势，也不能用 73 对 47 的命令数量证明功能全面更强。','',
'## 测试对象与方法','',
'- CLI：本地 UnityCompat CLI 0.1.0，Python 3.12，每次真实启动 `python -m unity_compat`；原子 JSON 文件邮箱，Unity 桥接包 `com.unitycompat.cli`。',
'- MCP：项目现有 Coplay MCP v10.0.0；实际访问 `http://127.0.0.1:8080/mcp`，执行 MCP initialize、resources/read、tools/call，复用 HTTP Session，经现有 Editor WebSocket 连接执行。不是用直接 C# 调用冒充 MCP。',
'- 本轮对话没有直接暴露 Unity MCP 连接器工具，因此对照通过真实 MCP 协议客户端进行；没有测 Codex UI 内部的连接器调度或模型推理时间。',
'- 查询环境：同一个 Beach-Tank Unity 2022.3.62f3c1 GUI Editor，BeachMainMenu 场景，5 个根节点、474 个物体，未进入 Play Mode。每种请求先预热 1 次，再做 20 次；交替改变调用顺序，减少先后顺序影响。',
'- 编辑环境：另一个同版本 Unity 无界面 Editor，`validation/Unity2022` 独立项目，安装原 MCP 与 CLI 两套桥接。每种接口 10 次完整编辑流程。不能把空测试场景的耗时直接当成 Beach 场景的耗时。',
'- 计时使用 Python `perf_counter`，从发送请求／启动 CLI 前到 JSON 返回并解析。p50 是中位数，p95 用 nearest-rank；查询各 20 样本、编辑各 10 样本，样本量不足以推断长期尾延迟或生产可用性。',
'- Token 使用 tiktoken 0.14.0 的 o200k_base／cl100k_base。这是固定分词器的文本开销代理，不是当前 Codex 模型的实际账单；不包含模型思考、应用工具消息外层、缓存、动态工具发现的收费策略。',
'- 成功优先于速度：操作流程必须验证 Cube、BoxCollider、AudioSource、音量 0.25、位置 (3,1,4)，再删除；批量结果逐项验证，不能只看外层 success。','',
'## 1. 真实 Beach-Tank 单次查询延迟','',
'单位毫秒；下表为修复前基线。CLI 每次启动进程，MCP 使用已连接服务。','',
'| 任务 | MCP p50 | MCP p95 | CLI p50 | CLI p95 | 成功 MCP／CLI |',
'|---|---:|---:|---:|---:|---|']
for case,label in labels.items():
    rows={route:[x for x in reads if x['case']==case and x['route']==route and x['round']>=0] for route in ['mcp','cli']}
    def metric(route, percentile=False):
        values=[x['ms'] for x in rows[route]]
        return f'{(p95(values) if percentile else statistics.median(values)):.1f}'
    lines.append(f'| {label} | {metric("mcp")} | {metric("mcp",True)} | {metric("cli")} | {metric("cli",True)} | {sum(x["success"] for x in rows["mcp"])}/20；{sum(x["success"] for x in rows["cli"])}/20 |')
lines+=['',
'窗口、选择、Inspector 和项目资料由 MCP resource 读取，在本场景通常快于启动 CLI。Hierarchy 与对象搜索经 MCP tool 调用时，CLI 在这轮更快。错误日志与 C# 查询则原 MCP 更快。不同入口有不同服务调度开销，不能用一个“传 JSON”的例子代表全部。',
'',
'补充：常驻 Python Mailbox API 不再每次启动 CLI，多数小查询 p50 约 104ms；这是另一种使用方式，不能拿它冒充 `ucl` 的实测耗时。原始 160 请求有 1 次 Windows 心跳文件读取权限错误，因此修复前成功率为 159/160；CLI 与 MCP 单次查询各 160/160。','',
'## 2. Agent 常见操作流程与批量读取','',
'| 流程／环境 | 次数（各接口） | MCP p50／p95 ms | CLI p50／p95 ms | 成功 |',
'|---|---:|---:|---:|---|']
for label,case,data in [('创建→添加组件→改属性→移动→验证→删除／独立无界面 Editor','create_modify_verify_delete',work),('三项批量读取／独立无界面空场景','batch_three_reads',work),('三项批量读取／真实 Beach 主菜单','beach_batch',batch)]:
    r={route:[x for x in data if x['route']==route and (case=='beach_batch' or x['case']==case)] for route in ['mcp','cli']}
    stats={route:f'{statistics.median(x["ms"] for x in r[route]):.1f}／{p95([x["ms"] for x in r[route]]):.1f}' for route in r}
    lines.append(f'| {label} | {len(r["cli"])} | {stats["mcp"]} | {stats["cli"]} | 两者 {sum(x["success"] for x in r["cli"])}/{len(r["cli"])} |')
lines+=['',
'三项批量内容一致：Hierarchy、对象搜索、错误日志。MCP 用 batch_execute，CLI 用 cli_batch；均指定顺序执行，逐项核验。原 MCP 同样具有批量工具，不能把批量支持说成 CLI 独有。空场景批量仅约 9ms 是无界面 Editor 的结果，真实 Beach 场景约 98ms，报告分开列出。',
'',
'原 MCP 编辑流程中位约 0.92 秒，CLI 约 2.17 秒。这说明当前“每操作启动一次 Python＋100ms 响应轮询”的路径，对连续小操作有明显成本。文件 IPC 没有天然更快。','',
'## 3. Token 与信息体积','',
'o200k_base 计数；同一基线返回样本。MCP 完整结果包括 content／structuredContent 或 contents；MCP 纯文本列只统计其中的 text。CLI 默认是 `--json`，紧凑列通过真实运行 `--format ndjson` 得到。','',
'| 请求 | CLI 默认输出 | CLI 紧凑输出 | MCP 完整结果 | MCP 纯文本 |',
'|---|---:|---:|---:|---:|']
for row in tokens['rows']:
    lines.append(f'| {labels[row["case"]]} | {row["cli_pretty"]["o200k_base"]} | {row["cli_compact_actual"]["o200k_base"]} | {row["mcp_full_result"]["o200k_base"]} | {row["mcp_text_only"]["o200k_base"]} |')
total={c:sum(x[c]['o200k_base'] for x in tokens['rows']) for c in ['cli_pretty','cli_compact_actual','mcp_full_result','mcp_text_only']}
lines.append(f'| 8 项合计 | {total["cli_pretty"]} | {total["cli_compact_actual"]} | {total["mcp_full_result"]} | {total["mcp_text_only"]} |')
lines+=['',
'默认 CLI 输出在这组样本中没有省 Token。其协议 ID、session、完成时间、嵌套包装与缩进增加开销。MCP 某些 tool 同时返回文本和 structuredContent，客户端是否将二者同时交给模型取决于客户端，不能直接按完整结果重复内容推断收费。',
'',
'这里的项目资料响应也不是等信息量：CLI 多返回渲染管线、输入方式和包可用性，MCP 只返回基础项目资料，所以这行 Token 差额包含额外信息。Hierarchy 的有效数据 Token 两边均为 475；组件约 886／898，差异主要来自空字段处理。协议类型本身不是节省 Token 的依据。',
'',
'完整发现数据：原 MCP 47 工具的类型化 schema 约 25,527 Token，资源列表约 1,249；CLI 离线目录约 9,721，README＋命令文档约 3,603。CLI 目录缺少类型化参数 schema，信息量不等，且这些数据不一定每次被完整注入上下文；这些数字不能当成“每次省 1.5 万 Token”。按需加载、分页和只返回需要字段，两种接口都能实现。',
'',
'请求参数本身也有额外开销：CLI 需要命令、路径和 JSON 的 shell 参数，MCP 可以直接发结构化工具参数。脚本可在本地过滤 CLI JSON 后再交给模型；MCP 返回也可在工具编排层过滤，所以这属于编排策略而非 CLI 独占优势。','',
'## 4. 错误处理、可靠性与隔离','',
'五类错误：缺参数、未知 action、找不到目标、非法 C#、未知命令；每接口各 5 次，均正确拒绝。原 MCP 缺参数／未知命令在服务端提前拒绝，分别约 9／12ms；CLI 需要到 Editor 才发现，对应约 223／225ms。CLI 的稳定退出码利于 shell；MCP schema 与结构化参数利于 Agent 提前约束。',
'',
'发现并保留的缺陷：修复前常驻 Mailbox API 有 1/160 的瞬时 PermissionError。修复仅在读取文件时对 PermissionError 最多重试 9 次、间隔 5ms，不重发命令，避免重复执行修改。新增针对短暂拒绝、持续拒绝和非法 JSON 的测试；现有单元测试合计 12 项通过。修复后 2000 次状态读取跨 12 次心跳更新、约 12 秒，0 错误；这只是有限样本，不宣称长期可靠性已被证明。',
'',
'CLI 用显式 project-path 定位本地邮箱。原 MCP 在多 Editor 下依赖 set_active_instance；本服务返回的 session_key 是 global。测试结束已将路由恢复到 Beach-Tank，并通过原 MCP 验证 Application.dataPath 指向该项目。独立测试 Editor 已停止，测试物体没有残留。',
'',
'Beach-Tank 未进入 Play Mode，没有切场景或保存场景；原 MCP 和 CLI 最后都确认主菜单 isDirty=false。唯一必要的项目接入变化是 CLI 桥接包及包清单，另保存玩法文档；用户既有脚本／资源修改没有被重置。','',
'## 5. 功能与 Agent 使用体验','',
'| 方面 | 原 MCP | 当前 CLI | 判断 |',
'|---|---|---|---|',
'| 参数发现 | 47 工具的类型化 schema；资源 URI | 73 命令的描述目录＋文档，参数约束不足 | 日常 Agent 调用原 MCP 更完整 |',
'| 连续编辑与批量 | 已有 batch_execute，本轮速度更快 | cli_batch 能执行工具与资源命令 | 两者都有批量，当前性能原 MCP 更好 |',
'| 专用检查 | 可用通用 C#／反射等途径完成 | 新增序列化检查、快照、地形／音频／导航、导入器、依赖等入口 | CLI 部分任务更直接；不是原 MCP 做不到 |',
'| 脚本文本工具 | apply_text_edits、get_sha、validate_script 等原生入口 | 可用本地 Python／文件工具或 manage_script，未全部提供同名接口 | CLI 尚不是完全替代品 |',
'| 附加服务 | 需要 MCP 服务与 Editor 连接 | 不需要额外 server；仍需要 Editor 桥接包 | CLI 满足独立终端需求 |',
'| 多项目 | 选择活动实例；应核验实际路由 | 每命令指定 project-path | CLI 路由更明确 |',
'| 超时与重载 | 本轮未故障注入重载／断连 | 有 job ID、取消与中断机制；历史测试另记 | 未做对照，不能据此判定谁更可靠 |',
'',
'原 MCP 有 13 个服务侧工具名未在 CLI 命令目录中出现，包括文本编辑、SHA、脚本校验、工具管理和 Unity 文档查询；部分可用通用执行或本地文件操作替代，但原生接口确实有缺口。CLI 的 73 包含 34 个原有 C# 工具、19 个资源和 20 个新增入口；原 MCP 的 47 是 tools，另有 resources，因此计数口径不同。','',
'## 6. 选择建议与下一步','',
'我对“好用”的判据是：先确认目标正确且成功，再看常见任务 p50／p95、有效信息 Token、参数约束和运行依赖。没有给不同需求套任意权重凑一个总分。',
'',
'- 当前在这个项目中让 Agent 查询场景、检查组件、连续修改对象：优先原 MCP。已有连接、参数 schema、原生文件工具和本轮实测都支持这个选择。',
'- 要在终端／脚本／CI 直接操作 Unity，或明确要求无需另一个 server：使用 CLI。它提供了可行的独立入口和若干更直接的检查命令，但目前应作为补充工具。',
'- CLI 若要成为日常 Agent 首选，优先改善按需字段输出和协议包装、补充类型化参数说明与本地校验、减少逐操作启动与轮询成本，再做同一矩阵对比。不要先堆更多命令后宣称更强。','',
'本轮没有进行两个真实 LLM Agent 的多轮盲测，也没有当前模型的逐工具计费数据。因此不对模型推理 Token、自动规划错误率或长期大型项目完成率给出虚构数字。这里测试的是确定参数的代表开发流程和真实接口性能。','',
'## 复现与原始记录','',
'测试脚本在 `scripts/benchmark_reads.py`、`benchmark_workflows.py`、`benchmark_batch_beach.py`、`benchmark_tokens.py`；MCP 协议客户端在 `scripts/probe_mcp.py`。Token 脚本使用独立 `.venv-bench`，不会给 CLI 运行时增加第三方依赖。','',
'原始记录：`artifacts/benchmark-20261011/read-records.json`（逐请求延迟与成功）、`read-samples.json`（完整请求／返回）、`workflow-records.json`（六步修改与验证）、`beach-batch-records.json`（真实场景批量）、`token-counts.json`（两种分词器）、`status-stress-after-fix.json`（心跳读测试）、`tools-list.json`（原 schema）。','',
'修复前 protocol.py 保存在该目录，SHA-256 `36DE5804CB90C01915E103A3D4697907D941E72474A7E8AB63A959061CD8EF24`。查询基线修复前执行，后续流程明确为修复后；没有混合改动前后的结果隐藏失败。','']
(root/'docs/benchmark-20261011.md').write_text('\n'.join(lines),encoding='utf-8')
print(root/'docs/benchmark-20261011.md')

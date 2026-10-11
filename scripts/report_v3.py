"""Publish measured data with the report; no fabricated timing or billing data."""
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / "artifacts/benchmark-v3-20261011"
EVIDENCE = ROOT / "docs/evidence-v3"

def read(project, name):
    return json.loads((BASE / project / (name + ".json")).read_text(encoding="utf-8"))

def table(project):
    rows = read(project, "summary")
    result = ["| 查询 | 旧 MCP | 单次 CLI 直连 | 单次 CLI + daemon | 常驻 shell + daemon | 原生 stdio |", "| --- | ---: | ---: | ---: | ---: | ---: |"]
    for case, label in [("windows", "窗口"), ("hierarchy", "完整层级页"), ("full_inspector", "完整 Inspector"), ("console", "Console")]:
        values = {r["route"]: f'{r["p50"]:.2f} / {r["p95"]:.2f}' for r in rows if r["case"] == case}
        result.append("| " + label + " | " + " | ".join(values[r] for r in ["old_mcp", "oneshot", "daemon", "shell", "stdio"]) + " |")
    return "\n".join(result)

def bursts(project):
    values = read(project, "bursts")
    lines = ["| 轮次 | 旧 MCP p95 / 总耗时 | stdio p95 / 总耗时 |", "| --- | ---: | ---: |"]
    for n in range(3):
        pair = {r["route"]: r for r in values if r["round"] == n}
        lines.append(f'| {n + 1} | {pair["old_mcp"]["p95"]:.2f} / {pair["old_mcp"]["totalMs"]:.2f} | {pair["stdio"]["p95"]:.2f} / {pair["stdio"]["totalMs"]:.2f} |')
    return "\n".join(lines)

for project in ["Beach-Tank", "Unity2022"]:
    destination = EVIDENCE / project
    destination.mkdir(parents=True, exist_ok=True)
    for name in ["summary", "records", "samples", "environment", "bursts", "discovery", "eval-cold", "large"]:
        file = BASE / project / (name + ".json")
        if file.exists():
            shutil.copy2(file, destination / file.name)

samples = read("Beach-Tank", "samples")
old_tokens = samples["four_fields-old_mcp"]["outputTokens"]
new_tokens = samples["four_fields-projected_stdio"]["outputTokens"]
discovery = read("Beach-Tank", "discovery")
large = read("Unity2022", "large")
large_summary = large["summary"]
large_rows = "\n".join(f'| {r["route"]} | {r["p50"]:.2f} | {r["p95"]:.2f} |' for r in large_summary)
cold = read("Beach-Tank", "eval-cold")
new_snippets = ", ".join(f'{v["wallMs"]:.2f}' for v in cold["newSnippets"])
large_bursts = {r["route"]: [v["p95"] for v in large["bursts"] if v["route"] == r["route"]] for r in large["bursts"]}
report = f"""# 0.3 实测报告：2026-10-11

## 判断

原生 CLI 的单次进程调用仍有启动成本；daemon 复用后端连接也没有消除前端进程启动。对 Agent 连续任务，当前推荐原生 stdio，或者一次进入 shell / batch 完成多个步骤。三种方式都使用同一个可独立运行的 ucl.exe，无需原来的 Python MCP/HTTP server；stdio 是可选的 Agent 接入方式。

Beach-Tank 中相同层级查询的 p95 从 594.37ms 降为 102.39ms（约 82.8%），Console 从 200.49ms 降为 128.12ms（约 36.1%）。窗口和完整 Inspector 没有变快，后者从 100.86ms 增为 110.94ms。字段投影显著缩小输出，但该项目的四字段查询 p95 仅从 103.08ms 变为 102.33ms。结论是部分工作负载已有实测收益，不能声称所有命令更快，也不能用独立 batchmode 项目的个位数毫秒代表游戏 GUI 的延迟。

## 方法与范围

- Windows，Unity 2022.3.62f3c1。Beach-Tank 为 GUI，NativeAOT ucl.exe 为 6,980,608 bytes；独立 Unity2022 fixture 为带图形 batchmode，使用同版较早构建（6,979,584 bytes），之后仅修正错误映射和工具组过滤，未修改所测读取路径。各环境原始记录保留实际大小。
- 旧工具为当前项目 Coplay MCP，HTTP 客户端会话与 Editor WebSocket 复用；新工具使用命名管道。计时为测试客户端请求到完整响应的 wall time，单次 CLI 包含进程启动，不包含 PowerShell/Codex 调度及模型思考时间。
- 每项基础查询每条路径 20 次，一次预热排除，交替反转执行顺序。p50 为中位数，p95 用 nearest rank；20 个样本取第 19 个，10 个样本取最大值。未删除离群值，小样本分位数不代表长期服务保证。
- 基础查询断言业务结果相同，忽略焦点和时间戳等瞬态字段；Hierarchy 使用同一 get_hierarchy 页（page size 50），没有用裁剪结果冒充相同数据。Console 读取 10 条 error。
- 四字段单独比较旧完整 Inspector 与新指定字段输出，并逐一验证字段值；这是减少所取数据的对比。完整 Inspector 的公平对比仍保留在基础表中。
- 每个项目 460 次计入串行统计的调用（400 基础、40 字段、20 Eval），另有预热、首次/新片段 Eval 及 144 次小查询并发调用。500 节点测试另有 40 次串行、144 次并发调用。没有测量大模型任务成功率、实际账单或一次完整 Agent 游戏开发任务耗时。
- shell 在本轮 daemon 运行时复用前端进程并经过 daemon；stdio 直接复用 Editor 连接池；单次直连使用 --direct。五条路径的启动/调度范围不同，均明确记录。
- Beach-Tank 测试前后 active scene 均为 BeachMainMenu、5 个根对象、isDirty=false；查询没有改变场景状态。创建 500 节点、测试失败和播放控制均只在独立 fixture 执行。

## Beach-Tank GUI

单位 ms，各单元格为 **p50 / p95**。

{table("Beach-Tank")}

24 个并发窗口查询，三轮交替启动旧/新路径；总耗时为整批完成时间。

{bursts("Beach-Tank")}

GUI 的串行小请求普遍在约 100ms，说明 Editor 调度仍是主要限制；连接池允许排队查询成组处理，因此并发尾延迟下降。该解释是根据计时分布作出的推断，尚未进行 CPU profiler 归因。

## 独立 fixture

{table("Unity2022")}

{bursts("Unity2022")}

fixture 的第三轮小查询并发旧 MCP 比 stdio 更快，不能省略此反例。batchmode 的 Editor 更新节奏与 GUI 不同。

## 相同 500 节点完整 Hierarchy

在独立 fixture 创建并最终删除测试根对象和 500 个子对象。两个路径均请求相同父对象、page size 500 / max nodes 500，并断言完整结果相同；没有用字段投影缩小数据。

| 路径 | p50 ms | p95 ms |
| --- | ---: | ---: |
{large_rows}

三轮 24 并发的 p95：旧 MCP 为 {', '.join(f'{v:.2f}' for v in large_bursts['old_mcp'])}ms，stdio 为 {', '.join(f'{v:.2f}' for v in large_bursts['stdio'])}ms，三轮均有改善。这仍只是一个固定场景和节点规模的测试。

## Eval 冷启动、重复与新代码

Beach-Tank 重复同一片段 10 次：旧 CodeDOM p50 / p95 为 1157.64 / 5778.74ms；新 Roslyn 缓存为 53.91 / 112.06ms。两者返回相同 Unity 版本与根对象数。缓存优势不能推广成任意新代码首次执行的优势。

本轮首次 Roslyn 编译 wall time 为 {cold['firstWallMs']:.2f}ms，其中 compileMs 为 {cold['first']['compileMs']:.2f}ms，cacheHit=false。编译器热身后五个新片段 wall time 为 {new_snippets}ms。独立 fixture 首次约 1289.5ms，后续新片段约 19–32ms。域重载会清空代码缓存；最多缓存 256 个独特片段/域。Eval 已验证不触发域重载，但它是认证后的本地完整权限 C# 执行，不能称为安全沙盒。

## Token 代理计数

使用 tiktoken o200k_base，不是实际模型账单。响应仅计 content 文本，不重复计 structuredContent；工具发现计序列化 tools/list JSON。

- BeachMainMenu 完整 Inspector 文本 {old_tokens} tokens，取 characters/startButton/chapterMenu/resumePanel 四字段 {new_tokens} tokens，约减少 {(1-new_tokens/old_tokens)*100:.1f}%。这依赖任务确实只需要这四个字段。
- 旧发现返回 {discovery['oldTools']} 工具、{discovery['oldTokens']} tokens；默认 compact 返回 {discovery['compactTools']} 工具、{discovery['compactTokens']} tokens，约减少 {(1-discovery['compactTokens']/discovery['oldTokens'])*100:.1f}%。客户端若已经按需加载旧工具，此数值不能视为真实节省比例；按需 describe 也会增加后续上下文。
- 完整数据相同的响应没有宣称必然大幅减少 Token。批处理减少交互次数的收益尚未测成端到端 Agent 成本。

## 替代程度与已知边界

静态目录包含 102 个入口：98 个 Editor 入口及 4 个原生前端接口；full MCP 是这些入口加 3 个代理工具，项目自定义方法会额外注册。旧 47 个工具名称均有对应入口，但目录覆盖不等于每个 action 已验证，也不等于完全相同的接口语义。

当前 Unity 实测仅 2022.3；兼容目标最低为 2021.3 LTS，2021.1/2021.2 不在支持范围，2021.3 与 Unity 6 尚未实机验证。macOS/Linux 原生通道、所有可选包动作、Player 完整构建、完整 PlayMode 测试矩阵仍待验证。截图为 Camera.Render 的 PNG，不能替代整个 Editor/GameView 的截图，ScreenSpaceOverlay UI 不包含在内。

新 manage_tools 控制本地 stdio 会话的工具组；set_active_instance 使用项目路径；unity_docs 是官方页面直取而非旧 indexed search。旧 batch_execute 与新 cli_batch 的命令范围不同。不要把这些接口描述成无差别的直接替换。

## 复核入口

[GUI 原始逐次记录](evidence-v3/Beach-Tank/records.json)、[GUI 环境和场景状态](evidence-v3/Beach-Tank/environment.json)、[响应样本和 Token](evidence-v3/Beach-Tank/samples.json)、[Eval 冷启动](evidence-v3/Beach-Tank/eval-cold.json)、[500 节点完整记录](evidence-v3/Unity2022/large.json)。这些数据随 ZIP 文档一起打包。

复现脚本：scripts/benchmark_v3.py --project PROJECT；scripts/benchmark_large_v3.py 仅用于独立 fixture。后者会创建/删除测试物体，不应直接对游戏项目使用。环境、Editor 焦点、负载及版本改变时结果可能改变。
"""
(ROOT / "docs/benchmark-v3-20261011.md").write_text(report, encoding="utf-8")
print(json.dumps({"inspectorTokens": [old_tokens, new_tokens], "report": str(ROOT / "docs/benchmark-v3-20261011.md")}, ensure_ascii=False))

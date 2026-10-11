# Unity Compat CLI 0.3

Windows x64 原生 CLI + Unity Editor 内部桥接包。兼容目标 **Unity 2021.3+**；当前已实测 **2022.3.62f3c1**。直接 CLI 不需要外部 MCP server 或 HTTP 端口。也支持可选的本地 daemon 和原生 MCP stdio；常用命令无需 Python/.NET 运行时。Editor 必须打开，GUI 和 headless 均可。

[0.3 实测报告](docs/benchmark-v3-20261011.md)包含 Beach-Tank、独立 fixture、500 节点压力、Token 代理计数及未变快的反例。[0.3 改造说明](docs/v3-design.md)记录常驻会话、MCP stdio、动态 C# 和兼容边界。[0.2 基准报告](docs/benchmark-v2-20261011.md)保留历史数据；[研究来源](docs/cli-research.md)记录对官方 Pipeline 包的直接研究。

## 安装

### Windows 独立安装：不用 MCP、不用 Python

下载 [UnityCompatCLI-0.3.0-Windows.zip](https://github.com/himawalqam/UnityCompatCLI/raw/refs/heads/main/releases/UnityCompatCLI-0.3.0-Windows.zip)，将整个目录解压到例如 `D:\Tools\UnityCompatCLI`。保留 `ucl.exe`、`bridge/`、命令目录文件及许可证，不要只复制 exe。使用 Unity Hub 创建项目，例如 `D:\UnityProjects\MyGame`；项目创建后先关闭 Editor，再运行：

```powershell
& "D:\Tools\UnityCompatCLI\ucl.exe" install --project "D:\UnityProjects\MyGame"
# 用 Unity Hub 打开项目，等待导入和编译完成
& "D:\Tools\UnityCompatCLI\ucl.exe" status --project "D:\UnityProjects\MyGame"
```

返回 `state: ready` 即可使用。每个项目安装一次桥接包；Editor 保持打开，桥接自动加载。无需安装旧 MCP For Unity、配置 MCP 客户端或启动 HTTP server。CLI 与 Editor 在本机通过命名管道通信。普通命令不依赖 pip、Python 或 .NET 运行时。

给当前 PowerShell 会话加入命令路径，再切到项目：

```powershell
$env:Path = "D:\Tools\UnityCompatCLI;$env:Path"
Set-Location "D:\UnityProjects\MyGame"
ucl hierarchy --depth 1 --limit 40
ucl inspect MainCamera --component Transform --fields m_LocalPosition
ucl create cube --name DemoCube --position 0,1,0
```

需要长期使用，可在 Windows 用户环境变量 PATH 中加入解压目录，重新打开终端。场景修改不会自动保存；需要保存时在 Editor 中按 Ctrl+S。若 status 尚未 ready，先检查 Editor 是否完成导入/编译、有无编译错误，以及 --project 是否指向当前项目。

### 源码和可选 Python 管理入口

```powershell
git clone https://github.com/himawalqam/UnityCompatCLI.git
Set-Location UnityCompatCLI
python -m pip install .
unity-compat setup-fast --json
ucl --version
```

源码仓库包含预编译 Windows 原生程序及桥接 DLL。`unity-compat` 是 Python 兼容/管理入口，`open/doctor/editors/uninstall` 需要 Python；普通原生命令不需要它。pip 包尚未发布到 PyPI，以上从本地源码安装。不会覆盖官方 `unity` 命令。

安装/升级保留现有依赖版本，把旧桥接包与 manifest 归档到 `.unity-compat-backups`；首次依赖解析可能需要联网。源码与基准响应样本在仓库中；本机临时测试项目、日志、研究下载和缓存不随仓库提交。

### 让 Agent 直接使用 CLI

Agent 只需具备终端执行能力。向它提供 ucl.exe 与 Unity 项目路径，要求先执行 status、按需查看 help/describe、优先用字段筛选和 batch。无需额外创建 Markdown 文件；如希望把指引长期写进项目，可将[Agent 用法](docs/agent-guide.md)的适用内容加入项目已有说明。MCP stdio 是可选方式，直接 CLI 的上述步骤不会启动它。

## 直接命令

在 Unity 项目目录运行时自动定位项目。其他位置使用 `--project PATH` 或设置 `UCL_PROJECT`。全局选项可放在子命令前后。

```powershell
ucl hierarchy --depth 1 --limit 40
ucl hierarchy --root BeachLobbyUI --depth 2 --components
ucl find MainCamera
ucl inspect BeachLobbyUI
ucl inspect BeachLobbyUI --component BeachMainMenu --fields startButton
ucl windows
ucl snapshot --depth 0
ucl console --type error --count 5

ucl create cube --name DemoCube --position 1,2,3
ucl add DemoCube AudioSource
ucl set DemoCube AudioSource m_Volume 0.25 --expect 1
ucl set DemoCube Transform m_LocalPosition 3,1,4
ucl delete DemoCube
```

先检查返回的 ID，使用 ID 或完整路径避免重名。`inspect` 的字段名是 Unity 序列化名称，可能与 C# 公开属性名称不同。省略组件时列出组件；指定组件后列出顶层字段。`--fields a,b,nested.field` 精确读取，数组默认只显示数量；`--all --limit N` 展开嵌套数据。Hierarchy 默认 depth=2、limit=80；检查 `truncated/depthLimited` 后再决定是否深入。

默认 human 输出紧凑的 Hierarchy/Inspector 行，其余结果为紧凑 JSON。`--json` 返回业务数据；`--envelope` 保留完整协议，适合调试。原生 CLI 错误写入 stderr，stdout 留空。退出码：0 成功、2 用法、4 不可用、6 命令失败、7 超时/执行情况不确定、8 测试失败、9 取消、10 前置条件冲突、11 不支持、12 认证失败。Python 兼容入口保留历史封装及退出约定。

## 批处理和连续任务

把普通命令写入 `commands.ucl`，每行一条：

```text
create cube --name DemoCube --position 1,2,3
add DemoCube AudioSource
set DemoCube AudioSource m_Volume 0.25 --expect 1
inspect DemoCube --component AudioSource --fields m_Volume
delete DemoCube
```

```powershell
ucl batch commands.ucl
ucl batch -                    # 从 stdin 读取
ucl shell --json               # 多个指令复用一个 CLI 进程，exit 结束
```

批处理先检查所有命令名称，再顺序执行；默认失败即停止，`--continue-on-error` 可继续。已应用的修改保留，不会自动保存场景。支持单/双引号包围含空格的参数；复杂引号使用参数文件或代码文件。也兼容原 JSON 操作清单。[完整示例](examples/quickstart.ucl)。

## 更多能力

共有 **102 个静态入口**：98 个 Editor 入口（34 个复用工具、19 个资源、45 个扩展）及 4 个原生前端接口；另有文件操作、批处理和进程管理。项目方法会额外自动注册。入口数不能直接当作独立功能数。

| 能力 | 命令 |
| --- | --- |
| 场景、物体、组件、资源、Prefab、材质、Shader、动画、UI、物理、构建、包管理 | `scene/object/component/asset/prefab/material/shader/animation/ui/physics/build/package ACTION` |
| 导航、Terrain、音频、光照、动画事件、AssetBundle | `navigation/terrain/audio/lighting` 及相关底层命令 |
| 项目设置、资源引用、GUID/meta/缺失脚本检查 | `settings/references/audit` |
| Undo/Redo、选择、类型查询、自定义扩展 | `undo/selection/type_query`、`[CliCommand]` |
| 按字段检查和带旧值检查的修改 | `inspect --fields`、`set --expect` |
| 原生输入与紧凑输出 | 普通子命令、`batch`、`shell`、`--json`、`--format tsv` |
| 无需 Editor 的文件检查与替换 | `file sha/find/replace` |

```powershell
ucl help inspect
ucl list --offline --group agent
ucl describe manage_terrain
ucl command NAME --arg key=value --params @parameters.json
ucl eval --file code.cs
ucl test --mode EditMode --timeout 120
```

`--arg` 自动识别数值/布尔/数组，其余为字符串。复用工具保留原参数名；`--params @file.json` 支持中文和 BOM。离线目录的 argumentHints/actionHints 是源码提示，并非完整 schema。按需查看帮助，避免把整个目录交给 Agent。[Agent 简明用法](docs/agent-guide.md)、[扩展命令](docs/commands.md)。

## 任务与文本修改

```powershell
ucl command NAME --arg key=value --detach --timeout 300
ucl job status JOB_ID
ucl job wait JOB_ID --timeout 300
ucl job cancel JOB_ID
ucl build build --params @build.json --wait-job --timeout 1800

ucl file sha Assets/Example.cs
ucl file find Assets/Example.cs --search OldText
ucl file replace Assets/Example.cs --from OldText --to NewText --sha HASH --dry-run
```

超时/断开后操作可能已执行，先查 job，不能自动重新提交修改。队列中的过期/旧会话请求被拒绝；同步 Unity 调用不能强制终止，取消是协作式的。域重载会中断任务，已有修改不回滚、不重放。PlayMode 测试建议 detach 启动，再查询底层 test job。

`file replace` 要求 SHA 与唯一文本匹配；先 dry-run，再移除该选项应用。支持 UTF-8，保留 BOM/换行，在原子替换前再次验证内容。仅允许 Assets 内的普通文件，拒绝越界与 symlink/junction。修改脚本后等待 Unity 编译完成并检查 console。

## 构建与验证

```powershell
scripts/build_native.ps1
python -m unittest discover -s tests -v
python scripts/compile_bridge.py --editor "C:/Program Files/Unity/Hub/Editor/2022.3.62f3c1/Editor" --reference-project validation/Unity2022
python scripts/live_native.py
python scripts/verify_pipe_protocol.py
python scripts/live_v3.py
python scripts/helper_live_v3.py
python scripts/benchmark_v3.py --project "D:/unityProject/Beach-Tank"
```

构建原生程序需要 .NET 10 SDK 与 Visual Studio C++ 工具链。实际 Editor 检查脚本只用于独立 fixture；benchmark 在 Beach-Tank 做只读查询。[架构](docs/architecture.md)、[验证记录](docs/validation.md)。

**2021.3 尚未实际运行验证；macOS/Linux 尚未实现原生通道。** VFX Graph、URP、ProBuilder、AI 生成等可选功能取决于对应包/凭据。C# eval、导入、构建和测试本身仍有耗时；更快的通道不会消除它们，也不能保证模型思考时间。

本实现复用了 MIT 授权的 [CoplayDev/unity-mcp v10](https://github.com/CoplayDev/unity-mcp/tree/v10.0.0)，新增 CLI/管道代码独立实现。没有把官方核心 CLI 宣称为已开源或已回移植。[第三方声明](THIRD_PARTY_NOTICES.md)。

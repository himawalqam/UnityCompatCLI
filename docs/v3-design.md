# 0.3 实现与使用

## 连接与 Agent 使用

普通命令直接连接 Editor 的本地命名管道。`shell` 复用前端进程，
`daemon` 允许多次独立 CLI 调用复用后台 Editor 会话，`mcp` 由 Agent
直接启动原生可执行文件，使用 stdio，免除每条指令启动 PowerShell。

```powershell
ucl daemon start --project D:\unityProject\Beach-Tank
ucl hierarchy --depth 1 --fields id,name --format tsv
ucl daemon status
ucl daemon stop
ucl shell --json
ucl mcp --project D:\unityProject\Beach-Tank --profile compact
```

daemon 是可选的隐藏本地进程，不是 HTTP/MCP 中间服务器。stdio 本身已有
连接池，无需再启动 daemon。连接池最多 8 个 Editor 连接；空闲时每 5 秒
发送认证 ping，Editor 30 秒空闲关闭未保活连接。所有 Unity API 仍由主线程
执行；并发请求不会让 Unity API 变成多线程。每轮主线程最多处理 32 项或
12ms，短期唤醒请求后 0.25 秒恢复空闲，避免长期空转。

Windows daemon 用不继承句柄的 CreateProcess 启动，以免后台进程持有
Agent 的输出管道。PID/启动时间不再匹配时只清理本项目的死 descriptor；
不会按一个旧 PID 去终止其他进程。断线后不自动重放已经传送的修改。

Agent 配置样例见 [mcp-config.example.json](../examples/mcp-config.example.json)。
默认 compact 暴露 9 个接口：查询、Eval、状态、命令路由和按需发现。
`--profile full` 在无自定义项目命令时暴露 105 个工具，其中资源也可按
工具调用；另有 19 个资源与带参数的 URI 模板。大型目录不应每次读入上下文。

`manage_tools` 的开关属于当前原生 MCP 会话；改变后重新调用 tools/list。
它与旧 MCP Editor 窗口的开关独立。`set_active_instance` 接受绝对 Unity
项目路径；当前项目名也可选中当前项目。旧服务器端口及 Name@hash 不属于
该连接架构，返回明确错误。单次 CLI 的选择仅影响该次调用；其他 CLI
命令用 `--project` 或 `UCL_PROJECT` 指定项目。

## C# 即时执行

```powershell
ucl eval 'return UnityEngine.Application.unityVersion;'
ucl eval --file examples/eval.cs --metrics
ucl eval 'return (int)args["value"];' --arg 'args={"value":42}'
```

独立重定位的 Roslyn 3.8 在内存中编译，不写入 Assets，不触发脚本编译或
Domain Reload。默认只返回结果；`--metrics` 返回编译时间、缓存命中、日志、
session 等证据。冷启动、首次编译、不同代码编译和缓存执行的成本不同。
相同代码使用 args 传值可复用编译结果。每个 domain 最多 256 个不同的成功
编译片段；Mono 无法单独卸载程序集，达到上限需显式重载 domain。

Eval 具有 Editor 本地进程的完整权限。管道 Token 与 Windows 用户 DACL
提供调用者认证，不能使任意 C# 成为安全沙盒；不声明能够隔离文件、进程或
网络访问。超时不能强制安全终止正在运行的同步 Unity/C# 代码。

## 方法扩展

在项目 Editor 脚本中添加静态方法，无需编写 JSON handler：

```csharp
using UnityCompat.Editor;
public static class ProjectCommands {
    [CliCommand("project_echo", "Project helper", Group = "project")]
    public static object Echo(
        [CliArg("message", Required = true)] string message,
        [CliArg("count")] int count = 2) => new { message, count };
}
```

```powershell
ucl describe project_echo --json
ucl command project_echo --message 中文 --count 3
```

注册器生成参数 schema，支持默认值、枚举、nullable、数组、JObject/JToken
及 Task 返回值。缺参、未知参数、类型转换失败提供结构化错误。
`MainThreadRequired=false` 适合纯计算/网络方法，不得在工作线程调用 Unity API。
generic、ref/out 方法不注册。原有 class + JObject 扩展仍可使用。

## 基础动作、投影和状态

```powershell
ucl play
ucl pause
ucl step
ucl stop
ucl recompile
ucl refresh
ucl build-targets --installed
ucl test --mode EditMode --timeout 90
ucl screenshot --width 640 --height 360
ucl inspect MainCamera --component Transform --fields 'm_LocalPosition.*' --limit 10
ucl hierarchy --depth 2 --fields id,parent,name --format tsv
```

播放/停止、刷新/重编译先返回 `transition_requested`，随后观察状态或新
session。原生 test 只启动一次，获得 Test Runner job ID 后跨 domain 重连
读取状态。测试失败退出 8；不会替调用者自动保存未保存场景。

screenshot 渲染场景 Camera 到 PNG，恢复原 RenderTexture 状态；不是整个
Editor 窗口截屏，ScreenSpaceOverlay UI 不包含在 Camera.Render 中。无图形
支持返回 UNSUPPORTED；输出路径限定项目内，不覆盖已有文件。

Inspector 使用序列化路径与 `prefix.*`；深度、字段数、Hierarchy 节点数均有
上限，并返回截断标志。TSV 仅输出选定列。整个 Inspector、完整 Hierarchy
与几个字段是不同信息量，比较 Token 时必须说明任务范围。

错误以 `{success:false,state,exitCode,error:{code,message},data}` 写入 stderr。
协议响应具有 completed/failed 状态；超时返回 unknown 和可检查的 job ID。
排队取消与异步取消是合作式的，已生效操作不回滚。读取可以在重载后重试，
修改不因超时或重载自动重放。

## 覆盖与边界

现有 47 个 MCP 工具名称中，34 个 Editor handler 被复用；9 个原服务器
脚本/自定义工具 helper 新增对应 Editor 路由；4 个会话/文档接口由原生前端
处理。对应名称均可发现，但并非完整的旧服务器协议二进制替换：实例选择
改为项目路径，工具开关改为原生会话，文档 lookup 为并行获取确定的官方
页面候选，不含模糊索引搜索。`unity_docs` 支持 get_doc/get_manual/
get_package_doc/lookup，单页文本上限 12,000 字符，HTTP 错误/未命中明确返回。

云端资产生成、ProBuilder、URP、Navigation 等依赖原项目安装的包或供应商
凭据；保留真实不支持/错误结果。98 个 Editor 入口不代表全部可选能力均在
每个 Unity 版本实测。最低目标为 2021.3 LTS；2021.1/2021.2 不在当前安装
范围。只有本机 2022.3.62f3c1 完成真实运行验证，2021.3 与 Unity 6、macOS/
Linux 仍需各自的 Editor 矩阵。

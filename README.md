# Unity Compat CLI

在 Windows 命令行中查看和操作 Unity 项目，可用于人工操作、脚本自动化和 Agent 开发。

当前已实测 **Unity 2022.3.62f3c1**。CLI 不需要外部 MCP server 或 HTTP 端口。Windows ZIP 包可以直接运行，无需安装 Python 或 .NET 运行时。

使用时需要打开 Unity 项目，并在该项目中安装本工具的桥接包。桥接包是安装在 Unity Editor 内的插件，负责执行命令行发来的操作。

## 第一次使用

下面使用两个示例路径，请替换成自己的目录：

| 路径 | 含义 |
| --- | --- |
| `D:\Tools\UnityCompatCLI` | 工具解压目录 |
| `D:\UnityProjects\MyGame` | Unity 项目根目录，包含 Assets、Packages、ProjectSettings |

### 1. 下载并解压

下载 [UnityCompatCLI-0.3.0-Windows.zip](https://github.com/himawalqam/UnityCompatCLI/raw/refs/heads/main/releases/UnityCompatCLI-0.3.0-Windows.zip)，解压到工具目录。

保留整个解压目录，其中包括 `ucl.exe`、`bridge`、命令说明文件和许可证。不要只复制 `ucl.exe`。

**下面的命令使用程序完整路径，不需要设置 PATH 或其他环境变量。** 请在 PowerShell 中执行；开头的 `&` 表示运行后面指定的程序。

### 2. 给 Unity 项目安装桥接包

可以使用已有项目，也可以先用 Unity Hub 创建一个新项目。项目创建完成后，关闭该项目的 Unity Editor，运行：

```powershell
& "D:\Tools\UnityCompatCLI\ucl.exe" install --project "D:\UnityProjects\MyGame"
```

命令会将桥接包放入项目的 `Packages/com.unitycompat.cli`，并补充缺少的依赖。首次下载依赖可能需要联网。已有依赖版本会保留，升级前的桥接包和配置会备份。

**同一个项目只需首次安装或升级工具时执行 install。** 每个新项目需要分别安装。

### 3. 打开 Unity，检查是否可以使用

用 Unity Hub 打开该项目，等待导入和编译结束，再运行：

```powershell
& "D:\Tools\UnityCompatCLI\ucl.exe" status --project "D:\UnityProjects\MyGame"
```

结果中出现 `"state":"ready"` 表示可以执行命令。无需在 Unity 中点击连接按钮或启动 MCP。

## 日常使用

保持 Unity Editor 打开。在任意 PowerShell 目录中都可以用完整路径执行：

```powershell
# 查看场景层级
& "D:\Tools\UnityCompatCLI\ucl.exe" hierarchy --depth 2 --limit 50 --project "D:\UnityProjects\MyGame"

# 查看 MainCamera 上 Transform 的位置字段
& "D:\Tools\UnityCompatCLI\ucl.exe" inspect MainCamera --component Transform --fields m_LocalPosition --project "D:\UnityProjects\MyGame"

# 创建一个立方体
& "D:\Tools\UnityCompatCLI\ucl.exe" create cube --name DemoCube --position 0,1,0 --project "D:\UnityProjects\MyGame"
```

`MainCamera` 需要在当前场景中存在。名称重复时，使用查询结果里的对象 ID。场景修改不会自动保存；需要保存时在 Unity 中按 `Ctrl+S`。

如果要连续输入多条命令，可以先进入工具的命令会话：

```powershell
& "D:\Tools\UnityCompatCLI\ucl.exe" shell --project "D:\UnityProjects\MyGame"
```

进入后，每行输入一条命令，无需再写 exe 路径和项目路径：

```text
hierarchy --depth 1 --limit 40
inspect MainCamera --component Transform --fields m_LocalPosition
play
stop
exit
```

`exit` 只结束命令会话，不关闭 Unity。重新打开会话时，再执行上面的 shell 命令即可。

## 项目关闭或电脑重启后

| 情况 | 操作 |
| --- | --- |
| 关闭后重新打开同一个 Unity 项目 | 等待编译完成，执行 status，ready 后继续使用；无需重新安装 |
| 电脑重启或换一个终端窗口 | 打开 Unity 项目，直接使用 exe 完整路径；无需重新设置 PATH |
| 新建或换到另一个 Unity 项目 | 对新项目执行一次 install，打开项目后使用它的项目路径 |
| 移动了工具或项目目录 | 将命令中的路径改成新位置；项目内的桥接包随项目保留 |
| 更新了工具版本 | 解压新版工具，对需要更新的项目执行 install，然后等待 Unity 重新加载 |

Unity 启动时会自动加载已安装的桥接包。CLI 会读取当前连接信息，不需要手动记录或填写连接地址。常驻 shell 会在后续调用时检查新的 Unity 会话；Unity 尚未恢复 ready 时，命令会返回不可用或连接错误。

如果一条修改命令执行途中断开或超时，不要立即重复执行；先检查修改是否已经生效，或按返回的任务 ID 查询状态。

## 让 Agent 使用

Agent 需要有运行终端命令的能力。第一次使用时，提供工具路径、项目路径和以下要求：

```text
使用 Unity Compat CLI 操作此项目。
程序：D:\Tools\UnityCompatCLI\ucl.exe
项目：D:\UnityProjects\MyGame

通过终端执行 CLI，不使用 MCP。
先运行 status，确认 ready 后再操作。
按需查看 help 和 describe；读取 Inspector 时只取需要的字段。
```

**Unity 项目重启不会让桥接包丢失，Agent 无需重新安装工具。** Agent 继续当前会话时，只需重新检查 status。开启新的 Agent 会话时，需要让它知道以上路径和要求。

如果使用支持读取 `AGENTS.md` 的 Agent，可以将上述内容写入 Unity 项目根目录的 `AGENTS.md`，让它在新会话中读取。已有该文件时追加相关说明即可。这是可选的使用指引，不是工具运行依赖；无需为使用 CLI 另外创建 Markdown 文件。

更多参数和操作约定见 [Agent 使用说明](docs/agent-guide.md)。

## 功能与帮助

| 功能 | 命令示例（在 shell 会话内执行） |
| --- | --- |
| 场景层级、对象查找 | `hierarchy`、`find MainCamera` |
| Inspector 读取和修改 | `inspect MainCamera --component Transform`、`set` |
| 物体和组件操作 | `create`、`add`、`delete` |
| Editor 窗口、错误日志、状态 | `windows`、`console --type error --count 5`、`snapshot` |
| 播放、暂停、单帧、停止 | `play`、`pause`、`step`、`stop` |
| 资源、Prefab、材质、场景和构建 | `asset`、`prefab`、`material`、`scene`、`build` |
| C# 执行、测试、重编译 | `eval --file code.cs`、`test --mode EditMode`、`recompile` |
| 更多命令及参数说明 | `list`、`describe manage_scene` |

普通 PowerShell 中可这样查看帮助，无需打开 Unity：

```powershell
& "D:\Tools\UnityCompatCLI\ucl.exe" help
& "D:\Tools\UnityCompatCLI\ucl.exe" help inspect
```

已确定的多个步骤可以写入文本文件，每行一条命令，然后一次执行：

```powershell
& "D:\Tools\UnityCompatCLI\ucl.exe" batch "D:\Tasks\commands.ucl" --project "D:\UnityProjects\MyGame"
```

文件格式见 [批处理示例](examples/quickstart.ucl)。批处理默认在第一条错误处停止，已执行的修改保留。

## 使用范围

- Windows x64；当前实际运行验证版本为 Unity 2022.3.62f3c1。
- 安装程序要求 Unity 2021.3 或更新版本。Unity 2021.3、Unity 6 的实际运行验证尚未完成；macOS/Linux 原生通道尚未实现。
- 可选功能依赖相应的 Unity 包或服务凭据，例如 VFX Graph、ProBuilder 和在线资源生成。
- C# Eval 在本地 Editor 权限下运行。截图使用 Camera.Render，不包含整个 Editor 窗口或 ScreenSpaceOverlay UI。
- 可选 daemon、MCP stdio 及 Python 管理入口的说明见 [架构说明](docs/v3-design.md)。本文安装步骤使用普通 CLI。

## 开源来源与致谢

本项目复用或参考了以下项目与文档，感谢原作者的工作：

| 来源 | 使用方式 |
| --- | --- |
| [CoplayDev/unity-mcp（MCP For Unity）](https://github.com/CoplayDev/unity-mcp)，[v10.0.0 源码](https://github.com/CoplayDev/unity-mcp/tree/v10.0.0/MCPForUnity) | 复用 MIT 授权的 Unity Editor 工具处理器和辅助代码，并进行命名空间隔离、兼容适配与接口扩展；原许可证保留在 [LICENSE-Coplay.txt](src/unity_compat/bridge/LICENSE-Coplay.txt) |
| [Unity 官方 CLI 文档](https://docs.unity.com/en-us/unity-cli/unity-cli-reference)、[Unity 官方 CLI skill](https://github.com/Unity-Technologies/skills/tree/main/skills/unity-cli)、[com.unity.pipeline 包](https://packages.unity.com/com.unity.pipeline) | 参考命令组织、连接生命周期、自定义命令和实时 C# 执行的架构；未分发 Pipeline 源码、DLL 或官方 CLI 程序 |
| [fujisheng/UnityCli](https://github.com/fujisheng/UnityCli) | 参考本地命名管道、认证和 Unity 主线程调度设计；未复制其实现代码 |
| [vercel-labs/agent-browser](https://github.com/vercel-labs/agent-browser) | 参考原生命令行、常驻进程和按需返回数据的组织方式；未复制其实现代码 |
| [Microsoft Roslyn](https://github.com/dotnet/roslyn) | 使用其编译器实现内存中的 C# 编译；保留许可证，调整程序集和命名空间以避免 Unity 插件冲突 |
| [.NET Runtime](https://github.com/dotnet/runtime)、[Mono.Cecil](https://github.com/jbevain/cecil) | 分别用于原生 CLI 编译与编译器程序集处理；许可证及依赖记录见第三方声明 |

本项目使用 [MIT License](LICENSE)，并保留所复用代码与依赖的许可证。完整来源、版本和修改记录见 [第三方声明](THIRD_PARTY_NOTICES.md)、[桥接代码修改记录](docs/bridge-changes.md)和[架构参考资料](docs/cli-research.md)。本项目是独立工具，不是 Unity 官方产品。

## 开发与验证资料

源码目录：`native/Ucl` 为原生命令行程序，`src/unity_compat/bridge` 为 Unity 桥接包，`scripts` 为构建和测试脚本。源码构建需要 .NET 10 SDK 与 Visual Studio C++ 工具链；下载 ZIP 后使用不需要这些开发工具。

- [命令扩展说明](docs/commands.md)
- [验证记录](docs/validation.md)
- [0.3 实测报告与原始数据](docs/benchmark-v3-20261011.md)
- [0.2 历史基准报告](docs/benchmark-v2-20261011.md)

Python 包尚未发布到 PyPI。需要 Python 管理入口时，在源码仓库根目录运行 `python -m pip install .`；日常使用按本文 Windows ZIP 步骤即可。

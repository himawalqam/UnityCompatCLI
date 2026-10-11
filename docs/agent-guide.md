# Agent 使用说明

本工具通过终端命令操作正在运行的 Unity Editor。使用普通 CLI 时不需要 MCP 配置。

## 提供路径

需要告诉 Agent 两个路径：工具解压目录中的 `ucl.exe`，以及包含 Assets、Packages、ProjectSettings 的 Unity 项目根目录。

下面是可复制给 Agent 的指引；请替换示例路径：

```text
使用 Unity Compat CLI 操作此项目，通过终端执行命令，不使用 MCP。
工具：D:\Tools\UnityCompatCLI\ucl.exe
项目：D:\UnityProjects\MyGame

每次开始操作先执行 status，确认 ready。
按需执行 help COMMAND 或 describe NAME 查询参数。
读取 Hierarchy 时限制 depth 和 limit，读取 Inspector 时用 fields 指定字段。
对象重名时使用查询返回的 ID。
一次可确定的多个操作用 batch；需要逐步判断时可用 shell。
修改失败或超时后先检查实际状态，不自动重复提交修改。
场景修改不自动保存，按任务要求保存。
```

支持读取 `AGENTS.md` 的 Agent 可以从项目根目录中的该文件获取上述指引。已有文件时追加，不要覆盖原有要求。其他 Agent 在新会话中需要重新提供这些信息。该文件不是 CLI 运行依赖。

## 调用方式

PowerShell 使用完整路径，无需设置 PATH：

```powershell
& "D:\Tools\UnityCompatCLI\ucl.exe" status --project "D:\UnityProjects\MyGame"
& "D:\Tools\UnityCompatCLI\ucl.exe" hierarchy --depth 1 --limit 40 --project "D:\UnityProjects\MyGame"
& "D:\Tools\UnityCompatCLI\ucl.exe" inspect MainCamera --component Transform --fields m_LocalPosition --project "D:\UnityProjects\MyGame"
```

如果 Agent 的终端工具支持选择 shell，普通命令可使用 cmd.exe，命令形式如下；cmd.exe 中不加 PowerShell 的 `&`：

```bat
"D:\Tools\UnityCompatCLI\ucl.exe" status --project "D:\UnityProjects\MyGame" --json
```

在本机基准中，新启动 PowerShell 额外增加了数百毫秒；cmd.exe 可用于只需执行程序的调用。这个结果不代表所有电脑上的固定差值。

## 控制读取范围

先查询需要的对象，再读取具体组件和字段。字段名是 Unity 序列化字段名，可能与 C# 公开属性不同。

- `inspect TARGET` 列出组件；添加 `--component TYPE` 查看该组件字段。
- `--fields a,b,nested.field` 指定字段；数组元素可用 `items.Array.data[0].value`。
- `--all --limit N` 展开嵌套数据。
- 检查 `truncated` 和 `depthLimited`，不要把截断结果当作完整场景。
- `--json` 返回业务数据；`--envelope` 包含传输和任务信息。

## 连续任务与项目重启

`batch FILE` 读取每行一条命令的文件，一次提交多个操作。默认失败即停止；已应用的修改保留。`shell --project PATH` 复用 CLI 进程，输入 exit 结束；只在命令会话内可以省略程序路径。

同一 Unity 项目重启后，已安装的桥接包会自动加载。等待 status 为 ready，再继续操作；无需重新执行 install。CLI 后续调用会检查当前 Unity 会话。

新 Agent 会话不会自动继承之前对话中的路径说明；让它读取项目使用指引，或再次提供工具和项目路径即可。新终端使用完整路径时无需恢复环境变量。

## 修改与错误处理

错误写入 stderr，非零退出码表示失败。超时或断开时操作可能已经执行；查看返回的任务 ID，用 job status / job wait 查询，不要自动重新提交。取消是协作式的，同步 Unity 操作不能强制终止；已应用的修改不回滚。

文本替换先通过 file find 获取 SHA，再使用 file replace --sha HASH --from OLD --to NEW --dry-run 检查；确认后移除 dry-run。修改脚本后等待 Unity 编译结束并检查错误日志。

`eval --file code.cs` 在内存中编译 C#，不会为执行代码创建项目脚本或触发域重载。重复代码可复用缓存，首次或新代码需要编译。代码以本地 Editor 权限运行。

安装步骤和功能入口见 [README](../README.md)，接口扩展见[命令说明](commands.md)。

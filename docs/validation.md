# 验证记录

以下为 0.1 历史记录。验证日期：2026-10-11。Windows，本机 Python 3.12，Unity 2022.3.62f3c1。该阶段的测试项目为仓库中的 `validation/Unity2022`，当时尚未向 Beach-Tank 安装工具。0.3 的当前结果见文末。

## 已通过

- 三个 C# 程序集针对已安装 Unity 2022 及其 .NET 参考程序集编译，0 errors，C# 9.0。
- 实际 Unity Editor 自己的 Package Manager/脚本编译和域加载成功。
- Python 10 项测试：前后位置的全局选项、中文/JSON 参数、失效心跳/项目匹配、10 个并发请求、超时不重放、响应 ID 验证、路径/大小校验、安装保留版本、升级失败保留原包、卸载归档和版本检查。
- 35 项真实 Editor 检查：发现 74 个命令（73 个内置 + 1 个项目扩展）；中文扩展命令；未知命令/参数错误；C# eval；创建立方体并核对位置；序列化检查；选择；读取重力；设置并读取 companyName；场景和资源审计；类型查询；音频/光照/NavMesh 状态；Terrain 创建、写高度图并回读；依赖/反向引用；导入器和 AssetBundle 列表；批处理失败停止和批处理预检；旧会话拒绝；8 个并发命令；排队取消和过期请求不执行；Console；保存场景；EditMode 测试成功。
- 2 项生命周期检查：故意失败的 EditMode 测试返回失败且进程退出码为 6；异步命令执行中请求 domain reload，结果为 INTERRUPTED，并不重放。
- pip wheel 构建、完整桥接源码/第三方许可证打包校验、从 wheel 独立安装后发现 73 个命令，以及从安装后的 wheel 向另一个临时项目安装桥接包，均通过。测试 Editor 已通过 CLI 正常退出。

机器可读记录：[live-validation.json](live-validation.json)、[lifecycle-validation.json](lifecycle-validation.json)。测试中只使用 Editor 内桥接包和 CLI，没有 MCP/HTTP server 进程。

测试发现并修复了：Windows 短暂文件共享冲突、Unity 类型的 JSON 序列化、测试结果为空时的解析、失败测试退出码，以及未保存场景在 batchmode 中触发保存对话框的问题。

## 尚未完成的测试范围

- Unity 2021.3 和 Unity 6 的实际安装/编译/运行矩阵。
- macOS/Linux Editor 定位和文件系统行为。
- 所有上游工具的全部 action，尤其是可选包、图形截图、Profiler、VFX Graph、ProBuilder、在线模型/图片生成。
- 实际 Player/AssetBundle 输出构建、完整 PlayMode 测试重载流程和跨平台打包。
- 多个 Editor 同时打开时的压力测试；目标选择已明确要求项目路径，邮箱位于各自项目中。

因此此版本是可运行并经过核心流程验证的第一版，不应描述成已完成所有 Unity 版本/平台/工具动作的全面认证。

## 0.2 历史验证

- 三个桥接程序集针对 Unity 2022 参考程序集编译：零错误；Windows NativeAOT 发布：零警告。
- 19 项离线/协议单元检查、47 项真实 Editor 原生 CLI 检查、5 项管道协议检查通过。
- 真实 Editor 检查包含普通命令创建/删除、组件添加、序列化字段与旧值检查、向量、中文、批处理、24 个并发读取、C# 错误、持久任务；协议检查包含重复请求保留原结果、旧会话、过期、认证与超时后恢复。
- Beach-Tank GUI 与独立 headless fixture 各 640 次计入基准的读取，共 1,280/1,280 成功；GUI 测试前后 active scene 数据相同。
- Beach-Tank 已安装 0.2 桥接包；本轮未修改游戏逻辑或场景内容。独立 fixture 已通过 CLI 正常关闭。

原始记录在 `artifacts/native-live.json`、`artifacts/pipe-protocol.json` 和 `artifacts/benchmark-v2-20261011/`；测试方法、实际 Agent shell 开销与 Token 代理计数见[新版报告](benchmark-v2-20261011.md)。Unity 2021.3 尚未运行验证，macOS/Linux 原生通道仍未完成。

## 0.3 当前验证

- 三个 C# 桥接程序集编译零错误；Windows NativeAOT 发布零警告。最终二进制重新运行 24 项离线/协议测试，全部通过。
- 47 项真实 Editor 原生 CLI 检查、37 项新增功能及生命周期检查、5 项管道协议检查通过。包含中文强类型自定义方法、异步方法、参数默认值和错误、Roslyn 编译诊断/缓存/无域重载、深层字段、播放/暂停/单帧/停止、重编译，以及跨域重载的 EditMode 测试等待。
- 16 项脚本辅助/失败测试检查通过，包含已有文件不覆盖、SHA 与文本修改、旧 SHA 拒绝且退出码 10、越界拒绝、验证/查找、扩展调用、删除，以及故意失败测试返回退出码 8。只修改独立 fixture，测试代码最终恢复。
- 官方 Python MCP SDK 2.3.0 真实 stdio 互操作通过：full profile 发现 108 个工具（105 个标准入口/代理工具加 3 个 fixture 方法）、强类型方法、中文、标量 Eval、错误及资源读取。24 个并发请求、35 秒空闲后复用、工具组、官方文档读取、实例选择和协作取消也已验证。
- live `list --group agent` 在 Beach-Tank 只返回 5 个 Agent 入口，不混入前端工具。
- 在带图形 fixture 实际产生并解码 640×360 的相机 PNG，像素非均匀；无图形模式明确返回 UNSUPPORTED。截图为 Camera.Render，不含 ScreenSpaceOverlay UI，不能当作 Editor 窗口截图。
- Beach-Tank GUI 与独立带图形 fixture 各 460 次计入串行统计的调用、144 次小查询并发调用；另有独立 500 节点测试 40 次串行和 144 次并发。查询断言业务结果相同，GUI 测试前后场景数据相同；详见[0.3 实测报告](benchmark-v3-20261011.md)及随附原始数据。

原始功能记录在 `artifacts/native-live-v3.json`、`live-v3.json`、`pipe-protocol-v3.json`、`helpers-v3.json`、`mcp-sdk-v3.json` 和 `mcp-advanced-v3.json`。所有统计仅对应当前机器，不包含模型推理/计费；工具入口存在不代表所有 action 通过验证。Unity 2021.3/Unity 6 实机矩阵、macOS/Linux、完整 Player 构建及所有可选包仍未验证。

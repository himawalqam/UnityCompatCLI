# 新增命令参考

统一调用：`ucl command NAME --arg key=value --project-path PROJECT --json`。下面的 `action` 参数也可以通过快捷命令的位置参数提供。

| 命令 | 动作和参数 |
| --- | --- |
| `cli_list` | 可选 `group`；发现当前 Editor 的全部命令 |
| `cli_describe` | `name`；返回命令来源、分组、异步/轮询元数据 |
| `cli_batch` | `operations` 数组，每项 `{command,params}`，1–256 项；`continue_on_error` 默认 false；不允许嵌套 batch |
| `cli_poll` | `command` 和 `params`；等待工具内部的 PendingResponse 或测试 job 完成 |
| `project_settings` | `action=list/get/set`；`section=player/quality/editor/physics/physics2d/time/render`；get/set 需要 `property`，set 需要 `value`；只支持公开静态属性 |
| `asset_references` | `action=dependencies/referencers`，`path` 为资源路径或 GUID；dependencies 的 `recursive` 默认 true；referencers 的 `limit` 默认 1000、最多 10000，返回是否截断 |
| `project_audit` | `action=scene/assets`；scene 检查已加载的场景；assets 的 `limit` 默认 5000、最多 100000，返回是否截断 |
| `serialized_inspect` | `path` 指向资源；或 `target` 指向场景物体，可选 `component`；可选 `prefix`、`limit`（默认 300、最多 2000）；读取原生序列化属性 |
| `asset_importer` | `action=get/set/reimport`，`path`；set 需要 `property`、`value`，支持整数/布尔/浮点/字符串/枚举；修改后重新导入 |
| `undo_history` | `action=status/undo/redo/begin/collapse`；begin 可选 `name`，collapse 可选 `group` |
| `manage_selection` | `action=get/set/clear/frame`；set/frame 需要 `target` |
| `scene_snapshot` | 可选 `limit`，默认 1000、最多 10000；返回物体位置/旋转/缩放/组件列表 |
| `type_query` | `query`、可选 `components_only`、`members`、`limit`（默认 100、最多 1000） |
| `manage_terrain` | `action=list/create/get_heights/set_heights`；create：`path`（未使用的 Assets 下 .asset）、`name`、`resolution`（33/65/129/257/513）、`size=[x,y,z]`；heights：`target`、`x/y`、`width/height`，set 还要二维 `heights`，数值在 [0,1] |
| `manage_audio` | `action=list/clips/set/play/stop`；set/play/stop 需要 `target`；set 可选 `clip`（资源路径）、`volume`、`loop`、`spatial_blend`、`mixer`（AudioMixer 路径）和 `group`（匹配唯一分组） |
| `manage_navigation` | `action=status/sample/path/destination`；sample：`position`、可选 `distance/area_mask`；path：`from/to`、可选 `area_mask`；destination：`target` 和 `position`，要求 Agent 已在 NavMesh 上 |
| `manage_lighting` | `action=status/list/bake/cancel`；bake 异步启动，后续用 status 查询，不自动等待 |
| `animation_events` | `action=get/set`，`path` 为 AnimationClip 资源；set 的 `events=[{time,function,string,int,float}]`，time 不能超过 clip 长度；导入模型内的剪辑应修改 importer |
| `asset_bundles` | `action=list/set/build`；set：`path/name/variant`；build：可选 `output`（项目内目录，默认 Builds/Bundles）、`target`（BuildTarget 枚举）；调用真实 BuildPipeline |
| `editor_shutdown` | 可选 `discard=true` 显式丢弃未保存场景；否则拒绝退出；返回响应后安排关闭 |

`target` 支持物体 instance ID、唯一名称或完整层级路径；多个物体匹配会失败，不会擅自选择。不同场景中存在同名/同路径物体时使用 instance ID。

复用的 MCP 工具保留上游参数结构。用 `ucl describe NAME --offline --json` 查看源码路径和参数提示，再参考对应 C# 实现。`--params @file.json` 最适合嵌套 JSON。

## 0.2 Agent 命令

这些命令还提供普通 CLI 子命令，无需手写参数 JSON。

| Editor 入口 / CLI | 用法 |
| --- | --- |
| `agent_hierarchy` / `hierarchy` | `--depth` 默认 2、`--limit` 默认 80；可选 `--root` 与 `--components`；显式返回截断/深度限制 |
| `agent_find` / `find` | 名字/路径子串；可选组件过滤，默认最多 30 个结果 |
| `agent_inspect` / `inspect` | 目标 ID/名称/路径，`--component`、`--fields a,b,nested.field`；可选 `--all`、`--types`、`--limit` |
| `agent_set` / `set` | `set TARGET COMPONENT FIELD VALUE`；可选 `--expect OLD_VALUE`；Undo、Dirty 与 Prefab 修改记录 |
| `agent_snapshot` / `snapshot` | 一次返回 Hierarchy 摘要、窗口、选择和 Editor 状态；支持深度/数量/组件选择 |

字段使用 Unity 序列化名称；数组默认显示数量。完整普通命令示例与 Windows Agent 调用方式见[Agent 指南](agent-guide.md)。

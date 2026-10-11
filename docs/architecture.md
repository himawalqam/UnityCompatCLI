# Architecture

Primary Windows path: NativeAOT CLI → framed local named pipe → EditorApplication.update → shared main-thread command registry → Unity API → framed response.

Compatibility/detached path: Python/native client → atomic JSON mailbox → the same gate and registry → durable response file. No HTTP listener, MCP negotiation or external service process is required. The editor package must be loaded inside a live Editor; headless Unity is supported and still requires normal licensing.

## Native transport

`endpoint.json` holds a per-domain pipe name, project, session and auth token. Frames use a little-endian 32-bit length followed by UTF-8 JSON (4 MiB request / 64 MiB response limit). A current-user Windows DACL restricts access. Unity Mono's incomplete WindowsIdentity API is bypassed by querying the SID through Win32. The token is checked before dispatch. Framing/parsing run on workers; all Unity operations run on the main thread.

Both transports share one execution gate. Pipe commands retain durable running/response records; callers can inspect timed-out jobs. Duplicate IDs are rejected without overwriting the original result. Expired/session-mismatched work cannot start. Clients check response identity and never automatically retry or fall back after transmission. Domain reload stops listeners and interrupts callers.

`shell` reuses the caller's CLI process and creates no background service. Optional
`daemon` reuses up to eight Editor connections across independent CLI processes.
Native `mcp` provides newline JSON-RPC stdio and its own session pool; it does not
need the daemon or an HTTP relay. `batch` parses ordinary commands and sends one
sequential Editor request. Human/JSON output removes protocol and success wrappers;
`--envelope` retains them. Hierarchy depth and serialized field selection bound
payloads at the source. Detailed 0.3 semantics are in [v3-design.md](v3-design.md).

## Wire contract

Project mailbox: `Library/UnityCompat`.

- `status.json`: protocol 1, session nonce, project root, Unity version, PID, Unix UTC heartbeat and state.
- `requests/<uuid>.json`: protocol, ID, session, command, params object, created/deadline (Unix UTC).
- `running/<uuid>.json`: mailbox request renamed to claim it, or pipe request persisted before execution.
- `responses/<uuid>.json`: protocol, matching ID, success, data or error, completion time, session.
- `cancellations/<uuid>.json`: cancellation marker.

Clients write `.tmp` then rename. Unity's shared gate serializes execution and
drains up to 32 completed requests or 12ms per callback. Main-thread commands use
Unity's SynchronizationContext; explicitly background project methods may execute
away from it and must avoid Unity APIs. Windows sharing conflicts are retried
briefly. Maximum request size: 4 MiB; batch size: 256. Durable job records remain
in Library for inspection and are not automatically pruned.

Each assembly load creates a new session. Old-session requests are rejected before any mutation. Already-claimed work at reload/restart receives INTERRUPTED and is never replayed. That status cannot prove a mutation did not happen: the editor may stop between an API side effect and writing a response. Batch and async polling cancellation is cooperative; synchronous engine calls cannot be forcibly stopped safely. This protocol does not promise transactional rollback or exactly-once side effects under process crashes.

The mailbox has the same trust boundary as the project directory. Local scripts that can write the project can issue commands, including C# evaluation. It is intended for local developer automation, not a remotely shared multi-user service.

## Command implementation

`UnityCompatVendor.Runtime`/`UnityCompatVendor.Editor` contain namespaced Coplay v10 tools and dependencies. `UnityCompat.Editor` owns mailbox lifecycle, discovery, flow control and additional commands. It reflects `[McpForUnityTool]`, `[McpForUnityResource]`, and `[CliCommand]`; it calls handlers directly, ignoring MCP server group visibility. Legacy transports/configuration/windows remain as dormant source dependencies; their auto-load, auto-start, setup, config rewrite, migrations and menu hooks are disabled. No CLI command path invokes their server startup automatically.

Optional vendor features require their own installed Unity packages or external providers. The CLI reports the original tool's unsupported/error response instead of presenting a stub as successful. All returned tool failures produce a non-zero CLI exit code.

## Compatibility

Initial minimum: Unity 2021.3, C# 9.0. New commands use public 2021-era APIs. Upstream compatibility helpers handle several Unity API differences; editor internals such as Console reflection remain version-sensitive. Unity 2022 actual compilation/execution is checked locally; Unity 2021.3 and newer Unity 6 releases need their own editor matrix. The Windows Python and filesystem behavior is validated; macOS/Linux packaging/editor discovery is future work.

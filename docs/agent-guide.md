# Agent quick reference

Run from the Unity project, or add `--project PATH`. Use `ucl`, the Windows native entry, for normal work. `unity-compat` is the slower Python mailbox and administration fallback.

In Codex on Windows, run plain `ucl` commands with `exec_command(shell="C:/Windows/System32/cmd.exe", login=false)`. Starting PowerShell for every simple command added hundreds of milliseconds in the measured environment; native executable timing alone misses that overhead. Use PowerShell when the command actually needs PowerShell syntax. Group independent operations into one `batch` or `snapshot` call.

Start with `ucl status`, then the narrowest command that answers the task:

```powershell
ucl hierarchy --depth 1 --limit 40
ucl find MainCamera
ucl inspect MainCamera
ucl inspect MainCamera --component Transform --fields m_LocalPosition
ucl windows
ucl console --type error --count 5
```

Use returned IDs to avoid ambiguous object names. Serialized field names can differ from public C# properties; `inspect TARGET --component TYPE` shows them. `--fields` accepts comma-separated nested serialized paths. Arrays default to their count; explicit `--fields items.Array.data[0].value` reads an element. `--all` walks nested properties, up to `--limit`.

```powershell
ucl create cube --name TestCube --position 1,2,3
ucl add TestCube AudioSource
ucl set TestCube AudioSource m_Volume 0.25 --expect 1
ucl set TestCube Transform m_LocalPosition 3,1,4
ucl delete TestCube
```

Group known operations in `ucl batch commands.ucl`; normal commands, one per line. `ucl batch -` reads stdin. All command names are validated before mutation. Operations stop on the first error; applied changes are retained. For sequential decisions use `ucl shell --json`, which reuses the CLI process. Neither needs an external service.

Default human output is compact. `--json` returns compact domain data; `--envelope` preserves transport and tool metadata. A nonzero exit code means failure. Check `truncated` / `depthLimited` before assuming complete coverage.

Use `ucl help COMMAND` and `ucl describe NAME` on demand. Native MCP starts with
9 compact tools; use `ucl_discover` to obtain only the necessary command schema.
Connect the native exe directly with `mcp --project PATH`, not through PowerShell.
It already keeps Editor connections alive. Optional daemon only helps standalone
CLI calls; it cannot remove the caller's own shell startup overhead.

Static `[CliCommand]` / `[CliArg]` methods support typed ordinary arguments.
`eval --file code.cs` uses in-memory Roslyn without domain reload; reuse the code
with args for cache hits. First and new-snippet compilations cost more than cached
execution. This is trusted local C#, not a security sandbox.

On timeout/disconnection, inspect the returned ID with `ucl job status ID` or `ucl job wait ID`. Do not reissue a mutation automatically. Queued work can be cancelled; already-applied changes are not rolled back. Save scenes explicitly only when the task calls for it. PlayMode tests, asset imports and builds have their own execution costs.

For text edits, `ucl file find Assets/file --search TEXT` returns a SHA. `ucl file replace ... --sha HASH --from OLD --to NEW --dry-run` previews an exact single replacement. Remove `--dry-run` to apply. UTF-8 BOM and newlines are preserved. Ordinary source editing tools remain available.

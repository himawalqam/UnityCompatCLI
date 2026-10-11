# Unity Compat CLI Editor Package

Unity 2021.3+ compatibility target; actual testing currently covers Unity 2022.3.62f3c1 on Windows.

Install the Python `unity-compat-cli` package, then run `unity-compat install --project-path PROJECT`.
This package is automatically loaded in the Editor. Query `unity-compat status --project-path PROJECT`.
Direct CLI needs no external MCP server or HTTP listener. Optional native daemon
and MCP stdio modes are supplied by ucl.exe, not by this Editor package's dormant
legacy transport code.

Transport: authenticated Windows named pipes and JSON mailbox in `Library/UnityCompat`.
Unity API commands run on the main thread; explicitly background project methods
must avoid Unity APIs. In-memory Roslyn eval is trusted local C#, not an OS sandbox.
Use `unity-compat list` for discovery and `unity-compat command NAME --params @file.json` to invoke.
Do not confuse the dormant upstream MCP source dependencies with this package's transport.

Full documentation is in the UnityCompatCLI repository's README.md and docs/commands.md.
Upstream source is CoplayDev/unity-mcp v10.0.0, MIT; see LICENSE-Coplay.txt and the repository's
THIRD_PARTY_NOTICES.md. Upstream transport/setup/config-rewrite auto-load and menus are disabled.

Extend with `[UnityCompat.Editor.CliCommand]` on public static methods, using
`[UnityCompat.Editor.CliArg]` for typed/default arguments. Legacy class annotations
with `HandleCommand(JObject)` and Task<object> are also supported.

# CLI implementation references, 2026-10-11

The useful pattern is a small CLI frontend plus an already-running application backend, with compact task-oriented output. Using the word CLI or changing JSON to another encoding does not automatically reduce latency or model tokens.

| Primary source | What it actually does | Applied here |
| --- | --- | --- |
| [Unity official CLI reference](https://docs.unity.com/en-us/unity-cli/unity-cli-reference) | Direct terminal commands, multiple output formats, on-demand help, warm `shell` process | Direct subcommands, human/JSON/TSV output, help by command, `shell` |
| [Unity official skills source](https://github.com/Unity-Technologies/skills/tree/main/skills/unity-cli) | Open documentation for using the CLI and Pipeline integration | Instruction design reference; this is not the CLI core source |
| [fujisheng/UnityCli](https://github.com/fujisheng/UnityCli) | Documents Unity 2021.3+, framed named pipes, token authentication, main-thread dispatch, .NET client | Independently written local pipe transport with shared Editor execution gate |
| [vercel-labs/agent-browser](https://github.com/vercel-labs/agent-browser) | Native Rust frontend, a browser daemon, task-specific snapshots and object references | Native executable, bounded queries and an optional local native daemon |
| [Microsoft Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) | Ahead-of-time native compilation, self-contained deployment, reduced startup overhead | Windows x64 NativeAOT binary; no Python/.NET runtime required for normal commands |

These are architecture references, not comparative benchmark evidence. In particular, agent-browser's advertised numbers are not measured on this Unity project. UnityCli is a relevant open source implementation, but its speed/maturity is not assumed from the README. No source from these repositories was copied into the new pipe/native implementation.

I found the official Unity skill/docs source and official CLI distribution instructions. I did not establish that the official CLI's core implementation has a public, reusable source repository. Therefore this version is an independent compatibility implementation, rather than a claimed port of official source. Editor control in Unity's official workflow uses its Pipeline package; this project's 2022 integration continues to use our own Editor package.

JSON remains an internal framed wire format: parsing this small local message was not the dominant cost in version 0.1. The measured bottlenecks were process startup and fixed file polling intervals. Response verbosity affected model context separately. Version 0.2 addresses these independently and keeps the mailbox for detached jobs and compatibility.

## 0.3 direct inspection of Pipeline

The public UPM registry returned `com.unity.pipeline` **0.8.0-exp.1**, Unity minimum
`6000.0`. Its tarball SHA-1 matched the registry:
`62c08c808737255821f3449466689a527a2eea50`.
Source: https://packages.unity.com/com.unity.pipeline and the corresponding
https://download.packages.unity.com/com.unity.pipeline/-/com.unity.pipeline-0.8.0-exp.1.tgz.
The inspected package is retained only in ignored `research/`, not distributed.

Observed implementation patterns: a main-thread dispatch queue; native local
connection and lifecycle management; method-level `CliCommand`/parameter-level
`CliArg` discovery; and relocated Roslyn assemblies for in-memory C# evaluation.
These informed the independently written 0.3 session pool, budgeted queue drain,
typed method router, and relocated compiler. Official advanced usage is documented
at https://github.com/Unity-Technologies/skills/blob/main/skills/unity-cli/references/integration-advanced.md.
This is an architectural compatibility implementation, not a redistribution or
source port of the Unity-licensed Pipeline package.

Native stdio follows the MCP JSON-RPC lifecycle and newline transport contract:
https://modelcontextprotocol.io/specification/2025-06-18/basic/transports.
Actual interoperability was checked with the official SDK 2.3.0 from
https://github.com/modelcontextprotocol/python-sdk, in addition to raw framing tests.

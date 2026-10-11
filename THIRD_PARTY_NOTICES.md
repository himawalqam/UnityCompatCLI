# Third-party notices

The bridge vendors CoplayDev/unity-mcp v10.0.0 (MIT).
Source: https://github.com/CoplayDev/unity-mcp/tree/v10.0.0/MCPForUnity

Imported from the user's embedded package, including its documented WebSocket cancellation patch.
Changes: renamed namespaces/assemblies/package; disabled upstream transport, setup, migrations,
client-config rewriting and UI auto-start/menu hooks; added an independent file mailbox and commands.
Legacy network code remains dormant to satisfy dependencies of upstream tool helpers;
the CLI never calls it. Online asset generation still needs its provider credentials.

Upstream license: `src/unity_compat/bridge/LICENSE-Coplay.txt`.
Original file hashes: `docs/vendor-provenance.json`.

The Windows native executable includes the .NET 10 NativeAOT runtime (MIT).
Runtime source: https://github.com/dotnet/runtime/tree/v10.0.10
The upstream runtime license and third-party notices are distributed beside the executable:
`LICENSE-dotnet.txt` and `THIRD-PARTY-NOTICES-dotnet.txt`.

The Editor eval compiler includes Microsoft Roslyn 3.8.0 and seven associated
assembly files from pinned MIT-licensed NuGet packages (eight DLLs total).
Assembly identities, friend declarations, type namespaces and resource names
are independently relocated to avoid collisions with other Editor plugins.
Package versions, source URLs and SHA-256 hashes are recorded in
`src/unity_compat/bridge/Editor/Compiler/provenance.json`; upstream licenses and
notices are distributed in that directory. `scripts/prepare_roslyn.py` and
`native/RelocateRoslyn` reproduce these transformations. The build helper uses
Mono.Cecil 0.11.6 (MIT), which is not shipped as an Editor dependency.

Unity's `com.unity.pipeline` 0.8.0-exp.1 was inspected for architecture research
under its Unity Terms license. No Pipeline source, DLLs, or Unity CLI binary are
included in this distribution. This compatibility tool is independently
implemented and is not an official Unity product.

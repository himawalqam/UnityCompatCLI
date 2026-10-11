# Local patch: cancellation during Editor assembly reload

This embedded package is MCP for Unity 10.0.0, originating from
https://github.com/CoplayDev/unity-mcp/tree/v10.0.0/UnityCompatVendor.

`Editor/Services/Transport/Transports/WebSocketTransportClient.cs`:

- Observe cancellation before testing socket initialization and again after acquiring the send lock.
- Keep a local socket reference across asynchronous waits.
- Treat exceptions after connection cancellation as normal loop termination in the receive and keep-alive loops. Live connection failures retain their warning and recovery paths.

`ForceStop` cancels the connection token before disposing its socket. Previously, a pending command result could inspect the disposed/null socket first and report `WebSocket is not initialised` as an unexpected receive failure during compilation. The patch preserves the intended cancellation outcome.

The package was embedded through Unity Package Manager so the fix survives clearing Library. Its version and dependencies are unchanged.

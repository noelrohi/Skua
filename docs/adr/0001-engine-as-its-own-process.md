# The Engine runs as its own long-lived process; Control Surfaces talk to it over JSON-RPC on a Unix socket

On macOS the Engine runs as a standalone process (`skua-engine`) that owns its Game Host as a child process. The CLI (`skua`) and the MCP server (`skua mcp`) are short-lived clients. They connect over JSON-RPC 2.0 (StreamJsonRpc) on a per-user Unix domain socket at `<SkuaDIR>/engines/<engine-name>.sock`, and auto-start the Engine if it isn't running, but never log in on their own. We chose this so that a logged-in game and long-running Scripts outlive any single agent session, and so that the CLI and every MCP session share the same game (and the Test Account is logged in once).

## Considered Options

- **The MCP server hosts the Engine in-process.** Rejected: MCP clients spawn a stdio server for each session and kill it afterwards, and parallel sessions would each log in the Test Account.
- **HTTP/SSE via Kestrel, or gRPC.** Rejected: heavier dependencies and plumbing for no gain on a localhost-only, C#-to-C# link. A Unix socket with mode 0600 gives localhost-only access and uses filesystem permissions as auth.

## Consequences

- One Engine process holds exactly one Game Client, because Core relies on process-wide statics (`Ioc.Default`, the static messengers, `IScriptInterface.Instance`). Several Game Clients later means several named Engine processes.
- Socket paths must stay under macOS's 104-byte `sun_path` limit, so Engine Names are capped at 16 characters and the path is checked at startup.

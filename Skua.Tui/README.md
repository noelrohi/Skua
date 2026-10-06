# Skua.Tui

`skua-tui` is a terminal UI (Ratatui) for windowless Engines, over the Control Surface ([ADR 0002](../docs/adr/0002-control-surface-typed-ops-plus-eval.md)): the Skua Manager's accounts by group, each with its Engine, and the selected Engine's Overview, Inventory, Quests, Logs, Game, Chat and Hooks tabs, and actions on the marked accounts' Engines. How to run it: `BUILD.md`, "The TUI".

This Cargo project is not in `Skua.sln`, and `dotnet build` doesn't build it. It shares the Game Host's toolchain (`rust-toolchain.toml`).

```
cargo build --release --locked   # target/release/skua-tui
cargo test --locked              # a fake Engine on a Unix socket in a temp folder, and TestBackend renders of the screen
```

- `src/rpc.rs` speaks JSON-RPC as StreamJsonRpc frames it (`Content-Length` headers), with positional params as the C# client sends them.
- `src/engine.rs` says `hello` with `PROTOCOL`, which must equal `ControlProtocol.Version`; bump both together. An Engine of another protocol is an error, so nothing it says is shown.
- `src/dto.rs` mirrors only the `Skua.Control` DTOs the TUI shows.
- `src/poller.rs` reads every Engine's `status` once a second on its own thread, and the selected one's logs (`logs --tail 200`, then from the cursor), its Hook runs (`hook.ran` among its newest events, then from the logs) and its tab's data; and whether a Hook Runner holds `<SkuaDIR>/hooks.lock`. On the Overview it also reads the inventory for Bags.
- `src/bags.rs` counts what a run gained: the inventory now against the one its `script.started` event carries, else against the first read.
- `src/chat.rs` follows the Chat tab's Engine with `subscribe` on a connection of its own, reading the stream as StreamJsonRpc sends an `IAsyncEnumerable` (`$/enumerator/next` with its token); closing the tab closes the connection, which ends the Engine's subscription.
- `src/actions.rs` runs each action as one op on one Engine, on a thread and a connection of its own; `src/app.rs` only queues them (`App::jobs`) and applies their outcomes (`App::on_outcome`), so the tests run them in place.

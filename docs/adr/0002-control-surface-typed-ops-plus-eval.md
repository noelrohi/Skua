# The Control Surface is a small set of typed operations plus a C# `eval`, and never carries credentials

The v1 Control Surface exposes a deliberately small set of typed operations (status and a few state queries, login/logout/join/jump, Script start/stop/status/wait/options, Script Source search/update, Script Dialog answering, logs, screenshot). Everything else goes through `eval`, which compiles a C# snippet against `IScriptInterface Bot` exactly as a Script would. Agents may already run any Script on the Test Account, so `eval` grants no new power. It keeps the contract and the MCP tool list small, instead of growing one tool per game feature. `login` takes no credentials: the Engine reads the Test Account from Keychain itself, so the Test-Account-only rule holds by construction rather than by convention. (ADR 0005 amends this: a developer chooses the account `login` uses, with `skua account`, and agents reach it only when it allows them.)

## Considered Options

- **A typed operation for every game feature** (quests, shop, equip, packets, auto-attack, bank). Rejected: an endless contract and tool list, each piece a one-line `eval`.
- **A generic reflection API ("get or call any member by path").** Rejected: untyped, and it would leak the Core models, which serialize with AS3 wire names and have throwing getters.
- **`login(username, password)`.** Rejected: callers would handle secrets, and any account could be used.

## Consequences

- State DTOs live in Skua.Control and are mapped by hand in the Engine; the Core models never cross the wire.
- `eval` is allowed while a Script runs (for inspection), but the typed game actions are not. The exception is `chat_send` (#171), which doesn't move the player.
- The first Keychain read prompts macOS for access once ("Always Allow"), possibly again after unsigned rebuilds.

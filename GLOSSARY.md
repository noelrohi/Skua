# Skua

Skua is a third-party client for AdventureQuest Worlds that plays the game on a player's behalf by running Scripts.

## Language

### Running the game

**Game Client**:
Skua's own SWF together with the AQW game it loads; the thing that actually plays.
_Avoid_: SWF, flash, client (alone)

**Game Host**:
The runtime that executes the Game Client.
_Avoid_: Flash player, player, container

**Game View**:
The live picture of the Game Client in the Mac App, rendered at its size in device pixels; clicks, keys, the wheel and the clipboard in it go to the game, and the game's cursor shows over it.
_Avoid_: game window, stream, preview, viewer

**Bridge**:
The two-way channel over which the Engine and the Game Client call each other.
_Avoid_: ExternalInterface, flash call, IPC

### Driving the game

**Engine**:
Skua's core, running without any UI: it owns game state, runs Scripts and accepts commands.
_Avoid_: bot, core, daemon, backend

**Control Surface**:
Anything that drives the Engine from outside its process, such as a CLI, an MCP server or a GUI.
_Avoid_: frontend, UI, client

**TUI**:
The terminal Control Surface for windowless Engines: the Skua Manager's accounts by group, each with its Engine. It reads the accounts and never changes them.
_Avoid_: dashboard, console, terminal manager

**Hook**:
An executable in `<SkuaDIR>/hooks/` named after an event type (`inventory.full`), which the Hook Runner runs on each such event with the event's JSON on stdin; what to do about the event is up to it.
_Avoid_: trigger, handler, callback, rule

**Hook Runner**:
`skua hooks`: the process that follows the events of a data folder's Engines and runs their Hooks, recording each run as a `hook.ran` event. One per data folder; it needs no TUI or agent.
_Avoid_: watcher, daemon, hook server

**Mac App**:
Skua's desktop app on macOS: the Game View and Skua's panels, with its own Engine inside it. It is not a Control Surface, but Control Surfaces reach its Engine.
_Avoid_: GUI, viewer, frontend, desktop client

**Skua Manager**:
The Mac App's companion process (`Skua --manager`) that keeps a developer's accounts, their passwords in Keychain, and launches one Mac App per account, with the account's name as its Engine Name.
_Avoid_: launcher, account manager (alone)

**Engine Name**:
The short name that identifies one Engine on a Mac, so several Engines can run side by side; the default is `default`, or the file name of the `SKUA_ENGINE_SOCKET` a `skua` command talks to.
_Avoid_: instance, profile, session

**Script**:
A C# program, compiled and run by the Engine, that plays the game toward some goal.
_Avoid_: bot, plugin

**Script Source**:
The repository the Engine fetches Scripts from.
_Avoid_: script repo, scripts folder

**Script Report**:
A structured result a Script records with `Bot.Report`, seen as a `script.report` event.
_Avoid_: result log, report line

**Script Dialog**:
A message a Script raises for a human; it is either a Notice or a Question.
_Avoid_: message box, popup, prompt

**Notice**:
An OK-only Script Dialog; it never waits for an answer.
_Avoid_: info box, alert

**Question**:
A Script Dialog offering a choice (yes/no or named buttons) that waits for a Control Surface, or the Mac App's window, to answer it until it times out.
_Avoid_: prompt, confirm, pending dialog

**Test Account**:
An AQW account reserved for automated sessions; agents log in with no other account unless a developer added it with `--allow-agents`.
_Avoid_: alt, bot account

**Active Account**:
The account in Keychain that a developer's login uses when it names none, chosen with `skua account`; by default the Test Account. An agent's login uses it only if it was added with `--allow-agents`, and the Test Account otherwise. A login may name another account, such as a Skua Manager account, without making it active.
_Avoid_: profile, current user

### The player's items

**Inventory**:
Everything the player holds outside the bank, whichever Space it fills, classes included.
_Avoid_: bag

**Space**:
A limited number of slots in the Inventory that certain kinds of item fill, one slot per distinct item however many it stacks. Classes and houses fill no Space.
_Avoid_: pool, slots (alone), inventory slots

**Bag Space**:
The Space for gear, pets and consumables; its size is the player's bag size, which they can buy more of.
_Avoid_: item space, bag

**Misc Space**:
The 100-slot Space for items, notes, quest items and resources. A consumable is not a misc item even when the game labels it "Item".
_Avoid_: misc bag, misc slots

**Favorite**:
An item the player starred in the game to keep it: Scripts don't sell or discard it unless one deliberately allows that, and never bank it to make room. The game keeps Favorites per character on one device, so a star made elsewhere isn't seen.
_Avoid_: starred item, locked item

**Boosted Gear**:
Gear that raises the player's damage, against everything or a kind of foe, or the gold, XP, reputation or class points they earn. Banking to make room leaves it alone unless the player opts in.
_Avoid_: meta gear, boosted weapon (when armor, helms, capes and pets count too)

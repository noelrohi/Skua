# Skua for Mac

What's new in Skua on macOS, newest first. To install or update, follow [Install on macOS](./BUILD.md#install-on-macos).

---

## October 9, 2026

### Fixes
* A `skua eval` or Console snippet that runs for a while no longer fails with "Could not load file or assembly" when another eval compiles, or a Script run starts or ends, before it first uses one of its includes.

## October 8, 2026

### New
* `skua-tui`'s Overview shows the server in its title, and the player's frame shows the game's social settings (`goto pm party friend duel guild`, struck out when off) and Skua's options that are on, such as `lag killer · private rooms`. `skua status` prints them as `Social` and `Options` lines, and `status --json` gives them as the player's `social` and the game's `options`. See [The TUI](./BUILD.md#the-tui-skua-tui).

### Fixes
* A run no longer loses 10 minutes at a time to `quest.stalled`. Quests a Script leaves accepted from an earlier step no longer stall or get accepted again, the moment between a turn-in and the Script's next accept is no stall, and the Engine's accepts wait for the player's quest packets to pause, so the game server no longer refuses the Script's accept with "Please slow down". A quest whose accept the game server refused that way is accepted again within seconds. See [Logs and events](./BUILD.md#logs-and-events).

## October 7, 2026

### New
* Scripts can target a monster without walking to it or attacking it: `Bot.Combat.Target("Nulgath")` or `Bot.Combat.Target(id)` selects the monster in the player's cell, so the next skill fires from where the player stands. `Bot.Combat.Attack` still walks to the monster and attacks it.

### Changes
* A run's goal gives its `farm` and `buy` rates only once the step has been measured for 10 minutes, so a few minutes of bursty gains no longer give a wrong ETA; `SKUA_GOAL_RATE_SEC` sets another time. `skua-tui`'s Bags rates each item from when it first rose in the run rather than from the run's start, and shows its rate and ETA only after the same 10 minutes. See [Logs and events](./BUILD.md#logs-and-events) and [The TUI](./BUILD.md#the-tui-skua-tui).

## October 6, 2026

### New
* `skua-tui`'s Overview shows **Bags**: what the run changed in the inventory since it started, each stack it is filling as a bar toward its max with its rate and when it will be full, then what it filled up, what's new and, on one line that fits, what was spent or banked: the largest first, the single items as one (`Unidentified 1, 6, 9 -1 each`), and `+N more` for the rest. See [The TUI](./BUILD.md#the-tui-skua-tui).
* `skua scripts check <path>` compiles a Script and its includes without running it or starting an Engine, and exits non-zero with each error at its file and line. Given a file in a Scripts checkout, it compiles against that checkout. See [Script Source](./BUILD.md#script-source).
* `skua scripts update --verify` hashes every local Script against `scripts.json` and downloads each one that differs or is missing, so a Script changed on a branch no longer keeps running its old code.
* `skua mcp` has an `engine_list` tool: every Engine in the data folder with its status, as `skua engine list` shows them, without starting any. See [Building the macOS Engine and CLI](./BUILD.md#building-the-macos-engine-and-cli).

### Changes
* When a run's quests stall (`quest.stalled`), the Engine accepts them again, since the game server can drop a quest the client still shows accepted and then stop counting its requirements; the event's `reaccepted` lists them. `SKUA_QUEST_STALL_REACCEPT=0` turns it off. See [Logs and events](./BUILD.md#logs-and-events).
* `skua-tui`'s Overview gives Current Quests the full height on the right on a wide terminal, and turns the chat into a strip across the bottom with the Room in its title, which gives way first on a short terminal. The accounts list is narrower, and a marked account shows its name in blue instead of `[x]`.
* `skua scripts` and `skua status` start no Engine. `status` of an Engine that isn't running fails and says how to start it. `scripts update` and `scripts source` are refused while any Engine runs a Script.
* `skua mcp`'s `status` and `scripts_*` tools start no Engine either, so an agent that only checks on things no longer starts an idle `default` Engine. `status` of an Engine that isn't running fails with `EngineUnavailable`, and `scripts_update` is refused while any Engine runs a Script. See [Building the macOS Engine and CLI](./BUILD.md#building-the-macos-engine-and-cli).

### Fixes
* A Script with a byte order mark or a zero-width space no longer shows as outdated, and is no longer downloaded again on every update: Skua now measures a Script as the Scripts generator does for `scripts.json`.

## October 5, 2026

### New
* `skua engine list --brief` shows every Engine on one line (class, HP, map and cell, target, Script, run time, kills a minute and deaths), so watching a party takes one command instead of a `skua status` per account. `status` now names the target too. See [Building the macOS Engine and CLI](./BUILD.md#building-the-macos-engine-and-cli).
* An example Hook, `docs/hooks/game.disconnected`, logs an account back in when the game logs it out unexpectedly, as its idle kick does, and leaves it logged out after `skua logout` or `skua engine stop`. A new `engine.stopping` event says why an Engine stops, and `game.disconnected` names the account and server. See [Hooks](./BUILD.md#hooks).
* A refused quest turn-in is no longer silent. The game server's answer to every turn-in, a Script's included, is recorded as a `quest.completed` or `quest.rejected` event, and a refusal also as a line in the Script log with the server's reason. `skua quests` shows a quest's last refusal until it is turned in, and `skua quests complete <id>` turns a quest in and prints the server's answer. See [Logs and events](./BUILD.md#logs-and-events).
* `skua quests` shows whether a daily, weekly or monthly quest has been done since the game last reset it, e.g. `weekly, done this week`. See [Moving and reading the game](./BUILD.md#moving-and-reading-the-game).

### Changes
* After installing a new build, `skua` says once per Engine that it kept an Engine from the old build because its Script runs, instead of on every command; `skua status` and `skua engine list` keep showing it.
* A logout the game gives no reason for, such as its idle kick, is now a `game.disconnected` with reason `unknown` and leaves the game `disconnected`, instead of passing for a deliberate logout.

## October 4, 2026

### New
* `skua --skill` prints a skill for agents that drive your Engines: listing them, logging accounts in, running and watching Scripts, checking quest and item progress, and setting up Butlers. `skua --help` points agents at it, and `npx skills add noelrohi/Skua --skill skua` installs it. See [Building the macOS Engine and CLI](./BUILD.md#building-the-macos-engine-and-cli).
* `skua logs -f --tail N` prints the newest N entries, then follows new ones, like `tail -f -n N`, so watching a Script no longer takes a `--tail 1` to fetch the cursor first. With `--after`, it prints the newest N after the cursor.

### Changes
* `skua-tui`'s Overview is the game's screen in text: the player's frame with HP, MP and gold, the Script's run and goal, the quests in progress, any Question, the game's line when a quest requirement rises, the players and monsters in the player's cell with their HP and who targets what, then the chat and who else is on the map. See [The TUI](./BUILD.md#the-tui-skua-tui).
* The window is the game, with **Auto**, **Jump** and **+** above it and the status strip below. Its menu row is gone (Scripts, Options, Helpers and the rest are in the macOS menu bar, as before), and so are Log in, Log out and the server picker: log in from the Skua Manager, the game's own login screen or `skua login`, and out with `skua logout`.

### Fixes
* An app the Skua Manager launches opens its window down and right of the other apps' windows instead of exactly over them, where closing the top one looked like the close button did nothing.

## October 3, 2026

### New
* `skua-tui`'s Game tab shows the game's picture in Ghostty, kitty or WezTerm, refreshed every 2 seconds, and `p` opens it in Preview from any tab. See [The TUI](./BUILD.md#the-tui-skua-tui).
* `skua-tui`'s Overview is a Party board: the account and its run in plain words, what its Script is working toward as a tree (quest › item to buy › material to farm › what it kills, with counts, pace, time left and how often a death reset the wave), every account on the same map with its HP, target, kills and deaths, the monsters in its cell and who is on each, and the game's chat. The Engine reports each run's deaths, the player's target and the Script's goal. See [The TUI](./BUILD.md#the-tui-skua-tui).
* The Engine counts each run's kills, and `skua-tui` shows them with kills per minute. A quest stall while the Script still kills (a rare-drop grind) shows in yellow and is no longer an alert; only a stall with no kills is. Quest requirements count what the bank holds too, so a banked 1/1 item reads as done. See [The TUI](./BUILD.md#the-tui-skua-tui).
* `skua-tui` takes the mouse: click an account, a tab or an Inventory category, and scroll with the wheel. The Inventory tab splits into category tabs (Weapons, Classes, Gear, Items, Quest items, Other) and scrolls (`pgup`/`pgdn`, or click the list and use `j`/`k`). See [The TUI](./BUILD.md#the-tui-skua-tui).
* `skua-tui` shows how long each account's quests have gone without progress. A stall of 10 minutes or more shows in red next to the account's Script and counts as an alert, and the Quests tab now shows only the quests in progress, each unmet requirement on its own line with its idle time, rate and time left. The Engine records `quest.stalled` when a run's quests stall, so a Hook can alert you. See [The TUI](./BUILD.md#the-tui-skua-tui).
* `./install-macos.sh` installs `skua-tui` too, next to `skua`, so it's on PATH and updates with it. It needs `cargo` ([Rust](https://rustup.rs)), as the Game Host already did; `SKUA_NO_TUI=1 ./install-macos.sh` installs everything but `skua-tui`, without Rust. See [Install on macOS](./BUILD.md#install-on-macos).
* `skua-tui` has a Chat tab: the selected Engine's game messages as they arrive, and an input line that sends zone chat or `/w <name> <text>` whispers. See [The TUI](./BUILD.md#the-tui-skua-tui).
* Hooks: put an executable named after an event type in `<SkuaDIR>/hooks/` (say `hooks/inventory.full`) and `skua hooks` runs it on each such event of every windowless Engine, with the event on stdin. Each run is recorded as a `hook.ran` event, and `skua-tui`'s Hooks tab shows them; `H` starts the runner. See [Hooks](./BUILD.md#hooks).
* `skua-tui` acts now: start a windowless Engine (`E`) or stop one (`X`, after a confirm), log in with a server picker (`L`; each Engine as its own account), log out (`O`), search a Script, set its options and start it (`s`) or stop it (`x`), answer a Question (`d`), join a map (`J`) and update the Scripts (`U`). Marked accounts all get the action. See [The TUI](./BUILD.md#the-tui-skua-tui).

### Fixes
* A long Script no longer crashes with `Could not load file or assembly 'Core…'` after a `skua eval` or a Console line runs while it plays (#177).

## October 2, 2026

### New
* `skua-tui`, a terminal UI for windowless Engines: the Skua Manager's accounts by group with each Engine's state, and the selected one's player, Script, logs, inventory, quests and map. It only shows for now; see [The TUI](./BUILD.md#the-tui-skua-tui).
* `skua login --account <name>` logs a named account in, such as one of the Skua Manager's, without making it the active account; MCP's `login` takes `account` too, for the Test Account or one added with `--allow-agents`.
* `skua logs --tail N` prints the newest N log entries, without paging from the oldest.
* Every `skua` command takes `--engine <name>` to talk to a named Engine, instead of setting `SKUA_ENGINE_SOCKET`.
* A full inventory is now an `inventory.full` event: once as it fills, and once for each new item that drops with no room for it.
* The Engine now keeps the game's chat, windowless too: zone, party and guild chat, whispers, and server messages and warnings. `skua logs game` reads them and `skua logs game -f` prints them as they arrive.
* `skua chat send <text>` sends zone chat and `skua chat whisper <name> <text>` whispers a player.

### Fixes
* `skua logs -f` now says the connection to the Engine was lost when the Engine stops, instead of crashing with a stack trace.
* A `skua` command no longer renames an Engine "default": an Engine it replaces after an update keeps its name, and one it starts at `SKUA_ENGINE_SOCKET` is named after the socket's file.
* A lost connection now starts one relogin instead of several at once, so a farming Script with auto-relogin on comes back on one server instead of logging in again and again.
* `skua status` always has a `Player` line, `none (not playing)` at the login screen or with the game closed. While playing, when the game is too busy to answer in time, as in heavy combat, it shows the last reading marked stale with its age (`playerAgeSec` in `--json`), or the player as unknown.
* A Script that stops itself, as CoreBots' Scripts do when you aren't logged in, now ends as `stopped` in `skua script status` and `script.stopped` instead of `completed`.

---

## October 1, 2026

### New
* Skua for Mac now comes as a release you download: it checks for a newer one once a day, asks before installing it, and reopens updated. **Skua › Check for Updates…** checks now. See [Install a release](./BUILD.md#install-a-release-on-macos).
* A windowless `skua-engine` now loads the plugins in its data folder's `plugins` folder as it starts, as the app does, so Butlerv4 works between windowless Engines. A plugin that fails to load is logged and skipped.

### Fixes
* A quest's requirements now come in the same order as on Windows, so Scripts such as King's Echo hunt the right monster for each item (Wandering Light, Gilded Peace).
* Skua no longer jumps in front of the app you're using while a Script runs. A window a Script opens, such as its options, waits until you click Skua, and a notification tells you it's there.
* A Script that restarts itself after a relogin, as CoreBots' Scripts do, goes on with the options you saved instead of waiting on its options window.
* A player no longer stays dead after dying while the game is busy: Skua asks for the respawn again when the game's own request came too soon, so a farming Script goes on instead of standing dead until the AFK logout.

---

## September 29, 2026

### Fixes
* The game no longer sometimes stops answering right after Skua starts.

---

## September 28, 2026: the Mac App

### Playing in the window
* **Skua.app**: Skua is now a Mac app, with the game in its window. Click and type in the game as you would in a browser.
* The game is sharp on Retina displays, shows its hand cursor over buttons, and takes ⌘C and ⌘V in chat.
* Log in from the window, and see your account, map and Script in its status bar.
* Closing the window keeps the game and your Script running. Click Skua in the Dock to bring the window back; ⌘Q quits, and asks first while a Script runs.
* When a Script stops, hits an error or relogs while Skua isn't in front, you get a macOS notification.
* Window › Top Most keeps any Skua window above the others, and is remembered.
* The title shows Skua's version and, if you turn it on, your username.

### Scripts
* Load, start and stop Scripts from the Scripts window, with the Script's log beside it. Search Scripts finds one to download.
* Script options and the CoreBots options edit as on Windows.
* Scripts come from a Mac-ready copy of Skua's Scripts, updated from them every day, since many of the originals need Windows.
* At start-up Skua updates your Scripts, skill sets, junk items and quest data, as the Windows app does. Logging in no longer waits for it.
* Reset Scripts downloads every Script again, and Open in VSCode opens one Script in VS Code.
* When a Script asks a question, it shows on a sheet in the window. Its notices go to a list with a badge, and never hold the Script up.

### Every panel from Windows
* Options (Game, Application and Themes), HotKeys, Skills, Runtime, Fast Travel, Current Drops, Auto and Jump.
* Tools and Bank: the Loader, the Grabber, Junk Items, Stats, the Console and Plugins.
* Packets: the Spammer, the Logger and the Interceptor.
* The **Bot Window** (the **+** button, or Window › Bot Window) shows every panel in one window, with a search.
* About, Change Logs and GitHub login are in the Skua menu. Your GitHub token is kept in Keychain.
* Application Options that do nothing on macOS are hidden.
* Change Logs shows this page first, then Skua's own change log.

### Skua Manager
* Keep your accounts, with their passwords in Keychain, and launch a Skua app for each account or group. Open it from the Manager menu.
* **Running** lists every Skua app with its game and Script. Bring one to the front, or stop it.
* **Import Windows list…** brings over the Windows Manager's accounts.

### Command line
* One command installs Skua and the `skua` command. Updating never stops a running Script.
* `skua` sets up your accounts, lists Scripts and what's new in them, keeps Scripts up to date, and shows a Script's progress live.

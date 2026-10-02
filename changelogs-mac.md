# Skua for Mac

What's new in Skua on macOS, newest first. To install or update, follow [Install on macOS](./BUILD.md#install-on-macos).

---

## October 3, 2026

### New
* `skua-tui` has a Chat tab: the selected Engine's game messages as they arrive, and an input line that sends zone chat or `/w <name> <text>` whispers. See [The TUI](./BUILD.md#the-tui-skua-tui).
* Hooks: put an executable named after an event type in `<SkuaDIR>/hooks/` (say `hooks/inventory.full`) and `skua hooks` runs it on each such event of every windowless Engine, with the event on stdin. Each run is recorded as a `hook.ran` event, and `skua-tui`'s Hooks tab shows them; `H` starts the runner. See [Hooks](./BUILD.md#hooks).
* `skua-tui` acts now: start a windowless Engine (`E`) or stop one (`X`, after a confirm), log in with a server picker (`L`; for now only the Engine whose account is the Active Account), log out (`O`), search a Script, set its options and start it (`s`) or stop it (`x`), answer a Question (`d`), join a map (`J`) and update the Scripts (`U`). Marked accounts all get the action. See [The TUI](./BUILD.md#the-tui-skua-tui).

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

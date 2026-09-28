# Skua Build Guide

This document provides instructions for building the Skua project from source, including automated build scripts for x64, x86, and WiX installer creation.

## Table of Contents
- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
- [Build Scripts](#build-scripts)
- [Manual Building](#manual-building)
- [CI/CD](#cicd)
- [Troubleshooting](#troubleshooting)

## Prerequisites

### Required Software

1. **.NET 10.0 SDK or later**
   - Download from: [Microsoft](https://dotnet.microsoft.com/download)
   - Verify installation: `dotnet --version`
   - Project targets: `net10.0-windows` for applications and UI libraries; `net10.0` for Skua.Core, Skua.Core.Interfaces, Skua.Core.Models and Skua.Core.Utils, and for the macOS projects

2. **Visual Studio 2026** (for MSBuild and WiX support)
   - Workloads required:
     - .NET desktop development
     - Desktop development with C++
   - Or install Build Tools for Visual Studio separately

3. **WiX CLI v6.0+** (for installer)
   - Install using: [WiX Toolset v6.0.2](https://github.com/wixtoolset/wix/releases/tag/v6.0.2)
      - Install both: `wix-cli-x64.msi` and `WixAdditionalTools.exe`
   - The Visual Studio extension: [HeatWave](https://marketplace.visualstudio.com/items?itemName=FireGiant.FireGiantHeatWaveDev17)
   - [WiX Documentation](https://wixtoolset.org/docs/tools/)

4. **PowerShell 7 or later**
   - For PowerShell: [PowerShell Github](https://github.com/PowerShell/PowerShell/releases)

5. **FlashDevelop or IntelliJ IDEA Ultimate (for building Skua.AS3 project - skua.swf)**
   - **Option A - FlashDevelop**: [download](https://github.com/fdorg/flashdevelop/raw/refs/heads/development/Releases/FlashDevelop-5.3.3.exe)
   - **Option B - IntelliJ IDEA Ultimate**: [download](https://www.jetbrains.com/idea/download/)
     - Requires ActionScript & Flash plugin

### Optional Software

- **Git** for version control
- **GitHub CLI** for releases

## Quick Start

### Easiest Method: PowerShell Script

1. Clone the repository:

   ```bash
   git clone https://github.com/auqw/Skua.git
   cd Skua
   ```

2. Right-click `Build-Skua.ps1` and select "Run with PowerShell"
   - This builds the Release configuration for both `x64` and `x86`
   - Includes WiX installer creation
   - Output is placed in the `build` folder
   - **The window stays open after completion**, showing build results

## Build Scripts

### PowerShell Script (`Build-Skua.ps1`)

The main build automation script with full control over the build process.

#### Basic Usage

```powershell
# Build everything (x64, x86, installer)
.\Build-Skua.ps1

# Build specific configuration
.\Build-Skua.ps1 -Configuration Debug

# Build specific platforms
.\Build-Skua.ps1 -Platforms "x64"
.\Build-Skua.ps1 -Platforms "x86"

# Skip installer
.\Build-Skua.ps1 -SkipInstaller

# Skip cleaning
.\Build-Skua.ps1 -SkipClean

# Custom output path
.\Build-Skua.ps1 -OutputPath "C:\MyBuilds"
```

#### Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| Configuration | String | Release | Build configuration (Debug/Release) |
| Platforms | String | "x64", "x86" | Target platforms to build |
| SkipInstaller | Switch | $false | Skip building WiX installer |
| SkipClean | Switch | $false | Skip cleaning before build (faster incremental builds) |
| Parallel | Switch | $false | Build platforms and installers in parallel (~23% faster) |
| BinaryLog | Switch | $false | Generate .binlog files for build analysis |
| OutputPath | String | .\build | Output directory for artifacts |

#### Output Structure

```powershell
build/
├── Release/
│   ├── x64/
│   │   ├── Skua.App.WPF/
│   │   └── Skua.Manager/
│   └── x86/
│       ├── Skua.App.WPF/
│       └── Skua.Manager/
└── Installers/
    ├── Skua_Release_x64_Skua.Installer.msi
    └── Skua_Release_x86_Skua.Installer.msi
```

### Running the Build Script

The PowerShell script can be run in several ways:

1. **Right-click method**: Right-click `Build-Skua.ps1` → "Run with PowerShell"
2. **Command line**: Open PowerShell and run `.\Build-Skua.ps1`
3. **Batch files**: We have 3 next to the powershell script `Build.bat`, `Buildx64.bat`, and `Buildx64noInstaller.bat`
4. **Make your own**: Make a batch script with target:

   ```powershell
   powershell.exe -ExecutionPolicy Bypass -File "Build-Skua.ps1"
   ```

## Manual Building

### Using Visual Studio

1. Open `Skua.sln` in Visual Studio
2. Select configuration (Debug/Release) and platform (x64/x86)
3. Build → Build Solution (Ctrl+Shift+B)

### Using .NET CLI

```powershell
# Restore packages
dotnet restore

# Build x64 Release
dotnet build --configuration Release -p:Platform=x64

# Build x86 Release
dotnet build --configuration Release -p:Platform=x86

# Build specific project
dotnet build Skua.App.WPF\Skua.App.WPF.csproj --configuration Release
```

### Building skua.swf

`skua.swf` targets Flash Player 32.0. `playerglobal28_0.swc` can no longer be downloaded, so the build uses `playerglobal32_0.swc`.

- **Windows:** `Skua.AS3\compile-as3.ps1` runs `mxmlc` from your Flex SDK. The SDK needs `frameworks\libs\player\32.0\playerglobal.swc`: download [`playerglobal32_0.swc`](https://fpdownload.macromedia.com/get/flashplayer/updaters/32/playerglobal32_0.swc) (SHA-256 `7d4d6168d27603cfb3b750302448e354e0bbc1bdd58f5d101c3dcf6891e9bb65`) and save it there under that name.
- **macOS:** `Skua.AS3/compile-as3.sh` downloads Apache Flex SDK 4.16.1 and `playerglobal32_0.swc` itself, checks them against pinned SHA-256s, and caches them in `~/Library/Caches/skua-as3`. It needs Java (17 works).

Both scripts write `Skua.AS3/skua/bin/skua.swf` and print the SHA-256 of its `DoABC` tags. Compare builds by that hash, not the file hash: `mxmlc` writes a compile timestamp into every SWF.

### Install on macOS

```sh
./install-macos.sh
skua status
```

The script needs the .NET 10 SDK (`brew install dotnet`), [Rust](https://rustup.rs) and Java (17 works). It builds everything (the first run takes about 5 minutes, for the Game Host) and publishes `skua`, `skua-engine`, `skua-gamehost` and `skua.swf` side by side in `~/.local/share/skua/versions/<build>/`. The publish is self-contained, so they start without `DOTNET_ROOT`. It links `~/.local/bin/skua` to that build, and says so if `~/.local/bin` isn't on PATH yet. `SKUA_INSTALL_DIR` and `SKUA_BIN_DIR` change the two folders, and any arguments go to `dotnet publish`, such as the escape hatches in the next section.

To update, `git pull` and run it again. A build is the version and the commit, plus the time for uncommitted changes. The script keeps the two builds before the new one, and any build a process still runs from. The next `skua` command finds the old build's Engine and replaces it, saying so on one line. It never stops an Engine whose Script is running:

- If that Engine speaks the same protocol version, the command still runs against it, with a notice on stderr, and a later command replaces it once the Script ends.
- If it speaks another protocol version, the command fails with `ScriptRunning` (exit 12). Wait for the Script to end, or run `skua engine stop`, which stops the Script too.
- The Mac App's Engine is never replaced: a `skua` from another build says so (same protocol) or fails with `ProtocolMismatch` and a hint to quit the app (another protocol).

`skua mcp` never replaces an Engine, because an MCP server outlives an update and would replace the newer Engine with its own; restart the MCP client after an update.

#### The Mac App

```sh
./install-macos.sh --app
open ~/Applications/Skua.app
```

`--app` builds the Mac App too, as `versions/<build>/Skua.app`: a self-contained, ad-hoc signed bundle with `skua`, `skua-engine`, `skua-gamehost` and `skua.swf` from the same build inside it. The app is in `Contents/MacOS` and the CLI in `Contents/Helpers`, as `Skua` and `skua` are one name on a case-insensitive disk. `~/.local/bin/skua` links to the `skua` inside the bundle, so the app and the CLI never disagree on the build. `~/Applications/Skua.app` (`SKUA_APPS_DIR` changes the folder) is a small app with the same name and icon that opens that build: open it from Finder, Launchpad, Spotlight or the Dock, or with `open -a Skua --args --name <engine-name>`.

- **Updating:** run `./install-macos.sh` again, with or without `--app`: once the app is installed, it updates both. A running app keeps its build, and the build's folder, until you quit and reopen it; the script says when one is running. Until then, a `skua` from the new build reaches it as described above.
- **Keychain:** each build is a new binary to Keychain, so the first login from a newly installed app may make macOS ask whether `security` may read the account again: choose "Always Allow".
- **Gatekeeper:** a build made on your Mac isn't quarantined, so it opens without a prompt. A `Skua.app` copied from another Mac or downloaded is quarantined, and an ad-hoc signed app isn't notarised, so its first open says macOS can't verify it. Open it once from System Settings › Privacy & Security › **Open Anyway** (on macOS 14 and earlier, Control-click it in Finder and choose **Open**), or remove the quarantine with `xattr -dr com.apple.quarantine <path>/Skua.app`.
- **Dock:** to keep Skua in the Dock, drag `~/Applications/Skua.app` there from Finder. **Keep in Dock** on the running app may pin its build instead, which a later update removes.
- `dotnet publish Skua.App.Mac -c Release -r osx-arm64 -p:SkuaAppBundle=<folder>/Skua.app` makes the bundle alone, afresh each time.

### Building the macOS Engine and CLI

`Skua.MacOS.slnf` builds the headless Engine (`skua-engine`), the CLI (`skua`, which is also the MCP server as `skua mcp`), the Mac App (`Skua`) and their tests. The Engine itself is the `Skua.Engine` library, which both `skua-engine` and the Mac App host:

```bash
dotnet build Skua.MacOS.slnf
dotnet test Skua.MacOS.slnf
```

On macOS, building `Skua.App.Engine` also builds the Game Host and `skua.swf`, so it needs [Rust](https://rustup.rs) and Java (17 works); a missing `cargo` or `java` fails the build with an install hint.

- **`skua-gamehost`:** `cargo build --release --locked` in `Skua.GameHost/`. The first build takes about 5 minutes; after that it's a no-op of about a second.
- **`skua.swf`:** `Skua.AS3/compile-as3.sh`, rerun only when an AS3 source changes.
- **Escape hatches:** `-p:SkuaGameHostPath=<file>` uses a prebuilt `skua-gamehost` instead of running cargo, and `-p:SkuaSwfPath=<file>` a prebuilt `skua.swf` instead of running mxmlc.

The output is flat: `skua`, `skua-engine`, `skua-gamehost` and `skua.swf` sit side by side in `Skua.App.Engine/bin/<Configuration>/net10.0/` (and in `dotnet publish` output). `skua` auto-starts the `skua-engine` next to it, and the Engine starts the `skua-gamehost` and `skua.swf` next to itself. For MCP clients, the config is `{"command": "skua", "args": ["mcp"]}`. These builds need an installed .NET runtime: with a Homebrew .NET, set `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec` so the executables find it. A dev build's `skua` replaces an Engine from another build as an installed one does, but its build is only the version and the commit: after rebuilding uncommitted changes, run `skua engine stop`.

### Running the Mac App

The Mac App (`Skua.App.Mac`, ADR 0006) is the Game View with its own Engine inside it. Building it also puts `skua-gamehost` and `skua.swf` next to it:

```bash
dotnet build Skua.App.Mac
Skua.App.Mac/bin/Debug/net10.0/Skua [--name <engine-name>]
Skua.App.Mac/bin/Debug/net10.0/Skua --manager   # the Skua Manager
```

- Without `--name`, it serves the Engine Name it last served (`default` at first), so opening it from the Dock returns to the Engine you used last. The Skua Manager's launches don't count.
- It starts its Engine, and binds the Engine's socket, before its window opens, so `skua status` in a terminal talks to it.
- If a `skua-engine` already holds the name, the app shows what it is doing (account, map, Script) and offers **Take over** or **Quit**. Take over stops it (its game closes and any Script stops) and starts the app's own Engine; if a Script is running, it asks a second time first. It never takes over silently, and it can't take over another Skua app's Engine: use that app, or give this one another `--name`.
- The app owns its Engine: `skua engine stop` refuses with `EngineOwnedByApp` (exit 27), "The Skua app owns Engine '<name>'; quit the app to stop it.", and the game keeps running. `skua status` shows `host: "app"`.
- Closing the window keeps the app and its Engine running, headless: `skua` and MCP keep working, and clicking the Dock icon shows the window again. Quitting (Cmd-Q) stops the Engine (its Script, Game Host, connections, socket and lock) and asks first if a Script is running; SIGTERM quits the same way without asking. After that, the next `skua` command auto-starts a headless Engine as usual.
- **Log in** above the Game View logs the Active Account in on the server picked beside it (or on one it picks, as `skua login` does), and says whom it logged in as; **Log out** returns to the login screen. When a login fails (no Active Account, Keychain access denied, a full or offline server), it says why there. Typing into the game's own login screen works too.
- The status strip below the Game View shows the Engine Name, the game state, and while logged in the account, server, map and cell, level and running Script. It follows logins from anywhere, `skua login` in a terminal included.
- A Script's Question shows as a sheet over the main window: its caption, text, a button per choice and a countdown to its timeout. It is still a Question of the Engine's one broker, so `skua dialogs answer`, MCP `dialog_answer` and `skua script start --follow` can answer it too: the first answer wins, the sheet closes, and a later click is ignored. A window answer is `answeredBy: user`. The timeout and `script stop` still answer with the fallback. While the app isn't frontmost, a Question posts a macOS notification instead of bringing the window forward.
- The app's own Questions, such as the Scripts panel asking whether to stop the running Script, show the same way and never freeze the window; they belong to no run (`script` is null), so `dialogs` lists them and an agent can answer them.
- A Notice never waits: it goes to **Notices** beside the status strip, whose badge counts the unread ones, with a macOS notification while the app isn't frontmost. Opening one shows its full text to select and copy.
- `ShowDialog` opens a real dialog over the active window (the input dialog, a Script's options, the Fast Travel editor, a skill's use rules, and a message box or buttons dialog a Script or plugin shows itself), and the file dialogs are macOS open and save panels.
- A Script's options open in an editor, as on Windows: from the Scripts panel's **Edit Script Options**, and when Core opens its options window at a Script's start. Closing the editor saves the options you changed to the Script's options file, the one `skua script options` reads and the next run uses, over what is stored then, so options an agent set meanwhile stay. **Options → CoreBots** edits the logged-in player's CoreBots options (`options/CBO_Storage(<player>).txt`, where CoreBots reads them); **Save** and closing the window write them. `skua-engine` keeps the headless behaviour below.
- **Options → Game** applies each game option to the game at once and saves the ones you change as you change them (`UserOptions` in `Skua.settings.json`, where the next start reads them); what a running Script sets stays unsaved, and **Save** saves every option as it is, as on Windows. The lag killer follows the option: unlike `skua-engine`, the app doesn't turn it on at every login.
- **Options → Application Themes** switches every open window's theme at once: a theme's light or dark base and its primary colour (the accent) and the text on it. Choosing or saving a theme keeps it in the same settings the Windows client uses (`CurrentTheme`, `UserThemes`). **Open Themes Folder** opens it in Finder; **Open VSCode** and **Edit Script** open VS Code (through its `code` command, or its app), a Script falling back to the default text editor.
- **Helpers** has Runtime (the drops to pick up, registered quests, boosts, and Notify Drop), Fast Travel and Current Drops, as on Windows. **Auto** and **Jump** sit at the right of the window's menu row: auto attack or hunt (a ● marks it running), and picking a cell jumps there. In a list, ⌫ removes the selected entries and ⌥⌫ removes them all.
- **Skills** builds and saves skill sets as on Windows: add skills with their use rules, order them (⌘↑/⌘↓ move one, ⌫ removes it, Return edits its rules), and save them under a class and mode. A Script that loads that class (`Bot.Skills.LoadAdvanced`, as CoreBots does when it equips one) gets the saved set; double-click a saved set to edit it. In the saved sets, ⌘C copies the selected ones to the clipboard as JSON, and ⌘V (or the list's context menu) saves the sets on the clipboard, replacing any with the same class and mode.
- **Options → HotKeys** lists Core's hotkeys (toggle or load a Script, Bank, Console, auto attack and hunt, lag killer); click one and press its new key, with ⌘, ⌥ or ⇧. They work in every app window and are saved in `HotKeys` in `Skua.settings.json`, with `Ctrl` meaning ⌘. By default they are ⌘ and the digit of the Windows F-key (⌘0 toggles the Script, ⌘9 loads one, ⌘2–⌘6 the rest), so plain keys stay with the game. In the Game View a hotkey's key goes to the hotkey, not the game, unless you are typing in the game (chat, the login fields), when every key goes to the game. The app registers no system-wide hotkey: they work only while one of its windows is in front.
- **Tools** has the Windows panels: **Loader** loads a shop or quests by ID and lists the quests in `QuestData.json`; **Grabber** grabs the shop, quests, inventories, bank, monsters or map items and shows the selected one's properties (read-only); **Junk Items**, **Stats** and **Console**, which compiles and runs a line against the game as a Script would. **Bank** opens the game's bank panel.
- **Packets** has the Windows panels. **Spammer** sends a packet to the server, or with **Send to Client** to the game as if the server had sent it, and **Start** sends its list's packets in turn with the delay between them until **Stop**. **Logger** lists the packets the game sends while **Enabled**; an unchecked filter's packets aren't logged. **Interceptor** reconnects the game to the picked server through a proxy on 127.0.0.1 at the server's port and lists every packet it relays, both ways, coloured by direction (blocked ones red); the filters and the search hide packets from the list. The Game Host's sockets are plain TCP, so the proxy works as on Windows. In the lists, ⌘C copies the selected packets.
- Plugins load from `<SkuaDIR>/plugins` when the window opens, as on Windows, and **Plugins › View Plugins** loads, unloads and configures them; a plugin's own menu items join the Plugins menu. A plugin that needs WPF (Windows-only) fails to load, or its menu item fails, with a line in the `debug` log saying so; the app runs on.
- The app menu (**Skua**) has **About Skua** and **Change Logs**, Skua's readme and change logs as the Windows Manager shows them, and **GitHub Login…**, GitHub's device-flow sign-in, whose token raises the Script Source's GitHub rate limits. The token is kept in Keychain (service `skua-github-token`), never in `Skua.settings.json` or a log, and the app reads it back at every start.
- Each window's **Window › Top Most** keeps it above other windows, as the Windows title bar's menu does; the app remembers it per window (the main window, and each panel) in `MacTopMostWindows` under `client` in `Skua.settings.json`.
- The app is a new binary to Keychain, so its first login makes macOS ask whether `security` may read the account: choose "Always Allow". An unsigned rebuild may ask again.
- Click the game and type in it. The Game View is live (the Game Host renders every 33 ms) only while the window is on screen; minimised, hidden or fully covered, the Game Host goes back to the headless defaults.
- Every 60 s (`SKUA_GAMEHOST_STATS_SEC`) the `debug` log gets a `[gameview] stats` line (frames shown, frame age p50/p95) next to `[gamehost] stats`.

### The Skua Manager

The Skua Manager keeps your accounts and launches a Mac App per account, as `Skua.Manager` does on Windows (ADR 0006). Open it from any app's menu bar (**Manager › Skua Manager…**) or Dock menu, or run `Skua --manager`. It is its own process, one per data folder: opening it again brings it to the front.

- **Accounts.** Add one with its username, an optional display name and its password; saving the same username again changes its password or display name. The password goes only to Keychain, as a `skua account` item named after the username (`Main User` → `main-user`, under `skua-account-main-user`), so `skua account use main-user` works on it too. Adding one doesn't change the Active Account. The rest of the list (display names, tags, groups, the last server) is in `<SkuaDIR>/Skua.manager.json`, which never holds a password. Removing an account deletes its Keychain item.
- **Tags and groups** filter and launch as on Windows: tag an account or the selected ones, pick tags to show only accounts with any of them, and put accounts in groups, each with its own Launch.
- **Import Windows list…** reads the Windows Manager's `Skua.settings.json` (or an older `ManagerSettings.json`), moves each password into Keychain and removes it from the file. It keeps a copy of the file as it was, `<file>.<time>.bak`, readable only by you; that copy still holds the passwords, so delete it once you've checked the import.
- **Launch** starts `Skua --name <account> --account <account>` for each account (selected, all shown, one, or a group), a second apart. Each app has the account's name as its Engine Name, logs that account in once its window shows (on the server picked with **On server**, or on one it picks), and starts the Script if **With Script** is on. No password is on a command line. That app's Engine always uses its own account, `skua login` against its socket included; an agent's login there gets the Test Account unless the account was added with `--allow-agents` (ADR 0005). Launching an account whose app is running brings it to the front.
- **Running** lists every Skua app and Engine using the data folder, with its game and Script. **Bring to front** shows an app's window; **Stop** quits an app (or stops a headless Engine), asking first while a Script runs. Its game logs out; the others keep playing.
- **Updates** shows the build `install-macos.sh` installed, the checkout this app was built from, and whether they match. To update, run the command it gives (`cd <checkout> && ./install-macos.sh`), then quit and reopen the apps. It downloads nothing.
- **Goals** shows Skua's funding goals, as on Windows.

The Manager's tests launch `fake-app` (`Skua.FakeApp`, set with `SKUA_APP_EXECUTABLE`) in place of the Mac App: it takes the same command line and hosts the same Engine, without a window.

The tests never run the real Game Host, read the real Keychain or reach AQW: they point `SKUA_GAMEHOST` at a fake that speaks the Bridge frames and simulates the game, `SKUA_SECURITY_TOOL` at a fake `security`, and `SKUA_AQ_SERVERS_URL` at a fake servers API.

Environment overrides:

| Variable | Overrides |
|---|---|
| `SKUA_DIR` | The Skua data folder (default `~/Library/Application Support/Skua`) |
| `SKUA_ENGINE` | The `skua-engine` that auto-start launches |
| `SKUA_ENGINE_SOCKET` | The Engine's socket (default `<SkuaDIR>/engines/default.sock`); the path must fit in 103 bytes |
| `SKUA_GAMEHOST`, `SKUA_SWF` | The Game Host the Engine runs, and the SWF it loads (default `skua-gamehost` and `skua.swf` next to the Engine); a missing file fails the start with an error naming its path |
| `SKUA_GITHUB_RAW_URL`, `SKUA_GITHUB_API_URL` | `https://raw.githubusercontent.com/` and `https://api.github.com/`, for tests |
| `SKUA_AQ_SERVERS_URL` | The game's servers API, `http://content.aq.com/game/api/data/servers`, for tests |
| `SKUA_SECURITY_TOOL` | The `security` tool that `skua account` and the Engine's login use for Keychain (default `/usr/bin/security`), for tests |
| `SKUA_APP_EXECUTABLE` | The app the Skua Manager launches (default the binary it runs from), for tests |
| `SKUA_BIN_DIR`, `SKUA_CHECKOUT` | Where the Manager's Updates tab finds the installed `skua` link (default `~/.local/bin`), and the checkout it compares it with (default the one the app was built in) |
| `SKUA_GAMEHOST_STATS_SEC` | Seconds between the Game Host's stats lines (ticks, frame rate, largest tick gap) in the `debug` log, and the Mac App's Game View stats lines: 60 by default, 0 for none |

#### Accounts and the Test Account

Three steps from a fresh install to a running Script, with your own account:

```sh
skua account add                           # asks for the username and password
skua login Galanoth                        # logs it in; without a server the Engine picks one
skua script start Farm/Leveling.cs --follow
```

`skua account add [username]` asks for the password without echoing it, and stores both in Keychain as a generic password, whose account is the username. The password goes to `security` on its standard input, so it never appears on a command line, in shell history or in any output. It stores a personal account, named after its username in lower case (`MainUser` becomes `mainuser`, under the service `skua-account-mainuser`) or `--name`, and makes it the active account. Nothing needs editing or restarting: the `TestAccountService` setting (under `client` in `<SkuaDIR>/Skua.settings.json`) names the active account, and the Engine reads it and the account from Keychain at every login, relogging a game that plays with another account. Choose "Always Allow" when macOS asks whether `security` may read it (an unsigned rebuild may ask again).

```sh
skua account add --name alt --allow-agents   # agents' logins may use it while it is active
skua account add --name alt --replace        # replaces a stored account, e.g. its password
skua account show [name]                     # username, service and whether agents may use it; never the password
skua account use alt                         # the next 'skua login' uses it
skua account remove [name]                   # deletes it; removing the active one makes the Test Account active
```

The Test Account keeps its reserved name `test` and service `skua-test-account`, which agents and the live tests use. `account add` never touches it except with `--test` (which stores or replaces it, and leaves the active account as it is) or `--name test --replace`, and `account remove` deletes it only by name (`skua account remove test`).

`skua login [server]` (MCP `login`) logs in and returns once it is playing with its inventory loaded, with the server, the account's username and whether it is the Test Account, and prints them: `Logged in as SkuaTester (the Test Account) on Galanoth.`; without a server it picks an online, non-member server with room, and `skua servers` lists them. The CLI's `login` uses the active account. MCP's `login`, an agent's, uses the Test Account unless the active account was added with `--allow-agents`; see ADR 0005. It takes no credentials, and no MCP tool sets or switches an account. The password and the game's `<pword>` login token are redacted from every log, event and log file. While logged in, the Engine holds off idle sleep (`pmset -g assertions` lists it) and keeps the lag killer on, lifting it for screenshots.

#### Moving and reading the game

Once playing, `skua join <map> [cell] [pad]` and `skua jump <cell> [pad]` (MCP `join`, `jump`) move the player and print where it ended up; `skua status` adds a player summary. `skua inventory [inventory|bank|temp|house]`, `skua quests [loaded|active]`, `skua map` and `skua drops` (MCP tools of the same names) read the game as DTOs; the bank is fetched from the game server the first time it is listed after each login. Anything else is for `eval`.

#### Script Source

`skua scripts update` (MCP `scripts_update`) syncs Scripts into `<SkuaDIR>/Scripts` from the Script Source, `noelrohi/Scripts@Skua` by default on macOS (see below). The first sync from a Script Source downloads every Script; later ones check the head commit once and download only the Scripts changed since the last synced commit, ending with a summary such as "3 new, 12 changed". `skua script start` runs the same update first, unless given `--no-update`; when GitHub can't be reached it warns and starts the Scripts on disk.

Finding Scripts, and what's new:

```sh
skua scripts search [query] [--tag <tag>]   # MCP scripts_search; no query lists every Script
skua scripts list [folder]                  # MCP scripts_list; one folder as a tree, e.g. 'skua scripts list Farm', with descriptions
skua scripts new [--since <date|commit>]    # MCP scripts_new; what updates added or changed, and when: the last 7 days by default
```

Each update that downloads Scripts is recorded in `<SkuaDIR>/scripts-history.json`, next to `scripts-commit.txt`, so `scripts new` works offline. A full download isn't listed as new, but it also records the Script Source's commits of the last 7 days from GitHub's commits API, so `scripts new` has news from the first day: the `.cs` files each commit added or changed, with its time and SHA, marked as the Script Source's history rather than an update. It reads at most the 20 newest commits, with at most 21 API requests (the list, then each commit's files) and no retries, since GitHub allows 60 unauthenticated requests an hour. When GitHub refuses them, the download still succeeds, and `scripts new` says it has no history from before the download rather than that nothing changed; when it had more commits than it read, `scripts new` says where the history starts. The history keeps the latest 200 updates and commits.

`--since` takes a date or a time, read as local time: `--since 2026-09-01` starts at midnight on the Mac's clock, not UTC. Add an offset to give another zone, e.g. `2026-09-01T00:00Z`. The Engine reads the date in the time zone it started with, which it takes from the CLI that starts it: the Mac's own, or `TZ` if set. After changing the zone, run `skua engine stop` so the next command starts an Engine in the new one.

On macOS the default Script Source is `noelrohi/Scripts@Skua`, not upstream's `auqw/Scripts@Skua`, which the Windows app keeps. Upstream's `CoreBots.cs` uses Windows Forms, so every Script that includes it, `Farm/Leveling.cs` among them, fails to compile on macOS. The fork is upstream plus two `skua-macos` patches that put the Windows Forms code behind `#if !MACOS`; a workflow in the fork merges upstream into it daily, and opens an issue there when a merge conflicts instead of forcing it.

`skua scripts source` shows the Script Source and whether it is the default, and changes it:

```sh
skua scripts source                         # MCP scripts_source; e.g. "noelrohi/Scripts@Skua (the default)"
skua scripts source auqw/Scripts@Skua       # owner/repo@branch
skua scripts source --default               # back to the default
```

It writes `ScriptSource` under `shared` in `<SkuaDIR>/Skua.settings.json`, or removes it for the default, under Core's settings lock, leaving the rest of the file alone; a `ScriptSource` set there by hand wins over the default too. Before the macOS default changed, Core wrote `auqw/Scripts@Skua` into the file whenever it saved it, so an older Mac setup may still name upstream: `skua scripts source` then says it is set, and `skua scripts source --default` moves it to the fork. It takes effect at once, without restarting the Engine, which reads the setting every time it reaches the Script Source. It is refused while a Script runs or an update is in flight, and MCP can only read it. The Scripts on disk stay until the next `skua scripts update` (or `script start`), which downloads every Script from the new Script Source, since `<SkuaDIR>/scripts-source.txt` names another; later updates are incremental again.

Script files always come from the Script Source itself, not from the `downloadUrl`s in `scripts.json`, which a fork keeps pointing at upstream.

#### Running Scripts

A Script is named by its path in the Script Source (`Farm/Leveling.cs`) or by an absolute path.

```sh
skua script start Farm/Leveling.cs --follow           # updates the Scripts, starts it, prints its log under a live status line
skua watch                                            # the same live view for a Script already running; Ctrl-C leaves it running
skua script options Farm/Leveling.cs                  # keys, types, stored values, defaults, choices
skua script start Farm/Leveling.cs --option key=value # stores the values, compiles, starts
skua script wait --timeout 600                        # returns when the run ends, or on timeout
skua script status
skua script stop                                      # cooperative; about 10 s at most
skua eval 'Bot.Player.Level'                          # a C# expression or statements against Bot
skua dialogs                                          # the pending Questions
skua dialogs answer 3 Yes                             # the first answer wins
```

On a terminal, `--follow` and `skua watch` keep a status line under the log, refreshed every second: level, XP toward the next level as a percentage, gold with the change since the follow began, map, and the run's elapsed time. `--follow` also asks the run's Questions there. Without a terminal, `--follow` prints only the log, and `skua watch [--interval <s>]` prints one status line per interval (a `ProgressDto` per line with `--json`). `status` (MCP `status`) reports the same: the player's `xp`, `requiredXp` and `xpPercent`, and the run's `elapsedSec`.

The MCP tools are `script_options`, `script_start`, `script_stop`, `script_status`, `script_wait`, `dialogs`, `dialog_answer` and `eval`. A compile failure is `CompileFailed` with the compiler's diagnostics. While a Script runs, `login`, `logout`, `join`, `jump`, `scripts update`, `scripts source <source>` and `script options` are refused with `ScriptRunning`; queries, logs, screenshots and `eval` still work. Each run has a number, which its log entries carry as `run`, and `script.started`, `script.error` and `script.stopped` events. A restart by Core's auto-relogin is the same run, counted in its `relogins`. Core's options window, which it opens at a Script's first start, does nothing headless: the Script runs with its stored values. `eval` runs off the Script Thread with a 30 s limit, and returns the value as JSON, the log lines it wrote, and what it threw.

A Script's message boxes are Script Dialogs. An OK-only one is a Notice: it never waits, returns null at once, and arrives as a `notice.shown` event with its full text (up to 64 KB). A yes/no or buttons one is a Question, and only the thread that raised it waits. With `--dialogs ask` (the default) it stays pending for `--dialog-timeout` seconds (120 by default), listed by `skua dialogs` (MCP `dialogs`) and `status`, and `script wait` returns as soon as one is pending; `skua dialogs answer <id> <choice>` (MCP `dialog_answer`) answers it, and a later answer fails with `DialogNotPending`. Unanswered, or with `--dialogs cancel`, it gets the fallback: null or `DialogResult.Cancelled`, never the first button. `script stop` gives pending Questions the fallback first. `question.raised` and `question.answered` events record each one, with `answeredBy` `agent`, `user` (the Mac App's window, protocol 11), `timeout` or `fallback`. Headless, other dialogs (`ShowDialog` and the file dialogs) are never shown; they return null and are logged. The Mac App shows them for real.

#### Compile check

After each upstream merge into the Scripts fork, check that every Script still compiles on macOS. The check copies a Scripts checkout into a throwaway data folder, calls `script_options` for every Script its `scripts.json` lists, and fails with each failing Script and its diagnostics:

```sh
git clone --branch Skua https://github.com/noelrohi/Scripts.git ../Scripts
dotnet build Skua.Engine.Tests
SKUA_SCRIPTS_CHECKOUT="$(realpath ../Scripts)" dotnet test Skua.Engine.Tests --no-build \
  --filter FullyQualifiedName~CompileCheckTests.Every_Script_in_the_Scripts_checkout_compiles --logger "console;verbosity=detailed"
```

Scripts broken upstream on every platform are listed in `Skua.Engine.Tests/compile-check-known-failures.txt`: the report still shows them, but only a failure missing from that list fails the check, or a listed Script that no longer fails. Without `SKUA_SCRIPTS_CHECKOUT`, the test is skipped. The `Scripts compile check` workflow runs it daily and on demand against `noelrohi/Scripts@Skua`. It never runs on pull requests, so it doesn't block them.

#### Live-game tests

`LiveGameTests` prove the Engine against the real game: a real `skua-engine` with the real Game Host and the Test Account, driven through the Control Surface. They never run in CI, and each logs in once, so run one at a time, on a quiet Mac (1-minute load average under 3, with no other tests, builds or Game Hosts running; the test refuses to start otherwise). Each needs a `noelrohi/Scripts@Skua` checkout, which it copies into a throwaway data folder with the one-time Script Dialog files (`OneTimeMessages.txt`, `DataCollectionSettings.txt`) pre-seeded:

```sh
git clone --branch Skua https://github.com/noelrohi/Scripts.git ../Scripts
dotnet build Skua.Engine.Tests
SKUA_LIVE=smoke SKUA_SCRIPTS_CHECKOUT="$(realpath ../Scripts)" \
  dotnet test Skua.Engine.Tests --no-build --filter FullyQualifiedName~LiveGameTests
```

| `SKUA_LIVE` | Run | Passes when |
|---|---|---|
| `smoke` | Login; `battleon` for 5 min; `Farm/Leveling.cs` for 15 min (about 25 min) | Game Host footprint < 2 GB; getter p99 ≤ 50 ms every minute but a phase's first; every join ≤ 10 s; no `bridge.error`, panic, death, disconnect or relogin; correct screenshots |
| `memory` | `Farm/Leveling.cs` for 2 h | As the smoke, and the footprint flat: its slope over the last hour ≤ 1 MB/min |
| `crowded` | Idle in `battleon` for 2 h | As `memory` |
| `hidden` | `Farm/Leveling.cs` for 30 min with the screen locked or the display asleep; after the Script starts, lock the screen or run `pmset displaysleepnow` (the test waits `SKUA_LIVE_HIDE_WAIT_MIN`, 10 min) | It stays hidden, the player's gold or level rises every 5 min, the Game Host reports 30 fps and ticks at ≥ 90% of it with no tick gap over 250 ms, and screenshots are correct |

`SKUA_LIVE_SERVER` picks the server (Galanoth by default). A login failure, a disconnect, a relogin or the Game Host exiting ends the run at once, and it stops the Script so Core's auto-relogin can't log in again. Every minute the run records the Game Host's and the Engine's footprint (Activity Monitor's Memory, the gate metric) and RSS, and the round trips of `Bot.Player.Cell` measured by an `eval` loop. It writes them to `report.txt` in `Skua.Engine.Tests/bin/<Configuration>/net10.0/live-results/<time>-<run>/` (or `SKUA_LIVE_OUT`), with its screenshots. A failed run also leaves `failure-screenshot.png`, `logs-all.jsonl` (`logs(all)` since its start) and the Engine's JSONL log files there. `LiveRunTests` dry-run the same scenarios against the fake Game Host in CI.

### Building the Installer

Requires WiX CLI and MSBuild:

```powershell
# First install WiX CLI if not already installed
dotnet tool install --global wix

# Using MSBuild directly
msbuild Skua.Installer\Skua.Installer.wixproj /p:Configuration=Release /p:Platform=x64

# Or find MSBuild path first
"C:\Program Files\Microsoft Visual Studio\18\{EDITION}\MSBuild\Current\Bin\MSBuild.exe" ^
  Skua.Installer\Skua.Installer.wixproj ^
  /p:Configuration=Release ^
  /p:Platform=x64
```

## CI/CD

The `macOS` workflow builds `Skua.MacOS.slnf` (including `skua-gamehost` and `skua.swf`), runs the Game Host's `cargo test` and then the .NET tests, on every push to `master` and on every pull request. It caches cargo (rust-cache, saved only on `master`) and the Flex SDK downloads (`~/Library/Caches/skua-as3`, keyed on `compile-as3.sh`).

### Local CI Testing

Test the build process locally before pushing:

```powershell
# Full build test
.\Build-Skua.ps1 -Configuration Release

# Debug build test
.\Build-Skua.ps1 -Configuration Debug -Platforms "x64"
```

## Troubleshooting

### Common Issues

#### WiX CLI Not Found

- **Error**: "WiX CLI v6+ not found"
- **Solution**: Install WiX CLI using: `dotnet tool install --global wix` or [get it here](https://github.com/wixtoolset/wix/releases/tag/v6.0.2)
- **Verify**: Run `wix --version` to confirm installation

#### MSBuild Not Found

- **Error**: "MSBuild not found"
- **Solution**: Install Visual Studio or Build Tools for Visual Studio
- Alternative: Use Developer Command Prompt

#### NuGet Restore Failures

```powershell
# Clear NuGet cache
dotnet nuget locals all --clear

# Restore with verbose output
dotnet restore --verbosity detailed
```

#### Platform Build Issues

```powershell
# Ensure platform is specified correctly
dotnet build -p:Platform=x64  # Not "--platform x64"

# For x86, may need explicit target
dotnet build -p:Platform=x86 -p:PlatformTarget=x86
```

#### Permission Errors

- Run PowerShell as Administrator if needed
- Check execution policy: `Get-ExecutionPolicy`
- Temporarily bypass: `powershell -ExecutionPolicy Bypass -File Build-Skua.ps1`

#### Optimization Tips

1. **Parallel builds**: Use `-Parallel` flag to build platforms and installers simultaneously
2. **Incremental builds**: Use `-SkipClean` when testing (fastest for development)
3. **Skip installer**: Use `-SkipInstaller` during development
4. **Binary logging**: Use `-BinaryLog` to analyze build performance with MSBuild Structured Log Viewer

#### Recommended Build Commands

```powershell
# Daily development (fastest)
.\Build-Skua.ps1 -Parallel -SkipClean -SkipInstaller

# Full build for release
.\Build-Skua.ps1 -Parallel

# Debug and analyze build
.\Build-Skua.ps1 -BinaryLog
```

### Validation

Verify your build:

```powershell
# Check output files exist
Get-ChildItem -Path build -Recurse -Include *.exe, *.dll, *.msi

# Test the application
.\build\Release\x64\Skua.App.WPF\Skua.exe

# Verify installer
msiexec /i "build\Installers\Skua_Release_x64_Skua.Installer.msi" /quiet
```

## Advanced Configuration

### Build Version Management

Versions are centrally managed in `Directory.Build.props` at the repository root:

```xml
<!-- Directory.Build.props -->
<?xml version="1.0" encoding="utf-8"?>
<Project>
  <PropertyGroup>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>1.0.0.0</FileVersion>
    <Version>1.0.0.0</Version>
  </PropertyGroup>
</Project>
```

## Contributing

When contributing build system changes:

1. Test all platforms (x64, x86)
2. Test both Debug and Release configurations
3. Verify the installer builds correctly
4. Update this documentation if needed
5. Test GitHub Actions workflow locally if possible

## Support

For build issues:

1. Check [Prerequisites](#prerequisites) are installed
2. Review [Troubleshooting](#troubleshooting) section
3. Check existing GitHub issues
4. Create a new issue with:
   - Build error messages
   - System information (Windows version, .NET version)
   - Steps to reproduce

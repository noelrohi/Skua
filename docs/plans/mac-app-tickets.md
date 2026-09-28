# Mac App: ticket breakdown (#61)

These are the proposed issues for the Mac App, written as vertical slices with the walking skeleton first. The design is in [ADR 0006](../adr/0006-mac-app-hosts-the-engine.md), and the evidence is in [the investigation](mac-app-investigation.md). None of these is on GitHub yet: they get reviewed first.

Every ticket's parent is **#61 Mac desktop app for Skua (Avalonia, Skua-like, interactive game view)**. `M1`…`M15` are placeholders for issue numbers.

## Order

| Ticket | Blocked by |
|---|---|
| M1 Walking skeleton: live game, click and type | none |
| M2 The app owns its Engine | M1 |
| M3 Status and login | M1 |
| M4 Scripts with the Script log | M1 |
| M5 Script options | M4 |
| M6 Questions and Notices | M4 |
| M7 Options: Game, Application and Themes | M4 |
| M8 HotKeys | M7 |
| M9–M12 Helpers, Tools and Bank, Skills, Packets | M4 |
| M13 Game View polish | M1 |
| M14 Package and install | M2, M4 |
| M15 Live check with the Game View open | M1, M4, M13 |

Suggested sequence to a usable app: M1, M2, M3, M4, M6. After M4, the panel tickets (M5, M7–M12) can run in parallel.

| Ticket | Suggested label |
|---|---|
| M1–M14 | `ready-for-agent` |
| M15 | `ready-for-human`: it needs a live login |

M1 also has one manual check on the real login screen.

---

## M1. Walking skeleton: the Mac App shows the live game, and you can click and type in it

### What to build

The thinnest end-to-end Mac App (ADR 0006). It opens a window showing the live Game Client, and the developer can click and type in it: on the AQW login screen, the username field takes focus and text. Its Engine serves the normal socket, so `skua status` in a terminal talks to it.

- **Engine as a library:**
  - Move `Engine`, `EngineServices`, the operations, the logging and `GameHostSupervisor` from `Skua.App.Engine` into a new `Skua.Engine` library. `skua-engine` keeps `Program`, `Detach` and `MacBuild.targets`, and its behaviour and output don't change.
  - Add the host options: headless or app, plus a hook to add services before `Ioc.Default` is configured. In app mode there are no stderr logs, no `--detach` and no signal handlers.
- **Projects:**
  - `Skua.Avalonia`: a library on Avalonia 12.
  - `Skua.App.Mac`: an exe with assembly `Skua`. `MacBuild.targets` puts `skua-gamehost` and `skua.swf` next to it.
  - Both go in `Skua.sln` with `Build.0` for `Any CPU` only, and in `Skua.MacOS.slnf`.
- **App startup:**
  1. Resolve the Engine Name (`--name`, default `default`).
  2. Start the in-process Engine, which binds the socket before Avalonia starts.
  3. Open a main window holding only the `GameView`.
  - If another Engine holds the name, show a message and quit. Taking over comes in M2.
- **Game Host:**
  - `--frame-buffer=<shm name>`: map the Frame Buffer (header plus three RGBA8 slots) before the first ping reply.
  - `W` switches the render policy between live (33 ms, no budget) and the headless defaults.
  - When live, the render thread copies each finished frame from the readback buffer into the free slot and publishes it.
  - `U` input frames become `PlayerEvent`s through `Player::handle_event`, followed by an immediate tick. Kinds: mouse move, down, up and leave, wheel, key down and up, text, focus gained and lost.
  - `Q` stats gain `live`, `framesWritten` and `inputEvents`.
  - Unit tests cover the `U`/`W` parsing and the Frame Buffer layout (no GPU).
- **Skua.MacOS:**
  - The Engine creates the shm object (mode 0600) and unlinks it after the first ping.
  - A Frame Buffer reader (seqlock, latest frame wins) and an input sender on `GameHostProcess`.
- **`GameView`:**
  - Draws the latest frame, scaled to fit with letterboxing, polling on `RequestAnimationFrame`.
  - Maps the pointer back to viewport pixels.
  - Maps Avalonia's physical key and key symbol to Ruffle's `KeyDescriptor`, using Ruffle `desktop/src/util.rs:64` as the reference, and sends `TextInput` as text events.
  - Takes focus on click, and sends focus and mouse-leave.
  - It is live while the window is visible, and not live while minimised, hidden or occluded.
  - Every 60 s it logs frames shown and frame age (p50/p95) to the Engine debug log, next to `[gamehost] stats`.
- **Fake Game Host:**
  - Accepts `--frame-buffer`, `W` and `U`.
  - Writes a synthetic numbered frame while live.
  - Records each input event in its call log.

### Acceptance criteria

- [ ] `skua-engine` behaves as before: the existing `Skua.Engine.Tests` pass unchanged.
- [ ] The Mac App starts, and `skua status` connects to its Engine and returns engine fields.
- [ ] Against the fake Game Host:
  - the Game View shows the synthetic frames, and the frame number advances;
  - a click at a known view point arrives as `MouseDown`/`MouseUp` at the expected viewport pixels, including when letterboxed;
  - typed text arrives as text events, and keys as key events, in order.
- [ ] Minimising the window sends `W` headless, and restoring it sends `W` live. The Game Host's `Q` stats show the switch.
- [ ] `dotnet build Skua.MacOS.slnf` builds the app on macOS CI, and the Windows `x64` build of `Skua.sln` doesn't build the new projects.
- [ ] Manual check, with no login: on the real login screen, clicking the username field and typing shows the text, and Tab moves focus.
- [ ] 30 min live on the login screen: frames shown at the Game Client's frame rate (capped at 30 fps), frame age p95 ≤ 50 ms, and a Game Host footprint slope ≤ 1 MB/min over the last 15 min. Record the numbers in the PR.

### Blocked by

None.

---

## M2. The app owns its Engine: take over, close versus quit, and the CLI can't stop it

### What to build

The rules for sharing one Engine Name between the Mac App, the CLI and MCP (ADR 0006, "Process model").

- **Protocol:**
  - `hello` and `status` return `host: "engine" | "app"`. This bumps the protocol version.
  - On an app-hosted Engine, `shutdown` and `shutdown_if_idle` return a new `EngineOwnedByApp` error.
  - The CLI maps that error to its own exit code and the message "the Skua app owns Engine '<name>'; quit the app to stop it". `ReplaceStale` never replaces an app Engine.
- **Take over:** when a headless Engine holds the name at launch, the app shows that Engine's status (account, map, Script and whether it's running) and offers **Take over** or **Quit**.
  - Take over calls `shutdown`, waits for the lock, then starts the app's Engine. If a Script is running, it confirms a second time.
  - A mismatched `hello` (another build) gets the same dialog. Take over still works through `shutdown`.
- **Close and quit:**
  - Closing the main window keeps the app and Engine running: the Game View goes headless, and the Dock icon reopens the window.
  - Cmd-Q asks for confirmation if a Script is running, then stops the Engine (Script, Game Host, connections, socket, lock) and exits.
  - SIGTERM quits the same way, without asking.

### Acceptance criteria

- [ ] `skua engine stop` against the app's Engine exits with the `EngineOwnedByApp` code and leaves the game running.
- [ ] A CLI from another build doesn't replace the app's Engine.
- [ ] With a headless Engine running, launching the app offers Take over. Taking over leaves one Engine, owned by the app, and `skua status` shows `host: "app"`.
- [ ] Closing the window keeps `skua status` working. Quitting removes the socket and releases the lock. A later `skua status` auto-starts a headless Engine.
- [ ] Engine tests cover the refusals and the `host` field. The app-side flows are covered with the fake Game Host.

### Blocked by

- M1

---

## M3. Status and login in the window

### What to build

The main window gains a status strip and the login controls, so a developer can go from launch to in-game without a terminal.

- **Status strip:** Engine Name, host, game state (no game / login screen / logged in), account, server, map and cell, level, and whether a Script is running.
  - It is fed by the same in-process state the `status` RPC maps (`GameStateTracker`), updated on game events rather than polled.
- **Log in:**
  - A server picker (the `servers` list) and a **Log in** button that uses the Active Account, the same path as `skua login` without `asAgent`. It shows the username it logged in as.
  - **Log out.**
  - Errors are shown inline: no Active Account, a Keychain denial, or a server that is full or offline.
- Logging in by typing into the game's own login screen (M1) keeps working. The status strip follows either route.
- The first login from the app binary prompts Keychain once. Note it in the README.

### Acceptance criteria

- [ ] Against the fake Game Host (with the fake Keychain tool and servers API): Log in reaches "logged in", the strip shows the account, map and server, and Log out returns to the login screen.
- [ ] A login started from `skua login` in a terminal updates the strip too.
- [ ] Each error case shows a message and leaves the controls usable.
- [ ] No agent path changes: an MCP login with `asAgent` still follows ADR 0005.

### Blocked by

- M1

---

## M4. Scripts: load, start and stop, with the Script log

### What to build

The first reuse of the Windows app's screens: the main menu shell, the Scripts panels and the Logs panel. It runs on Core's own view models, with Avalonia views ported from `Skua.WPF`.

- **Avalonia services** in `Skua.Avalonia`, registered by `AddAvaloniaServices()` through the app's host hook:
  - `IDispatcherService`, `IWindowService` (managed windows as Avalonia windows, by key, as `ManagedWindows` registers them), `IClipboardService` and `IFileDialogService`.
  - `AddSkuaMainAppViewModels` is registered too.
- **UI-thread marshalling:** property changes that Core's view models raise off the UI thread reach bindings on the UI thread. Do it in `Skua.Avalonia`'s binding layer, with no Core change. `ScriptLoaderViewModel.ScriptStopped` and `CurrentDropsViewModel` are the known cases.
- **Main menu:**
  - A native macOS menu bar, plus the in-window menu, built from `MainMenuViewModel`.
  - Scripts and Logs are enabled. The other items are shown disabled until their tickets land.
- **Views** ported from `Skua.WPF`: `ScriptLoaderViewModel`, `ScriptRepoViewModel` (including the search and filtering logic now in `ScriptRepoView.xaml.cs`), `LogsViewModel` and `LogTabViewModel`.
- **Logs:** `EngineLogService` also sends `AddLogMessage`, so the Logs panel's Script, Debug and Flash tabs update live. The Engine's own log store is unchanged.
- The Script Source is the Engine's (the Mac-ready fork). Update and search use the same operations as `skua scripts`.

### Acceptance criteria

- [ ] From the Scripts panel: pick a Script from the Script Source, start it, watch its log lines appear live, and stop it. This is tested against the fake Game Host.
- [ ] A Script started from `skua script start` shows as running in the panel. Stopping it from the panel ends `skua script wait`.
- [ ] Stopping a Script from its own thread updates the panel with no "Call from invalid thread" error. A test drives a view model change from a background thread.
- [ ] `Skua.Avalonia.Tests` (Avalonia.Headless): each registered view model in this ticket resolves a view and binds with no binding errors.
- [ ] No file in `Skua.WPF` or `Skua.App.WPF` changes. If Core changed, list the Windows check for #37.

### Blocked by

- M1

---

## M5. Script options

### What to build

A Script's options are edited in the window, as on Windows.

- An Avalonia `IScriptOptionContainer`, replacing `HeadlessScriptOptionContainer` in app mode. When a Script calls its options UI, the developer gets an editor; saving writes the same option files the Engine reads.
- Core's CoreBots options (`CoreBotsViewModel`) under Options → CoreBots.
- The `script_options` RPC and the panel read and write the same values. An agent setting options while the app is open sees the same result.

### Acceptance criteria

- [ ] A Script with options opens the editor. Values saved there are the ones the next run uses, and `skua script options` shows them.
- [ ] Options set with the CLI show in the editor the next time it opens.
- [ ] CoreBots options load, edit and persist.
- [ ] `skua-engine` still uses the headless container.

### Blocked by

- M4

---

## M6. Questions and Notices in the window

### What to build

A Script's Questions can be answered from the window as well as by agents, and its Notices are visible without ever blocking (ADR 0006, "Script Dialogs keep one broker").

- In app mode, `IDialogService` message boxes still go through `ScriptDialogBroker`, and the app subscribes to its events.
- **Question:** a sheet on the main window with the caption, the text, the choice buttons and a countdown to the run's timeout.
  - The first answer wins, whether it comes from the window, `dialog_answer`, the timeout or a stop. Any other open sheet for that Question closes.
  - A window answer records `answeredBy: user`. This bumps the protocol, and the CLI and MCP accept `user`.
- **Notice:** a non-blocking list (with a badge), and a macOS notification when the app isn't frontmost. The full text can be opened and copied.
- `ShowDialog(vm)` and the file dialogs become real Avalonia dialogs in app mode. Scripts that call them work as on Windows.

### Acceptance criteria

- [ ] A Question raised by a Script against the fake Game Host shows a sheet. Answering it resumes the Script with that choice, and the event log shows `question.answered` with `answeredBy: user`.
- [ ] `dialog_answer` from the CLI first: the sheet closes, and a late click is ignored.
- [ ] The timeout and stop still answer with the fallback.
- [ ] A Notice shows in the list and never stalls the Script. The test from #32 (a Notice on the timer thread) passes with the app host.
- [ ] `skua script start --follow` in a terminal still prompts, and whichever answer comes first wins.

### Blocked by

- M4

---

## M7. Options: Game, Application and Themes

### What to build

The Options panels, and the rule that the window's game follows the developer's options.

- Views for `GameOptionsViewModel`, `ApplicationOptionsViewModel` and `ApplicationThemesViewModel` (the theme and colour-scheme editors).
- An Avalonia `IThemeService`, built on the Fluent theme with Skua's light and dark schemes and accent colours. `IThemeService` is typed as `object`, so it returns Avalonia colours with no Core change.
- **LagKiller in app mode:** the Engine no longer forces it on at login. The Game Options value applies, as on Windows. `skua-engine` keeps forcing it.
- Settings persist where the Engine's settings service keeps them. They are shared with the CLI where both read the same key.
- `BackgroundThemeViewModel`'s `explorer.exe` moves behind `IProcessService` ("reveal in Finder" on macOS). Windows behaviour stays identical.

### Acceptance criteria

- [ ] Changing a game option (for example hide players or LagKiller) takes effect in the Game View at once, and survives a restart.
- [ ] After a login in the app, LagKiller follows the option. After a login in `skua-engine`, it is forced on, as before.
- [ ] Switching the theme restyles every open window.
- [ ] The only Core change is `BackgroundThemeViewModel`, with its Windows check listed for #37.

### Blocked by

- M4

---

## M8. HotKeys

### What to build

Skua's hotkeys work in the Mac App.

- An Avalonia `IHotKeyService`, with window-level key bindings, and the HotKeys options panel, including a port of the key capture in `AssignHotKeyDialog`.
- **Game View focus:**
  - When the Game View has focus, a key that is a bound hotkey goes to the hotkey and not to the game.
  - Keys typed into an AS3 text field, such as chat, always go to the game.
  - Hotkeys take ⌘ and ⌥ combinations by default, so plain keys stay with the game.
- On macOS the hotkey service doesn't depend on Core's `user32` call (`HotKeys.cs:12-16`).

### Acceptance criteria

- [ ] A bound hotkey fires from any Mac App window.
- [ ] Typing in the game's chat never fires a hotkey.
- [ ] Assigning a hotkey in the panel persists, and it works after a restart.

### Blocked by

- M7

---

## M9. Helpers: Runtime, Fast Travel, Current Drops, Auto and Jump

### What to build

- Views for `RuntimeHelpersViewModel`, `FastTravelViewModel`, `CurrentDropsViewModel`, `NotifyDropViewModel`, and the main window's `AutoViewModel` and `JumpViewModel` (auto attack and hunt, and jump to cell or pad).
- Enable those menu items.

### Acceptance criteria

- [ ] Against the fake Game Host:
  - jumping to a cell moves the player;
  - auto attack starts and stops;
  - a drop appears in Current Drops;
  - a Fast Travel entry joins its map.
- [ ] Each view binds without errors (Avalonia.Headless).

### Blocked by

- M4

---

## M10. Tools and Bank: Loader, Grabber, Junk Items, Stats, Console and Plugins

### What to build

- Views for `LoaderViewModel`, `GrabberViewModel`, `JunkItemsViewModel`, `ScriptStatsViewModel`, `ConsoleViewModel` and `PluginsViewModel`.
- **The Grabber's property grid:** port `Skua.WPF/PropertyGrid.xaml.cs` (677 lines), or take an Avalonia property grid package. Record the choice in the PR.
- **Bank** runs `IScriptBank.Open`, which opens the in-game bank in the Game View, as on Windows.
- **Plugins:** they load and run as `skua-engine` would load them. A plugin that needs WPF fails with a logged error, not a crash.

### Acceptance criteria

- [ ] Against the fake Game Host:
  - the Loader loads a shop or quest;
  - the Grabber lists inventory items and shows an item's properties;
  - Console runs a line.
- [ ] Bank opens the game's bank panel.
- [ ] Each view binds without errors.

### Blocked by

- M4

---

## M11. Skills

### What to build

- Views for `AdvancedSkillsViewModel` and its children, including saved skill sets with copy and paste through `IClipboardService`.
- Enable Skills in the menu.

### Acceptance criteria

- [ ] A saved skill set copies to the clipboard and pastes back.
- [ ] Choosing a skill set applies it to the running Script's skills, the same as on Windows.
- [ ] Each view binds without errors.

### Blocked by

- M4

---

## M12. Packets: Spammer, Logger and Interceptor

### What to build

- Views for `PacketSpammerViewModel`, `PacketLoggerViewModel` and `PacketInterceptorViewModel`, including the Interceptor's filtering from its code-behind.
- **Check the Interceptor's capture path first.** Find out whether it relies on a local proxy between the Game Client and the game server, and whether that works with the Game Host's socket backend. If it doesn't, ship Spammer and Logger, and split the Interceptor into its own issue with the finding.

### Acceptance criteria

- [ ] The Logger shows packets from the fake Game Host's `pext`/`packet` traffic.
- [ ] The Spammer sends a packet (the fake host records it).
- [ ] The Interceptor either works end to end, or has a follow-up issue that says why it doesn't.

### Blocked by

- M4

---

## M13. Game View polish: Retina rendering, cursor and clipboard

### What to build

- **Backing-size rendering:** the Game Host renders at the Game View's pixel size (`set_viewport_dimensions` with the scale factor) instead of 958×550, and the Frame Buffer slots grow to match.
  - `screenshot` keeps returning the native stage size, or `maxWidth`.
  - Measure the memory and frame age against M1's numbers. If the copy cost matters, write an IOSurface follow-up (ADR 0006, "Considered Options").
- **Cursor:** a `UiBackend` in the Game Host reports `set_mouse_cursor` changes (the hand over buttons) as a new Game Host → Engine frame, and the Game View sets that cursor.
- **Clipboard:** ⌘C and ⌘V in AS3 text fields, such as chat, work through the `UiBackend` clipboard. The headless host keeps a null clipboard.
- **Wheel:** scroll uses lines or pixels to match the device (trackpad or mouse).

### Acceptance criteria

- [ ] On a Retina display the Game View is sharp. The M1 numbers (fps, frame age, memory slope) are re-measured and recorded.
- [ ] The cursor changes over the game's buttons.
- [ ] Text copied on the Mac pastes into the game's chat, and back.
- [ ] `skua screenshot` output is unchanged in size.

### Blocked by

- M1

---

## M14. Package and install: Skua.app

### What to build

- **Bundle:** `Skua.App.Mac` publishes a self-contained `Skua.app` with `Info.plist`, the Skua icon, `Contents/MacOS/Skua`, `skua-gamehost`, `skua.swf` and the `skua` CLI from the same build. It is ad-hoc signed; notarisation is out of scope.
- **Install:**
  - `install-macos.sh --app` builds it and installs it to `~/Applications/Skua.app`.
  - It links `skua` from the same build, so `hello` never mismatches between the app and the CLI.
  - It keeps the pruning rules for older builds.
- **Launch:** the app takes `--name` and remembers the last Engine Name it used.

### Acceptance criteria

- [ ] A clean `install-macos.sh --app` gives a working `Skua.app` and a `skua` on PATH that connects to it.
- [ ] Reinstalling while the app runs doesn't break it. The new build is used on the next launch.
- [ ] Keychain prompts once for the new app binary, as the README says.

### Blocked by

- M2, M4

---

## M15. Live check: memory and latency with the Game View open

### What to build

The #34-style live gate, run with the Mac App open. It is for a human (live login), and follows the usual rules: one Test Account login at a time, on Galanoth, on a quiet Mac.

- A 2 h `Farm/Leveling.cs` run started from the app, with the Game View live the whole time.
- A 1 h idle run in battleon with the Game View live, to compare against #67's headless numbers.
- Record frame age, click-to-frame latency, Bridge getter p99, and the Game Host and app footprint slopes.

### Acceptance criteria

- [ ] Game Host footprint: last-hour slope ≤ 1 MB/min and under 2 GB in both runs. Otherwise, file an issue with the numbers.
- [ ] Frame age p95 ≤ 50 ms, click-to-frame p95 ≤ 100 ms, and getter p99 < 1 ms.
- [ ] The app process's own memory is flat. A leak in `WriteableBitmap` or in the log lists would show up here.
- [ ] Results and artifacts are recorded as for #34.

### Blocked by

- M1, M4, M13

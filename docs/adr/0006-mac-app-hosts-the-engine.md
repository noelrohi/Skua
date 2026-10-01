# The Mac App hosts the Engine in its own process; the game reaches it through shared memory, and input goes back as Bridge frames

Status: proposed (#61)

The Mac App, Skua's Avalonia desktop app on macOS, runs an Engine in its own process. That Engine serves the same Control Surface socket as `skua-engine`, so the CLI and MCP keep working alongside the window. We chose this because the maintainer asked to reuse the Windows app's screens (#61). Those screens are Core's own view models (`Skua.Core/ViewModels/**`, plain `net10.0`), and they call Core in-process: `IScriptInterface`, `IScriptManager`, the messengers. As a client of `skua-engine`, the app could reuse none of them without putting every panel on the wire, which ADR 0002 rules out.

The Game Host writes each rendered frame into a shared-memory Frame Buffer that the app reads. The app forwards mouse and keyboard input to Ruffle as new Bridge frames. The Windows app is unchanged: the new projects are macOS-only, and views are ported, not shared. Evidence: [the investigation](../plans/mac-app-investigation.md).

## Process model

- **One Engine per app.** The Mac App serves one Engine Name: `default`, or `--name <n>`.
  - It holds the same flock lock, binds the same `<SkuaDIR>/engines/<name>.sock` (mode 0600), and answers every `IEngineRpc` method. ADR 0001's rule holds: one process, one Game Client.
  - The app's view models are registered into the Engine's container, so `Ioc.Default` is configured once, as in the WPF app.
- **The Engine becomes a library.** The hosting code (`Engine`, `EngineServices`, the operations, the logs and the Game Host supervisor) moves from `Skua.App.Engine` into a `Skua.Engine` class library. The library takes host options:
  - **Headless** (`skua-engine`): today's behaviour. It keeps the stderr logging, `--detach` and the SIGTERM/SIGINT handlers, forces LagKiller on at every login, and uses the headless dialog services.
  - **App**: it skips all of the headless behaviour listed above. It logs only to the Engine's log files, quits through the app's lifetime, and leaves LagKiller to the user's Game Options, as on Windows. It also gets a hook that adds the app's services before the container is built.
  - The app starts its Engine, and binds the socket, before Avalonia's lifetime starts, so the process-wide `umask` around `bind` can't race the UI threads.
- **The app owns its Engine.**
  - `hello` and `status` gain `host: "engine" | "app"`, with a protocol bump.
  - `shutdown` and `shutdown_if_idle` are refused on an app-hosted Engine with a new error code (`EngineOwnedByApp`, "quit the Skua app"). So `skua engine stop` and the CLI's stale-Engine replacement never kill the window's game.
  - Closing the window keeps the Engine playing, and the Dock icon reopens it. Quitting the app stops the Engine as `StopAsync` does, and asks first if a Script is running.
- **One owner at a time.** If a headless `skua-engine` already holds the name when the app starts, the app shows its status (account, map, Script) and offers to take over.
  - Taking over stops that Engine with `shutdown` after the developer confirms; the game logs out and any Script stops. Then the app starts its own Engine.
    - That start comes after Avalonia's lifetime has started, so the `umask` around `bind` can race the UI threads: a file one of them creates during the bind gets owner-only permissions, which is harmless for a per-user app.
  - It never takes over silently. When the app isn't running, the CLI and MCP auto-start a headless Engine exactly as before.
- **Script Dialogs keep one broker.** In the app, message boxes still go through `ScriptDialogBroker`, so an agent's `dialog_answer` and the window can answer the same Question.
  - The window shows pending Questions and answers through the broker; `answeredBy` gains `user`.
  - Notices never block, as before.
  - `ShowDialog(vm)`, file dialogs and the Script options container are real Avalonia dialogs in the app, and stay headless in `skua-engine`.

## Frames and input

- **Frames: a shared-memory Frame Buffer, triple-buffered, latest frame wins.**
  - The Engine creates a POSIX shared-memory object (`shm_open`, mode 0600, a short name derived from the pid) and passes its name to the Game Host as `--frame-buffer=<name>`.
    - It is not a file, so 60 MB/s of frames never reach the disk as dirty pages.
    - The Game Host maps it before it answers its first ping, and the Engine unlinks the name after that ping.
  - Layout: a small header, then three slots sized for the largest frame.
    - Header fields: magic, version, width, height, stride, format, `latest` slot, a sequence number per slot, and a `mach_absolute_time` write stamp.
    - The format is RGBA8 (the target's `Rgba8Unorm`), opaque. No PNG and no un-premultiply: the stage has no alpha.
  - **Writer:** when the Game View is live, the Game Host's render thread maps the readback buffer after each `submit_frame`. `TextureTarget` already copies into that buffer on every submit (Ruffle `render/wgpu/src/target.rs:314`). It copies the rows into the slot that is neither `latest` nor being read, then publishes it by bumping `latest`.
  - **Reader:** the app polls the header on each display refresh (`TopLevel.RequestAnimationFrame`). On a new sequence it copies the slot into a `WriteableBitmap`, checking the sequence again after the copy (a seqlock).
  - There is no per-frame message on any pipe.
- **Cadence.** A new Engine → Game Host frame, `W` (view), switches the render policy.
  - **Live**: the `--show-game` policy, a 33 ms interval with no render budget, at the viewport the frame carries (Resolution, below).
  - **Headless defaults**: anything else, at the stage size.
  - The app sends live while the Game View is on screen, and headless when the window is minimised, hidden or fully occluded, or when the app quits. It learns of occlusion from AppKit's `NSWindowDidChangeOcclusionStateNotification` for the window, which Avalonia doesn't expose.
  - It sends live again, with the new viewport, when the view's size or its display's scale changes.
- **Input: a new Engine → Game Host frame, `U` (user input), fire-and-forget.**
  - Format: `u32 id` (0, no reply) + `u8 kind` + fields.
  - Kinds: mouse move, down, up and leave (x, y as `f32` in Game Host viewport pixels; button), wheel (lines or pixels), key down and up (physical key, logical key, location, as Ruffle's `KeyDescriptor`), text (a code point), text control (Ruffle's `TextControlCode`, such as Backspace: Ruffle edits text fields only through these), focus gained and lost, and clipboard (below).
  - The wheel goes as Ruffle's desktop player sends it: pixels from a device with precise deltas (a trackpad), lines from a mouse wheel. Avalonia's wheel delta doesn't say which, so the app reads AppKit's current scroll event.
  - The Game Host turns each into a `PlayerEvent`, calls `Player::handle_event` (Ruffle `core/src/player.rs:1060`), and ticks without waiting for the next frame.
  - The app maps pointer positions from the scaled, letterboxed image back to viewport pixels. Ruffle maps viewport pixels to stage coordinates itself.
  - Input stays inside the app process's Engine. It isn't on the Control Surface: agents keep `eval` and the typed operations.
- **Resolution.** A live Game View has the Game Host render at the view's backing size: `W` carries the stage's letterboxed rectangle in device pixels and the display's scale factor, and the Game Host calls `set_viewport_dimensions` with them (#87).
  - The viewport is never smaller than the stage, so a screenshot is scaled down from it, never up, and never larger than three times it (2874×1650), which sizes the Frame Buffer's slots. The Engine creates them at that size: the object's pages are only touched as frames grow into them.
  - The app draws a frame at the view's size one frame pixel to one device pixel, on whole device pixels. Any other size (a view smaller than the stage, or larger than the cap) is scaled to fit.
  - Headless, and whenever the view isn't live, the viewport is the native 958×550 stage.
  - `screenshot` stays at the native stage size, or `maxWidth`: a larger frame is scaled down to it.
- **Cursor and clipboard.** A Game Host with a Frame Buffer gets a Ruffle `UiBackend` for the Game View; a headless one keeps Ruffle's null backend, with its empty clipboard.
  - Cursor changes (`set_mouse_cursor`: the hand over buttons, the I-beam over text, and `Mouse.hide()`) go to the Engine as a new Game Host → Engine frame, `O` (cursor), and the Game View sets that cursor.
  - Ruffle reads the clipboard synchronously while it handles a Paste, when the Game Host can't ask the app. So on ⌘V the app sends the Mac's clipboard text as a `U` clipboard event, then the Paste. Input that comes while it reads the clipboard waits, so it reaches the game after the paste.
  - What the game copies (a Copy or Cut in a text field, or `System.setClipboard`) goes to the Engine as `K` (clipboard), and the app puts it on the Mac's clipboard.
- **Targets**, on an M-series Mac with the Game View live:

  | Measure | Target |
  |---|---|
  | Frame rate | The Game Client's frame rate, capped at 30 fps |
  | Frame age (write stamp to present) | p95 ≤ 50 ms |
  | Click or key to `handle_event` | p95 ≤ 5 ms |
  | Click to the first frame that shows it | p95 ≤ 100 ms |
  | Bridge getter round trips | Unchanged from headless (p99 < 1 ms, #17) |
  | Game Host memory | #34's gate: a last-hour footprint slope ≤ 1 MB/min, under 2 GB |

  The Game Host's `Q` stats gain the frames written, the input events, the live flag and the viewport. The app measures frame age itself.

## The Skua Manager

The Skua Manager, the counterpart of `Skua.Manager`, is **a process of its own, not a window in the Mac App** (#93): `Skua --manager`, from the same binary, one per data folder. Each app's menu bar (Manager › Skua Manager…) and Dock menu start it, or bring the running one to the front; it holds `<SkuaDIR>/manager.lock` and writes its pid next to it. It hosts no Engine and no Game Host.

- **Why not a window in the app.** An app is one Engine Name playing one account, and the Manager outlives any one of them: quitting the app that showed the Manager would close it while the apps it launched play on. Core's Manager view models also talk over the process-wide messengers the app's own panels use: the account list answers every Script load (`LoadScriptMessage`) with a message box, and `ShowMainWindowMessage` has a handler in both. In its own process none of that crosses.
- **Accounts live in Keychain.** Each account is a Keychain item written by the code `skua account add` uses (`Skua.Control.Accounts`), under its account name's service (`skua-account-<name>`). Its name is its username's default name, made unique; `test` and `default` are never used. The rest (display names, tags, groups, the last server) is in `<SkuaDIR>/Skua.manager.json`, owner-only, which holds no password and which no Engine writes. The Windows Manager keeps its list in `Skua.settings.json` with plain-text passwords (`SettingsModels.cs:13`); Core's settings service saves that whole file from memory, so the Manager reads and writes none of it.
  - Core's `AccountManagerViewModel` runs unchanged, over a settings service that sends a typed password to Keychain and returns none. Its Start messages go to the Mac launcher instead of `Skua.exe -p <password>`.
  - Removing an account deletes its Keychain item; if it was the Active Account, the Test Account becomes active again, as `skua account remove` does.
  - **Import** reads a Windows list (`manager.ManagedAccounts` and `AccountGroups` in `Skua.settings.json`, or the older `ManagerSettings.json`, objects or `display{=}user{=}password` strings). It moves each password to Keychain, copies the file to `<file>.<time>.bak` (owner-only; it still holds the passwords), and then blanks the passwords that moved, under Core's settings lock. An Engine reads the file's `manager` section afresh before it saves, so an Engine that read the passwords at its start never writes them back.
- **Launch.** Each launch starts `Skua --name <account> --account <account> [--server <s>] [--script <path>]`, detached, with its stdio on `/dev/null` and no `SKUA_ENGINE_SOCKET`. The Engine Name is the account name, so each account has its own app process and Engine (the one-per-Engine-Name rule above). No password is on the command line.
  - `--account` pins the Engine's account (`EngineHostOptions.AccountService`): its logins use that account instead of the Active Account, whoever asks. An agent's login keeps ADR 0005's rule with the pinned account in the Active Account's place, so it gets the Test Account unless that account allows agents. Manager launches are human logins.
  - Once its window shows, the app logs the account in through the login bar's own path, then starts the Script if one was given.
  - Launching an account whose app runs brings it to the front instead.
- **Running** lists every Engine whose socket in the data folder answers `hello` and `status`, apps and headless ones alike.
  - **Bring to front** sends the app `SIGUSR1`, which shows its main window as the Dock icon does, and activates it with `NSRunningApplication`, which macOS allows from the active app.
  - **Stop** asks first while a Script runs. An app gets `SIGTERM`, which quits it without asking; a headless Engine gets `shutdown`. Both are checked against the pid `hello` gives just before.
- **Updates** replaces Client Updates, which downloads Windows release zips. It shows the build `install-macos.sh` installed (the `skua` link's `versions/<build>`), the checkout's build (`<Version>+<commit>`, from the checkout the app was built in), and whether they match. To update, it gives the `./install-macos.sh` command. It downloads nothing.
- **Goals** is Core's `GoalsViewModel`, ported as is. Windows' Launcher (`Skua.exe` processes), Options (download folder, theme sync) and Client Updates have no macOS counterpart.

## Project layout

| Project | What it is |
|---|---|
| `Skua.Engine` (new, `net10.0` library) | The Engine, moved out of `Skua.App.Engine`, with the headless/app host options. |
| `Skua.App.Engine` | `skua-engine`: `Program`, `Detach` and `MacBuild.targets`. Its output is unchanged. |
| `Skua.Avalonia` (new, `net10.0` library, Avalonia 12) | The counterpart of `Skua.WPF`. Details below. |
| `Skua.App.Mac` (new, `net10.0` exe, assembly `Skua`) | The counterpart of `Skua.App.WPF`. Details below. |
| `Skua.MacOS` | Gains the Frame Buffer reader and the input sender next to `GameHostProcess`. |
| `Skua.GameHost` | Gains `--frame-buffer`, `W` and `U`. |
| `Skua.FakeGameHost` | Speaks `W` and `U`, writes a synthetic frame, and records input in its call log, so tests can check both. |
| `Skua.FakeApp` (new, test double) | The Mac App without a window, for the Skua Manager's tests: the same command line and App-hosted Engine, with the account pin. |
| `Skua.Avalonia.Tests` (new) | Avalonia.Headless tests: every view model has a view, and each view binds without binding errors. |

- **`Skua.Avalonia`:**
  - Avalonia views for Core's view models, ported from `Skua.WPF`'s XAML, with the same one-template-per-view-model mapping.
  - The `GameView` control.
  - The Avalonia implementations of `IWindowService`, `IDispatcherService`, `IClipboardService`, `IThemeService`, `IDialogService`, `IFileDialogService`, `IHotKeyService`, `ISoundService` and `IScriptOptionContainer`.
  - An `AddAvaloniaServices()` extension.
  - It marshals view-model property changes to the UI thread in its binding layer. WPF does this itself, but Core's view models raise `PropertyChanged` from Script and game threads.
- **`Skua.App.Mac`:** the composition root, the main window and the `.app` bundle layout. `skua-gamehost` and `skua.swf` sit next to the app binary through the same `MacBuild.targets` step.

The new projects join `Skua.sln` with a `Build.0` entry only for the `Any CPU` configurations, which `Skua.MacOS.slnf` and macOS CI build. The Windows build uses `x64`/`x86` (`Build-Skua.ps1:139`), so it never builds them. They also join `Skua.MacOS.slnf`.

## Packaging

`install-macos.sh --app` (#88) installs the app from the checkout, next to the CLI it already installs.

- **One bundle per build.** `Skua.App.Mac` publishes a self-contained, ad-hoc signed `Skua.app` into `versions/<build>/`. The app is in `Contents/MacOS`, and `Skua.App.Engine`'s publish (`skua`, `skua-engine`, `skua-gamehost`, `skua.swf`) is in `Contents/Helpers`: `Skua` and `skua`, and their `.dll` and `.deps.json` files, are one name on a case-insensitive disk. The CLI's files that match the app's are APFS clones, so the second runtime takes no space.
- **One build for the app and the CLI.** The `skua` link points into the bundle, so `hello` never refuses the app's Engine for its build.
- **The installed `Skua.app` only opens the build.** `~/Applications/Skua.app` is a small signed bundle, with the app's `Info.plist` and icon, whose executable is a script that `exec`s the build's app. Reinstalling swaps that small bundle and never changes a file a running app uses, since .NET loads assemblies, and the Engine starts the Game Host, from the app's folder long after launch. The pruning rule keeps a build while any process has a file open in it, so a running app's build stays until it quits. The next launch opens the new build.
- **Updates follow the app.** Once the app is installed, a plain `./install-macos.sh`, the command the Manager's Updates tab gives, updates it too.
- **Launch.** Without `--name`, the app serves the Engine Name it last served (`<SkuaDIR>/last-engine-name`), so the Dock reopens the Engine last used. The Manager's launches (`--account`) don't count.

Rejected options for the installed `Skua.app`:

- **A copy of the build.** Replacing it under a running app would change the files that the app loads later, and the build would mismatch its Game Host.
- **A symlink to the build, or a bundle whose `Contents` is one.** Finder and Launchpad treat a symlinked app as an alias. A symlinked `Contents` fails `codesign --verify` ("unsealed contents present in the bundle root").

A release (#148) is the other layout: one `Skua.app`, wherever the user puts it, that Sparkle replaces in place from the fork's GitHub Releases. As with the rejected copy above, a replaced bundle would change the files a running process loads later, so the app lets an update proceed only while nothing else runs from its bundle (another app, the Manager, or an Engine or MCP server its `skua` started); the app itself is replaced after it quits. A dev build from `install-macos.sh` never updates itself.

LaunchServices records a running app under the build's bundle (`lsappinfo` shows its path), so **Keep in Dock** on a running app may pin that build, which a later install prunes. Dragging `~/Applications/Skua.app` to the Dock pins the one that stays.

## How the Windows app stays untouched

This carries on ADR 0001 and #18's user stories 57 and 58.

- The app makes no edits to `Skua.WPF`, `Skua.App.WPF` (or Lite, Follower, Sync), their XAML, or `AddWindowsServices`. Views are ported copies, not shared files.
- Core changes happen only where the app reaches a Windows tie, and each one sits behind an existing interface or a platform guard, keeping Windows behaviour identical. An example is `BackgroundThemeViewModel`'s `explorer.exe` (`Skua.Core/ViewModels/Theme/BackgroundThemeViewModel.cs:80`) moving to `IProcessService`. The UI-thread marshalling lives in `Skua.Avalonia`, not in Core.
- `skua.swf` is unchanged: input reaches the Game Client through Ruffle, as a real Flash Player's input would, so the Bridge's `C`/`E` contract and the `DoABC` comparison in ADR 0003 still hold.
- Each ticket that touches Core lists the Windows check it needs; they run in #37's Windows VM pass.

## Considered Options

- **The Skua Manager as a window in each app.** Rejected (see [The Skua Manager](#the-skua-manager)): it would close with whichever app showed it, and Core's Manager and panel view models share process-wide messengers.
- **A separate `Skua Manager.app` bundle.** Deferred: `install-macos.sh` installs only `Skua.app` (see [Packaging](#packaging)), and `Skua --manager` is that app's process whenever a bundle of its own comes.

- **The app as a client of `skua-engine`, over the Control Surface.** Its advantage is that the game outlives the window. Rejected because:
  - Core's view models need Core in-process, and over the socket every panel would need its own typed operations. ADR 0002 rejected exactly that endless contract, and the Core models never cross the wire.
  - Frames would still need a cross-process path from the Game Host to a third process.
- **The Game Host shows its own window.** This covers the minifb debug window, a winit window with Ruffle desktop's input, and reparenting it into the app through the private `CALayerHost`/`CAContext`. Rejected: it gives two windows in two processes that can't compose with Avalonia's panels, or needs private API. It also gives the Game Host a GUI, which ADR 0003 turned down when it rejected forking Ruffle's desktop app.
- **Frames over the Bridge pipe.** Rejected: 958×550 RGBA at 30 fps is about 63 MB/s on the same stdout pipe as `R` replies, and it would block getters whose p99 is under 1 ms (#17).
- **Frames over a separate socket.** Rejected: it adds copies and framing, and gains nothing over shared memory on a one-to-one local link.
- **IOSurface, zero-copy.** Deferred rather than rejected. It needs three things: a mach-port handoff of the surface, an IOSurface-backed `MTLTexture` through wgpu-hal's `texture_from_raw` (Ruffle has no API for it), and GPU interop on Avalonia's side. It saves one 2 MB memcpy per frame, which `--show-game` already does at 30 fps. Revisit if rendering at Retina size makes the copy matter. At Retina size (#87) each copy is 8.4 MB; the measurements there found the frame age and CPU cost still within the targets, so it stays deferred.
- **Polling `screenshot` PNGs.** Rejected: PNG encoding on every frame, and over 100 ms of latency.
- **Sharing XAML with `Skua.WPF`.** Rejected: WPF and Avalonia XAML differ in namespaces, triggers, styles and controls. Sharing would mean editing the Windows views.

## Consequences

- Quitting the app ends its game and Script for every CLI and MCP session using that Engine Name. Closing the window doesn't.
- The protocol version goes up: `host` in `hello`/`status`, `EngineOwnedByApp`, and `answeredBy: user`.
- `hello` refuses a mismatched build, so the app and the CLI must come from the same build. `install-macos.sh` installs both from one commit.
- The app is a new binary to Keychain, so the first login from it prompts for "Always Allow" again (ADR 0002). Unsigned rebuilds may prompt again. Signing and notarisation are out of scope.
- A live Game View changes the Game Host's render cadence from about 1 s to 33 ms, which the headless memory defaults were not tuned for. The walking skeleton measures memory on the login screen, and a live run checks #34's gate with the view open. #67 (headless idle growth) stays separate.
- Hotkeys and typing in the game compete for the same keys. The Mac's `IHotKeyService` (#82) settles it: in the Game View a bound gesture goes to its hotkey before the game sees it, unless the Game Client's focus is on an input text field (chat, the login fields), which it asks over the Bridge (`stage.focus`, with a 200 ms limit; no answer means the key goes to the game). Its defaults are ⌘ gestures, so plain keys stay with the game, and a gesture's `Ctrl` is ⌘. It binds in the app's windows only, with no system-wide hotkey and no `user32` foreground check.
- Several Engines in one app would break ADR 0001's one-Game-Client-per-process rule. They would mean one app process per Engine Name.
- The Skua Manager is a third kind of process from the app's binary. It launches apps with the binary it runs from, so the Manager and its apps are always one build.
- A Manager-launched app's Engine uses its own account even for `skua login` against its socket. The Active Account still governs every other Engine.
- Removing an account in the Manager deletes the Keychain item that `skua account` shares with it.

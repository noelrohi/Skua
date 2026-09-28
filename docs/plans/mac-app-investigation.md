# Mac App: investigation (#61)

This is the evidence behind [ADR 0006](../adr/0006-mac-app-hosts-the-engine.md) and the [ticket plan](mac-app-tickets.md). It was gathered by reading the code at `9f8dc03`; nothing was run. Ruffle paths are relative to the pinned checkout, `~/.cargo/git/checkouts/ruffle-bdf71cedc404c260/171ab77` (rev `171ab779…`, tag `skua-20260927b-a1277c0`).

## 1. How coupled the WPF app is to WPF, and what Avalonia can reuse

**Short answer:** every view model is already WPF-free. The views, nine platform services and the Flash host are WPF-only.

**View models**
- All of them live in `Skua.Core/ViewModels/**` (about 89 files); `Skua.WPF` and `Skua.App.WPF` hold none.
- `Skua.Core` targets plain `net10.0` (`Skua.Core/Skua.Core.csproj:4`), as do Core.Interfaces, Core.Models and Core.Utils. Only `Skua.WPF` and `Skua.App.WPF` are `net10.0-windows` with `UseWPF` (`Skua.WPF.csproj:4-7`, `Skua.App.WPF.csproj:5-8`).
- They use CommunityToolkit.Mvvm 8.4.0 (`Skua.Core.csproj:25`), with `[ObservableProperty]` and `[RelayCommand]` source generation throughout.
  - Base classes: `ObservableObject`, `ObservableRecipient`, and `BotControlViewModelBase : ObservableRecipient, IManagedWindow` (`Skua.Core/ViewModels/BotControlViewModelBase.cs:6`).
- No view model references `System.Windows`. Dispatching, clipboard, dialogs, windows and themes all go through interfaces in `Skua.Core.Interfaces/Services/`.
- The Windows ties left in Core are few:
  - `Process.Start("explorer.exe")` in `Skua.Core/ViewModels/Theme/BackgroundThemeViewModel.cs:80`.
  - `notepad` in `Skua.Core/Services/ProcessStartService.cs:60`. The Engine already replaces it with `HeadlessProcessService`.
  - The `user32` P/Invoke in `Skua.Core/AppStartup/HotKeys.cs:12-16`. It is caught at `:35-47`, so hotkeys never fire off Windows.
  - The Manager's `Skua.exe` launchers.
- `IThemeService` is typed as `object` (`Skua.Core.Interfaces/Services/IThemeService.cs:15-35`), so an Avalonia implementation returns Avalonia colours. Core needs no change for that.

**The threading difference (the one real reuse risk)**
- Core view models raise `PropertyChanged` from Script and game threads.
  - `ScriptLoaderViewModel.ScriptStopped` sets `ToggleScriptEnabled` and `ScriptStatus` on the Script's thread (`Skua.Core/ViewModels/ScriptLoaderViewModel.cs:253-262`, registered at `:27-29`).
  - `CurrentDropsViewModel` raises `CurrentDrops` from the game-event channel (`CurrentDropsViewModel.cs:21, 80-83`).
- WPF marshals scalar `PropertyChanged` to the UI thread by itself. Avalonia doesn't, and throws "Call from invalid thread".
- Collection changes are already dispatched in Core, because WPF needs that too. For example, `LogTabViewModel.AddLog` goes through `IDispatcherService` (`Skua.Core/ViewModels/AppLogs/LogTabViewModel.cs:52-60`).

**Services the app has to implement**
- The WPF implementations are in `Skua.WPF/Services/` and registered by `AddWindowsServices` (`Skua.WPF/Services/ConfigureServices.cs:10-26`):

  | Service | Declared at |
  |---|---|
  | `IWindowService` | `IWindowService.cs:3` |
  | `IDialogService` | `IDialogService.cs:5` |
  | `IFileDialogService` | `IFileDialogService.cs:3` |
  | `IDispatcherService` | `IDispatcherService.cs:3` |
  | `IClipboardService` | `IClipboardService.cs:12` |
  | `IThemeService` | `IThemeService.cs:9` |
  | `IHotKeyService` | `IHotKeyService.cs:5` |
  | `ISoundService` | `ISoundService.cs:3` |
  | `IFlashUtil` | (the Flash host) |

- The macOS Engine registers only the headless ones (`Skua.MacOS/ConfigureServices.cs:16-29`): `BridgeFlashUtil`, `HeadlessDialogService`, `HeadlessFileDialogService`, `HeadlessProcessService` and `HeadlessScriptOptionContainer`. Nothing on macOS implements windows, dispatching, clipboard, themes, hotkeys or sound yet.
- `MainMenu.CreateViewModel` (`Skua.Core/AppStartup/MainMenu.cs:13`) and `LogTabs.cs:13` resolve those services, and 12 view models call `Ioc.Default` directly (for example `MainMenuItemViewModel.cs:39`). The app must therefore configure `Ioc.Default` itself.

**DI registration**
- The WPF composition root is `Skua.App.WPF/App.xaml.cs:193-212`. It calls:
  - `ISettingsService`;
  - `AddWindowsServices`;
  - `AddCommonServices`, `AddScriptableObjects`, `AddCompiler` and `AddSkuaMainAppViewModels`, all in `Skua.Core/AppStartup/Services.cs:25, 46, 39, 109-195`;
  - `Ioc.Default.ConfigureServices`.
- The Engine's root, `Skua.App.Engine/EngineServices.cs:16-39`, uses the same Core extensions, minus `AddSkuaMainAppViewModels`, plus `AddMacServices` and `EngineLogService`.
- So the Mac App's root is the Engine's root, plus `AddSkuaMainAppViewModels`, plus Avalonia services.

**Views**
- View models map to views through 47 implicit `DataTemplate DataType` entries (`Skua.WPF/XAML/DataTemplates.xaml:8-147`, merged at `Skua.App.WPF/App.xaml:20`). This maps one-to-one onto Avalonia `DataTemplates`.
- There are 80 XAML files in total.
- Code-behind worth porting deliberately:
  - `Skua.WPF/PropertyGrid.xaml.cs` (677 lines, used by the Grabber);
  - `Views/ScriptRepoView.xaml.cs` (260 lines of search and filtering, `:17-242`);
  - `AssignHotKeyDialog.xaml.cs:71` (key capture);
  - `SavedAdvancedSkillsUserControl.xaml.cs:53, 72` (copy and paste);
  - `PacketInterceptorView.xaml.cs`;
  - `Views/AccountManager.xaml.cs` (Manager only).
- The WPF-only libraries don't carry over and need Avalonia replacements or plain Avalonia: MaterialDesignThemes 4.9.0 (`App.xaml:13`), Xaml.Behaviors, MdXaml, Ookii.Dialogs and Hardcodet.NotifyIcon (`Skua.WPF.csproj:22-33`).

**Screens**
- The main menu is `Skua.Core/AppStartup/MainMenu.cs:15-49`:

  | Group | Items |
  |---|---|
  | Scripts | |
  | Options | Game, Application, CoreBots, Application Themes, HotKeys |
  | Helpers | Runtime, Fast Travel, Current Drops |
  | Tools | Loader, Grabber, Junk Items, Stats, Console |
  | Skills | |
  | Packets | Spammer, Logger, Interceptor |
  | Bank | |
  | Logs | |

- Each item opens a managed window by key (`MainMenuItemViewModel.cs:39`, keys at `Skua.Core/AppStartup/ManagedWindows.cs:14-42`).
- `BotWindowViewModel` shows the same 21 panels as tabs (`Services.cs:114-137`).
- The main window stacks `MainMenuUserControl` over `GameContainerUserControl` (`Skua.App.WPF/MainWindow.xaml:23-24`).

**The Flash host**
- `GameContainerUserControl` (`Skua.WPF/UserControls/GameContainerUserControl.xaml:18`) holds a `WindowsFormsHost`.
- `FlashUtil` builds an `AxShockwaveFlash` (`Skua.WPF/Flash/FlashUtil.cs:30-58`) and hands it over through `FlashChangedMessage` (`:46`; received at `GameContainerUserControl.xaml.cs:23-35`).
- On macOS, `BridgeFlashUtil` already takes its place (`Skua.MacOS/GameHost/BridgeFlashUtil.cs:53-66`).

## 2. The WPF app hosts Core in-process; the macOS Engine hosts it headless

**WPF**
- `Program.cs:13-23` runs `new App().Run()`, and `App()` configures services (`App.xaml.cs:40`).
- It then resolves `IScriptInterface`, runs `SkuaStartupHandler` (`:57`) and opens `MainWindow` (`:93-95`).
- The Bridge:
  - The ActiveX `FlashCall` event goes to `IFlashUtil.FlashCall` (`FlashUtil.cs:104-108`).
  - Calls into the game go through `CallFunction(FlashXml.Invoke(...))` (`:134`).
  - `ScriptInterface` subscribes at `Skua.Core/Scripts/ScriptInterface.cs:155`.
- Core, the UI and the game all live in one process.

**macOS Engine**
- `Skua.App.Engine/Program.cs:7-39` parses `--name` and `--detach`. `Engine.RunAsync` (`Engine.cs:73-122`) then:
  1. takes the flock lock (`:265-290`);
  2. redirects stdio when detached (`Detach.cs:20-33`);
  3. resolves `skua-gamehost` and `skua.swf` from `AppContext.BaseDirectory` (`:105`);
  4. builds services (`:114`);
  5. starts `GameHostSupervisor` (`:115`) and serves the socket (`:118`).
- `BridgeFlashUtil.InitializeFlash` starts `GameHostProcess` (`Skua.MacOS/GameHost/GameHostProcess.cs:37-56`), which uses a reader thread and a dispatch thread (`:101-102`). Frames are framed in `Skua.MacOS/GameHost/BridgeFrames.cs:9-48`.
- The Control Surface is `IEngineRpc` (`Skua.Control/IEngineRpc.cs:17-251`, protocol 9 at `ControlProtocol.cs:14`), served per connection by `Engine.AcceptAsync` (`Engine.cs:323-343`).
  - Nothing is pushed as a JSON-RPC notification. `subscribe` is a long-polled `IAsyncEnumerable<LogPage>` (`Engine.cs:193-207`), and Script Dialogs, Script lines and game events all arrive as log events (`Skua.Control/Logs.cs:50-118`).
- `EngineClient`/`EngineConnection` (`Skua.Control/EngineClient.cs`, `EngineConnection.cs:34-158`) have no CLI dependency. A GUI could use them as a client.

**What stands in the way of embedding the Engine in an app process**
- **Visibility:** `Engine` is `internal sealed` with a private constructor (`Engine.cs:21, 46`), and the project is an Exe (`Skua.App.Engine.csproj:4`).
- **One `Ioc.Default` per process:** `EngineServices.cs:36` and `App.xaml.cs:210` each configure it. Core resolves through it everywhere (`ScriptManager.cs:386`, `ScriptInterface.cs:184`, `IScriptInterface.Instance` at `Skua.Core.Interfaces/Scripts/IScriptInterface.cs:40`). So the app's view models must go into the Engine's container. ADR 0001's one-Game-Client-per-process rule is unchanged.
- **Console-process assumptions:**
  - logging to `Console.Error` (`EngineLog.cs:24`);
  - SIGTERM/SIGINT registration (`Engine.cs:294-295`);
  - a process-wide `umask` around `bind` (`Engine.cs:302-310`);
  - `Detach` rewiring fds 0-2 (`Detach.cs:20-33`).
- **Headless-only behaviour:**
  - LagKiller is forced on at every login (`Skua.App.Engine/GameHostSupervisor.cs:52-53`: "The Engine never shows the game").
  - `EngineLogService` writes to `EngineLogs` but never sends `AddLogMessage`, which the Logs panel listens for (`LogTabViewModel.cs:52`).
- **Replacing a stale Engine:** a CLI with `ReplaceStale` replaces an idle Engine from another build (`Skua.Control/EngineClient.cs:20-28, 71`). It would do that to an app's Engine too.
- **No run-loop conflict:** nothing in the Engine touches NSApplication or the main thread, and Metal lives in the separate Game Host process.

**Testing seam**
- `Skua.Engine.Tests` starts a real Engine against `Skua.FakeGameHost` (`FakeGameHost.cs:198-206`, `GameFixture.cs:40`), a scripted Bridge peer (`Skua.FakeGameHost/Program.cs:1-28`).
- The Mac App's Engine and Game View can be tested the same way.

## 3. The Game Host: frames and input

**Rendering today**
- The stage is fixed at 958×550 (`Skua.GameHost/src/main.rs:41-42`). It renders on a Metal-only, low-power device (`:87-94`) into an offscreen `TextureTarget` (`:96-100`), with `StageScaleMode::ShowAll` and viewport 958×550 at scale 1.0 (`:119-131`).
- There is no UI or audio backend, so Ruffle falls back to `NullUiBackend` and `NullAudioBackend` (Ruffle `core/src/player.rs:3169, 3188`).
- The loop ticks by the SWF's own frame rate, at most every 4 ms and waking at least every 33 ms (`main.rs:45-47, 240-326`).
- The GPU half of each frame runs on a render thread (`src/render.rs:108-137`).
- `render_if_due` (`main.rs:331-357`) renders under the headless render budget: a 1 s interval, 10% of wall time, capped at 5 s (`src/opts.rs:6-17, 32-39, 61-74`).
  - A keep-alive render is required: without it Ruffle's CPU-side state grows (README, "Headless defaults").
- `--show-game` switches to a 33 ms interval with no budget (`opts.rs:101-104`). Every frame it does `capture_frame` into a minifb window (`main.rs:137-147, 343-356`).
  - So a CPU readback at about 30 fps already works, in a debug build of the same host.

**Screenshots**
- The Engine sends `S` (`src/frame.rs:7`). The host then:
  1. forces `p.render()`;
  2. queues `capture_frame` on the render thread (`main.rs:267-276`);
  3. maps the readback buffer and waits (Ruffle `render/wgpu/src/utils.rs:131-181`);
  4. un-premultiplies, optionally resizes, encodes PNG, and replies with `I` (`main.rs:427-453`).
- On the C# side the path is `GameHostProcess.Screenshot` (`GameHostProcess.cs:121-133`) → `ScreenshotOperations` (`Skua.App.Engine/ScreenshotOperations.cs:32-101`). It has a 10 s timeout, lets callers share one capture, and lifts LagKiller for the capture.
- `TextureTarget` already copies the frame texture into its `MAP_READ` buffer on **every** submit (Ruffle `render/wgpu/src/target.rs:301-341`, copy at `:314`). So a frame for the Game View costs a map plus a memcpy, and no extra GPU copy.

**`Q`**
- `Q` is the stats request and its JSON reply, not quit (`frame.rs:9, 15`; `main.rs:278-281, 369-423`). The Engine polls it every 60 s (`GameHostSupervisor.cs:17-35, 95-100`).
- There is no quit frame. Closing stdin ends the host (`src/bridge.rs:77`).
- The diag build also uses `M G Y Z B V`. Free letters for new frames include `U` and `W`.

**Input**
- The Game Host forwards no input today: there is no `handle_event`, `PlayerEvent` or `with_ui` in `Skua.GameHost/src`.
- Ruffle's entry point is `Player::handle_event(&mut self, PlayerEvent) -> bool` (Ruffle `core/src/player.rs:1060`). `PlayerEvent` (`core/src/events.rs:10-51`) has these variants:
  - `MouseMove{x,y}`;
  - `MouseDown{x,y,button,index}` and `MouseUp{x,y,button}`;
  - `MouseLeave`;
  - `MouseWheel{delta: Lines|Pixels}`;
  - `KeyDown/KeyUp{key: KeyDescriptor{physical_key, logical_key, key_location}}` (`:888-892`);
  - `TextInput{codepoint}`, `TextControl{code}` and `Ime`;
  - `FocusGained/FocusLost`.
- Coordinates are viewport pixels. Ruffle maps them through `stage.inverse_view_matrix()` (`player.rs:1421-1424`), so letterboxing and the scale factor are its concern. At today's 958×550 at scale 1.0 they equal stage pixels.
- `set_viewport_dimensions` (`player.rs:997`) resizes the target (`render.rs:198-201`).
- Ruffle's desktop app is the reference mapping from winit to `PlayerEvent` (Ruffle `desktop/src/app.rs:119-244`, keys at `desktop/src/util.rs:64`).
- Cursor and clipboard go through `UiBackend` (`core/src/backend/ui.rs:106-141`). They are no-ops under `NullUiBackend` (`:214-231`).

**Zero-copy (IOSurface)**
- It's plausible, but nothing is ready-made. It needs:
  - `TextureTarget`'s public `texture` (`target.rs:263`);
  - wgpu 30's `Texture::as_hal` / `Device::create_texture_from_hal`;
  - wgpu-hal Metal's `Device::texture_from_raw` (`wgpu-hal-30.0.1/src/metal/device.rs:415`) over an IOSurface-backed `MTLTexture`.
- Plus a cross-process handoff of the surface, and GPU interop on Avalonia's side.

**Continuous rendering is the open memory question**
- A live Game View renders every 33 ms instead of about every 1 s.
- The headless defaults (texture-pool trim every 30 ticks, the main-frame pool trimmed every frame, pass budget 64, in-flight 2, layer flush 8; `main.rs:82-84`) were tuned for the headless cadence. #67 (idle growth in battleon) is still open.

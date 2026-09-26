# Game Host candidates for the macOS port

Research for [noelrohi/Skua#2](https://github.com/noelrohi/Skua/issues/2) (part of #1, blocking #4). Facts only; choosing a Game Host is a later ticket.

## 1. Question, date, method

**Question (verbatim scope).** Which Game Hosts can run the Game Client (`skua.swf` + the AQW game it loads) natively on Apple Silicon macOS, and what does each need?

For each candidate — at least Harman AIR and Ruffle; note any other viable option (e.g. how Artix's own launcher runs AQW on macOS):
- Licensing and cost (AIR free tier terms, splash screen, redistribution), download/account gating.
- arm64 support and minimum macOS version.
- AS3 coverage good enough for AQW (known AQW status for Ruffle).
- How the Game Client can talk to another local process (sockets, `ExternalInterface`, NativeProcess, stdin/stdout, LocalConnection).
- Behaviour with a hidden/minimised window (frame-rate throttling, timers).
- Whether it can capture a screenshot of the stage.
- Can the Game Client's SWF (currently built with `mxmlc`, target-player 28, see `Skua.AS3/`) load unchanged, and what changes?

**Researched:** 2026-09-26, on an arm64 Mac running macOS 27 (Darwin 27.0).

**Method.**
- Read the local source in this repo (`Skua.AS3/`, the C# Flash host in `Skua.WPF/`).
- Read primary sources: Harman's AIR SDK site, licence PDF and release notes; airsdk.dev and help.adobe.com API docs; the `airsdk/Adobe-Runtime-Support` tracker; the `ruffle-rs/ruffle` source at commit `a1277c0` (master, 2026-09-25), its wiki, releases and issues; Artix's own pages; Apple and Adobe pages.
- Inspected binaries: Artix Game Launcher 2.20 DMG, Adobe Flash Player 32 projector DMG, the AIR SDK 51.4.1.1 zip (headers via HTTP range requests), and the live AQW game SWF.
- All GitHub access was read-only.

**Conventions.**
- `R/` = `https://github.com/ruffle-rs/ruffle/blob/a1277c06e5d46c03726d813c3cc1faa49a238c54/`.
- **Unverified** means no primary source was found.
- **Inferred** means reasoned from a cited source, not stated by it.
- **Docs silent** means the owner's docs do not address it.

## 2. What the Game Client relies on (local source)

### 2.1 Build

| Fact | Where |
|---|---|
| Compiled with `mxmlc -source-path skua\src -default-size 958 550 -output skua\bin\skua.swf skua\src\skua\Main.as -target-player 28.0 -optimize` | `Skua.AS3/compile-as3.ps1:35` |
| `asconfig.json`: `target-player` "28.0", 30 fps, 958×550, `use-network: true`, `static-link-runtime-shared-libraries: true`, single file `src/skua/Main.as` | `Skua.AS3/skua/asconfig.json:8-14,22,26,29` |
| `asconfig.json` source-path also lists Adobe Animate CC 2019 UI component source (a Windows path) | `Skua.AS3/skua/asconfig.json:5` |
| FlashDevelop project: `version="28"`, `platform="Flash Player"`, `preferredSDK="Flex 4.16.1 Air 51.1"` | `Skua.AS3/skua/skua.as3proj:11,13,15` |
| IntelliJ module says `target-player="32.0"`, which is inconsistent with the above | `Skua.AS3/skua/skua.iml:6` |
| Pure AS3: no `fl.*` or `mx.*` imports in `skua/src` (grep) | `Skua.AS3/skua/src/skua/**` |
| The SWF is a build output, not in git. The WPF app links `..\Skua.AS3\skua\bin\skua.swf` | `Skua.App.WPF/Skua.App.WPF.csproj:40` |
| `[SWF(frameRate="30", backgroundColor="#000000", width="958", height="550")]` | `Skua.AS3/skua/src/skua/Main.as:27` |

Target-player 28 means SWF version 39. This uses the usual FP N → SWF N+11 mapping; I did not check it against a Harman table.

### 2.2 How it loads AQW

**Load sequence:**
1. `Security.allowDomain('*')` runs first (`Main.as:91`).
2. A `URLLoader` GETs `https://game.aq.com/game/api/data/gameversion` (`Main.as:35-36,92-94`).
3. It JSON-parses the result and takes `sFile` (`Main.as:99-100`).
4. `Loader.load(new URLRequest('https://game.aq.com/game/gamefiles/' + sFile))` runs with **no `LoaderContext`** (`Main.as:104-107`). The game therefore gets its own security domain and a child ApplicationDomain; the ApplicationDomain part is inferred from Flash defaults.
   - On 2026-09-26 the API returned `{"sFile":"Game3098r27.swf",…,"sVersion":"R0047"}`.
   - Skua loads the game SWF directly. It does not go through `Loader3.swf`, which is what Artix's launcher and aq.com load (§3.3).

**Stage and runtime access after load:**
- On complete, it removes the stage's child 0 and adds `loader.content` **directly to the Stage**, then sets `scaleMode=SHOW_ALL` and `align=TOP` (`Main.as:113-117`).
- It copies flashvars (`root.loaderInfo.parameters`) into `game.params` (`Main.as:119-128`).
- It then reaches into the game's objects freely:
  - `game.sfc`, the SmartFox client (`Main.as:131`)
  - `game.stage` keyboard events (`Main.as:138`)
  - classes via `gameDomain.getDefinition` (`Main.as:286`), including `it.gotoandplay.smartfoxserver.handlers.ExtHandler` (`Main.as:398`)
  - arbitrary property paths through `_getObjectA` (`Main.as:355-361`)
- Everything therefore relies on **cross-SWF scripting** from `skua.swf` into content from `game.aq.com`.
  - How this is allowed today is **unverified**. `Security.allowDomain('*')` in Skua only grants the game access to Skua, not the reverse.
  - On Windows, `skua.swf` is injected as bytes, not loaded from a URL (§2.5).

**Other loading:**
- `injectScript(uri)` loads an arbitrary SWF with `LoaderContext.allowCodeImport = true` and calls `content.run(Main.instance)` (`Main.as:375-390`).
- The custom background is loaded twice from a caller-supplied URL (`Main.as:210-242`).

**`game.aq.com/crossdomain.xml`** (fetched 2026-09-26) allows only `www.aq.com`, `game.aq.com`, `*.aqworlds.com` and `*.aq.com`, with `permitted-cross-domain-policies="master-only"`.

### 2.3 The Bridge today (AS3 side)

**Inbound (Engine → Game Client):**
- **73** `ExternalInterface.addCallback` registrations, all made through `Externalizer.addCallback` → `ExternalInterface.addCallback` (`Externalizer.as:23-124,130-132`).
- Groups:
  - core/load: `loadClient`, `setBackgroundValues`, `isTrue`, `isNull`
  - generic object access: `getGameObject*`, `setGameObject*`, `callGameFunction*`, `selectArrayObjects`
  - server, player, world, monster, skills, combat, shop, auras
  - packets: `sendClientPacket`, `catchPackets`
  - `injectScript`
  - a remote-object registry: `lnk*` (9) and `fc*` (6), in `remote/RemoteRegistry.as`
  - modules
- Nearly all return JSON strings, e.g. `Main.as:262-265`.
- Calls are **synchronous**: the C# caller blocks for the return value (§2.5).

**Outbound (Game Client → Engine):**
- `ExternalInterface.call(name, rest)` (`Externalizer.as:134-136`). This passes the varargs **as a single array argument**.
- Eight names are used:

| Name | Where |
|---|---|
| `requestLoadGame` | `Externalizer.as:127` |
| `debug` | `Externalizer.as:139` |
| `pre-load` | `Main.as:66` |
| `loaded` | `Main.as:146` |
| `pext` | `Main.as:150`, on every SmartFox extension response |
| `openWebsite` | `Main.as:161-176`, `module/QuestRequirementWiki.as:70` |
| `packet` | `Main.as:431`, on every debug-message packet once `catchPackets` is on |
| `packetFromServer` | `Main.as:433` |

- `pext`, `packet` and `packetFromServer` fire per server message, so the Bridge must handle a continuous stream (inferred from code).

**Handshake order:**
1. Externalizer init → `requestLoadGame`
2. Engine calls `loadClient`
3. → `pre-load`
4. Engine calls `setBackgroundValues`
5. … → `loaded`

(`Externalizer.as:127`, `SkuaStartupHandler.cs:30-32`, `ScriptInterface.cs:397-414`)

### 2.4 Other flash.* APIs the Game Client itself uses

**Imports across `skua/src`** (grep of imports):
- `flash.display` (Loader, LoaderInfo, MovieClip, Sprite, Stage, StageAlign, StageScaleMode, DisplayObject)
- `flash.events` (Event, MouseEvent, KeyboardEvent, TimerEvent)
- `flash.net` (URLLoader, URLRequest)
- `flash.system` (ApplicationDomain, LoaderContext, Security)
- `flash.text.TextField`, `flash.ui.Keyboard`, `flash.geom.Point`, `flash.filters.GlowFilter`
- `flash.utils` (Timer, getQualifiedClassName)
- `flash.external.ExternalInterface`

**Timers and frame handlers:**
- `Timer` is used in `Main.as:219`, `api/Server.as:34`, `api/Auras.as:22,241` and `api/World.as:32`.
- Per-frame work runs through `ENTER_FRAME` (`Main.as:135-136`).
- Throttling of timers and frames on a hidden window therefore affects bot logic directly.

**Not used in Skua's own code:** Socket, LocalConnection, BitmapData, SharedObject, fscommand.

### 2.5 The Windows Game Host today (C# side)

**Loading:**
- `AxShockwaveFlash` ActiveX (`Skua.WPF/Flash/FlashUtil.cs:1,44-53`; interop DLLs referenced at `Skua.App.WPF/Skua.App.WPF.csproj:61-65`).
- `skua.swf` is read from disk and injected as bytes through `OcxState` (`FlashUtil.cs:54-62`).

**Required workarounds:**
- An **EOL time-bomb workaround**: a CoreHook detour on `kernel32!GetSystemTime` sets the year to 2020 while Flash initialises (`FlashUtil.cs:39-40`, `Skua.WPF/Flash/EoLHook.cs:19-29`).
- **Clean Flash** must be installed; otherwise the app shows a "Clean Flash missing" dialog and exits (`FlashUtil.cs:64-68`).

**The Bridge:**
- Outbound calls from AS3 arrive as `FlashCall` events carrying Flash's XML `<invoke>` format (`FlashUtil.cs:109-115`).
- The Engine calls into the SWF with `CallFunction("<invoke name=… returntype=\"xml\">…")`, a synchronous call with an XML return (`FlashUtil.cs:135-151`).
- Values are marshalled with Flash's XML serialisation (`FlashUtil.cs:160-222`).
- The interface is `Skua.Core.Interfaces/Flash/IFlashUtil.cs:22-73`.

**Screenshots:** there is no screenshot code on the C# side (grep for screenshot/DrawToBitmap/CopyFromScreen/PrintWindow found none).

### 2.6 What the AQW game SWF itself needs

Local inspection on 2026-09-26 of the LZMA/zlib-decompressed `Game3098r27.swf` string pool. This is a strings scan, not a decompile.

- Header `CWS`, **SWF version 43** (FP 32 era), about 4.6 MB uncompressed.
- **SmartFoxServer client** (`it.gotoandplay.smartfoxserver.SmartFoxClient`, `ExtHandler`, `SysHandler`) with a `Socket`/`ByteArray` connection (`socketConnection`, `handleSocketData`) and a **BlueBox HTTP fallback** ("Socket connection failed. Trying BlueBox"). A Game Host must support `flash.net.Socket` TCP to AQW's game servers.
- The game has its own `ExternalInterface.addCallback`/`call` use, for Facebook and ads (`fbLoginNoAuth`, `openAdsWindow`, `GetCurrentUser`, `linkAccount`).
- It calls `Security.allowDomain`/`loadPolicyFile` at least for `fbcdn-profile-a.akamaihd.net`. Whether it also calls `allowDomain('*')` is **unverified**.
- It uses `flash.text.engine`, `SharedObject` (preferences) and Animate's `fl.data.DataProvider`.
- No `Stage3D`, `Context3D`, `StageVideo` or `NetStream` strings were found.

## 3. Candidates

### 3.1 Harman AIR (desktop, captive-runtime bundle)

**Licence and cost**

**Tiers** (pricing page text read from the site's JS bundle, <https://airsdk.harman.com/pricing>, <https://airsdk.harman.com/main.91194927b40d4044023f.js>):

| Tier | Price per seat per year | Revenue cap |
|---|---|---|
| Free | $0 | < $50k/yr |
| Basic | $199 | $100k |
| Professional | $799 | $500k |
| Enterprise | $1199 | none |

- "A license is required for developers to compile, debug, package or distribute an AIR-based application." Free-tier developers may do so, "but a splash screen will be displayed upon application launch."
- There is no free tier on Linux.

**Licence agreement** (<https://airsdk.harman.com/assets/pdfs/HARMAN%20AIR%20SDK%20License%20Agreement.pdf>):
- §4: free-tier apps show a HARMAN/Adobe splash screen.
- §3.3(a)(i): the captive runtime may be redistributed only as the build tools embed it.
- §3.3(a)(iv): the build tools may not be redistributed.
- §3.2: the runtime may not be modified.
- §6.4: the embedded runtime "will not send your end user information back to HARMAN". The SDK and build tools do send usage data (OS, app ID, packaging settings, SDK version, anonymised machine ID).

**Splash screen and package checks** (51.4.1 release notes §9.1, <https://airsdk.harman.com/api/versions/51.4.1.1/release-notes/Release_Notes_AIR_SDK_51.4.1.pdf>):
- The splash shows "for around 2 seconds".
- "The processing of ActionScript is delayed until after the splash screen has been removed."
- From 51.2 the runtime validates the package at start-up. `PackageValidation` in `adt.cfg` disables this; it matters if the bundle is post-processed.

**Download gating:**
- No login required. `https://airsdk.harman.com/api/versions/51.4.1.1/sdks/AIRSDK_MacOS.zip?license=accepted` returned HTTP 200 without an account (checked 2026-09-26). The same pattern is used by <https://github.com/joshtynjala/setup-adobe-air-action>.
- The AIR SDK Manager is the recommended route for 51.2 and later (<https://github.com/airsdk/airsdkmanager-releases>).
- Install docs mention no account (<https://github.com/airsdk/airsdk.dev/blob/main/docs/basics/install/macos.mdx>).
- Paid tiers use an `adt.lic` file.

**arm64 and minimum macOS**
- Universal (arm64 + x86_64) macOS support shipped in **33.1.1.476** as AIR-3226, "AIR SDK for MacOS to support M1 i.e. universal binaries" (<https://airsdk.harman.com/api/versions/33.1.1.929/release-notes/Release_Notes_AIR_SDK_33.1.1.929.pdf>; <https://github.com/airsdk/Adobe-Runtime-Support/discussions/1311>).
- **Latest SDK 51.4.1.1**: release notes dated 2026-09-17, API date 2026-09-23 (<https://airsdk.harman.com/api/config-settings/download>).
- The captive `Adobe AIR.framework/Versions/1.0/Adobe AIR` and `bin/adl` in that zip are universal (x86_64 + arm64). Checked from Mach-O headers.
- Minimum macOS: the release notes give only "Minimum macOS Target: 10.13", a build setting. An end-user minimum is **docs silent**.
- ADT is `lib/adt.jar`: it needs JDK 11+ (17/21 recommended) and so runs on any arm64 JDK (inferred).
- **Use a captive bundle, not the shared runtime:** the shared runtime installer is x86_64. `.air`-installed apps "will need to run using Rosetta2 on Apple Silicon" (ajwfrost, <https://github.com/airsdk/Adobe-Runtime-Support/discussions/3156>). Captive bundles (`adt -target bundle`) are the native route.
- Open macOS issues: #4291 (launch crash on macOS 26.6), #4326 (title bar missing on macOS 27), #3998 (Cmd+W/Cmd+Q on macOS 26) (<https://github.com/airsdk/Adobe-Runtime-Support/issues>).

**AS3 coverage / AQW**
- AIR runs the real Flash AVM2, so full AS3 is available.
- **Prior art:** <https://github.com/anthony-hyo/aqw-mobile> ("AQW Pocket", MIT, unofficial, "not affiliated with or endorsed by Artix") runs the AQW client on HARMAN AIR 51.3 for Android, Windows and macOS.
  - Descriptor namespace 51.3, profiles `desktop extendedDesktop`.
  - At build time it downloads the game SWF from `game.aq.com/game/gamefiles/…`, patches its bytecode ("patches are applied to the ActionScript bytecode to make the client compatible with mobile/AIR constraints"), ships it as `app:/gamefiles/game.swf`, and loads it with `allowCodeImport=true` into a new ApplicationDomain.
  - Its macOS release assets are x64 only (gh releases).
  - This is evidence the AQW client runs under AIR with modification. It is not evidence it runs unmodified.
- There is no official Artix AIR client. The 2017 design note says AQW mobile work was Unity/WebGL (<https://www.aq.com/gamedesignnotes/aqwmobilefuture-6482>), and the aq.com Android tag lists no AIR-based AQW (<https://www.aq.com/gamedesignnotes/tag/android>). The search was not exhaustive.

**Bridge options**
- **ExternalInterface: not usable.**
  - "Since an AIR application does not have an external container, this external interface does not generally apply" (<https://airsdk.dev/docs/development/networking-and-communication/using-the-external-api>).
  - It works only for a SWF inside HTML in an `HTMLLoader` (<https://airsdk.dev/reference/actionscript/3.0/flash/external/ExternalInterface.html>).
  - The WebKit HTMLLoader engine was dropped after 33.1.1.743/744, and "Loading a SWF within HTML requires the Flash Player" (<https://github.com/airsdk/Adobe-Runtime-Support/discussions/3232>, #2107).
  - `addCallback`/`call` throw when unsupported, so `Externalizer.as:131` would throw.
- **NativeProcess:**
  - Spawns a child with stdin/stdout/stderr pipes.
  - Requires the `extendedDesktop` profile and a native installer or captive bundle; `isSupported` is false for `.air` installs (<https://airsdk.dev/reference/actionscript/3.0/flash/desktop/NativeProcess.html>; <https://help.adobe.com/en_US/air/build/WSfffb011ac560372f709e16db131e43659b9-8000.html>).
  - The AIR app could start the Engine, or be started by it, with the latter using sockets.
- **ServerSocket / Socket:**
  - `ServerSocket` is AIR-only, application sandbox only (<https://airsdk.dev/reference/actionscript/3.0/flash/net/ServerSocket.html>).
  - "In AIR, a socket policy file is not required for content running in the application security sandbox" (<https://airsdk.dev/reference/actionscript/3.0/flash/net/Socket.html>).
  - So a localhost TCP Bridge in either direction is possible.
- **LocalConnection:** desktop only, `app#appID` naming (<https://airsdk.dev/reference/actionscript/3.0/flash/net/LocalConnection.html>). It uses Flash's private transport, so .NET would have to reimplement it (inferred).
- **The AIR app's own stdin/stdout:** no API found (docs silent).
- **Synchronous calls:** all of these are asynchronous event-driven channels, so a synchronous call-with-return like today's `CallFunction` needs a request/response protocol on top (inferred).

**Hidden / minimised window**
- `ThrottleEvent`: AIR for desktop does not dispatch it "because they do not yet support pausing or throttling" (<https://airsdk.dev/reference/actionscript/3.0/flash/events/ThrottleEvent.html>).
- `NativeApplication.executeInBackground` is iOS/Android only (<https://airsdk.dev/reference/actionscript/3.0/flash/desktop/NativeApplication.html>).
- For `NativeWindow.visible=false`, "all window properties and methods are valid" (<https://airsdk.dev/reference/actionscript/3.0/flash/display/NativeWindow.html>). The frame rate while hidden is **docs silent**.
- The only related tracker item is #2481, a Windows-only slowdown while minimised, fixed by skipping Present when not visible (<https://github.com/airsdk/Adobe-Runtime-Support/issues/2481>). Nothing macOS-specific was found for App Nap, occlusion or throttling.
- **macOS App Nap** applies to background apps not updating visible content, and includes "Timer throttling" (<https://developer.apple.com/library/archive/documentation/Performance/Conceptual/power_efficiency_guidelines_osx/AppNap.html>).
  - AIR can add Info.plist keys through `<macOS><InfoAdditions>` (<https://github.com/airsdk/airsdk.dev/blob/main/docs/building/application-descriptor-files/elements/macOS.md>).
  - `LSAppNapIsDisabled` is widely used but I found no Apple documentation for it (**unverified**).
  - An `NSProcessInfo` activity assertion would need an ANE (inferred).

**Screenshot**
- `BitmapData.draw()` of cross-domain content normally throws, but "This restriction does not apply to AIR content in the application security sandbox" (<https://airsdk.dev/reference/actionscript/3.0/flash/display/BitmapData.html>; <https://airsdk.dev/reference/actionscript/3.0/flash/system/LoaderContext.html>).
- `draw(stage)` from app-sandbox code is therefore permitted. PNG encoding is available through `BitmapData.encode` (inferred).
- No window-capture API was found. Stage3D would need `Context3D.drawToBitmapData`, but AQW uses no Stage3D (§2.6).

**Can `skua.swf` load unchanged?** No. Changes needed:
1. **Packaging.** It needs an app descriptor (`<initialWindow><content>`, `supportedProfiles` including `extendedDesktop` if NativeProcess is used) and `adt -target bundle`.
   - SWF version 39 under a 51.x descriptor namespace should run, with APIs capped at the lower of the two versions (ajwfrost, <https://github.com/airsdk/Adobe-Runtime-Support/discussions/2480>). This is inferred; there is no explicit table.
   - Using `ServerSocket`/`NativeProcess` requires compiling against `airglobal.swc` rather than `playerglobal.swc` (inferred).
2. **Bridge.** Replace `ExternalInterface` in `Externalizer.as` with Socket/ServerSocket or NativeProcess messaging.
3. **`Security.allowDomain('*')` (`Main.as:91`) throws in the application sandbox.** "Calling this method from the application security sandbox will throw an error" (<https://help.adobe.com/en_US/as3/dev/WS5b3ccc516d4fbf351e63e3d118666ade46-7e5c.html>; <https://airsdk.dev/reference/actionscript/3.0/flash/system/Security.html>).
4. **Game loading (`Main.as:104-117`).**
   - A remote SWF loaded with `Loader.load` goes into a remote, per-domain sandbox (<https://airsdk.dev/docs/development/security/security-sandboxes>).
   - Remote code cannot touch app-owned objects such as the Stage: "SecurityError: Error #2070 … caller cannot access Stage owned by app:/…". This is a community report (<https://community.adobe.com/questions-537/load-remote-swf-and-access-to-stage-84539>), so treat it as likely but **unverified** for AQW.
   - Placing it in the app sandbox with `LoaderContext.securityDomain`/`applicationDomain` "is prevented" (help.adobe.com link above).
   - The known workaround:
     1. Fetch the bytes with `URLLoader`.
     2. Call `Loader.loadBytes` with `allowCodeImport=true` (default false in the app sandbox; <https://airsdk.dev/reference/actionscript/3.0/flash/display/Loader.html>).
     3. The game then runs **with full application privileges**; Adobe warns about exactly this (<https://help.adobe.com/en_US/as3/dev/WS5b3ccc516d4fbf351e63e3d118666ade46-7e5a.html>).
   - This is the route AQW Pocket takes, using a bundled patched copy.
5. **`injectScript`** (`Main.as:375-381`) uses `Loader.load(URL)` with `allowCodeImport`, which faces the same sandbox issue (inferred).
6. The game SWF's own `ExternalInterface`/`allowDomain` calls (§2.6) might also throw when it runs in the app sandbox. AQW Pocket patches the bytecode; whether that is necessary for desktop is **unverified**.

### 3.2 Ruffle (desktop app, or ruffle-web inside a WebView/browser)

**Licence and cost**
- MIT OR Apache-2.0 (<https://github.com/ruffle-rs/ruffle/blob/master/LICENSE.md>; `R/Cargo.toml`).
- Free; no account or download gating.
- Contribution policy rejects unreviewed LLM-generated PRs (see closed #24590, #23212), which matters if the port needs to upstream fixes.

**arm64 and minimum macOS**
- Stable releases exist since **v0.2.0 (2026-05-16)**. Latest is **v0.6.0 (2026-09-06)**; nightlies are daily (<https://github.com/ruffle-rs/ruffle/releases>; <https://ruffle.rs/downloads>). Confirmed via `gh api …/releases/latest`.
- The macOS asset is universal only, `ruffle-0.6.0-macos-universal.tar.gz`, a lipo of aarch64 and x86_64 containing `Ruffle.app` plus a Safari extension (`R/.github/workflows/release.yml:140-150,300-315`).
- Deployment target is **macOS 11.0 for aarch64** (10.7 for x86_64) (`release.yml:144,150`).
- **Signing:**
  - CI signs (Developer ID, hardened runtime), notarises and staples, but both steps are `continue-on-error` (`release.yml:343-368`).
  - #22873 "macOS app blocked by Gatekeeper" (expired Apple membership) was fixed 2026-02-09.
  - Entitlements enable App Sandbox with `network.client`, user-selected file read-write and `disable-library-validation` (`R/desktop/assets/macOSEntitlements.plist`).
  - Whether the sandboxed app can load a local `skua.swf` by path from the CLI is **unverified**. A self-built binary avoids this (inferred).
- Open Apple GPU crash issues: #16148 (M1 Max Metal panic) and #21120 ("Parent device is lost").

**AS3 coverage / AQW status**
- <https://ruffle.rs/compatibility>: AVM2 language 90%, API 82% (matches nightly `avm2_report.json`).
- Per class (`avm2_report.json`, nightly-2026-09-26):
  - `Socket`: full, except `bytesPending` stubbed.
  - `XMLSocket`: full.
  - `ExternalInterface`: only `marshallExceptions` missing.
  - `Security.allowDomain/allowInsecureDomain/loadPolicyFile`: stubs.
  - `Loader.load/loadBytes/unloadAndStop`: partial.
  - `BitmapData.encode/applyFilter/generateFilterRect`: stubbed.
- **AQW issues (ruffle-rs/ruffle):**
  - **#15599** "Aq.com (online mmorpg in flash) is stuck during server connection": closed 2025-10-22 on a user comment ("just a matter of creating a TCP tunnel"), with no linked PR. A later user (2025-12-08) says it still fails in the extension.
  - **#15301** "Stuck on loading screen" (open): a 2024 comment reports that on desktop with `--tcp-connections allow`, login and server connect worked, but it hung at "Loading map at 100%".
  - Crash reports on game.aq.com, all open: #22362, #22471 and #23860 (wgpu OOM, Android GPUs), #23099 (dlmalloc assertion during GC, Windows, 2026-02-23), #17287 (Android Kiwi).
  - #24392 (closed, unmerged): its author maintains "a downstream Ruffle build … for AdventureQuest Worlds, where it has been shipping since July". Who ships it is **unverified**.
  - #24590 "Fix/aqw memory leak": closed unmerged under the LLM policy.
- **Weak references are ignored**, a likely leak source (inferred):
  - `new Dictionary(true)` keeps strong keys (`R/core/src/avm2/globals/flash/utils/Dictionary.as:11-14`).
  - `addEventListener` `useWeakReference` is a TODO (`R/core/src/avm2/globals/flash/events/event_dispatcher.rs:42`).
- **Artix's own statements:**
  - 2025-02-14: an experimental Ruffle option in Artix's launcher could "fill up 3 to 6 Gigabytes of RAM and crash the game by walking back and forth between two maps" (<https://aq2d.com/posts/daily_updates_2025_03/>).
  - 2026-02-02: Ruffle "had a massive breakthrough that could allow AdventureQuest Worlds … to be played in your web browser again", but "Ruffle has a memory leak issue which prevents us from using it on our bigger games" (<https://www.aq.com/gamedesignnotes/aqw-update-ruffleanimateinfinity-10262>; <https://www.artix.com/posts/adobeanimateupdate/>).
  - Whether the leak is fixed in v0.6.0 is **unverified**.
- **Third-party prior art:** <https://github.com/aquaspy/aquastar-ruffle> is an Electron app running AQW on ruffle-web through a local WebSocket→TCP bridge on :8181. It ships an unsigned universal macOS DMG; its README says performance is "still not great with many players in the same room".

**Bridge options**
- **Desktop ExternalInterface:**
  - `ExternalInterface.available` is false unless a provider is set; calls then throw #2067 (`R/core/src/avm2/globals/flash/external/external_interface.rs:28-31`).
  - The desktop app sets a provider only with `--dummy-external-interface`, which returns `undefined` for every call except `window.location.href` spoofing (`R/desktop/src/player.rs:279-280`; `R/desktop/src/backends/external_interface.rs`).
  - There is **no IPC bridge in the stock desktop app**. #23003 (open) requests configurable return values.
- **Fork/embed:**
  - `ruffle_core` has `PlayerBuilder::with_external_interface(Box<dyn ExternalInterfaceProvider>)` and `Player::call_internal_interface(name, args)` for host→AS3 callbacks (`R/core/src/player.rs:2513,2926`).
  - The provider trait is synchronous: `call_method(&self, ctx, name, args) -> Value` (`R/core/src/external.rs:356-362`).
  - A custom desktop build could bridge these to the Engine over any IPC (inferred).
  - `ruffle_core` is not on crates.io, so it would be a git dependency (crates.io API).
- **ruffle-web:**
  - ExternalInterface works through page JavaScript with `allowScriptAccess`. `addCallback` names become methods on the `<ruffle-player>` element (`R/web/packages/core/src/internal/player/ruffle-player-element.tsx:45-55`; <https://github.com/ruffle-rs/ruffle/wiki/Using-Ruffle>).
  - Hosting in WKWebView, Electron or a headless browser, then bridging JS to .NET, is inferred to be possible.
- **Sockets from the Game Client to the Engine:** desktop `flash.net.Socket` is real TCP (`R/frontend-utils/src/backends/navigator.rs:303-362`). A localhost Socket Bridge would work, gated by `--socket-allow`/`--tcp-connections` (inferred).
- **LocalConnection** works only within one Ruffle instance (stated in closed PR #23212).
- **`trace()`** goes to stdout under target `avm_trace` (`R/core/src/backend/log.rs:17-18`; `R/desktop/src/main.rs:166-178`). This is a crude one-way channel, and mxmlc here uses `omit-trace-statements: true` (`asconfig.json:19`).

**AQW's SmartFox socket**
- **Desktop:** real TCP.
  - Flags `--socket-allow host:port` and `--tcp-connections allow|deny|ask` (default ask, a prompt per connection) (`R/desktop/src/cli.rs:145-152`; `R/desktop/src/player.rs:224`).
  - No policy file is fetched (`R/core/src/avm2/globals/flash/system/security.rs:74-80`).
- **Web:** requires a `socketProxy` entry pointing at a WebSocket→TCP proxy such as websockify (<https://github.com/ruffle-rs/ruffle/wiki/Frequently-Asked-Questions-For-Users#how-can-i-connect-to-a-tcpsocket-or-xmlsocket-from-the-web>; `R/web/packages/core/src/public/config/load-options.ts:329-343`).

**Security sandbox semantics**
- `allowDomain` and `loadPolicyFile` are no-ops. crossdomain.xml is not enforced.
- Desktop fetch has "TODO: honor sandbox type" (`R/frontend-utils/src/backends/navigator.rs:189`), so a local SWF can load `https://game.aq.com/…` and cross-script it (inferred from code).
- `allowCodeImport` is never checked (inferred from grep). `loadBytes` works (`R/core/src/avm2/globals/flash/display/loader.rs:236-300`).
- **Web:** normal browser CORS applies.
  - `Loader3.swf` sends `access-control-allow-origin: *`.
  - `/game/api/data/gameversion` did not in a single probe on 2026-09-26, so it would need a proxy or same-origin hosting.

**Hidden / minimised window**
- **Desktop:** since commit `ae8139b` "desktop: Track window occlusion" (landed in PR #24520, merged 2026-08-25), rendering is skipped while minimised or occluded, but ticks continue on a `ControlFlow::WaitUntil` timer (`R/desktop/src/app.rs:25-103,366-410,694-707`).
  - #15953 (open, Windows): memory grows while minimised.
  - App Nap: docs silent, no issues found, **unverified**.
- **Web:** `backgroundExecutionMode` (PR #23395, merged 2026-04-20). Hidden tabs switch to a Web Worker tick loop; the current default is to keep running (`R/web/packages/core/src/public/config/default.ts:62`; `R/web/packages/core/src/internal/player/inner.tsx:468-497`).

**Screenshot**
- The desktop app has no capture feature; frame capture exists only behind the Tracy profiling feature (`R/desktop/src/tracy.rs`).
- `BitmapData.draw` is implemented (`R/core/src/avm2/globals/flash/display/bitmap_data.rs:912`). With no security checks, `draw(stage)` should work (inferred, untested). `BitmapData.encode` is stubbed, so PNG encoding must happen in AS3 or in the Engine.
- `ruffle_exporter` renders SWF frames to PNG headlessly, but has no network backend (`R/exporter/src/exporter.rs:77-84`), so it cannot run a live session.
- Web: #13538 "An API to screenshot Ruffle (web)" is open. A page screenshot from a headless browser is an inferred alternative.

**Can `skua.swf` load unchanged?**
- **Loading:** likely yes. The default player version is 32 and the newest is 51 (`R/core/src/lib.rs:73-76`); no SWF-version cap was found (inferred).
- **What changes:**
  - Desktop: the Bridge needs either a custom Ruffle build with an `ExternalInterfaceProvider`, which keeps `Externalizer.as` unchanged, or AS3 changes to use a Socket Bridge.
  - Web: page JavaScript can serve the existing ExternalInterface contract, so `Externalizer.as` is unchanged, but the SmartFox socket needs a WebSocket proxy.
  - `Security.allowDomain('*')` is harmless (a stub).
- **Possible gaps:** TextField `getXMLText`/`getRawText` missing, weak references ignored, `System.exit` no-op (`R/core/src/avm2/globals/flash/system/system.rs:35-55`). Anything else is **unverified** until run.

### 3.3 Artix Game Launcher (Electron 8 + Adobe Pepper Flash), for reference

**How Artix runs AQW on macOS** (inspected `https://launch.artix.com/latest/Artix%20Game%20Launcher.dmg`, linked from <https://www.artix.com/downloads/artixlauncher/>, "New Version 2.20"):
- Electron **8.5.5 / Chrome 80** (string in Electron Framework).
- Main executable and framework are **x86_64 only**. `LSMinimumSystemVersion` 10.10.0.
- DMG dated 2025-05-09, signed "Developer ID Application: Artix Entertainment LLC (VNM8A8XCRW)", notarised.
- Bundles `Resources/plugins/PepperFlashPlayer.plugin`: Adobe Flash Player 32.0.0.344, i386 + x86_64.
- `main.js` sets `ppapi-flash-path` and `ppapi-flash-version 32.0.0.371`, and opens each game in a `BrowserWindow` with `plugins:true`, loading the SWF URL directly.
  - AQW URL in `main.js:68`: `https://game.aq.com/game/gamefiles/Loader3.swf?ver=a`.
  - Not stated on any public Artix page.
- **Kill switch (inferred):** plugin 32.0.0.344 predates builds reported to enforce the 2021-01-12 block (>32.0.0.371). This is secondary: <https://en.wikipedia.org/wiki/Adobe_Flash_Player>.
- **Ruffle path:** the launcher bundles Ruffle only for Windows and Linux and disables it on macOS (`case 'darwin': … enableRuffle = false`). Elsewhere it runs `ruffle <swf> --tcp-connections allow --player-version 9`.
- **Public statements:** Artix's pages say only "PC or Mac" (<https://www.aq.com/gamedesignnotes/artixgameslauncher-aqw-8173>; <https://www.aq3d.com/news/artix_launcher_2/>) and never name the technology or Apple Silicon.
- **Summary:** licence is Artix freeware (terms not examined); arm64 none, needs Rosetta; fidelity is real Flash; Bridge: no Skua integration; not usable for loading `skua.swf` without repackaging Adobe's plugin (inferred).
- **AdventureQuest Worlds: Infinity** is a separate remake, not a Flash host. It is Unity, inferred from its dev-log "Remove Made with Unity loading screen" (<https://aq2d.com/posts/aqworldsinfinity/>). It "does not require Adobe Animate at all" (<https://www.aq.com/gamedesignnotes/aqw-update-ruffleanimateinfinity-10262>). Out of scope as a Game Host.

### 3.4 Adobe Flash Player standalone projector (macOS)

- **EOL:** Adobe "stopped supporting Flash Player beginning December 31, 2020" and "blocked Flash content from running in Flash Player beginning January 12, 2021". Old versions are not offered (<https://www.adobe.com/products/flashplayer/end-of-life.html>). Adobe's pages are silent on projectors.
- **Time bomb:** a secondary source says projectors lack the time bomb (<https://en.wikipedia.org/wiki/Adobe_Flash_Player>). An Adobe community answer says it "might work, but it's going to be unsupported" (<https://community.adobe.com/questions-638/information-on-the-flash-projector-support-end-of-life-after-2020-717482>).
- **Architecture:** `flashplayer_32_sa.dmg` (32.0.0.465), from a GitHub mirror but Adobe-signed and notarised, is **x86_64 only**.
  - Under Rosetta on this arm64 Mac (macOS 27) it launched a minimal AS3 SWF and stayed alive for 6 s.
  - Rendering was not visually confirmed; AQW was not tested.
- **Bridge:** the projector has no ExternalInterface container (**unverified**; Adobe docs for ExternalInterface require a container). Sockets are available.
- **Licence:** Adobe proprietary; redistribution is not offered after EOL.

### 3.5 Other options (brief)

**Rosetta context for every x86_64-only option:**
- Apple (2026-09-01): "macOS 27: Final release to support Rosetta — Intel-only apps will no longer run on Mac computers with Apple silicon after this update" (<https://developer.apple.com/news/?id=w5ngl9k2>).
- Apple Support says that from macOS 28, Rosetta is kept only "for certain older, unmaintained games" (<https://support.apple.com/en-us/102527>).
- Whether the projector, Electron+Pepper or an x64 AIR app would qualify is **unverified**.

| Option | Facts |
|---|---|
| **Harman enterprise Flash Player** | "available via our enterprise licensing scheme - it's not a generally available binary" (<https://github.com/airsdk/Adobe-Runtime-Support/discussions/1417>). A "packaged browser" for business continuity via <https://services.harman.com/partners/adobe> (discussion #594). No macOS projector, arm64 build or pricing found. |
| **Electron + Pepper Flash** | Electron 12 "Removed: Pepper Flash support" (<https://github.com/electron/electron/blob/main/docs/breaking-changes.md>). Electron 11 first shipped arm64 Mac builds (<https://www.electronjs.org/blog/electron-11-0>), but the Mac Pepper Flash plugin is x86_64 only, so an arm64 app cannot load it (inferred). Redistributing Adobe's plugin is a licensing question (unverified). |
| **Clean Flash Player** | Third-party repackaging with FlashPatch (<https://github.com/darktohka/clean-flash-builds>); the Windows host already depends on it (`FlashUtil.cs:66`). Its Mac PPAPI plugin 34.0.0.376 (release v1.53, 2026-03-25) is **x86_64 only** (checked). The builds repo has no licence, and it redistributes Adobe binaries. |
| **CheerpX for Flash** (Leaning Technologies) | Commercial. Runs Adobe Flash compiled to WebAssembly in the browser; "fully supports ActionScript 2/3, Flex, Spark"; "only designed to run on Desktop". Sockets/WebSockets: "Not at the moment". A "commercial redistribution licence will need to be procured from Harman" (<https://labs.leaningtech.com/docs/cheerpx-for-flash/faq>, undated; 2026 status unverified). No socket support means AQW's SmartFox connection would fail (inferred). |
| **AwayFL** | Apache-2.0, browser JS, plays SWFs "published for FP versions 6 and up" (<https://github.com/awayfl/awayfl-player>). Docs silent on ExternalInterface and sockets; AQW status unknown. |
| **Lightspark** | LGPL-3.0 (<https://github.com/lightspark/lightspark>). No official macOS build (release 2026-09-21 has Windows and Flatpak only); a PR "Remove macOS from the Ci for now" exists (#1149). Docs silent on ExternalInterface and sockets; no AQW issues found. |
| **Flashpoint Archive** | Launcher 14.0.3 ships an arm64 Mac DMG (<https://github.com/FlashpointProject/launcher/releases>). It is a preservation archive; the Mac support page (403 to fetch) reportedly runs Flash "in a browser" or via Wine (unverified). Not a live-MMO host (inferred). |
| **Wine / CrossOver + Windows Flash** | Current builds need Rosetta; a native-arm64 CrossOver is "penciled in for a release in early 2027" (<https://appleinsider.com/articles/26/07/31/first-apple-silicon-native-crossover-build-in-testing-as-rosettas-end-nears>, secondary). AQW status unknown. |

## 4. Summary table

| Candidate | Licence / cost | Gating | arm64 / min macOS | AS3 / AQW fidelity | Bridge options | Hidden-window behaviour | Screenshot | SWF changes |
|---|---|---|---|---|---|---|---|---|
| **Harman AIR** (captive bundle) | Proprietary. Free under $50k/yr revenue, with a ~2 s splash that delays AS3; paid tiers $199–$1199/seat/yr. Captive runtime redistributable only as ADT embeds it | Free download, no account (URL with `?license=accepted`) | Universal since 33.1.1.476; latest 51.4.1.1. Min macOS docs silent (build target 10.13). Shared runtime is x64/Rosetta: use a bundle | Real AVM2, full AS3. AQW runs under AIR in AQW Pocket (unofficial), but with a patched, bundled game SWF | ServerSocket/Socket (localhost), NativeProcess stdin/stdout (extendedDesktop). **No ExternalInterface** | Desktop does not throttle (ThrottleEvent not dispatched). Hidden-window frame rate docs silent. App Nap unverified | `BitmapData.draw(stage)` allowed from the app sandbox; `encode` available | **Yes, several:** Bridge rewrite, remove `Security.allowDomain` (throws), load the game via `URLLoader` + `loadBytes` + `allowCodeImport` (runs remote code with app privileges), `injectScript` likewise, app descriptor + ADT packaging; the game's own `allowDomain`/EI may need patching (unverified) |
| **Ruffle desktop** | MIT/Apache-2.0, free | None | Universal; macOS 11+ on arm64; signed/notarised, but CI can ship unsigned | AVM2 API ~82%. AQW: login + server connect reported working (2024), hang at "Loading map 100%" (#15301, open); **memory leak** blocks Artix (2026-02); weak refs ignored | Stock: none usable (dummy EI returns `undefined`). **Custom build** with `ExternalInterfaceProvider` keeps the EI contract; or AS3 Socket to localhost; trace→stdout one-way | Rendering skipped when occluded or minimised, ticks continue (since 2026-08-25). App Nap unverified | No built-in capture. `BitmapData.draw` implemented, `encode` stubbed | SWF loads unchanged (inferred). Bridge needs a Ruffle fork or a Socket rewrite. `allowDomain` is a harmless stub. Native TCP to SmartFox (`--tcp-connections allow`) |
| **Ruffle web** (in WKWebView / Electron / headless browser) | MIT/Apache-2.0, free | None | Runs wherever the browser runs; native arm64 WebKit/Chromium (inferred) | Same core as desktop; aquastar-ruffle runs AQW this way ("still not great" with crowds) | **ExternalInterface via page JS** (existing contract works); JS↔.NET via the webview host | `backgroundExecutionMode` keeps ticking in hidden tabs (default); host webview throttling unverified | Open API request #13538; page screenshot from host (inferred) | SWF unchanged (inferred). Needs a **WebSocket→TCP proxy** for SmartFox and CORS handling for `gameversion` |
| **Artix Launcher** (Electron 8 + Pepper Flash 32.0.0.344) | Artix freeware + Adobe proprietary plugin | Free download | **x86_64 only**, Rosetta; min 10.10 | Real Flash | No Skua integration; EI→JS→Electron IPC in principle (inferred) | Chromium/Electron behaviour, unverified | Unverified | Not a host for `skua.swf` as shipped |
| **Flash Player projector** 32 | Adobe proprietary, EOL, no redistribution | Not offered by Adobe | **x86_64 only**, Rosetta (ends for most apps after macOS 27) | Real Flash | Sockets; no EI container (unverified) | Unverified | Unverified | EI Bridge replacement |
| **Electron + Pepper Flash / Clean Flash** | Adobe proprietary / unlicensed repackage | N/A | Plugin **x86_64 only** | Real Flash | EI → JS → Node IPC (inferred) | Unverified | Unverified | None for EI (inferred) |
| **CheerpX for Flash** | Commercial + Harman redistribution licence | Evaluation programme | Browser/Wasm (inferred arm64 OK) | Claims full AS3 | Unverified EI; **no sockets** | Browser | Browser | Unverified |
| **AwayFL / Lightspark / Flashpoint / Wine** | Apache-2.0 / LGPL-3.0 / various / LGPL (CrossOver commercial) | None | Browser / no Mac build / arm64 launcher / Rosetta | Unknown AQW status | Docs silent | – | – | – |

## 5. Red flags, open questions, needs a prototype

**Red flags**
1. **Rosetta ends.** Every host running genuine Adobe Flash on a Mac is x86_64-only: the Artix launcher, the projector, Pepper Flash and Clean Flash. Apple says macOS 27 is the last release where Intel-only apps generally run; from macOS 28 Rosetta is limited to certain old games. This Mac already runs macOS 27.
2. **The AIR sandbox fights Skua's design.**
   - `Security.allowDomain('*')` throws in the app sandbox.
   - Remote game SWFs cannot touch the app-owned Stage.
   - The workaround (`loadBytes` + `allowCodeImport`) runs code downloaded live from game.aq.com with full app privileges, including NativeProcess and file access if enabled.
   - The only known AIR AQW client bundles a patched game SWF rather than loading it live.
3. **Ruffle has an AQW memory leak.** Artix itself (2026-02) says a leak prevents Ruffle for its bigger games; weak references are not implemented; the #15301 "Loading map at 100%" hang is still open. A downstream AQW Ruffle build reportedly exists (#24392), owner unverified.
4. **No stock Game Host speaks today's Bridge.** ExternalInterface does not exist in AIR desktop or stock Ruffle desktop; only ruffle-web (via JS) or a custom Ruffle build preserves the synchronous call/return contract that `FlashUtil.Call` relies on (`FlashUtil.cs:135-151`).
5. **Ruffle desktop defaults prompt the user.** `--tcp-connections` defaults to ask, `--filesystem-access-mode` to ask and `--open-url-mode` to confirm; headless use needs flags. The signed app is App-Sandboxed.
6. **AIR licence constraints.** The free tier has a revenue cap and splash, and the runtime may not be modified. If Skua ever takes revenue over $50k/yr, a paid tier is needed (licence terms, not a recommendation).

**Open questions / needs a prototype**
- **Cross-scripting today:** how does `skua.swf` (injected as bytes into ActiveX) get permission to script `game.aq.com` content? Does the AQW game SWF call `Security.allowDomain('*')`? This determines what each host must permit.
- **AIR:** does `URLLoader` + `loadBytes(allowCodeImport)` of the live game SWF run AQW unpatched? Do the game's own `ExternalInterface`/`allowDomain` calls throw in the app sandbox? Does the SmartFox socket connect without a policy file (app sandbox: yes per docs; loaded code: inferred)?
- **AIR / Ruffle, App Nap:** do timers and `ENTER_FRAME` keep 30 fps when the window is hidden, minimised or occluded on macOS 27, with and without `LSAppNapIsDisabled` / `NSAppSleepDisabled`? What is the actual frame rate with `NativeWindow.visible=false`?
- **Ruffle desktop / v0.6.0:** does AQW get past "Loading map at 100%", and what is memory growth over a multi-hour session? Can the signed `Ruffle.app` open a local `skua.swf` from the CLI, or is a self-built binary required?
- **Ruffle fork:** effort and stability of a custom `ExternalInterfaceProvider` (synchronous trait) bridged to .NET, given `ruffle_core` is not on crates.io.
- **ruffle-web in a webview:** WKWebView background throttling of a hidden window; WebSocket→TCP proxy latency; CORS for `gameversion`; packet-rate throughput over JS↔.NET.
- **Throughput:** `pext`/`packet` fire per server message. Measure Bridge latency and rate on each transport.
- **Screenshots:** confirm `BitmapData.draw(stage)` in each host includes the game content and measure the cost; Ruffle needs PNG encoding outside `BitmapData.encode`.
- **AIR minimum macOS** for 51.4 captive bundles (docs silent), and the macOS 26/27 bugs #4291 and #4326.
- **Harman enterprise Flash Player 50:** Mac or arm64 availability and pricing (not public).

## 6. Sources

**Local (this repo)**
- `Skua.AS3/compile-as3.ps1:35`
- `Skua.AS3/skua/asconfig.json`
- `Skua.AS3/skua/skua.as3proj:11-15`
- `Skua.AS3/skua/skua.iml:6`
- `Skua.AS3/skua/src/skua/Main.as`
- `Skua.AS3/skua/src/skua/Externalizer.as`
- `Skua.AS3/skua/src/skua/remote/RemoteRegistry.as`
- `Skua.AS3/skua/src/skua/api/{Server,Auras,World}.as`
- `Skua.AS3/skua/src/skua/module/QuestRequirementWiki.as:70`
- `Skua.WPF/Flash/FlashUtil.cs`
- `Skua.WPF/Flash/EoLHook.cs`
- `Skua.WPF/Services/SkuaStartupHandler.cs:30-32`
- `Skua.Core/Scripts/ScriptInterface.cs:393-432`
- `Skua.Core.Interfaces/Flash/IFlashUtil.cs`
- `Skua.App.WPF/Skua.App.WPF.csproj:40,61-65`

**AQW live endpoints (fetched 2026-09-26)**
- <https://game.aq.com/game/api/data/gameversion>
- <https://game.aq.com/crossdomain.xml>
- <https://game.aq.com/game/gamefiles/Game3098r27.swf> (header and string pool inspected)

**Harman AIR**
- <https://airsdk.harman.com/pricing>
- <https://airsdk.harman.com/main.91194927b40d4044023f.js>
- <https://airsdk.harman.com/assets/pdfs/HARMAN%20AIR%20SDK%20License%20Agreement.pdf>
- <https://airsdk.harman.com/api/versions/51.4.1.1/release-notes/Release_Notes_AIR_SDK_51.4.1.pdf>
- <https://airsdk.harman.com/api/versions/33.1.1.929/release-notes/Release_Notes_AIR_SDK_33.1.1.929.pdf>
- <https://airsdk.harman.com/api/config-settings/download>
- <https://airsdk.harman.com/runtime>
- <https://github.com/airsdk/airsdkmanager-releases>
- <https://github.com/airsdk/airsdk.dev/blob/main/docs/basics/install/macos.mdx>
- <https://github.com/airsdk/airsdk.dev/blob/main/docs/building/application-descriptor-files/elements/macOS.md>
- Adobe-Runtime-Support discussions and issues:
  - <https://github.com/airsdk/Adobe-Runtime-Support/discussions/1311>
  - <https://github.com/airsdk/Adobe-Runtime-Support/discussions/2107>
  - <https://github.com/airsdk/Adobe-Runtime-Support/discussions/2480>
  - <https://github.com/airsdk/Adobe-Runtime-Support/discussions/3156>
  - <https://github.com/airsdk/Adobe-Runtime-Support/discussions/3232>
  - <https://github.com/airsdk/Adobe-Runtime-Support/discussions/1417>
  - <https://github.com/airsdk/Adobe-Runtime-Support/discussions/594>
  - <https://github.com/airsdk/Adobe-Runtime-Support/issues/2481>
  - <https://github.com/airsdk/Adobe-Runtime-Support/issues> (#4291, #4326, #3998)
- API reference and docs:
  - <https://airsdk.dev/docs/development/networking-and-communication/using-the-external-api>
  - <https://airsdk.dev/docs/development/security/security-sandboxes>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/external/ExternalInterface.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/desktop/NativeProcess.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/desktop/NativeApplication.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/net/ServerSocket.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/net/Socket.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/net/LocalConnection.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/events/ThrottleEvent.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/display/NativeWindow.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/display/Stage.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/display/BitmapData.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/display/Loader.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/system/LoaderContext.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/system/Security.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/display3D/Context3D.html>
  - <https://help.adobe.com/en_US/as3/dev/WS5b3ccc516d4fbf351e63e3d118666ade46-7e5c.html>
  - <https://help.adobe.com/en_US/as3/dev/WS5b3ccc516d4fbf351e63e3d118666ade46-7e5a.html>
  - <https://help.adobe.com/en_US/air/html/dev/WS5b3ccc516d4fbf351e63e3d118666ade46-7e5c.html>
  - <https://help.adobe.com/en_US/air/build/WSfffb011ac560372f709e16db131e43659b9-8000.html>
- Community and tooling:
  - <https://community.adobe.com/questions-537/load-remote-swf-and-access-to-stage-84539> (community)
  - <https://github.com/joshtynjala/setup-adobe-air-action>
  - <https://github.com/anthony-hyo/aqw-mobile>

**Ruffle**
- <https://github.com/ruffle-rs/ruffle> (source at `a1277c0`; files cited inline as `R/…`)
- <https://github.com/ruffle-rs/ruffle/blob/master/LICENSE.md>
- <https://github.com/ruffle-rs/ruffle/releases>
- <https://ruffle.rs/downloads>
- <https://ruffle.rs/compatibility>
- <https://github.com/ruffle-rs/ruffle/wiki/Using-Ruffle>
- <https://github.com/ruffle-rs/ruffle/wiki/Frequently-Asked-Questions-For-Users#how-can-i-connect-to-a-tcpsocket-or-xmlsocket-from-the-web>
- Issues/PRs: #15599, #15301, #22362, #22471, #23860, #23099, #17287, #24392, #24590, #23212, #23003, #22873, #16148, #21120, #15953, #13538, #23395, #24520, #11415, #12047, #13094
- <https://github.com/aquaspy/aquastar-ruffle>

**Artix**
- <https://www.artix.com/downloads/artixlauncher/>
- `https://launch.artix.com/latest/Artix%20Game%20Launcher.dmg` (inspected)
- <https://www.artix.com/downloads/launcherarchives/>
- <https://www.aq.com/gamedesignnotes/artixgameslauncher-aqw-8173>
- <https://www.aq3d.com/news/artix_launcher_2/>
- <https://aq2d.com/posts/daily_updates_2025_03/>
- <https://www.aq.com/gamedesignnotes/aqw-update-ruffleanimateinfinity-10262>
- <https://www.artix.com/posts/adobeanimateupdate/>
- <https://www.aq.com/gamedesignnotes/aqwmobilefuture-6482>
- <https://www.aq.com/gamedesignnotes/tag/android>
- <https://aq2d.com/posts/aqworldsinfinity/>
- <https://store.steampowered.com/news/app/1979810/view/534373847137255731>

**Adobe, Apple, others**
- <https://www.adobe.com/products/flashplayer/end-of-life.html>
- <https://www.adobe.com/products/flashplayer/end-of-life-alternative.html>
- <https://community.adobe.com/questions-638/information-on-the-flash-projector-support-end-of-life-after-2020-717482>
- <https://en.wikipedia.org/wiki/Adobe_Flash_Player> (secondary)
- <https://github.com/Grubsic/Adobe-Flash-Player-Debug-Downloads-Archive> (mirror; binary verified Adobe-signed)
- <https://developer.apple.com/news/?id=w5ngl9k2>
- <https://support.apple.com/en-us/102527>
- <https://developer.apple.com/library/archive/documentation/Performance/Conceptual/power_efficiency_guidelines_osx/AppNap.html>
- <https://github.com/electron/electron/blob/main/docs/breaking-changes.md>
- <https://www.electronjs.org/blog/electron-11-0>
- <https://www.chromium.org/flash-roadmap/>
- <https://github.com/darktohka/clean-flash-builds>
- <https://labs.leaningtech.com/docs/cheerpx-for-flash/faq>
- <https://github.com/awayfl/awayfl-player>
- <https://github.com/lightspark/lightspark>
- <https://github.com/FlashpointProject/launcher/releases>
- <https://flashpointarchive.org/downloads>
- <https://appleinsider.com/articles/26/07/31/first-apple-silicon-native-crossover-build-in-testing-as-rosettas-end-nears> (secondary)

**Could not fetch (HTTP 403):** support.artix.com, the Kickstarter FAQ, codeweavers.com, flashpointarchive.org/datahub.

# How `skua.swf` gets permission to script the AQW game SWF

Research for [noelrohi/Skua#12](https://github.com/noelrohi/Skua/issues/12). Follows up the open question in [`game-host-candidates.md`](game-host-candidates.md) §2.2 and §5. Facts only; choosing a Game Host is a later ticket.

## 1. Question, date, method

**Question.** The Game Client scripts the AQW game SWF freely, even though the two SWFs come from different origins. What permits this on the Windows Game Host today? Candidates:
- the sandbox that a bytes-injected SWF gets in the ActiveX control,
- the game SWF calling `Security.allowDomain`/`allowInsecureDomain`,
- `crossdomain.xml`,
- something else.

Also: what does each Game Host candidate need as a result?

**Researched:** 2026-09-26 (UTC), on an arm64 Mac running macOS 27. No Windows machine was available.

**Method.**
- Read the local source: `Skua.AS3/skua/src/skua/Main.as`, `Skua.WPF/Flash/FlashUtil.cs`, `Skua.Core/Utils/FlashTrustManager.cs` and the Flash interop assemblies.
- Fetched the live game SWF the same way `Main.as` does, with no login: `GET https://game.aq.com/game/api/data/gameversion`, then `GET https://game.aq.com/game/gamefiles/<sFile>`. Also fetched the `crossdomain.xml` files.
- Decompiled the game SWF with the FFDec CLI 24.0.1 from `FFDec/` (`java -jar ffdec.jar -export script`, Homebrew OpenJDK 25.0.2), then grepped the ActionScript. The output stays in the scratchpad, not the repo.
- Read primary docs:
  - the Adobe AS3 developer guide security chapters and the `Security`, `Loader`, `LoaderInfo` and `LoaderContext` references, on help.adobe.com and on Harman's airsdk.dev port;
  - the Adobe Flash Player 32 Administration Guide.
- Read the Ruffle source at commit `a1277c0`, which is still `master` on 2026-09-26.
- Probed CORS headers with `curl`.
- All GitHub access was read-only.

**Conventions** (as in `game-host-candidates.md`):
- `R/` = `https://github.com/ruffle-rs/ruffle/blob/a1277c06e5d46c03726d813c3cc1faa49a238c54/`.
- `G:` = a file from the FFDec export of `Game3098r27.swf`, sha256 `b2133d8d5f723fc5f4e6c035392c41d384d669c64530621c1beeb419a56e175c`. For example, `G:scripts/Game.as:4933`. Line numbers are from FFDec 24.0.1 output.
- **Unverified**: no primary source was found.
- **Inferred**: reasoned from a cited source, not stated by it.
- **Docs silent**: the owner's docs do not address it.

## 2. Findings

### 2.1 The live game SWF (fetched 2026-09-26 07:54 UTC)

| Item | Value |
|---|---|
| `gameversion` response | `{"sFile":"Game3098r27.swf","sTitle":"Pirate Event & Rares!","sBG":"Generic2.swf","sVersion":"R0047"}` |
| Game SWF | `https://game.aq.com/game/gamefiles/Game3098r27.swf`, 2,471,427 bytes, `last-modified: Fri, 11 Sep 2026 00:35:45 GMT` |
| sha256 | `b2133d8d5f723fc5f4e6c035392c41d384d669c64530621c1beeb419a56e175c` |
| Header | `CWS` (zlib), **SWF version 43**, 4,616,080 bytes uncompressed, 24 fps, 52 frames, FileAttributes `0x28` (AS3) |
| Document class | `Game` (SymbolClass id 0, frame 1) |
| `game.aq.com/crossdomain.xml` | `site-control permitted-cross-domain-policies="master-only"`; `allow-access-from` `www.aq.com`, `game.aq.com`, `*.aqworlds.com`, `*.aq.com`. **No `domain="*"`** |
| `www.aq.com/crossdomain.xml` | Identical to `game.aq.com`'s |
| `content.aq.com/crossdomain.xml` | `*.aqworlds.com`, `*.battleon.com`, `*.aq.com` |
| `account.aq.com/crossdomain.xml` | 404. `aq.com/crossdomain.xml` returns a 301. |

### 2.2 What the Game Client does (local source)

**Load sequence** (`Main.as`):
1. `Security.allowDomain('*')` (`Main.as:91`).
2. A cross-domain `URLLoader` GET of `gameversion` (`Main.as:94`).
3. `Loader.load(new URLRequest(…'gamefiles/' + sFile))` with **no `LoaderContext`** (`Main.as:107`). The game therefore stays in its own security domain; this is not import loading.
4. On `COMPLETE` (`Main.as:110`), Skua:
   - removes its own `Main` from the Stage (`Main.as:114`);
   - adds `loader.content` as the Stage's child (`Main.as:115`);
   - writes `game.params` (`Main.as:120`);
   - listens on `game.sfc` (`Main.as:131`);
   - takes the game's `applicationDomain` (`Main.as:132`) and later calls `getDefinition` on it (`Main.as:286,398`);
   - listens on `game.stage` (`Main.as:138`);
   - walks arbitrary property paths (`Main.as:355`).

**Windows Game Host** (`FlashUtil.cs`):
- `FlashUtil.cs:54-62` writes `[len+8][0x55665566][len][swf bytes]` into `AxShockwaveFlash.OcxState`. `1432769894` is `0x55665566`.
- No `Movie`, `EmbedMovie`, `LoadMovie`, `Base`, `AllowScriptAccess`, `AllowNetworking` or `FlashVars` is set anywhere in the C# source (grep). The interop assemblies do expose all of these (`Skua.App.WPF/Assemblies/Interop.ShockwaveFlashObjects.dll`, strings).
- The Game Client therefore has **no URL and no FlashVars** from the host. `root.loaderInfo.parameters` is empty, so nothing is copied at `Main.as:119-121` (inferred).

**Flash trust file:**
- `FlashTrustManager.EnsureTrustFile()` writes `%AppData%\Macromedia\Flash Player\#Security\FlashPlayerTrust\Skua.cfg`. The file lists the app directory and `%AppData%\Skua` (`Skua.Core/Utils/FlashTrustManager.cs:5-52`).
- It is called at start-up (`Skua.App.WPF/App.xaml.cs:42`, `Skua.Manager/App.xaml.cs:32`).
- It was added in commit `b780384` (2025-12-08, "Added a flash trust file"), together with local background-config loading.
- The Game Client scripted the game long before that (release 0.2, 2022-07-30, `70c712c`), so this file is **not** what originally made cross-scripting work (inferred from history).
- Whether a trust entry applies at all to a movie injected through `OcxState` with no path is **docs silent**.

### 2.3 What the game SWF does (decompiled)

**Only permission call on the load path: `Security.allowDomain("*.aq.com")`, in the `Game` document-class constructor:**

```actionscript
// G:scripts/Game.as:4932-4935  (inside public function Game(), which starts at :428)
addFrameScript(0,this.frame1,11,this.frame12,...);
Security.allowDomain("*.aq.com");
this.addEventListener(Event.ADDED_TO_STAGE,this.onAddedToStage);
this.sfc = new SmartFoxClient();
```

- **When it runs.** It runs when frame 1 is constructed. `LoaderInfo` `init` fires only after "all ActionScript code in the first frame … has been executed", and `complete` always follows `init` (LoaderInfo reference). So this call has already run when Skua's `onComplete` touches `loader.content` (inferred from those docs).
- **It is not `allowDomain('*')`.** Adobe: "The wildcard value does not work for subdomains … you can't use a wildcard value that way for the allowDomain() method" (Security reference). As documented, the call grants nothing. Even if it matched `*.aq.com`, it could not match the Game Client, which has no aq.com URL.

**No other `Security.*` call runs on Skua's path** (grep of the whole export):
- `allowInsecureDomain`: none.
- `Security.allowDomain("fbcdn-profile-a.akamaihd.net")` and `loadPolicyFile(...)` exist only in the `FBListener` constructor (`G:scripts/FBListener.as:34-40`). That is Facebook login.
- `SecurityDomain.currentDomain` is used only in `G:scripts/com/wildtangent/WildTangentAPI.as:86-87`.
- `sandboxType`, `parentAllowsChild`, `childAllowsParent` and `sameDomain` do not appear.

**Every other `LoaderContext` loads game assets:** `new LoaderContext(false, <ApplicationDomain>)`. Examples: `G:scripts/Game.as:463,5123,8604,10294,10603`, `G:scripts/World.as:199,2090`.

**The game runs `onAddedToStage` when Skua adds it to the Stage** (`G:scripts/Game.as:9027-9038`).
- It writes `stage.showDefaultContextMenu` and `stage.stageFocusRect`. Both are Stage properties restricted to the Stage owner's sandbox (see §2.4).
- It derives `serverFilePath` from its own `loaderInfo.url`: `this.loaderInfo.url.substring(0, lastIndexOf("/")+1)` (`:9034`). That path is then used for map, interface and asset loads (e.g. `:5123,8604,10294`).
- It runs `registerErrorSuppression` (`:10799-10830`), which reads `stage.getChildAt(0).loaderInfo` inside `try`.

**The game assumes it is Stage child 0.** Many classes find the game root through `MovieClip(stage.getChildAt(0))`, for example `G:scripts/cMenuMC.as:39` and `G:scripts/AvatarMC.as:222`. Skua satisfies this by removing itself from the Stage (`Main.as:114-115`).

**URL checks** (not anti-bot; they pick endpoints):
- `loaderInfo.url` containing `file://`, `cdn.aq.com` or `aqworldscdn.aq.com` switches to hard-coded `https://game.aq.com/game/…` or `https://www.aq.com/game/…` URLs (`G:scripts/Game.as:8579`, `G:scripts/FBListener.as:161,257`, `G:scripts/fbLinkWindow.as:36`).
- Otherwise the game uses `params.sURL` or `params.loginURL`, which Skua sets (`Main.as:124,128`).

**The game's own ExternalInterface use is gated off under Skua (inferred):**
- It sits behind `Game.ISWEB`. `init()` (frame 12) sets `ISWEB = this.params.isWeb` and then creates `ExternalCalls`. That class constructs `ExternalCallsWeb`, which calls `ExternalInterface.addCallback("SendMessage",…)`, only when `isWeb` is true (`G:scripts/Game.as:9045-9049`, `G:scripts/ExternalCalls.as:22-33`, `G:scripts/ExternalCallsWeb.as:25`).
- Skua never sets `isWeb`, so it coerces to `false`.

**No anti-bot check of the loader's URL, the host or the sandbox was found.** The grep covered `loaderURL`, `loaderInfo.url`, `pageDomain`, `sandboxType`, `Capabilities`, `ExternalInterface.available` and `parent`/`Loader` checks. The search was not exhaustive; obfuscated checks would be missed.

### 2.4 Adobe's rules

**(a) Cross-scripting between domains.** The SWF being accessed must permit the accessor.
- "By calling `Security.allowDomain("siteA.com")`, swfB.swf gives SWF files from siteA.com permission to script it." Permissions "are asymmetrical" (developer guide, *Cross-scripting*; `Security.allowDomain` reference).
- From SWF version 8, allowDomain "permits access only to itself".
- **Consequence for Skua.** The Game Client's own `allowDomain('*')` (`Main.as:91`) lets the game script the Game Client, not the reverse, as `game-host-candidates.md` §2.2 already says.
- **Stage.** The Stage "owner" is "the first SWF file loaded". `addChild`, `addEventListener`, `align`, `scaleMode`, `showDefaultContextMenu`, `stageFocusRect` and similar members are available only to the owner's sandbox "or those granted permission by a call to `Security.allowDomain()`" (*Cross-scripting*, "Stage security").
  - On Windows the Stage owner is the Game Client.
  - `Main.as:91` is what lets the game's `onAddedToStage` write `stage.showDefaultContextMenu`/`stageFocusRect` and add listeners to the Stage (inferred).

**(b) Local SWFs.**
- **local-with-networking:** "a local-with-networking SWF file is still not allowed to read any network-derived data unless … a URL policy file must grant permission to *all* domains by using `<allow-access-from domain="*"/>` or by using `Security.allowDomain("*")`" (*Security sandboxes*).
  - Scripting: "for a local SWF file with network-access permissions to script a SWF file on the Internet, the Internet SWF file being accessed must call `Security.allowDomain("*")` … (If the Internet SWF file is loaded from an HTTPS URL, the Internet SWF file must instead call `Security.allowInsecureDomain("*")`.)" (`Security.allowDomain` reference).
- **local-trusted:** "SWF files that are assigned to the local-trusted sandbox can interact with any other SWF files and can load data from anywhere (remote or local)" (*Security sandboxes*; *Permission controls*, FlashPlayerTrust directories).
- **Policy files do not grant scripting.** They govern bitmap/sound/video data, XML/text loads, **import** loading into the loader's security domain, and sockets (*Permission controls*, "Website controls"). `Main.as:107` does not import-load, so `crossdomain.xml` plays no part in Skua scripting the game.

**(c) The ActiveX control in a non-browser host.** Flash Player 32 Administration Guide, `mms.cfg` setting `EnforceLocalSecurityInActiveXHostApp` (p. 52, "Availability: Adobe Flash Player 9 and above"):

> "By default, local security is disabled whenever the ActiveX control is running in a non-browser host application. In rare cases when this causes a problem, you can use this setting to enforce local security rules for the specified application."

The same guide's `DisableNetworkAndFilesystemInHostApp` is the stricter opt-in for host apps.

**(d) OcxState and embedded movies.**
- What `Security.sandboxType` and `loaderInfo.url` a movie injected through `OcxState` (the `EmbedMovie` persistence format) gets is **docs silent**: no Adobe page describes it.
- It has no `http(s)` URL, so it cannot be in the `remote` sandbox of aq.com (inferred).

### 2.5 Answer: what permits the cross-scripting today

**Excluded by evidence:**
- **The game's `allowDomain`.** The game calls only `Security.allowDomain("*.aq.com")` (`G:scripts/Game.as:4933`). Adobe says subdomain wildcards do not work in `allowDomain`, and in any case the Game Client is not served from aq.com. The game never calls `allowDomain("*")` or `allowInsecureDomain("*")`, which local-with-networking content would need.
- **crossdomain.xml.** It lists only aq.com/aqworlds.com hosts, never `*`, and policy files do not govern cross-scripting anyway.
- **`Main.as:91`.** It grants the reverse direction only.

**Most likely mechanism** (**Inferred**, high confidence; not tested):
- The Windows Game Host is a non-browser application hosting the ActiveX control. Adobe says local security is disabled by default there.
- The Game Client is local content: it has no remote URL.
- It therefore runs with local-trusted-equivalent rights: it can "interact with any other SWF files and … load data from anywhere". That covers `loader.content`, `applicationDomain.getDefinition`, property walks on the game, and the cross-domain `gameversion` `URLLoader`.

**Corroborating behaviour** (inferred):
- The `gameversion` fetch at `Main.as:94` would itself fail under local-with-networking rules, because `game.aq.com/crossdomain.xml` has no `domain="*"`.
- Under enforced local rules, `loader.content` at `Main.as:115` would throw (SecurityError #2121 "cannot access … This may be worked around by calling Security.allowDomain").
- Both work on Windows today, which is consistent with local security being off.

**The FlashPlayerTrust file** (`Skua.cfg`, since 2025-12) would give the same local-trusted result for file-backed SWFs in the app directory. Whether it applies to the `OcxState`-injected movie is docs silent, and it post-dates working cross-scripting.

**The other direction:** the game (remote, `game.aq.com`) uses the Game Client-owned Stage only because of `Security.allowDomain('*')` at `Main.as:91`.

**Not tested.** There is no Windows machine here. The following would settle it:
1. Make a build of `skua.swf` that sends `Security.sandboxType`, `loaderInfo.url`, and `loader.contentLoaderInfo.{sameDomain, parentAllowsChild, childAllowsParent}` over `external.debug`.
2. Run it three ways:
   - as shipped;
   - with the `FlashPlayerTrust\Skua.cfg` file removed;
   - with `EnforceLocalSecurityInActiveXHostApp=<host exe name>` in `mms.cfg`.
3. Expected result (prediction, inferred):
   - Runs 1 and 2 report `localTrusted` and work.
   - Run 3 reports `localWithNetwork` and fails at the `gameversion` load (#2048) or at `loader.content` (#2121).

## 3. Consequences for each Game Host candidate

What each host must provide:
1. The Game Client can read the game's objects and `ApplicationDomain`.
2. The game can use the Stage it is added to (`showDefaultContextMenu`, `stageFocusRect`, listeners).
3. The game's `loaderInfo.url` stays under `https://game.aq.com/game/gamefiles/`, or `Game.serverFilePath` is overridden.
4. The game's constructor-time `Security.allowDomain("*.aq.com")` does not throw.

### 3.1 Harman AIR (captive bundle)

**Rules that apply:**
- App-sandbox code "can cross-script code from any domain" by default, but "files outside the AIR application sandbox are not permitted to cross-script" it (*Security sandboxes*; `Security.sandboxType` `APPLICATION`).
- "Remote files cannot directly access the application sandbox, regardless of calls to the `Security.allowDomain()` method" (AIR *Working securely with untrusted content*). So the game's own `allowDomain` cannot help in either direction.
- `Security.allowDomain` called from the app sandbox "throws a SecurityError exception". Importing non-application content into the app sandbox via `LoaderContext.securityDomain`/`applicationDomain` "is prevented" (AIR *Scripting between content in different domains*; `Security` reference).

**Route A: `Loader.load(URL)` unchanged; the game stays in the remote sandbox.**
- Skua → game: allowed (app sandbox scripts any domain).
- Game → Stage: the Stage is owned by app content. The game's `onAddedToStage` writes `stage.showDefaultContextMenu` (`G:scripts/Game.as:9031`), which is Stage-owner-restricted. The owner cannot grant access, because `allowDomain` throws in the app sandbox.
- **Expected:** `SecurityError #2070` "caller … cannot access Stage owned by …" (Adobe runtime errors). The rest of `onAddedToStage` would not run: `serverFilePath` and `gotoAndPlay("Login")` (inferred).
- The game's own constructor `allowDomain("*.aq.com")` runs in the remote sandbox and does not throw (inferred).
- **Sandbox bridges** (`parentSandboxBridge`/`childSandboxBridge`) pass "simple objects and functions" by value only. Skua's live object walking and listeners on `game.sfc` do not fit that model (inferred from the same page).
- **Result: route A needs the game patched** or the Stage accesses avoided (inferred).

**Route B: `URLLoader` bytes, then `loadBytes` with `allowCodeImport = true`; the game joins the app sandbox.**
- `loadBytes` with a null or `applicationDomain`-only context loads "into the current security domain" (Loader reference). `allowCodeImport` (AIR 2.0+) gates this (LoaderContext reference). The `allowLoadBytesCodeExecution` name is legacy (AIR *untrusted content* page).
- Cross-scripting and Stage access are then same-sandbox.
- **But `Security.allowDomain("*.aq.com")` in the `Game` constructor throws in the app sandbox** (Security reference, unconditional). It sits before `this.sfc = new SmartFoxClient()` and the rest of the constructor (`G:scripts/Game.as:4933-4935`). An uncaught throw there aborts construction, so **the game SWF needs a bytecode patch or wrapper for route B** (inferred from docs + decompile; untested). This matches AQW Pocket patching the game (`game-host-candidates.md` §3.1).
- **`loaderInfo.url` becomes a synthetic URL.**
  - Adobe's LoaderInfo docs are silent. Ruffle's source records Flash Player's form as `"url-of-loader-swf.swf/[[DYNAMIC]]/2"` (`R/core/src/loader.rs:897-904`).
  - `serverFilePath` (`G:scripts/Game.as:9034`) would then point inside the app bundle, not at `https://game.aq.com/game/gamefiles/`.
  - The Game Client would need to overwrite `Game.serverFilePath`/`serverGamePath` after `addChild`, or patch it (inferred).
- The game's ExternalInterface paths stay off as long as `params.isWeb` is unset (§2.3). ExternalInterface would throw in AIR desktop only if reached (`game-host-candidates.md` §3.1).
- **Risk (unchanged from §3.1 of the candidates doc):** code downloaded live from game.aq.com runs with full app privileges.

**Route C: no AIR route found** that keeps the game unmodified and the Stage usable. The Stage always belongs to app-sandbox content in an AIR window (inferred).

### 3.2 Ruffle desktop

No cross-scripting security is enforced, so nothing extra is needed (inferred from code; untested with AQW):
- `Security.allowDomain`, `allowInsecureDomain` and `loadPolicyFile` are stubs that return `undefined` (`R/core/src/avm2/globals/flash/system/security.rs:56-81`). The game's `allowDomain("*.aq.com")` is harmless.
- `LoaderInfo.content` returns the loaded root with no sandbox check (`R/core/src/avm2/globals/flash/display/loader_info.rs:133-153`).
- `childAllowsParent`/`parentAllowsChild` only compare hosts, with "TODO: respect allowDomain() and polices" (`R/core/src/avm2/globals/flash/display/loader_info.rs:255-330`).
- `ApplicationDomain` lookups have no sandbox check. `getQualifiedDefinitionNames` says of the documented SecurityError "We do not implement this" (`R/core/src/avm2/globals/flash/system/application_domain.rs:118-123`), and `getDefinition` has no security check (grep).
- In AVM2 core, `SandboxType` is used only by the `sandboxType` getter, `System.exit` gating and `loadBytes` bookkeeping (grep: `security.rs:46`, `system.rs:46`, `loader.rs:281`).
- A local `skua.swf` is inferred as `LocalWithNetwork` because of its `use-network` flag (`R/core/common/src/sandbox.rs:43-61`; `Skua.AS3/skua/asconfig.json:22`). The desktop fetch has "TODO: honor sandbox type" (`R/frontend-utils/src/backends/navigator.rs:189`), so it can still fetch `gameversion` and the game.
- `Loader.load(URL)` keeps the real `https://game.aq.com/…` URL, so `serverFilePath` is correct (inferred).

### 3.3 ruffle-web (WebView / browser)

**Same core as §3.2:** no Flash cross-scripting checks.

**New constraint: browser CORS.**
- ruffle-web's navigator uses the browser `fetch()` with no explicit mode, which defaults to `cors` (`R/web/src/navigator.rs:315-345`).
- Probe on 2026-09-26 ~08:02 UTC, one request each, with `Origin: https://example.com` and `Origin: null`:

| URL | `access-control-allow-origin` |
|---|---|
| `…/gamefiles/Game3098r27.swf` | `*` |
| `…/gamefiles/Loader3.swf` | `*` |
| `…/api/data/gameversion` | **absent** (`vary: *`) |
| `…/api/data/travelmap?v=R0047` | **absent** |
| `OPTIONS …/api/login/now` (preflight probe) | **absent** (302) |
| `/crossdomain.xml` | absent (irrelevant: ruffle does not read policy files) |

**Consequences:**
- The game SWF loads cross-origin.
- `gameversion` (`Main.as:94`) and the game's own API calls do not. These include `api/data/travelmap`, `servers`, `clientvars` and `api/login/now`; the endpoint strings are in `G:scripts/*.as`.
- The page therefore needs a same-origin proxy, hosting under an aq.com origin (not possible), or a host that disables CORS (e.g. a WebView/Electron setting). Inferred from the probe; a single probe per URL.

## 4. Open questions / needs a prototype

- **Windows confirmation (not done here).** Report `Security.sandboxType` and `loaderInfo.url` of the `OcxState`-injected Game Client, and re-run with `EnforceLocalSecurityInActiveXHostApp` and without `Skua.cfg` (§2.5).
- **AIR route B.** Confirm that the constructor's `allowDomain("*.aq.com")` throws and aborts `Game()` in the app sandbox. Find the smallest patch or workaround, and confirm `serverFilePath` can be overridden after `addChild`.
- **AIR route A.** Confirm #2070 at `Game.onAddedToStage` and whether any other Stage-restricted access follows.
- **Ruffle.** Confirm the Game Client's deep property access, `getDefinition` and `game.sfc` listeners work end-to-end (no enforcement found in code).
- **ruffle-web.** Confirm which game API calls fail CORS in practice, including the login POST, and what proxy path they need.

## 5. Sources

**Local (this repo)**
- `Skua.AS3/skua/src/skua/Main.as:91,94,107,110-146,286,355,379-380,398`
- `Skua.AS3/skua/asconfig.json:22`
- `Skua.WPF/Flash/FlashUtil.cs:44-62`
- `Skua.Core/Utils/FlashTrustManager.cs:5-52`
- `Skua.App.WPF/App.xaml.cs:42`
- `Skua.Manager/App.xaml.cs:32`
- `Skua.App.WPF/Assemblies/Interop.ShockwaveFlashObjects.dll`, `AxInterop.ShockwaveFlashObjects.dll` (property names via `strings`)
- Git history: `b780384` (2025-12-08), `70c712c` (2022-07-30)

**AQW (fetched 2026-09-26)**
- <https://game.aq.com/game/api/data/gameversion>
- <https://game.aq.com/game/gamefiles/Game3098r27.swf> (sha256 `b2133d8d…e175c`; decompiled with FFDec 24.0.1, `FFDec/ffdec.jar`)
- <https://game.aq.com/crossdomain.xml>
- <https://www.aq.com/crossdomain.xml>
- <https://content.aq.com/crossdomain.xml>
- <https://account.aq.com/crossdomain.xml> (404)
- CORS probes: `…/api/data/travelmap`, `…/api/login/now`, `…/gamefiles/Loader3.swf`

**Adobe / Harman docs**
- `flash.system.Security` reference: <https://help.adobe.com/en_US/FlashPlatform/reference/actionscript/3/flash/system/Security.html>; <https://airsdk.dev/reference/actionscript/3.0/flash/system/Security.html>
- Runtime errors #2070, #2121, #2048: <https://help.adobe.com/en_US/FlashPlatform/reference/actionscript/3/runtimeErrors.html>
- Developer guide:
  - *Security sandboxes*: <https://help.adobe.com/en_US/as3/dev/WS5b3ccc516d4fbf351e63e3d118a9b90204-7e3f.html>; <https://airsdk.dev/docs/development/security/security-sandboxes>
  - *Permission controls*: <https://help.adobe.com/en_US/as3/dev/WS5b3ccc516d4fbf351e63e3d118a9b90204-7c85.html>; <https://airsdk.dev/docs/development/security/permission-controls>
  - *Cross-scripting*: <https://airsdk.dev/docs/development/security/cross-scripting>
- AIR:
  - <https://airsdk.dev/docs/development/security/air-security/scripting-between-content-in-different-domains>
  - <https://airsdk.dev/docs/development/security/air-security/working-securely-with-untrusted-content>
- API references:
  - <https://airsdk.dev/reference/actionscript/3.0/flash/display/Loader.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/display/LoaderInfo.html>
  - <https://airsdk.dev/reference/actionscript/3.0/flash/system/LoaderContext.html>
- Adobe Flash Player 32.0 Administration Guide (Adobe, 2020-12-09), p. 52, `EnforceLocalSecurityInActiveXHostApp` and `DisableNetworkAndFilesystemInHostApp`. Adobe's original URL is gone. Mirror: <https://open-flash.github.io/mirrors/flash-player-admin-32.pdf> (sha256 `cd5f8da4728cb632701da0cfaf80a932e232e55686275927f0da157070018ce9`; PDF metadata: FrameMaker 2017 / Acrobat Distiller, created 2020-12-09).

**Ruffle** (commit `a1277c0`)
- `R/core/src/avm2/globals/flash/system/security.rs`
- `R/core/src/avm2/globals/flash/system/system.rs`
- `R/core/src/avm2/globals/flash/system/application_domain.rs`
- `R/core/src/avm2/globals/flash/display/loader_info.rs`
- `R/core/src/avm2/globals/flash/display/loader.rs`
- `R/core/src/loader.rs`
- `R/core/common/src/sandbox.rs`
- `R/frontend-utils/src/backends/navigator.rs`
- `R/web/src/navigator.rs`

**Other**
- Clean Flash installer source, <https://gitlab.com/CleanFlash/installer> (commit `9d88bad`): no `mms.cfg` or FlashPlayerTrust writes found by grep. Its binary payload was not inspected.

# PROTOTYPE (#6): Ruffle Game Host + Bridge spike

Throwaway code that answered "Can the chosen Game Host run the Game Client, carry the Bridge, and log in?".
Verdict and numbers: the #6 resolution. Nothing here is production code.

- `gamehost/`: Rust Game Host (`skua-gamehost`) embedding Ruffle `a1277c0` plus `gamehost/patches/`.
  Offscreen wgpu/Metal, Bridge over stdin/stdout frames (format in `src/main.rs` header), EOF => exit.
  Flags: `--show-game`, `--render-every-frame`, `--render-interval-ms=N`.
- `bridge-console/`: headless Engine host (Skua.Core retargeted to `net10.0`) with `MacFlashUtil`
  (FlashUtil's XML invoke format over the Game Host). Modes: `smoke`, `hold`, `compile`, `live`, `full`, `script`, `idle`.
- `gamehost/stress/`: synthetic SWFs that reproduce the Metal command-buffer exhaustion, the headless memory growth
  and the event ordering/throughput check without logging in.

## Build

1. Ruffle: `git clone https://github.com/ruffle-rs/ruffle ~/src/ruffle && git -C ~/src/ruffle checkout a1277c0 && git -C ~/src/ruffle apply <repo>/prototype/gamehost/patches/0001-ruffle-a1277c0-skua.patch`
2. wgpu-hal: copy `~/.cargo/registry/src/*/wgpu-hal-30.0.1` to `~/src/patched/wgpu-hal-30.0.1` and apply `patches/0002-…` (`patch -p1`).
3. `cd prototype/gamehost && cargo build --release` (rustc 1.98.1; Xcode for Metal).
4. `skua.swf`: Apache Flex SDK 4.16.1 + Adobe `playerglobal32_0.swc` at `frameworks/libs/player/32.0/`, then
   `PLAYERGLOBAL_HOME=<sdk>/frameworks/libs/player <sdk>/bin/mxmlc -source-path Skua.AS3/skua/src -default-size 958 550 -output skua.swf Skua.AS3/skua/src/skua/Main.as -target-player 32.0 -optimize`
5. `cd prototype/bridge-console && dotnet build -c Release`

## Run

```
export SKUA_GAMEHOST=<repo>/prototype/gamehost/target/release/skua-gamehost SKUA_SWF=<path>/skua.swf \
       SKUA_OUT=<scratch>/out SKUA_DIR=<scratch>/skuadir   # never the real SkuaDIR
dotnet bridge-console/bin/Release/net10.0/bridge-console.dll smoke
dotnet bridge-console/bin/Release/net10.0/bridge-console.dll full Scripts/Farm/Leveling.cs 120 --server Galanoth
```

`live`/`full`/`script` read the Test Account from Keychain (`skua-test-account`) and redact it from all output.

## #13 (Game Host memory) additions

Branch `prototype/gamehost-memory`. Verdict and numbers: the #13 resolution.

- **Measure `footprint`, not RSS.** On macOS, RSS hides compressed pages and most GPU memory. `stress/`-style runs can reach
  tens of GB of footprint while RSS falls. The host answers `'M'` (memory stats JSON: movie libraries, characters, GC objects,
  texture pools, wgpu resource counts) and `'G'` (full GC). `bridge-console` logs a `MEM` line (footprint buckets + `'M'`)
  every minute, plus a full-GC sample every 30 min. New mode: `loginscreen <min>` (no login).
- **Host fixes:** an autorelease pool per loop iteration (Metal objects were never released), an offscreen pool trim every 30
  ticks (`SKUA_TRIM_TICKS`, 0 = off), host flags via `SKUA_GAMEHOST_ARGS`, and `SKUA_NO_RENDERBENCH=1`.
- **Ruffle patch series** on `a1277c0` + `0001` (apply with `git am patches/00{03..16}-*.patch`):
  `0003`–`0006` upstream PR #24590 (movie-library lifetime by reachability; `unloadAndStop`; lazy tessellation),
  `0007` stats + texture-pool trim, `0008` gc-arena 0.7 external-accounting shim, `0009` #14's pass-budget flush,
  `0010` pool trim on by default (`SKUA_POOL_TRIM=off`), `0011` at most 16 in-flight submissions (`SKUA_MAX_INFLIGHT`),
  `0012`/`0014` GC census and retainer path (need `patches/gc-arena-0.7.0-census.patch` via `[patch.crates-io]`),
  `0013`/`0015` the null audio backend ends event sounds after their duration (AQW `SoundFX.activeChannels` leak),
  `0016` `Dictionary(weakKeys)` holds object keys weakly (AQW `Game._colorCache` leak).
  `0002` is only a peak counter now (stock wgpu-hal 4096 limit).
- **Diagnostics:** `'Y'` = census (live GC objects by Rust type and AS3 class), `'Z' <class>` = shortest root path to the oldest
  live instance of an AS3 display class; `bridge-console` with `SKUA_CENSUS=1`, `SKUA_RETAIN_CLASSES=A,B`, `SKUA_GC_AT=10,30,...`.
  Repro SWFs: `stress/Sounds.as` (channels kept until `SOUND_COMPLETE`), `stress/WeakDict.as` (weak-key cache);
  compile with `mxmlc -omit-trace-statements=false`.
- **Build of the gate config** (`~/src/ruffle-13lib` = pin + 0001 + 0003..0016; add
  `--config 'patch.crates-io.gc-arena.path="<gc-arena-0.7.0 + census patch>"'`):
  `cargo build --release --target-dir target-fix --config 'patch."https://github.com/ruffle-rs/ruffle".ruffle_core.path="<ruffle>/core"'`
  (same for `ruffle_render`, `ruffle_render_wgpu`, `ruffle_frontend_utils`).
- **Offline:** `stress/fp.py <label> <secs> <every> <swf> [flags]` samples footprint + `'M'`.
  **Live:** `bridge-console script <Leveling.cs> 120 --server Galanoth` with `SKUA_GAMEHOST_ARGS=--render-interval-ms=250`.

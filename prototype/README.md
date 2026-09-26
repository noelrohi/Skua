# PROTOTYPE (#6): Ruffle Game Host + Bridge spike

Throwaway code that answered "Can the chosen Game Host run the Game Client, carry the Bridge, and log in?".
Verdict and numbers: the #6 resolution. Nothing here is production code.

- `gamehost/`: Rust Game Host (`skua-gamehost`) embedding Ruffle `a1277c0` plus `gamehost/patches/`.
  Offscreen wgpu/Metal, Bridge over stdin/stdout frames (format in `src/main.rs` header), EOF => exit.
  Flags: `--show-game`, `--render-every-frame`, `--render-interval-ms=N`.
- `bridge-console/`: headless Engine host (Skua.Core retargeted to `net10.0`) with `MacFlashUtil`
  (FlashUtil's XML invoke format over the Game Host). Modes: `smoke`, `hold`, `compile`, `live`, `full`, `script`, `idle`.
- `gamehost/stress/`: `bench.py` (#14: render time, peak command buffers, device loss for one SWF, no login) and synthetic SWFs that reproduce the Metal command-buffer exhaustion, the headless memory growth
  and the event ordering/throughput check without logging in.

## Build

1. Ruffle: `git clone https://github.com/ruffle-rs/ruffle ~/src/ruffle-14 && git -C ~/src/ruffle-14 checkout a1277c0`, then `git apply` `patches/0001-…` and `patches/0003-…` (#14: mid-frame flush on a render-pass budget).
2. wgpu-hal: copy `~/.cargo/registry/src/*/wgpu-hal-30.0.1` to `~/src/patched/wgpu-hal-30.0.1-14` and apply `patches/0002-…` (`patch -p1`; a peak counter only, stock 4096 limit since #14).
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

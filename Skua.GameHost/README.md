# Skua.GameHost

`skua-gamehost` is the macOS Game Host: a small Rust program that runs the Game Client (`skua.swf`) in embedded
Ruffle, offscreen on Metal, and carries the Bridge to the Engine over its stdin/stdout. Why Ruffle, and why a
custom host: [ADR 0003](../docs/adr/0003-game-host-rust-embedding-ruffle.md). How Ruffle is pinned and patched:
[ADR 0004](../docs/adr/0004-ruffle-patches-on-a-fork-branch.md).

This Cargo project is not in `Skua.sln`, and Windows never builds it. On macOS, `dotnet build` of `Skua.App.Engine` builds it
and puts `skua-gamehost` next to `skua-engine` (see `BUILD.md`).

## Build and test

```
cargo build --release --locked   # target/release/skua-gamehost
cargo test --locked              # framing, XML and options; no GPU, no SWF
```

- The toolchain is pinned in `rust-toolchain.toml` (1.98.1, `aarch64-apple-darwin`); rustup installs it on first use.
  It moves only together with the Ruffle pin.
- The build needs the macOS SDK (Xcode or the Command Line Tools). wgpu compiles its Metal shaders at runtime.
- A cold build takes about 5 minutes on an M4 Pro. Always use the release build with the Engine: the debug build
  is too slow for the Bridge.
- The release profile keeps line tables, so panics still print file and line.

## Run

```
skua-gamehost [options] <skua.swf>
```

The Engine starts the Game Host as a child process. Without options it runs with the headless defaults below.

| Option | Default | Meaning |
|---|---|---|
| `--show-game` | off | Show the game in a debug window. It renders every 33 ms, with no render budget. |
| `--frame-buffer=NAME` | none | Write frames to this Frame Buffer (below) while the Game View is live. The Mac App's Engine passes it; `skua-engine` never does. |
| `--render-interval-ms=N` | 1000 | Keep-alive render interval; 0 = render only for screenshots. |
| `--render-budget-pct=N` | 10 | Render at most N% of wall time; 0 = off. |
| `--render-max-interval-ms=N` | 5000 | The budget never stretches the interval beyond this. |
| `--no-render-thread` | | Run the GPU half of each frame on the main thread. |
| `--pass-budget=N` | 64 | Render passes per GPU submission. |
| `--max-in-flight=N` | 2 | GPU submissions in flight; 0 = unbounded. |
| `--layer-flush=N` | 8 | Draw finished blend layers after every N; 0 = upstream behaviour. |
| `--trim-ticks=N` | 30 | Trim the offscreen texture pool every N ticks; 0 = off. |

| Environment | Meaning |
|---|---|
| `SKUA_GAMEHOST_LOG` | A `tracing` filter for Ruffle/wgpu logs (default `warn`). Lines at warn and above go to the Engine as `L` frames. |
| `SKUA_GAMEHOST_STDERR` | If set, also write the logs to stderr. |
| `SKUA_GAMEHOST_PANIC_ON=<name>` | Test hook: panic inside Ruffle when AS3 calls `ExternalInterface.call(name)`. The stress suite's lifecycle case uses it. |

## Bridge frames

Every frame is a `u32` LE length (covering the type byte plus the payload), then a `u8` type, then the payload.
Payloads are the Flash XML invoke strings the Windows ActiveX control uses, so `IFlashUtil`, the XML codec and
every generated getter stay unchanged. The authoritative list is `src/frame.rs`.

| Direction | Type | Payload |
|---|---|---|
| Engine → Game Host | `C` | `u32 id` + `<invoke>` XML: a synchronous call into AS3 |
| Engine → Game Host | `S` | `u32 id` + `u32 maxWidth` (0 = native): a screenshot |
| Engine → Game Host | `P` | `u32 id`: ping |
| Engine → Game Host | `Q` | `u32 id`: stats |
| Engine → Game Host | `W` | `u32 id` (0) + `u8 live` [+ `u32 width` + `u32 height` + `f32 scale`]: the Game View is live (render every 33 ms, no budget, at that viewport, and fill the Frame Buffer) or not (the headless defaults, at the stage size). No reply. |
| Engine → Game Host | `U` | `u32 id` (0) + `u8 kind` + fields: input from the Game View, handed to Ruffle's `Player::handle_event` (`src/input.rs`). No reply. |
| Game Host → Engine | `R` | `u32 id` + return XML |
| Game Host → Engine | `I` | `u32 id`, `u32 w`, `u32 h`, `u64 frame` + PNG bytes (w = h = 0 and no PNG if there's no image; `frame` is an estimate, time run × frame rate) |
| Game Host → Engine | `P` | `u32 id`: pong |
| Game Host → Engine | `Q` | `u32 id` + stats JSON (render and tick timings; the sums and maxima reset on each read) |
| Game Host → Engine | `E` | `<invoke>` XML: AS3 called `ExternalInterface.call`; the host returns `undefined` to AS3 at once |
| Game Host → Engine | `F` | Flash log: AS3 `trace()`, warnings and uncaught AS3 errors |
| Game Host → Engine | `L` | `u8 level` (1 error, 2 warn) + a Ruffle/wgpu log line |
| Game Host → Engine | `X` | a name AS3 registered with `ExternalInterface.addCallback` |
| Game Host → Engine | `O` | `u8 cursor` (0 arrow, 1 hand, 2 I-beam, 3 grab) + `u8 visible` (0 after `Mouse.hide()`): the Game View's cursor changed. Only with a Frame Buffer. |
| Game Host → Engine | `K` | UTF-8 text the game put on the clipboard (a Copy or Cut in a text field, or `System.setClipboard`). Only with a Frame Buffer. |

A frame of unknown type, or one too short for its type, is logged and skipped. A corrupt length ends the host
with status 2.

## The Game View: Frame Buffer and input

The Mac App shows the game from a Frame Buffer (ADR 0006): a POSIX shared-memory object the Engine creates (mode 0600)
and names with `--frame-buffer`. The host maps it before it reads stdin, so once the Engine has a reply to its first
ping it unlinks the name. The layout is in `src/frame_buffer.rs` (mirrored by `Skua.MacOS/GameHost/FrameBuffer.cs`):
a header, then three slots of RGBA8 rows at the render size, latest frame wins, with a seqlock per slot.

- **`W` live** switches the render policy to the `--show-game` one (33 ms, no budget) and the viewport to the one it
  carries: the Game View's size in device pixels, so it is sharp on Retina, kept between the stage size and the slots'
  size. `W` not live goes back to the command line's policy and the 958×550 stage. While live, the render thread maps
  the target's readback buffer after each frame (Ruffle's `TextureTarget` copies into it on every submit) and copies the
  rows into the free slot.
- **Screenshots** (`S`) are always the stage size, or `maxWidth`: a frame rendered for a larger Game View is scaled down.
- **`U`** kinds are mouse move, down, up and leave (viewport pixels), wheel (lines or pixels), key down and up
  (Ruffle's `KeyDescriptor`, by variant name), text (a code point), text control (a `TextControlCode` name, e.g.
  `Backspace`: Ruffle edits text fields only through these), focus gained and lost, and clipboard (the Mac's clipboard
  text, which the app sends just before a Paste). The loop ticks right after each one, even within 4 ms of the last tick.
- **The Game View's UI**: a host with a Frame Buffer gets `GameViewUi` (`src/backends.rs`) as Ruffle's `UiBackend`. It
  sends cursor changes as `O` and what the game copies as `K`, and pastes the text the last `U` clipboard gave. A
  headless host keeps Ruffle's null UI backend, with its empty clipboard.
- The `Q` stats gain `live`, `framesWritten` and `inputEvents` (both counts since start), and `viewportWidth` and
  `viewportHeight`.

## Lifecycle

- **stdin EOF ends the host at once** (status 0). The Engine closing the pipe or dying closes stdin; the stress
  suite measures about 5 ms.
- **Any panic aborts the process** (SIGABRT). A panic inside Ruffle leaves its `Player` mid-update and its mutex
  poisoned, so there is no sound state to go on from. The panic message and backtrace reach the Engine as an `L`
  frame and stderr first.

## Headless defaults

From #13 and #17. Together they keep a headless host under 2 GB and responsive on crowded maps.

| Default | Where |
|---|---|
| Drain an autorelease pool every loop iteration (Metal objects are autoreleased) | `main.rs`, `render.rs` |
| Trim the offscreen texture pool every 30 ticks; the main-frame pool trims itself every frame | `--trim-ticks`; fork commit `skua: Trim the texture pools, on by default` |
| Pass budget 64, in-flight bound 2, layer flush 8 | set from the flags at start-up |
| `submit_frame` on a render thread, under a 10% render budget: 1 s interval, capped at 5 s | `render.rs`, `opts.rs` |
| A keep-alive render even when nobody asks for a frame: without one, Ruffle's CPU-side state grows (Stress2: ~670 MB/min) | `Host::render_if_due` |
| Tick at most every 4 ms during bursts of Bridge calls | `MIN_TICK_GAP` |
| `--show-game`: a debug window updated every 33 ms | `opts.rs` |
| A live Game View (`W`) renders every 33 ms, as `--show-game` does, at the view's size | `opts::LIVE`, `view_viewport` |

The lag killer is also on while headless, but the Engine owns that.

## The Ruffle fork

`Cargo.toml` takes `ruffle_core`, `ruffle_render`, `ruffle_render_wgpu` and `ruffle_frontend_utils` from
[`noelrohi/ruffle`](https://github.com/noelrohi/ruffle) by full `rev`:

- **rev** `171ab779ee77ca36650531faaa215db713d97d2e`
- **tag** `skua-20260927b-a1277c0` (tags are immutable and never deleted, so every rev a Skua commit names stays reachable)
- **base** upstream Ruffle `a1277c0`

There is no `[patch]` section: wgpu-hal and gc-arena are stock. The `skua` branch holds one commit per fix, with
no diagnostics and no `SKUA_*` environment hooks. The host calls the setters instead.

### Patch table

The prototypes numbered their patches, and the numbers clash across branches (#14's `0003` is #13's `0009`).
Commits are named by subject from now on. This table keeps the old resolutions readable.

| Prototype patch | Commit on `skua` |
|---|---|
| 0003–0006 (Ruffle PR #24590, Farhan Riaz) | `core, desktop: Add opt-in memory reporting for loaded SWFs`; `core: Release the libraries of unloaded movies`; `core: Release loaded movie libraries by reachability, not by content root`; `core, render, desktop: Stop unloadAndStop collection storms; make resident assets cheap` |
| 0008 | `skua: Port the library-lifetime fix's GC accounting to gc-arena 0.7` |
| 0007 (trim half) + 0010 | `skua: Trim the texture pools, on by default` (`set_pool_trim`, default `(30, 4)`) |
| 0009 (#14's `0003`) | `skua: Flush mid-frame once a pass budget is reached` (`set_max_passes_per_submit`, default 64) |
| 0011 + 0017's setter | `skua: Bound the submissions in flight` (`set_max_in_flight`, default 2) |
| 0013 + 0015 | `skua: End the null audio backend's event sounds after their duration` |
| 0016 | `skua: Hold the keys of a weak-key Dictionary weakly` |
| 0017 (without its census) | `skua: Draw finished blend layers early` (`set_layer_flush`, default 8) |
| (none; #49) | `skua: Write whole numbers in JSON.stringify as Flash does` |
| 0001, 0007 stats, 0009 counter, 0012, 0014, 0017 census | 6 commits on `skua-diag` (not shipped) |
| 0002 (wgpu-hal peak counter) | `diag/wgpu-hal-30.0.1-peak-counter.patch` (not shipped) |
| gc-arena census | `diag/gc-arena-0.7.0-census.patch` (not shipped) |

### Upstreaming candidates

Offering these upstream would shrink the fork. Nobody has offered them yet.

- `skua: Hold the keys of a weak-key Dictionary weakly`
- `skua: Write whole numbers in JSON.stringify as Flash does`
- `skua: End the null audio backend's event sounds after their duration`
- `skua: Trim the texture pools, on by default`
- `skua: Flush mid-frame once a pass budget is reached` (a PR draft is on branch `prototype/gamehost-flush`)
- `skua: Draw finished blend layers early` (it would need `Send` bounds on Ruffle's handle traits)

Not candidates:

- the #24590 port, which upstream closed on policy (LLM-authored). We carry it until upstream lands an equivalent
  fix for movie-library lifetimes.
- the in-flight bound, a host concern that would need an upstream API discussion.

Known limit: the pass counter and the in-flight queue are process-global. That is fine with one Ruffle `Player` per Game
Host. The upstreaming shape moves both onto `Descriptors`.

## Changing Ruffle

**A new or fixed patch of ours is not a pin bump.** It is a new commit on `skua` at the same base, a new tag
`skua-<yyyymmdd>-<base7>` (with a letter after the date for a second series on the same day and base), and a `rev`
change here. The gate below still applies.

**The upstream base moves only on need, never on a schedule.** The triggers:

1. an upstream Ruffle fix or feature that AQW needs;
2. the pinned toolchain or Ruffle no longer building on current macOS or Xcode;
3. a security fix, in Ruffle or a dependency it pins, that the Game Host can reach (network, SWF parsing).

On every bump:

- drop the commits upstream has absorbed;
- re-check whether upstream now has an equivalent of #24590; if it does, drop the port;
- rebase `skua-diag` onto the new `skua`;
- move `rust-toolchain.toml` to the stable release the new base needs.

One Skua commit carries the new `rev`, the toolchain and `Cargo.lock` together.

### The gate

Each step gates the next.

1. **Ruffle's own tests, on the fork at the new tag.**
   - Run `cargo test -p tests --test tests -- avm2/`, then `-- avm1/`. The baseline at `skua-20260927b-a1277c0`
     is avm2 1,171 passed and avm1 765 passed.
   - Run #24590's lifetime tests (`loader_unload_releases_library`, `retained_class_keeps_library`,
     `released_class_frees_library`) and `weak_keys_are_collected`, one filter at a time.
   - A failure fails the step, unless the upstream base fails it too.
2. **The offline stress suite:** `stress/check.sh` (below). Every case must pass against `stress/baseline.txt`.
3. **One live smoke** with one Test Account login.
   - Log in, join `battleon` and hold there for 5 min with the headless defaults. Then run `Farm/Leveling.cs` for
     15 min.
   - It passes on all of these: footprint < 2 GB throughout; getter p99 ≤ 50 ms per minute (the join minute is
     excluded); joining `battleon` ≤ 10 s; 0 `bridge.error`, 0 panics, 0 deaths; a correct screenshot.
   - Until the Engine and CLI exist, this runs through the prototype `bridge-console` (branch
     `prototype/gamehost-crowded`). After that it is the live-game suite's smoke test.
4. **The 2-hour memory gate** (#13's run: `Farm/Leveling.cs`, footprint < 2 GB and flat). It runs only if the
   rebase had conflicts in the #24590 commits, the weak-key `Dictionary`, the trim or the in-flight bound, or
   anywhere under `core/display_object`, `library` or `avm2/object/dictionary*`. Otherwise the smoke is enough.

The Skua commit that moves `rev` records the results: the tag, the test counts, the stress table and the smoke
numbers.

## Offline stress suite

```
stress/check.sh                     # every case, about 5 min
stress/check.sh --only smoke,events
stress/check.sh --record            # print the measured values in baseline.txt's format
```

It builds `skua.swf` with `Skua.AS3/compile-as3.sh`, which caches Flex SDK 4.16.1 and playerglobal 32.0. It
builds the stress SWFs with that same SDK, and then the release host. Then it runs each case over the Bridge
frames. `SKUA_SWF`, `SKUA_FLEX_HOME` and `SKUA_GAMEHOST` skip those builds. Run it on a Mac with a real GPU, not in CI: a paravirtual GPU makes the footprint and render times
meaningless. The smoke case loads the live game's login screen, so it needs the network (no login). Output,
stderr and the smoke screenshot go to `stress/out/`.

| Case | SWF | Passes if |
|---|---|---|
| `stress2` | `Stress2.as`: an offscreen `BitmapData.draw` of a filtered, masked, blended avatar, 60 times a frame | the footprint is flat and its peak is under baseline + 20% |
| `stress3` | `Stress3.as`: 1,500 masked, filtered, layered children on one stage | the same |
| `stress4` | `Stress4.as`: 6,000 of them | the average `submit_frame` time on the render thread is under baseline + 20% |
| `sounds` | `Sounds.as`: AQW's `SoundFX`, which keeps each channel until `SOUND_COMPLETE` | the live channel count doesn't grow (the second half never exceeds the first) and stays under baseline + 20% |
| `weakdict` | `WeakDict.as`: AQW's `Game._colorCache`, a weak-key `Dictionary` | dead keys are collected: the count falls back to a fresh cycle's and stays under baseline + 20% |
| `events` | `Events.as`: 30,000 numbered `ExternalInterface.call`s in three interleaved streams | every event arrives, in order |
| `reads` | `Reads.as`: `getGameObject` of AQW-shaped objects: items with CharItemIDs past 2^28, a 1,000-item bank behind a getter, the players' weak-key `Dictionary` (and each player by key, as the Engine reads them), odd numbers and text | every reply parses as JSON with all its entries; every whole number is written as an integer and the `Dictionary` as `"Dictionary"`, as Flash writes them |
| `smoke` | `skua.swf` | the game loads, all 73 callbacks register, the screenshot is a 958×550 login screen, and no uncaught AS3 error reaches the flash log |
| `lifecycle` | `Events.as` | closing stdin ends the host within 1 s; a panic inside Ruffle aborts it with SIGABRT and an `L` frame |

"Flat" means the least-squares slope after warm-up is at most 10% of the baseline per minute. Without the
keep-alive render, Stress2 grows about 670 MB/min.

`stress/baseline.txt` is updated with each Ruffle series. Run `--record` a few times and take the worst value.

## Local Ruffle work and the diagnostics build

`diag/setup.sh <ruffle checkout>` writes a gitignored `.cargo/config.toml` that points the four Ruffle crates at a
local checkout. `diag/setup.sh --off` removes it. The override rewrites `Cargo.lock`, so build without `--locked`
and don't commit that change (`git checkout Cargo.lock`).

The **`diag` feature** adds the census and memory frames that #13 and #17 used to find leaks. It builds only
against the `skua-diag` branch, plus gc-arena and wgpu-hal patched with this folder's `diag/*.patch`:

```
git -C <ruffle> checkout skua-diag
diag/setup.sh <ruffle> --diag
cargo build --release --features diag
```

| Frame | Reply | Meaning |
|---|---|---|
| `M` | `Q` | memory stats: movie libraries, GC objects, texture pools, wgpu resource counts |
| `G` | `P` | a full GC |
| `Y` | `Q` | census: live GC objects by Rust type and AS3 class (send `G` first) |
| `Z` + class | `Q` | the shortest root path to the oldest live instance of an AS3 class |
| `B` + `u32 n` | `Q` | render the stage n times back to back, with a GPU wait each time |
| `V` + `key=value` | `P` | render knobs: `interval_ms`, `budget_pct`, `max_interval_ms`, `passes`, `layer_flush`, `inflight`, `thread` |

With `diag`, the `Q` stats also carry `framesRun`, `passBudgetFlushes`, `maxOutstandingCmdBufs` and the render
census (`rc`). CI never builds `diag`, so it may rot between leak hunts.

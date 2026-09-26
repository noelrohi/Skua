# The macOS Game Host is a custom Rust program embedding pinned Ruffle crates

On macOS the Game Client runs in `skua-gamehost`, a small Rust binary. It embeds Ruffle's crates, pinned to a commit, installs its own ExternalInterface handler, and carries the Bridge to the Engine over its stdin/stdout. It renders offscreen with no window and takes screenshots from its own frame. We chose this because it is the only native arm64 option that runs the live AQW game SWF unpatched and keeps `skua.swf` byte-identical to the Windows build, with the ExternalInterface contract intact. It also lets us patch Ruffle ourselves when AQW hits a Ruffle bug.

## Considered Options

- **Harman AIR.** Rejected: no route runs the live game SWF unpatched (Stage #2070 when loaded by URL; the game's constructor `allowDomain` throws when loaded as bytes, and then the code runs with full app privileges). It also needs a Bridge rewrite and has a proprietary licence with a splash screen.
- **Real Adobe Flash** (projector, Pepper, Artix launcher). Rejected: x86_64-only, and Rosetta ends after macOS 27.
- **Stock Ruffle desktop with an AS3 socket Bridge.** Rejected: it needs the largest AS3 change, and it can't run windowless or take screenshots from its own frame.
- **A fork of Ruffle's desktop app.** Rejected: the same as the chosen option, plus a GUI to strip and a bigger fork to rebase.
- **ruffle-web in headless Chromium over CDP.** Kept as the fallback if the custom Game Host fails #6. It keeps `skua.swf` unchanged, but needs Chromium, a WebSocket→TCP proxy, a CORS workaround, and an extra JavaScript hop per synchronous call.

## Consequences

- The repo gains a Rust/Cargo project, and building for macOS needs the Rust toolchain. Ruffle is a git dependency, so bumping the pin means following its API changes.
- Ruffle's fidelity is ours to fix. A blocking Ruffle bug gets a 3-dev-day patch timebox; if it can't be fixed in that time, the macOS v1 destination is blocked. We don't fall back to AIR or real Flash.
- The Windows app and its `skua.swf` are untouched.
- #6 found that the pin needs local patches from day one, so carrying patches is the normal state, not the exception. After #14 and #13 these are Ruffle patches (wgpu-backend mid-frame flush, texture-pool trimming, the AQW GC fix); the wgpu-hal limit patch was dropped.
- How the patches are carried (a `noelrohi/ruffle` fork branch pinned by rev and tag) and how the Game Host is built: ADR 0004.

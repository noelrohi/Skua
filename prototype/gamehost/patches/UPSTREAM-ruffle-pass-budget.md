# Draft upstream PR for ruffle-rs/ruffle (#14): not submitted

Needs a human with a GitHub account to open. Base it on `patches/0003`, but:
- move `PASSES_SINCE_SUBMIT` onto `Descriptors` (an `AtomicU32`; `CommandTarget::ensure_cleared` then needs `&Descriptors`);
- drop `SKUA_FLUSH_PASSES`, `set_max_passes_per_submit` and `SKUA_PASS_BUDGET_FLUSHES` (measuring hooks);
- keep `MAX_DRAWS_PER_FLUSH` at upstream's 100.

---

**Title:** wgpu: submit mid-frame once a render-pass budget is reached

**Body:**

A heavy frame records every render pass into one `CommandEncoder`. wgpu-core 30 encodes each render pass as its own backend command buffer, plus one for the barriers before it and one for copies after it. On Metal that is 3 per pass, and all of them are "created but not submitted" until the encoder is submitted. wgpu-hal's Metal backend allows 4096 of those and then reports the device as lost; older wgpu-hal allowed 2048 and hung forever in `-[MTLCommandQueue commandBuffer]`. After that, every GPU call panics.

Real content hits the limit. On AdventureQuest Worlds, the town map `battleon` records about 1,500–1,900 passes per frame (about 4,600–5,600 command buffers), so the first render after login loses the device. A synthetic SWF with 1,500 masked, glow-filtered, `BlendMode.LAYER` sprites does the same.

`ActiveFrame::maybe_flush` only counts cache-entry and offscreen draws, so it can't catch one heavy frame. This PR:
- counts render passes as they are begun;
- submits the active encoder between chunks in `Surface::draw_commands`, and in `maybe_flush`, once 256 passes are recorded. The staging belt is finished and recalled around the submit, and queue order keeps it correct.

Measured on an M4 Pro with Metal and wgpu 30.0.1:

| Content | Before | After |
|---|---|---|
| 1,500-sprite SWF | Device lost. With the limit raised to 65536: 266–283 ms/frame, 4,586 buffers outstanding | 94–98 ms/frame, 769 buffers outstanding, byte-identical output |
| AQW `battleon` | Device lost. With the limit raised: 169–189 ms | 77–83 ms |
| 1,500-sprite SWF rendered every 250 ms, with the limit raised | Process footprint grows 27 → 41 → 70 GB within 30 s | Flat at 5.2–5.5 GB |

Frame time drops with the budget until about 512 passes. At 1,024 passes (3,073 buffers) it is 3× slower again, and above 1,365 the device is lost.

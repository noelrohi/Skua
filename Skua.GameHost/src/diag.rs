//! Diagnostics frames (`diag` feature; README.md, "Diagnostics build"). They need the `skua-diag` Ruffle
//! branch plus the patched gc-arena and wgpu-hal in `diag/`, so production never builds them.
//!
//! Engine -> Game Host (replies are 'Q' JSON/text unless noted)
//!   'M' u32 id                     memory stats: movie libraries, GC objects, texture pools, wgpu counters
//!   'G' u32 id                     full GC (replies 'P')
//!   'Y' u32 id                     census: live GC objects by Rust type and AS3 class (send 'G' first)
//!   'Z' u32 id | utf8 class        shortest root path to the oldest live instance of an AS3 class
//!   'B' u32 id | u32 n             render the stage n times back to back, with a GPU wait each time
//!   'V' u32 id | utf8 "key=value"  render knobs (replies 'P'): interval_ms, budget_pct, max_interval_ms,
//!                                  passes, layer_flush, inflight, thread
//! 'Q' stats gain framesRun, passBudgetFlushes, maxOutstandingCmdBufs and the render census (rc).

use crate::{Host, backend, bridge, frame};
use ruffle_render_wgpu::backend as wgpu_backend;
use ruffle_render_wgpu::wgpu;
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

impl Host {
    pub(crate) fn diag(&mut self, id: u32, kind: u8, arg: &[u8]) {
        let reply = |text: String| bridge::send(frame::encode_with_id(b'Q', id, text.as_bytes()));
        let pong = || bridge::send(frame::encode_with_id(b'P', id, &[]));
        match kind {
            b'M' => {
                let mut p = self.player();
                let core = p.skua_mem_stats();
                let render = backend(&mut p).lock().0.skua_mem_stats();
                drop(p);
                reply(format!("{{{core},{render},\"renders\":{}}}", self.stats.renders));
            }
            b'G' => {
                self.player().skua_full_gc();
                pong();
            }
            b'Y' => reply(self.player().skua_census(60)),
            b'Z' => reply(self.player().skua_retainers(&String::from_utf8_lossy(arg), 40)),
            b'B' => {
                let n = arg.get(..4).map_or(1, |b| u32::from_le_bytes(b.try_into().unwrap()));
                reply(self.render_bench(n));
            }
            b'V' => {
                self.knob(&String::from_utf8_lossy(arg));
                pong();
            }
            _ => unreachable!("frame::parse_request only passes diag types"),
        }
    }

    fn render_bench(&mut self, n: u32) -> String {
        let before = wgpu_hal::SKUA_MAX_OUTSTANDING.swap(0, Ordering::Relaxed);
        backend(&mut self.player()).wait_idle();
        let _ = wgpu_backend::skua_render_census();
        let mut times = vec![];
        for _ in 0..n {
            let t0 = Instant::now();
            let mut p = self.player();
            p.render();
            backend(&mut p).wait_idle();
            drop(p);
            let _ = self.descriptors.device.poll(wgpu::PollType::Wait {
                submission_index: None,
                timeout: None,
            });
            times.push(t0.elapsed().as_secs_f64() * 1000.0);
        }
        self.stats.renders += times.len() as u64;
        let peak = wgpu_hal::SKUA_MAX_OUTSTANDING.load(Ordering::Relaxed);
        wgpu_hal::SKUA_MAX_OUTSTANDING.fetch_max(before, Ordering::Relaxed);
        times.sort_by(f64::total_cmp);
        let pct = |q: f64| {
            times
                .get(((times.len() as f64 - 1.0) * q) as usize)
                .copied()
                .unwrap_or(-1.0)
        };
        format!(
            "{{\"n\":{},\"p50Ms\":{:.2},\"p90Ms\":{:.2},\"maxMs\":{:.2},\"peakOutstandingCmdBufs\":{peak},\"rc\":{}}}",
            times.len(),
            pct(0.5),
            pct(0.9),
            pct(1.0),
            wgpu_backend::skua_render_census()
        )
    }

    fn knob(&mut self, kv: &str) {
        let Some((k, v)) = kv.split_once('=') else {
            return tracing::warn!("bad knob {kv}");
        };
        let n: u32 = v.trim().parse().unwrap_or(0);
        let ms = Duration::from_millis(n.into());
        match k.trim() {
            "interval_ms" => self.policy.interval = (n > 0).then_some(ms),
            "budget_pct" => self.policy.budget_pct = n,
            "max_interval_ms" => self.policy.max_interval = ms,
            "passes" => wgpu_backend::set_max_passes_per_submit(n),
            "layer_flush" => wgpu_backend::set_layer_flush(n),
            "inflight" => wgpu_backend::set_max_in_flight(n),
            "thread" => self.render.threaded.store(n != 0, Ordering::Relaxed),
            _ => tracing::warn!("unknown knob {k}"),
        }
    }
}

/// The diagnostics fields of the 'Q' stats (the counters reset when read).
pub fn stats_extra() -> String {
    format!(
        "\"framesRun\":{},\"passBudgetFlushes\":{},\"maxOutstandingCmdBufs\":{},\"rc\":{}",
        ruffle_core::SKUA_FRAMES_RUN.load(Ordering::Relaxed),
        wgpu_backend::SKUA_PASS_BUDGET_FLUSHES.swap(0, Ordering::Relaxed),
        wgpu_hal::SKUA_MAX_OUTSTANDING.swap(0, Ordering::Relaxed),
        wgpu_backend::skua_render_census()
    )
}

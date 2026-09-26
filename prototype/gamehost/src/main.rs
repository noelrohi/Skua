//! PROTOTYPE (#6): throwaway Game Host spike. Not production code.
//!
//! Runs skua.swf in embedded Ruffle, offscreen, and carries the Bridge over
//! stdin/stdout with length-prefixed binary frames:
//!
//!   frame = u32 LE length (type byte + payload) | u8 type | payload
//!
//! Engine -> Game Host
//!   'C' u32 id | utf8 <invoke> XML           synchronous call into AS3
//!   'S' u32 id | u32 max_width (0 = native)  screenshot
//!   'P' u32 id                               ping (transport-only round trip)
//!   'Q' u32 id                               stats (JSON)
//!   'V' u32 id | utf8 "key=value"             #17 render knobs: interval_ms, budget_pct, max_interval_ms, passes
//! Game Host -> Engine
//!   'R' u32 id | utf8 return XML             reply to 'C'
//!   'I' u32 id | u32 w | u32 h | u64 frames | PNG bytes   reply to 'S'
//!   'P' u32 id                               reply to 'P'
//!   'Q' u32 id | utf8 JSON                   reply to 'Q'
//!   'E' utf8 <invoke> XML                    ExternalInterface.call from AS3 (event)
//!   'F' utf8 text                            AS3 trace() / uncaught AS3 error (flash log)
//!   'L' u8 level | utf8 text                 Ruffle/wgpu log line (debug log)
//!   'X' utf8 name                            ExternalInterface.addCallback registered
//!
//! EOF on stdin => exit(0) immediately.

mod proxy;
mod xml;

use ruffle_core::backend::log::LogBackend;
use ruffle_core::backend::navigator::{OwnedFuture, SocketMode};
use ruffle_core::context::UpdateContext;
use ruffle_core::external::{ExternalInterfaceProvider, Value as ExternalValue};
use ruffle_core::{FloatDuration, Player, PlayerBuilder, StageScaleMode};
use ruffle_frontend_utils::backends::navigator::{
    ExternalNavigatorBackend, FutureSpawner, NavigatorInterface,
};
use ruffle_frontend_utils::content::{ContentDescriptor, PlayingContent};

use ruffle_render_wgpu::backend::{WgpuRenderBackend, create_wgpu_instance, request_adapter_and_device};
use ruffle_render_wgpu::descriptors::Descriptors;
use ruffle_render_wgpu::target::TextureTarget;
use ruffle_render_wgpu::wgpu;
use std::any::Any;
use std::collections::HashSet;
use std::io::{Read, Write};
use std::path::Path;
use std::rc::Rc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::util::SubscriberInitExt;
use url::Url;

// ---------------------------------------------------------------- output

static OUT: std::sync::OnceLock<Sender<Vec<u8>>> = std::sync::OnceLock::new();
static EVENTS_SENT: AtomicU64 = AtomicU64::new(0);

fn send_frame(kind: u8, payload: &[u8]) {
    let mut buf = Vec::with_capacity(5 + payload.len());
    buf.extend_from_slice(&((payload.len() + 1) as u32).to_le_bytes());
    buf.push(kind);
    buf.extend_from_slice(payload);
    if let Some(tx) = OUT.get() {
        let _ = tx.send(buf);
    }
}

fn send_with_id(kind: u8, id: u32, rest: &[u8]) {
    let mut p = Vec::with_capacity(4 + rest.len());
    p.extend_from_slice(&id.to_le_bytes());
    p.extend_from_slice(rest);
    send_frame(kind, &p);
}

fn start_writer() {
    let (tx, rx) = mpsc::channel::<Vec<u8>>();
    OUT.set(tx).unwrap();
    std::thread::Builder::new()
        .name("stdout-writer".into())
        .spawn(move || {
            let stdout = std::io::stdout();
            let mut out = std::io::BufWriter::with_capacity(1 << 16, stdout.lock());
            while let Ok(buf) = rx.recv() {
                if out.write_all(&buf).is_err() {
                    std::process::exit(0);
                }
                // Coalesce whatever is queued, then flush once.
                while let Ok(more) = rx.try_recv() {
                    if out.write_all(&more).is_err() {
                        std::process::exit(0);
                    }
                }
                if out.flush().is_err() {
                    std::process::exit(0);
                }
            }
        })
        .unwrap();
}

// ---------------------------------------------------------------- main loop messages

enum Msg {
    Task(async_task::Runnable<()>),
    Call { id: u32, xml: String },
    Screenshot { id: u32, max_width: u32 },
    Ping { id: u32 },
    Stats { id: u32 },
    RenderBench { id: u32, n: u32 },
    Mem { id: u32 },
    FullGc { id: u32 },
    Census { id: u32 },
    Retainers { id: u32, class: String },
    Knob { id: u32, kv: String },
}

fn start_reader(tx: Sender<Msg>) {
    std::thread::Builder::new()
        .name("stdin-reader".into())
        .spawn(move || {
            let stdin = std::io::stdin();
            let mut inp = std::io::BufReader::with_capacity(1 << 16, stdin.lock());
            loop {
                let mut len = [0u8; 4];
                if inp.read_exact(&mut len).is_err() {
                    // EOF: the Engine is gone.
                    std::process::exit(0);
                }
                let len = u32::from_le_bytes(len) as usize;
                let mut body = vec![0u8; len];
                if inp.read_exact(&mut body).is_err() {
                    std::process::exit(0);
                }
                let kind = body[0];
                let id = u32::from_le_bytes(body[1..5].try_into().unwrap());
                let msg = match kind {
                    b'C' => Msg::Call { id, xml: String::from_utf8_lossy(&body[5..]).into_owned() },
                    b'S' => Msg::Screenshot {
                        id,
                        max_width: u32::from_le_bytes(body[5..9].try_into().unwrap()),
                    },
                    b'P' => Msg::Ping { id },
                    b'Q' => Msg::Stats { id },
                    b'B' => Msg::RenderBench { id, n: u32::from_le_bytes(body[5..9].try_into().unwrap()) },
                    b'M' => Msg::Mem { id },
                    b'G' => Msg::FullGc { id },
                    b'Y' => Msg::Census { id },
                    b'V' => Msg::Knob { id, kv: String::from_utf8_lossy(&body[5..]).into_owned() },
                    b'Z' => Msg::Retainers { id, class: String::from_utf8_lossy(&body[5..]).into_owned() },
                    _ => continue,
                };
                if tx.send(msg).is_err() {
                    std::process::exit(0);
                }
            }
        })
        .unwrap();
}

// ---------------------------------------------------------------- Ruffle backends

struct BridgeExternalInterface;

impl ExternalInterfaceProvider for BridgeExternalInterface {
    fn call_method(&self, _ctx: &mut UpdateContext<'_>, name: &str, args: &[ExternalValue]) -> ExternalValue {
        EVENTS_SENT.fetch_add(1, Ordering::Relaxed);
        send_frame(b'E', xml::invoke(name, args).as_bytes());
        // No Game Client -> Engine call uses a return value.
        ExternalValue::Undefined
    }

    fn on_callback_available(&self, name: &str) {
        send_frame(b'X', name.as_bytes());
    }

    fn get_id(&self) -> Option<String> {
        Some("flash".into())
    }
}

struct BridgeLog;

impl LogBackend for BridgeLog {
    fn avm_trace(&self, message: &str) {
        send_frame(b'F', message.as_bytes());
    }
    fn avm_warning(&self, message: &str) {
        send_frame(b'F', format!("[warning] {message}").as_bytes());
    }
}

#[derive(Clone)]
struct HeadlessNavigatorInterface;

impl NavigatorInterface for HeadlessNavigatorInterface {
    fn navigate_to_website(&self, url: Url) {
        tracing::warn!("navigate_to_website ignored: {url}");
    }

    fn open_file(&self, path: &Path) -> impl std::future::Future<Output = std::io::Result<std::fs::File>> + Send {
        let path = path.to_owned();
        async move { std::fs::File::open(path) }
    }

    fn confirm_socket(&self, _host: &str, _port: u16) -> impl std::future::Future<Output = bool> + Send {
        async { true }
    }
}

/// Runs navigator futures on the main thread, like the desktop's WinitExecutor.
struct MainThreadSpawner(Sender<Msg>);

impl<E: std::error::Error + 'static> FutureSpawner<E> for MainThreadSpawner {
    fn spawn(&self, future: OwnedFuture<(), E>) {
        let future = async {
            if let Err(e) = future.await {
                tracing::error!("Async error: {}", e);
            }
        };
        let tx = self.0.clone();
        let schedule = move |r| {
            let _ = tx.send(Msg::Task(r));
        };
        let (runnable, task) = async_task::spawn_local(future, schedule);
        task.detach();
        runnable.schedule();
    }
}

/// Forwards tracing events at WARN and above as 'L' frames (uncaught AS3 errors
/// arrive here as ERROR "... Error in AVM2 ..." and are also sent as 'F').
struct FrameLayer {
    seen: Mutex<std::collections::HashMap<String, (Instant, u64)>>,
}

impl<S: tracing::Subscriber> tracing_subscriber::Layer<S> for FrameLayer {
    fn on_event(&self, event: &tracing::Event<'_>, _ctx: tracing_subscriber::layer::Context<'_, S>) {
        let level = *event.metadata().level();
        if level > tracing::Level::WARN {
            return;
        }
        struct V(String);
        impl tracing::field::Visit for V {
            fn record_debug(&mut self, field: &tracing::field::Field, value: &dyn std::fmt::Debug) {
                if field.name() == "message" {
                    self.0 = format!("{value:?}");
                }
            }
        }
        let mut v = V(String::new());
        event.record(&mut v);
        let mut text = format!("[{}] {}", event.metadata().target(), v.0);
        // Collapse identical lines within 10 s.
        {
            let mut seen = self.seen.lock().unwrap();
            let now = Instant::now();
            match seen.get_mut(&text) {
                Some((first, n)) if now.duration_since(*first) < Duration::from_secs(10) => {
                    *n += 1;
                    return;
                }
                Some((first, n)) => {
                    if *n > 0 {
                        text = format!("{text} (repeated {}x)", *n + 1);
                    }
                    *first = now;
                    *n = 0;
                }
                None => {
                    seen.insert(text.clone(), (now, 0));
                }
            }
        }
        let lvl = if level == tracing::Level::ERROR { 1u8 } else { 2u8 };
        if event.metadata().target() == "ruffle_core::avm2" && level == tracing::Level::ERROR {
            send_frame(b'F', format!("[uncaught] {text}").as_bytes());
        }
        let mut p = vec![lvl];
        p.extend_from_slice(text.as_bytes());
        send_frame(b'L', &p);
    }
}

// ---------------------------------------------------------------- main

/// A panic inside Ruffle poisons the player mutex; keep going with the inner value (prototype).
fn lock(p: &Arc<Mutex<Player>>) -> std::sync::MutexGuard<'_, Player> {
    p.lock().unwrap_or_else(|e| e.into_inner())
}

/// Runs `f`, turning a panic into an 'L' frame instead of killing the Game Host.
fn guarded<R>(what: &str, f: impl FnOnce() -> R) -> Option<R> {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)) {
        Ok(r) => Some(r),
        Err(e) => {
            let msg = e
                .downcast_ref::<String>()
                .cloned()
                .or_else(|| e.downcast_ref::<&str>().map(|s| s.to_string()))
                .unwrap_or_default();
            let mut p = vec![1u8];
            p.extend_from_slice(format!("[panic] recovered in {what}: {msg}").as_bytes());
            send_frame(b'L', &p);
            None
        }
    }
}

struct Opts {
    swf: String,
    show_game: bool,
    render_every_frame: bool,
    render_interval: Option<Duration>,
    /// #17: render at most this % of wall time (0 = off): after a frame that cost C, wait C*100/pct.
    render_budget_pct: u32,
    /// #17: never stretch the interval beyond this.
    render_max_interval: Duration,
    /// #17: run the GPU half of a frame on a render thread.
    render_thread: bool,
}

fn parse_args() -> Opts {
    let mut swf = None;
    let mut show_game = false;
    let mut render_every_frame = false;
    let mut render_interval = None;
    let mut render_budget_pct = 0;
    let mut render_max_interval = Duration::from_secs(30);
    let mut render_thread = false;
    for a in std::env::args().skip(1) {
        if let Some(v) = a.strip_prefix("--render-budget-pct=") {
            render_budget_pct = v.parse().unwrap();
            continue;
        }
        if let Some(v) = a.strip_prefix("--render-max-interval-ms=") {
            render_max_interval = Duration::from_millis(v.parse().unwrap());
            continue;
        }
        if let Some(ms) = a.strip_prefix("--render-interval-ms=") {
            render_interval = Some(Duration::from_millis(ms.parse().unwrap()));
            continue;
        }
        match a.as_str() {
            "--show-game" => show_game = true,
            "--render-every-frame" => render_every_frame = true,
            "--render-thread" => render_thread = true,
            _ => swf = Some(a),
        }
    }
    Opts {
        swf: swf.expect("usage: skua-gamehost [--show-game] [--render-every-frame] [--render-interval-ms=N] [--render-budget-pct=N] [--render-max-interval-ms=N] [--render-thread] <skua.swf>"),
        show_game,
        render_every_frame,
        render_interval,
        render_budget_pct,
        render_max_interval,
        render_thread,
    }
}

const WIDTH: u32 = 958;
const HEIGHT: u32 = 550;

fn px(p: &mut Player) -> &mut proxy::ProxyBackend {
    <dyn Any>::downcast_mut::<proxy::ProxyBackend>(p.renderer_mut()).unwrap()
}

fn png_reply(id: u32, frames: u64, max_width: u32, img: Option<image::RgbaImage>) {
    let (w, h, png) = match img {
        Some(mut img) => {
            if max_width > 0 && img.width() > max_width {
                let nh = (img.height() as f64 * max_width as f64 / img.width() as f64).round() as u32;
                img = image::imageops::resize(&img, max_width, nh, image::imageops::FilterType::Triangle);
            }
            let mut png = Vec::new();
            image::DynamicImage::ImageRgba8(img.clone())
                .write_to(&mut std::io::Cursor::new(&mut png), image::ImageFormat::Png)
                .unwrap();
            (img.width(), img.height(), png)
        }
        None => (0, 0, vec![]),
    };
    let mut rest = Vec::with_capacity(16 + png.len());
    rest.extend_from_slice(&w.to_le_bytes());
    rest.extend_from_slice(&h.to_le_bytes());
    rest.extend_from_slice(&frames.to_le_bytes());
    rest.extend_from_slice(&png);
    send_with_id(b'I', id, &rest);
}

fn main() {
    let opts = parse_args();
    start_writer();
    std::panic::set_hook(Box::new(|info| {
        let bt = std::backtrace::Backtrace::force_capture();
        let text = format!("[panic] {info}\n{bt}");
        eprintln!("{text}");
        let mut p = vec![1u8];
        p.extend_from_slice(text.as_bytes());
        send_frame(b'L', &p);
    }));

    let filter = tracing_subscriber::EnvFilter::builder()
        .parse_lossy(std::env::var("SKUA_GAMEHOST_LOG").as_deref().unwrap_or("warn"));
    tracing_subscriber::registry()
        .with(filter)
        // stderr copy only on request; log lines already travel as 'L' frames. Panics still hit stderr.
        .with(std::env::var("SKUA_GAMEHOST_STDERR").is_ok().then(|| {
            tracing_subscriber::fmt::layer().with_writer(std::io::stderr).with_ansi(false)
        }))
        .with(FrameLayer { seen: Mutex::new(Default::default()) })
        .init();

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
        .unwrap();
    let _guard = runtime.enter();

    let (tx, rx) = mpsc::channel::<Msg>();
    start_reader(tx.clone());

    // Offscreen wgpu (Metal) device.
    let instance = create_wgpu_instance(wgpu::Backends::METAL, wgpu::BackendOptions::default(), None);
    let (adapter, device, queue) = futures::executor::block_on(request_adapter_and_device(
        wgpu::Backends::METAL,
        &instance,
        None,
        wgpu::PowerPreference::LowPower,
    ))
    .expect("no wgpu adapter");
    let descriptors = Arc::new(Descriptors::new(instance, adapter, device, queue));
    let target = TextureTarget::new(&descriptors.device, (WIDTH, HEIGHT)).unwrap();
    let renderer = proxy::ProxyBackend::new(WgpuRenderBackend::new(descriptors.clone(), target).unwrap(), opts.render_thread);
    let rshared = renderer.shared.clone();

    let swf_path = std::fs::canonicalize(&opts.swf).expect("swf path");
    let movie_url = Url::from_file_path(&swf_path).unwrap();
    let content = PlayingContent::DirectFile(ContentDescriptor::new_local(&swf_path, None).unwrap());
    let navigator = ExternalNavigatorBackend::new(
        movie_url.clone(),
        None,
        None,
        MainThreadSpawner(tx.clone()),
        None,
        false,
        HashSet::new(),
        SocketMode::Allow,
        Rc::new(content),
        HeadlessNavigatorInterface,
    );

    let player: Arc<Mutex<Player>> = PlayerBuilder::new()
        .with_renderer(renderer)
        .with_navigator(navigator)
        .with_log(BridgeLog)
        .with_external_interface(Box::new(BridgeExternalInterface))
        .with_autoplay(true)
        .with_scale_mode(StageScaleMode::ShowAll, false)
        .with_viewport_dimensions(WIDTH, HEIGHT, 1.0)
        .with_max_execution_duration(Duration::from_secs(15))
        .with_default_font(true)
        .build();

    player
        .lock()
        .unwrap()
        .fetch_root_movie(movie_url.to_string(), vec![], Box::new(|_| {}));

    let mut window = if opts.show_game {
        let mut w = minifb::Window::new("skua-gamehost (debug)", WIDTH as usize, HEIGHT as usize, minifb::WindowOptions::default())
            .expect("window");
        w.set_target_fps(0);
        Some(w)
    } else {
        None
    };

    // #13: default on (every 30 ticks); SKUA_TRIM_TICKS=0 turns it off.
    let trim_every: Option<u32> = Some(std::env::var("SKUA_TRIM_TICKS").ok().and_then(|v| v.parse().ok()).unwrap_or(30)).filter(|n| *n > 0);
    let mut ticks_since_trim = 0u32;
    let started = Instant::now();
    let mut last_tick = Instant::now();
    let mut ticks: u64 = 0;
    let mut frames_est: f64 = 0.0;
    let mut calls: u64 = 0;
    let mut renders: u64 = 0;
    let mut last_render = Instant::now();
    // Largest gap between two ticks since the last stats request: App Nap / timer throttling shows up here.
    let mut max_tick_gap = Duration::ZERO;
    let mut tick_busy = Duration::ZERO;
    let mut max_tick = Duration::ZERO;
    // #17: render policy state (knobs can change it at runtime) and main-thread render time.
    let mut render_interval = opts.render_interval;
    let mut render_budget_pct = opts.render_budget_pct;
    let mut render_max_interval = opts.render_max_interval;
    let mut cur_gap = Duration::ZERO;
    let (mut prep_sum_us, mut prep_max_us, mut prep_n, mut last_prep_us) = (0u64, 0u64, 0u64, 0u64);
    macro_rules! note_prep {
        ($d:expr) => {{
            let us = $d.as_micros() as u64;
            prep_sum_us += us;
            prep_max_us = prep_max_us.max(us);
            prep_n += 1;
        }};
    }

    // #13: Metal returns autoreleased objects (command buffers, encoders, descriptors). A plain Rust loop
    // never drains an autorelease pool, so drain one per iteration like an AppKit/winit run loop does.
    let use_arp = std::env::var_os("SKUA_NO_ARP").is_none();
    let mut iteration = || {
        let wait = lock(&player).time_til_next_frame().min(Duration::from_millis(33));
        let msg = rx.recv_timeout(wait);
        match msg {
            Ok(Msg::Task(r)) => {
                guarded("task", || r.run());
            }
            Ok(Msg::Call { id, xml: req }) => {
                calls += 1;
                let reply = match xml::parse_invoke(&req) {
                    Ok((name, args)) => {
                        guarded(&name, || xml::value(&lock(&player).call_internal_interface(&name, args)))
                            .unwrap_or_else(|| "<undefined/>".to_string())
                    }
                    Err(e) => {
                        tracing::warn!("bad invoke: {e}");
                        "<undefined/>".to_string()
                    }
                };
                send_with_id(b'R', id, reply.as_bytes());
            }
            Ok(Msg::Screenshot { id, max_width }) => {
                // #17: the capture (GPU wait + readback + PNG) runs after the frame, on the render thread if any.
                let frames = frames_est as u64;
                let t0 = Instant::now();
                let mut p = lock(&player);
                let ok = guarded("screenshot", || p.render()).is_some();
                note_prep!(t0.elapsed());
                renders += 1;
                last_render = Instant::now();
                px(&mut p).after(move |r| png_reply(id, frames, max_width, if ok { r.capture_frame() } else { None }));
            }
            Ok(Msg::Ping { id }) => send_with_id(b'P', id, &[]),
            Ok(Msg::Mem { id }) => {
                // #13: Ruffle-side and wgpu-side memory accounting.
                let mut p = lock(&player);
                let core = p.skua_mem_stats();
                let stats = px(&mut p).lock().0.skua_mem_stats();
                let json = format!("{{{core},{stats},\"renders\":{renders}}}");
                send_with_id(b'Q', id, json.as_bytes());
            }
            Ok(Msg::Census { id }) => {
                // #13: live GC objects by Rust type (needs the patched gc-arena; run after 'G').
                let text = lock(&player).skua_census(60);
                send_with_id(b'Q', id, text.as_bytes());
            }
            Ok(Msg::Retainers { id, class }) => {
                // #13: who keeps the oldest live instance of an AS3 class alive (run after 'G').
                let text = lock(&player).skua_retainers(&class, 40);
                send_with_id(b'Q', id, text.as_bytes());
            }
            Ok(Msg::FullGc { id }) => {
                lock(&player).skua_full_gc();
                send_with_id(b'P', id, &[]);
            }
            Ok(Msg::RenderBench { id, n }) => {
                // Render the live stage n times back to back (with a GPU wait) and report per-frame render time.
                let mut times = vec![];
                let before = wgpu_hal::SKUA_MAX_OUTSTANDING.swap(0, Ordering::Relaxed);
                px(&mut lock(&player)).wait_idle();
                let _ = ruffle_render_wgpu::backend::skua_render_census();
                for _ in 0..n {
                    let t0 = Instant::now();
                    let ok = guarded("render-bench", || {
                        let mut p = lock(&player);
                        p.render();
                        px(&mut p).wait_idle();
                        drop(p);
                        let _ = descriptors.device.poll(wgpu::PollType::Wait { submission_index: None, timeout: None });
                    });
                    if ok.is_none() {
                        break;
                    }
                    times.push(t0.elapsed().as_secs_f64() * 1000.0);
                }
                renders += times.len() as u64;
                let peak = wgpu_hal::SKUA_MAX_OUTSTANDING.load(Ordering::Relaxed);
                wgpu_hal::SKUA_MAX_OUTSTANDING.fetch_max(before, Ordering::Relaxed);
                times.sort_by(|a, b| a.partial_cmp(b).unwrap());
                let pct = |q: f64| times.get(((times.len() as f64 - 1.0) * q) as usize).copied().unwrap_or(-1.0);
                let json = format!("{{\"n\":{},\"p50Ms\":{:.2},\"p90Ms\":{:.2},\"maxMs\":{:.2},\"peakOutstandingCmdBufs\":{},\"rc\":{}}}", times.len(), pct(0.5), pct(0.9), pct(1.0), peak, ruffle_render_wgpu::backend::skua_render_census());
                send_with_id(b'Q', id, json.as_bytes());
            }
            Ok(Msg::Knob { id, kv }) => {
                if let Some((k, v)) = kv.split_once('=') {
                    let n: u64 = v.trim().parse().unwrap_or(0);
                    match k.trim() {
                        "interval_ms" => render_interval = (n > 0).then(|| Duration::from_millis(n)),
                        "budget_pct" => render_budget_pct = n as u32,
                        "max_interval_ms" => render_max_interval = Duration::from_millis(n),
                        "passes" => ruffle_render_wgpu::backend::set_max_passes_per_submit(n as u32),
                        "layer_flush" => ruffle_render_wgpu::backend::set_layer_flush(n as u32),
                        "inflight" => ruffle_render_wgpu::backend::set_max_in_flight(n as u32),
                        "thread" => rshared.threaded.store(n != 0, Ordering::Relaxed),
                        _ => tracing::warn!("unknown knob {k}"),
                    }
                }
                send_with_id(b'P', id, &[]);
            }
            Ok(Msg::Stats { id }) => {
                let rs = &rshared.stats;
                let take = |a: &AtomicU64| a.swap(0, Ordering::Relaxed);
                let frames = take(&rs.frames);
                let (sub_us, sub_max) = (take(&rs.submit_us), take(&rs.submit_max_us));
                let (blk_us, blk_n, blk_max) = (take(&rs.main_blocked_us), take(&rs.main_blocked_n), take(&rs.main_blocked_max_us));
                let (prep_us, prep_max, prep_n) = (std::mem::take(&mut prep_sum_us), std::mem::take(&mut prep_max_us), std::mem::take(&mut prep_n));
                let render_json = format!(
                    "\"submitN\":{frames},\"submitMsAvg\":{:.1},\"submitMsMax\":{:.1},\"submitMsSum\":{:.0},\"prepN\":{prep_n},\"prepMsAvg\":{:.1},\"prepMsMax\":{:.1},\"prepMsSum\":{:.0},\"mainBlockedN\":{blk_n},\"mainBlockedMsSum\":{:.0},\"mainBlockedMsMax\":{:.1},\"renderGapMs\":{},\"threaded\":{},\"rc\":{}",
                    sub_us as f64 / 1000.0 / frames.max(1) as f64,
                    sub_max as f64 / 1000.0,
                    sub_us as f64 / 1000.0,
                    prep_us as f64 / 1000.0 / prep_n.max(1) as f64,
                    prep_max as f64 / 1000.0,
                    prep_us as f64 / 1000.0,
                    blk_us as f64 / 1000.0,
                    blk_max as f64 / 1000.0,
                    cur_gap.as_millis(),
                    rshared.threaded.load(Ordering::Relaxed),
                    ruffle_render_wgpu::backend::skua_render_census()
                );
                let json = format!(
                    "{{{render_json},\"uptimeMs\":{},\"ticks\":{},\"framesEst\":{},\"calls\":{},\"events\":{},\"renders\":{},\"frameRate\":{},\"maxTickGapMs\":{},\"framesRun\":{},\"tickBusyMs\":{},\"maxTickMs\":{},\"maxOutstandingCmdBufs\":{}}}",
                    started.elapsed().as_millis(),
                    ticks,
                    frames_est as u64,
                    calls,
                    EVENTS_SENT.load(Ordering::Relaxed),
                    renders,
                    lock(&player).frame_rate(),
                    max_tick_gap.as_millis(),
                    ruffle_core::SKUA_FRAMES_RUN.load(Ordering::Relaxed),
                    tick_busy.as_millis(),
                    max_tick.as_millis(),
                    wgpu_hal::SKUA_MAX_OUTSTANDING.swap(0, Ordering::Relaxed)
                );
                max_tick_gap = Duration::ZERO;
                max_tick = Duration::ZERO;
                send_with_id(b'Q', id, json.as_bytes());
            }
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => std::process::exit(0),
        }

        let now = Instant::now();
        let dt = now - last_tick;
        if dt > max_tick_gap {
            max_tick_gap = dt;
        }
        // Don't tick after every Bridge message: during a burst of calls that just adds the tick's
        // own cost (sockets, timers, streams) to each round trip. Tick at most every 4 ms.
        if dt >= Duration::from_millis(4) {
            let mut p = lock(&player);
            let fr = p.frame_rate();
            let t0 = Instant::now();
            guarded("tick", || p.tick(FloatDuration::from_std(dt)));
            // Without a per-frame render nothing polls the device, so finished submissions,
            // staging-belt chunks and map callbacks are never reclaimed. Poll without blocking.
            if std::env::var_os("SKUA_POLL_WAIT").is_some() {
                // #13 experiment: backpressure. Block until the GPU has finished everything submitted so far.
                let _ = descriptors.device.poll(wgpu::PollType::Wait { submission_index: None, timeout: None });
            } else if std::env::var_os("SKUA_NO_POLL").is_none() && !rshared.threaded.load(Ordering::Relaxed) {
                // #17: with a render thread, that thread polls; a poll here would wait on wgpu's device
                // lock while the render thread is inside a blocking poll (seen as ~1 s tick stalls).
                let _ = descriptors.device.poll(wgpu::PollType::Poll);
            }
            // #13: a host that isn't rendering still has to bound the offscreen pool and pending work.
            if let Some(n) = trim_every {
                ticks_since_trim += 1;
                if ticks_since_trim >= n {
                    ticks_since_trim = 0;
                    px(&mut p).after(|r| r.skua_trim());
                }
            }
            let spent = t0.elapsed();
            tick_busy += spent;
            max_tick = max_tick.max(spent);
            ticks += 1;
            if fr > 0.0 {
                frames_est += dt.as_secs_f64() * fr;
            }
            last_tick = now;
        }

        // Headless still renders now and then: Ruffle accumulates CPU-side state until a frame is rendered.
        // #17 render budget: after a frame that cost C (main-thread prep + GPU submit), wait at least
        // C*100/budget_pct before the next one (capped at max_interval), and never start a frame while
        // the render thread still has one.
        let interval = if window.is_some() || opts.render_every_frame { Some(Duration::from_millis(33)) } else { render_interval };
        if let Some(i) = interval {
            let mut gap = i;
            if render_budget_pct > 0 {
                let cost_us = last_prep_us + rshared.stats.last_submit_us.load(Ordering::Relaxed);
                let stretched = Duration::from_micros(cost_us * 100 / render_budget_pct as u64);
                gap = gap.max(stretched.min(render_max_interval));
            }
            cur_gap = gap;
            let busy = rshared.busy.load(Ordering::Acquire);
            if last_render.elapsed() >= gap && !busy {
                last_render = Instant::now();
                let t0 = Instant::now();
                let mut p = lock(&player);
                guarded("render", || p.render());
                let prep = t0.elapsed();
                note_prep!(prep);
                last_prep_us = prep.as_micros() as u64;
                renders += 1;
                if let Some(w) = window.as_mut() {
                    let img = px(&mut p).lock().0.capture_frame();
                    drop(p);
                    if let Some(img) = img {
                        let buf: Vec<u32> = img
                            .pixels()
                            .map(|px| ((px[0] as u32) << 16) | ((px[1] as u32) << 8) | px[2] as u32)
                            .collect();
                        let _ = w.update_with_buffer(&buf, img.width() as usize, img.height() as usize);
                    }
                }
            }
        }
    };
    loop {
        if use_arp {
            objc2::rc::autoreleasepool(|_| iteration());
        } else {
            iteration();
        }
    }
}

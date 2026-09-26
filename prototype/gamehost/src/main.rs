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
}

fn parse_args() -> Opts {
    let mut swf = None;
    let mut show_game = false;
    let mut render_every_frame = false;
    for a in std::env::args().skip(1) {
        match a.as_str() {
            "--show-game" => show_game = true,
            "--render-every-frame" => render_every_frame = true,
            _ => swf = Some(a),
        }
    }
    Opts { swf: swf.expect("usage: skua-gamehost [--show-game] [--render-every-frame] <skua.swf>"), show_game, render_every_frame }
}

const WIDTH: u32 = 958;
const HEIGHT: u32 = 550;

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
    let renderer = WgpuRenderBackend::new(descriptors.clone(), target).unwrap();

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

    let started = Instant::now();
    let mut last_tick = Instant::now();
    let mut ticks: u64 = 0;
    let mut frames_est: f64 = 0.0;
    let mut calls: u64 = 0;
    let mut renders: u64 = 0;
    let mut last_render = Instant::now();
    // Largest gap between two ticks since the last stats request: App Nap / timer throttling shows up here.
    let mut max_tick_gap = Duration::ZERO;

    loop {
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
                let img = guarded("screenshot", || {
                    let mut p = lock(&player);
                    p.render();
                    let renderer = <dyn Any>::downcast_mut::<WgpuRenderBackend<TextureTarget>>(p.renderer_mut()).unwrap();
                    renderer.capture_frame()
                })
                .flatten();
                renders += 1;
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
                rest.extend_from_slice(&(frames_est as u64).to_le_bytes());
                rest.extend_from_slice(&png);
                send_with_id(b'I', id, &rest);
            }
            Ok(Msg::Ping { id }) => send_with_id(b'P', id, &[]),
            Ok(Msg::Stats { id }) => {
                let json = format!(
                    "{{\"uptimeMs\":{},\"ticks\":{},\"framesEst\":{},\"calls\":{},\"events\":{},\"renders\":{},\"frameRate\":{},\"maxTickGapMs\":{}}}",
                    started.elapsed().as_millis(),
                    ticks,
                    frames_est as u64,
                    calls,
                    EVENTS_SENT.load(Ordering::Relaxed),
                    renders,
                    lock(&player).frame_rate(),
                    max_tick_gap.as_millis()
                );
                max_tick_gap = Duration::ZERO;
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
        if dt.as_micros() > 0 {
            let mut p = lock(&player);
            let fr = p.frame_rate();
            guarded("tick", || p.tick(FloatDuration::from_std(dt)));
            ticks += 1;
            if fr > 0.0 {
                frames_est += dt.as_secs_f64() * fr;
            }
            last_tick = now;
        }

        let want_render = window.is_some() || opts.render_every_frame;
        if want_render && last_render.elapsed() >= Duration::from_millis(33) {
            last_render = Instant::now();
            let mut p = lock(&player);
            p.render();
            renders += 1;
            if let Some(w) = window.as_mut() {
                let renderer = <dyn Any>::downcast_mut::<WgpuRenderBackend<TextureTarget>>(p.renderer_mut()).unwrap();
                if let Some(img) = renderer.capture_frame() {
                    drop(p);
                    let buf: Vec<u32> = img
                        .pixels()
                        .map(|px| ((px[0] as u32) << 16) | ((px[1] as u32) << 8) | px[2] as u32)
                        .collect();
                    let _ = w.update_with_buffer(&buf, img.width() as usize, img.height() as usize);
                }
            }
        }
    }
}

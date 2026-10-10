//! skua-gamehost: runs skua.swf (the Game Client) in embedded Ruffle, offscreen on Metal, and carries the
//! Bridge to the Engine over stdin/stdout (frame format: `frame.rs`). ADR 0003, ADR 0004.
//!
//! Lifecycle: exits on stdin EOF; aborts on any panic (`bridge.rs`).

mod backends;
mod bridge;
#[cfg(feature = "diag")]
mod diag;
mod frame;
mod frame_buffer;
mod input;
mod opts;
mod render;
mod storage;
mod xml;

use backends::{
    BridgeExternalInterface, BridgeLog, FrameLayer, GameViewUi, HeadlessNavigatorInterface, MainThreadSpawner,
};
use frame::{Request, Viewport};
use frame_buffer::FrameBuffer;
use opts::{Opts, RenderPolicy, USAGE};
use render::ThreadedBackend;
use ruffle_core::backend::navigator::SocketMode;
use ruffle_core::{FloatDuration, Player, PlayerBuilder, StageScaleMode};
use ruffle_frontend_utils::backends::navigator::ExternalNavigatorBackend;
use ruffle_frontend_utils::content::{ContentDescriptor, PlayingContent};
use ruffle_render::backend::ViewportDimensions;
use ruffle_render_wgpu::backend::{
    self as wgpu_backend, WgpuRenderBackend, create_wgpu_instance, request_adapter_and_device,
};
use ruffle_render_wgpu::descriptors::Descriptors;
use ruffle_render_wgpu::target::TextureTarget;
use ruffle_render_wgpu::wgpu;
use std::any::Any;
use std::collections::HashSet;
use std::rc::Rc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};
use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::util::SubscriberInitExt;
use url::Url;

/// The Game Client's stage size: the headless viewport, and the size of every screenshot.
const WIDTH: u32 = 958;
const HEIGHT: u32 = 550;
const NATIVE: ViewportDimensions = ViewportDimensions {
    width: WIDTH,
    height: HEIGHT,
    scale_factor: 1.0,
};
/// During a burst of Bridge calls, tick at most this often: a tick per call only adds the tick's own
/// cost (sockets, timers, streams) to every round trip.
const MIN_TICK_GAP: Duration = Duration::from_millis(4);
/// The longest the loop sleeps waiting for a message.
const MAX_WAIT: Duration = Duration::from_millis(33);

/// Work for the main thread.
pub enum Msg {
    /// A navigator future to poll.
    Task(async_task::Runnable<()>),
    Request(Request),
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|a| a == "--help" || a == "-h") {
        println!("{USAGE}");
        return;
    }
    let opts = Opts::parse(args).unwrap_or_else(|e| fail(2, &format!("{e}\n\n{USAGE}")));
    let swf_path =
        std::fs::canonicalize(&opts.swf).unwrap_or_else(|e| fail(2, &format!("{}: {e}", opts.swf.display())));

    bridge::start_writer();
    bridge::abort_on_panic();
    init_tracing();

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
        .expect("tokio runtime");
    let _guard = runtime.enter();

    // Mapped before the reader starts, so by the time the Engine gets its first ping reply it can
    // unlink the name.
    let frame_buffer = opts
        .frame_buffer
        .as_deref()
        .map(|name| Arc::new(FrameBuffer::open(name).unwrap_or_else(|e| fail(1, &format!("--frame-buffer: {e}")))));

    let (tx, rx) = mpsc::channel::<Msg>();
    let requests = tx.clone();
    bridge::start_reader(move |req| requests.send(Msg::Request(req)).map_err(|_| ()));

    // Headless defaults inside Ruffle's wgpu backend (#14, #13, #17).
    wgpu_backend::set_max_passes_per_submit(opts.pass_budget);
    wgpu_backend::set_max_in_flight(opts.max_in_flight);
    wgpu_backend::set_layer_flush(opts.layer_flush);

    // Offscreen Metal device.
    let instance = create_wgpu_instance(wgpu::Backends::METAL, wgpu::BackendOptions::default(), None);
    let (adapter, device, queue) = futures::executor::block_on(request_adapter_and_device(
        wgpu::Backends::METAL,
        &instance,
        None,
        wgpu::PowerPreference::LowPower,
    ))
    .unwrap_or_else(|e| fail(1, &format!("no Metal device: {e}")));
    let descriptors = Arc::new(Descriptors::new(instance, adapter, device, queue));
    let target = TextureTarget::new(&descriptors.device, (WIDTH, HEIGHT))
        .unwrap_or_else(|e| fail(1, &format!("render target: {e}")));
    let wgpu =
        WgpuRenderBackend::new(descriptors.clone(), target).unwrap_or_else(|e| fail(1, &format!("renderer: {e}")));
    let renderer = ThreadedBackend::new(wgpu, opts.render_thread);
    let render = renderer.shared.clone();

    let movie_url = Url::from_file_path(&swf_path).expect("absolute path");
    let content = ContentDescriptor::new_local(&swf_path, None)
        .unwrap_or_else(|| fail(2, &format!("{}: not a loadable file", swf_path.display())));
    let navigator = ExternalNavigatorBackend::new(
        movie_url.clone(),
        None,
        None,
        MainThreadSpawner(tx.clone()),
        None,
        false,
        HashSet::new(),
        SocketMode::Allow,
        Rc::new(PlayingContent::DirectFile(content)),
        HeadlessNavigatorInterface,
    );

    let builder = PlayerBuilder::new();
    // The Game View's cursor and clipboard; a headless host keeps Ruffle's null UI.
    let builder = match frame_buffer {
        Some(_) => builder.with_ui(GameViewUi::default()),
        None => builder,
    };
    let player = builder
        .with_renderer(renderer)
        .with_navigator(navigator)
        .with_storage(storage::open(opts.storage.as_deref()))
        .with_log(BridgeLog)
        .with_external_interface(Box::new(BridgeExternalInterface {
            panic_on: std::env::var("SKUA_GAMEHOST_PANIC_ON").ok(),
        }))
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

    let window = opts.show_game.then(|| {
        let mut w = minifb::Window::new(
            "skua-gamehost (debug)",
            WIDTH as usize,
            HEIGHT as usize,
            minifb::WindowOptions::default(),
        )
        .unwrap_or_else(|e| fail(1, &format!("--show-game window: {e}")));
        w.set_target_fps(0);
        w
    });

    let now = Instant::now();
    Host {
        player,
        descriptors,
        render,
        policy: opts.render,
        headless_policy: opts.render,
        frame_buffer,
        frames_written: Arc::new(AtomicU64::new(0)),
        live: false,
        viewport: NATIVE,
        input_pending: false,
        window,
        trim_ticks: opts.trim_ticks,
        ticks_since_trim: 0,
        started: now,
        last_tick: now,
        last_render: now,
        last_prep: Duration::ZERO,
        cur_gap: Duration::ZERO,
        stats: LoopStats::default(),
    }
    .run(rx)
}

fn fail(code: i32, msg: &str) -> ! {
    eprintln!("skua-gamehost: {msg}");
    std::process::exit(code)
}

fn init_tracing() {
    let filter = tracing_subscriber::EnvFilter::builder()
        .parse_lossy(std::env::var("SKUA_GAMEHOST_LOG").as_deref().unwrap_or("warn"));
    tracing_subscriber::registry()
        .with(filter)
        // A stderr copy only on request: log lines already travel as 'L' frames.
        .with(std::env::var_os("SKUA_GAMEHOST_STDERR").map(|_| {
            tracing_subscriber::fmt::layer()
                .with_writer(std::io::stderr)
                .with_ansi(false)
        }))
        .with(FrameLayer::default())
        .init();
}

/// Loop counters for the 'Q' stats. The `max_*` and `prep_*` values reset when read.
#[derive(Default)]
struct LoopStats {
    ticks: u64,
    frames_est: f64,
    calls: u64,
    renders: u64,
    /// 'U' events handed to Ruffle.
    input_events: u64,
    /// The largest gap between two ticks: App Nap and timer throttling show up here.
    max_tick_gap: Duration,
    tick_busy: Duration,
    max_tick: Duration,
    /// Main-thread render time (display tree walk; the whole frame when not threaded).
    prep_sum: Duration,
    prep_max: Duration,
    prep_n: u64,
}

struct Host {
    player: Arc<Mutex<Player>>,
    descriptors: Arc<Descriptors>,
    render: Arc<render::Shared>,
    policy: RenderPolicy,
    /// The policy from the command line, which a Game View that stops being live goes back to.
    headless_policy: RenderPolicy,
    frame_buffer: Option<Arc<FrameBuffer>>,
    /// Frames the render thread has written to the Frame Buffer.
    frames_written: Arc<AtomicU64>,
    /// The Game View is live: render every 33 ms and write each frame to the Frame Buffer.
    live: bool,
    /// The viewport Ruffle renders at: the Game View's size while live, else the stage's.
    viewport: ViewportDimensions,
    /// Input arrived since the last tick: the next one doesn't wait out `MIN_TICK_GAP`.
    input_pending: bool,
    window: Option<minifb::Window>,
    /// 0 = off.
    trim_ticks: u32,
    ticks_since_trim: u32,
    started: Instant,
    last_tick: Instant,
    last_render: Instant,
    last_prep: Duration,
    /// The render gap in force, for the stats.
    cur_gap: Duration,
    stats: LoopStats,
}

fn backend(p: &mut Player) -> &mut ThreadedBackend {
    <dyn Any>::downcast_mut::<ThreadedBackend>(p.renderer_mut()).expect("the renderer is a ThreadedBackend")
}

impl Host {
    fn player(&self) -> MutexGuard<'_, Player> {
        self.player.lock().unwrap()
    }

    fn run(mut self, rx: Receiver<Msg>) -> ! {
        loop {
            // Metal returns autoreleased objects (command buffers, encoders, descriptors). A plain Rust loop
            // never drains an autorelease pool, so drain one per iteration like an AppKit run loop does (#13).
            objc2::rc::autoreleasepool(|_| self.iteration(&rx));
        }
    }

    fn iteration(&mut self, rx: &Receiver<Msg>) {
        let mut wait = self.player().time_til_next_frame().min(MAX_WAIT);
        // A live Game View wakes for each render, or it would show a 33 ms cadence at about 25 fps.
        if self.live {
            wait = wait.min(
                self.policy
                    .interval
                    .unwrap_or(MAX_WAIT)
                    .saturating_sub(self.last_render.elapsed()),
            );
        }
        match rx.recv_timeout(wait) {
            Ok(Msg::Task(r)) => {
                r.run();
            }
            Ok(Msg::Request(req)) => self.handle(req),
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => std::process::exit(0),
        }
        self.tick_if_due();
        self.render_if_due();
    }

    fn handle(&mut self, req: Request) {
        match req {
            Request::Call { id, xml } => {
                self.stats.calls += 1;
                let reply = match xml::parse_invoke(&xml) {
                    Ok((name, args)) => xml::value(&self.player().call_internal_interface(&name, args)),
                    Err(e) => {
                        tracing::warn!("bad invoke: {e}");
                        "<undefined/>".to_string()
                    }
                };
                bridge::send(frame::encode_with_id(b'R', id, reply.as_bytes()));
            }
            Request::Screenshot { id, max_width } => {
                let frames = self.stats.frames_est as u64;
                let mut p = self.player.lock().unwrap();
                let t0 = Instant::now();
                p.render();
                // The capture (GPU wait, readback, PNG) runs after the frame, on the render thread if any.
                backend(&mut p).after(move |r| screenshot_reply(id, frames, max_width, r.capture_frame()));
                drop(p);
                self.note_render(t0.elapsed());
            }
            Request::Ping { id } => bridge::send(frame::encode_with_id(b'P', id, &[])),
            Request::Stats { id } => {
                let json = self.stats_json();
                bridge::send(frame::encode_with_id(b'Q', id, json.as_bytes()));
            }
            Request::View { live, viewport } => {
                self.live = live;
                self.policy = if live { opts::LIVE } else { self.headless_policy };
                let max = self.frame_buffer.as_ref().map(|fb| fb.max_size());
                let viewport = view_viewport(live.then_some(viewport).flatten(), max);
                if dims(viewport) != dims(self.viewport) {
                    self.viewport = viewport;
                    self.player().set_viewport_dimensions(viewport);
                }
            }
            Request::Input(input) => {
                self.stats.input_events += 1;
                self.input_pending = true;
                let mut p = self.player();
                match input {
                    input::Input::MouseMove { .. } if !p.mouse_in_stage() => p.set_mouse_in_stage(true),
                    input::Input::MouseLeave => p.set_mouse_in_stage(false),
                    input::Input::Clipboard(ref text) => {
                        if let Some(ui) = <dyn Any>::downcast_mut::<GameViewUi>(p.ui_mut()) {
                            ui.set_mac_clipboard(text.clone());
                        }
                    }
                    _ => {}
                }
                if let Some(event) = input.into_event() {
                    p.handle_event(event);
                }
            }
            #[cfg(feature = "diag")]
            Request::Diag { id, kind, arg } => self.diag(id, kind, &arg),
        }
    }

    fn tick_if_due(&mut self) {
        let now = Instant::now();
        let dt = now - self.last_tick;
        self.stats.max_tick_gap = self.stats.max_tick_gap.max(dt);
        // Input ticks at once, so the Game Client reacts to it without waiting for its next frame.
        if dt < MIN_TICK_GAP && !std::mem::take(&mut self.input_pending) {
            return;
        }
        // Any tick serves pending input.
        self.input_pending = false;
        let threaded = self.render.threaded.load(Ordering::Relaxed);
        let trim = self.trim_ticks > 0 && {
            self.ticks_since_trim += 1;
            self.ticks_since_trim >= self.trim_ticks
        };
        if trim {
            self.ticks_since_trim = 0;
        }
        let mut p = self.player.lock().unwrap();
        let frame_rate = p.frame_rate();
        let t0 = Instant::now();
        p.tick(FloatDuration::from_std(dt));
        // Without a per-frame render nothing polls the device, so finished submissions, staging-belt chunks
        // and map callbacks are never reclaimed. With a render thread that thread polls: a poll here would
        // wait on wgpu's device lock while it is inside a blocking poll (#17: ~1 s tick stalls).
        if !threaded {
            let _ = self.descriptors.device.poll(wgpu::PollType::Poll);
        }
        // A host that isn't rendering still has to bound the offscreen pool and pending work (#13).
        if trim {
            backend(&mut p).after(|r| r.skua_trim());
        }
        drop(p);
        let spent = t0.elapsed();
        let s = &mut self.stats;
        s.tick_busy += spent;
        s.max_tick = s.max_tick.max(spent);
        s.ticks += 1;
        if frame_rate > 0.0 {
            s.frames_est += dt.as_secs_f64() * frame_rate;
        }
        self.last_tick = now;
    }

    /// The keep-alive render: Ruffle accumulates CPU-side state until a frame is rendered, so a headless
    /// host still renders now and then, under the render budget (#17), and never while the render thread
    /// still has a frame.
    fn render_if_due(&mut self) {
        let last_cost =
            self.last_prep + Duration::from_micros(self.render.stats.last_submit_us.load(Ordering::Relaxed));
        let Some(gap) = self.policy.gap(last_cost) else { return };
        self.cur_gap = gap;
        if self.last_render.elapsed() < gap || self.render.busy.load(Ordering::Acquire) {
            return;
        }
        let mut p = self.player.lock().unwrap();
        let t0 = Instant::now();
        p.render();
        let prep = t0.elapsed();
        let image = self
            .window
            .is_some()
            .then(|| backend(&mut p).lock().0.capture_frame())
            .flatten();
        if let (true, Some(fb)) = (self.live, &self.frame_buffer) {
            let (fb, written) = (fb.clone(), self.frames_written.clone());
            backend(&mut p).after(move |r| {
                if write_frame(r, &fb) {
                    written.fetch_add(1, Ordering::Relaxed);
                }
            });
        }
        drop(p);
        self.note_render(prep);
        if let (Some(w), Some(img)) = (self.window.as_mut(), image) {
            let buf: Vec<u32> = img
                .pixels()
                .map(|px| u32::from_be_bytes([0, px[0], px[1], px[2]]))
                .collect();
            let _ = w.update_with_buffer(&buf, img.width() as usize, img.height() as usize);
        }
    }

    fn note_render(&mut self, prep: Duration) {
        self.last_render = Instant::now();
        self.last_prep = prep;
        let s = &mut self.stats;
        s.renders += 1;
        s.prep_sum += prep;
        s.prep_max = s.prep_max.max(prep);
        s.prep_n += 1;
    }

    fn stats_json(&mut self) -> String {
        let take = |a: &AtomicU64| a.swap(0, Ordering::Relaxed);
        let ms = |us: u64| us as f64 / 1000.0;
        let rs = &self.render.stats;
        let submit_n = take(&rs.frames);
        let (submit_us, submit_max_us) = (take(&rs.submit_us), take(&rs.submit_max_us));
        let (blocked_us, blocked_n, blocked_max_us) = (
            take(&rs.main_blocked_us),
            take(&rs.main_blocked_n),
            take(&rs.main_blocked_max_us),
        );
        let frame_rate = self.player().frame_rate();
        let s = &mut self.stats;
        let (prep_us, prep_max_us, prep_n) = (
            std::mem::take(&mut s.prep_sum).as_micros() as u64,
            std::mem::take(&mut s.prep_max).as_micros() as u64,
            std::mem::take(&mut s.prep_n),
        );
        let json = format!(
            concat!(
                "{{\"uptimeMs\":{},\"ticks\":{},\"framesEst\":{},\"frameRate\":{},\"calls\":{},\"events\":{},\"renders\":{},",
                "\"maxTickGapMs\":{},\"tickBusyMs\":{},\"maxTickMs\":{},",
                "\"submitN\":{},\"submitMsAvg\":{:.1},\"submitMsMax\":{:.1},\"submitMsSum\":{:.0},",
                "\"prepN\":{},\"prepMsAvg\":{:.1},\"prepMsMax\":{:.1},\"prepMsSum\":{:.0},",
                "\"mainBlockedN\":{},\"mainBlockedMsSum\":{:.0},\"mainBlockedMsMax\":{:.1},",
                "\"renderGapMs\":{},\"threaded\":{},\"live\":{},\"framesWritten\":{},\"inputEvents\":{},",
                "\"viewportWidth\":{},\"viewportHeight\":{}"
            ),
            self.started.elapsed().as_millis(),
            s.ticks,
            s.frames_est as u64,
            frame_rate,
            s.calls,
            backends::EVENTS_SENT.load(Ordering::Relaxed),
            s.renders,
            std::mem::take(&mut s.max_tick_gap).as_millis(),
            s.tick_busy.as_millis(),
            std::mem::take(&mut s.max_tick).as_millis(),
            submit_n,
            ms(submit_us) / submit_n.max(1) as f64,
            ms(submit_max_us),
            ms(submit_us),
            prep_n,
            ms(prep_us) / prep_n.max(1) as f64,
            ms(prep_max_us),
            ms(prep_us),
            blocked_n,
            ms(blocked_us),
            ms(blocked_max_us),
            self.cur_gap.as_millis(),
            self.render.threaded.load(Ordering::Relaxed),
            self.live,
            self.frames_written.load(Ordering::Relaxed),
            s.input_events,
            self.viewport.width,
            self.viewport.height,
        );
        #[cfg(feature = "diag")]
        let json = format!("{json},{}", diag::stats_extra());
        json + "}"
    }
}

/// Copies the frame just submitted from the target's readback buffer, which `TextureTarget` fills on
/// every submit, into the Frame Buffer. Runs on the render thread after the frame; waits for the GPU.
fn write_frame(r: &mut render::Wgpu, fb: &FrameBuffer) -> bool {
    let target = r.target();
    let Some(info) = &target.buffer else { return false };
    let (buffer, dimensions) = info.buffer.inner();
    let (width, height) = (target.size.width, target.size.height);
    ruffle_render_wgpu::utils::capture_image(r.device(), buffer, dimensions, None, |rgba, stride| {
        fb.write(rgba, width, height, stride as usize, frame_buffer::now_ns())
    })
}

/// The viewport for a 'W': the Game View's size while live, never below the stage's (so screenshots are scaled down,
/// never up) nor above the Frame Buffer's slots; the stage's otherwise.
fn view_viewport(view: Option<Viewport>, max: Option<(u32, u32)>) -> ViewportDimensions {
    match (view, max) {
        (Some(v), Some((max_w, max_h))) => ViewportDimensions {
            width: v.width.clamp(WIDTH, max_w.max(WIDTH)),
            height: v.height.clamp(HEIGHT, max_h.max(HEIGHT)),
            scale_factor: if v.scale.is_finite() && v.scale > 0.0 {
                v.scale
            } else {
                1.0
            },
        },
        _ => NATIVE,
    }
}

fn dims(v: ViewportDimensions) -> (u32, u32, f64) {
    (v.width, v.height, v.scale_factor)
}

/// Sends the 'I' reply: the frame at the stage size, or scaled down to `max_width` if narrower, as PNG (w = h = 0 if none).
fn screenshot_reply(id: u32, frames: u64, max_width: u32, image: Option<image::RgbaImage>) {
    let (width, height, png) = match image.map(|img| encode_screenshot(img, max_width)) {
        Some(Ok(encoded)) => encoded,
        Some(Err(e)) => {
            tracing::error!("screenshot PNG: {e}");
            (0, 0, Vec::new())
        }
        None => {
            tracing::error!("screenshot: the renderer captured no frame");
            (0, 0, Vec::new())
        }
    };
    bridge::send(frame::encode_image(id, width, height, frames, &png));
}

/// The frame as PNG at the stage size, or at `max_width` (0 = native) if narrower, keeping the stage's aspect ratio;
/// with its final size. A frame rendered for a larger Game View is scaled down to it.
fn encode_screenshot(mut img: image::RgbaImage, max_width: u32) -> image::ImageResult<(u32, u32, Vec<u8>)> {
    let (width, height) = if max_width > 0 && max_width < WIDTH {
        let height = (HEIGHT as f64 * max_width as f64 / WIDTH as f64).round().max(1.0) as u32;
        (max_width, height)
    } else {
        (WIDTH, HEIGHT)
    };
    if img.dimensions() != (width, height) {
        img = image::imageops::resize(&img, width, height, image::imageops::FilterType::Triangle);
    }
    let mut png = Vec::new();
    img.write_to(&mut std::io::Cursor::new(&mut png), image::ImageFormat::Png)?;
    Ok((img.width(), img.height(), png))
}

#[cfg(test)]
mod tests {
    use super::{NATIVE, Viewport, dims, encode_screenshot, view_viewport};

    fn stage() -> image::RgbaImage {
        image::RgbaImage::from_pixel(958, 550, image::Rgba([32, 64, 128, 255]))
    }

    /// A frame rendered for a Retina Game View at twice the stage's size.
    fn retina() -> image::RgbaImage {
        image::RgbaImage::from_pixel(1916, 1100, image::Rgba([32, 64, 128, 255]))
    }

    fn decoded_size(png: &[u8]) -> (u32, u32) {
        let img = image::load_from_memory_with_format(png, image::ImageFormat::Png).expect("a PNG");
        (img.width(), img.height())
    }

    #[test]
    fn a_screenshot_keeps_the_native_size_without_max_width() {
        let (w, h, png) = encode_screenshot(stage(), 0).unwrap();
        assert_eq!((w, h), (958, 550));
        assert_eq!(decoded_size(&png), (958, 550));
    }

    #[test]
    fn max_width_scales_a_wider_frame_down_keeping_its_aspect_ratio() {
        let (w, h, png) = encode_screenshot(stage(), 479).unwrap();
        assert_eq!((w, h), (479, 275));
        assert_eq!(decoded_size(&png), (479, 275));
    }

    #[test]
    fn max_width_never_scales_up() {
        let (w, h, _) = encode_screenshot(stage(), 2000).unwrap();
        assert_eq!((w, h), (958, 550));
    }

    #[test]
    fn a_frame_rendered_larger_is_scaled_to_the_stage_size() {
        let (w, h, png) = encode_screenshot(retina(), 0).unwrap();
        assert_eq!((w, h), (958, 550));
        assert_eq!(decoded_size(&png), (958, 550));
        assert_eq!(
            encode_screenshot(retina(), 2000).unwrap().0,
            958,
            "max_width never scales up"
        );
        let (w, h, _) = encode_screenshot(retina(), 479).unwrap();
        assert_eq!((w, h), (479, 275));
        // A viewport a pixel off the stage's aspect ratio still gives the stage's size.
        let odd = image::RgbaImage::from_pixel(1500, 862, image::Rgba([0, 0, 0, 255]));
        let (w, h, _) = encode_screenshot(odd, 0).unwrap();
        assert_eq!((w, h), (958, 550));
    }

    #[test]
    fn the_viewport_follows_a_live_game_view_within_the_stage_and_the_slots() {
        let view = |width, height, scale| Some(Viewport { width, height, scale });
        let max = Some((2874, 1650));
        assert_eq!(dims(view_viewport(view(1916, 1100, 2.0), max)), (1916, 1100, 2.0));
        let v = view_viewport(view(6000, 3444, 2.0), max);
        assert_eq!((v.width, v.height), (2874, 1650), "clamped to the slots");
        let v = view_viewport(view(479, 275, 1.0), max);
        assert_eq!((v.width, v.height), (958, 550), "never below the stage");
        assert_eq!(view_viewport(view(1916, 1100, f64::NAN), max).scale_factor, 1.0);
        assert_eq!(dims(view_viewport(None, max)), dims(NATIVE), "not live: the stage size");
        assert_eq!(
            dims(view_viewport(view(1916, 1100, 2.0), None)),
            dims(NATIVE),
            "no Frame Buffer: headless"
        );
    }

    #[test]
    fn a_tiny_max_width_keeps_at_least_one_row() {
        let (w, h, png) = encode_screenshot(stage(), 1).unwrap();
        assert_eq!((w, h), (1, 1));
        assert_eq!(decoded_size(&png), (1, 1));
    }
}

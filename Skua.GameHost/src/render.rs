//! A `RenderBackend` that owns the real wgpu backend behind a mutex, so the GPU half of a frame
//! (`submit_frame`: encode passes, submit, wait for in-flight work) can run on a render thread while the
//! main thread keeps ticking the player and servicing Bridge calls (#17).
//!
//! Ruffle's `Player::render` has two halves:
//!   (a) walk the display tree and build a `CommandList` (needs `&mut Player`, stays on the main thread);
//!   (b) `renderer.submit_frame(..)` (only needs the backend).
//! When threaded, (b) is handed to the render thread and `Player::render` returns right after (a).
//! Every other backend call from the main thread waits for the queued jobs to be picked up, then takes the
//! backend mutex, so backend operations keep their order.

use ruffle_core::swf::Color;
use ruffle_render::backend::{
    BitmapCacheEntry, Context3D, Context3DProfile, PixelBenderOutput, PixelBenderTarget, RenderBackend,
    RenderMemoryUsage, ShapeHandle, ViewportDimensions,
};
use ruffle_render::bitmap::{Bitmap, BitmapHandle, BitmapSource, PixelRegion, RgbaBufRead, SyncHandle};
use ruffle_render::commands::CommandList;
use ruffle_render::error::Error;
use ruffle_render::filters::Filter;
use ruffle_render::pixel_bender::{PixelBenderShader, PixelBenderShaderHandle};
use ruffle_render::pixel_bender_support::PixelBenderShaderArgument;
use ruffle_render::quality::StageQuality;
use ruffle_render::shape_utils::DistilledShape;
use ruffle_render_wgpu::backend::WgpuRenderBackend;
use ruffle_render_wgpu::target::TextureTarget;
use std::borrow::Cow;
use std::num::NonZeroU32;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::time::{Duration, Instant};

pub type Wgpu = WgpuRenderBackend<TextureTarget>;

/// Ruffle's handles (`BitmapHandle`, `ShapeHandle`) are `Arc<dyn ..>` without `Send`.
// SAFETY: they are only dereferenced inside backend methods, and every backend method runs with the
// backend mutex held, so no two threads touch them at once. Upstream this would be `Send` bounds on the
// handle traits.
pub struct SendBox<T>(pub T);
unsafe impl<T> Send for SendBox<T> {}

/// Work for the render thread, in order.
enum Job {
    Frame(SendBox<(Color, CommandList, Vec<BitmapCacheEntry>)>),
    /// Runs after every earlier job, with the backend locked (screenshots, trims).
    After(Box<dyn FnOnce(&mut Wgpu) + Send>),
}

/// Counters for the 'Q' stats; the sums reset when read.
#[derive(Default)]
pub struct RenderStats {
    /// Frames submitted (either mode) and their total/max `submit_frame` time.
    pub frames: AtomicU64,
    pub submit_us: AtomicU64,
    pub submit_max_us: AtomicU64,
    /// Main-thread time spent waiting for the backend while the render thread held it.
    pub main_blocked_us: AtomicU64,
    pub main_blocked_max_us: AtomicU64,
    pub main_blocked_n: AtomicU64,
    /// The last frame's `submit_frame` time, for the render budget.
    pub last_submit_us: AtomicU64,
}

pub struct Shared {
    inner: Mutex<SendBox<Wgpu>>,
    /// Jobs queued that the render thread hasn't taken the backend for yet.
    queued: Mutex<u64>,
    queued_cv: Condvar,
    /// The render thread has work.
    pub busy: AtomicBool,
    /// Hand frames to the render thread.
    pub threaded: AtomicBool,
    pub stats: RenderStats,
}

impl Shared {
    fn note_submit(&self, d: Duration) {
        let us = d.as_micros() as u64;
        self.stats.frames.fetch_add(1, Ordering::Relaxed);
        self.stats.submit_us.fetch_add(us, Ordering::Relaxed);
        self.stats.last_submit_us.store(us, Ordering::Relaxed);
        self.stats.submit_max_us.fetch_max(us, Ordering::Relaxed);
    }
}

pub struct ThreadedBackend {
    pub shared: Arc<Shared>,
    tx: Sender<Job>,
    viewport: ViewportDimensions,
    name: &'static str,
}

impl ThreadedBackend {
    pub fn new(inner: Wgpu, threaded: bool) -> Self {
        let viewport = inner.viewport_dimensions();
        let name = inner.name();
        let shared = Arc::new(Shared {
            inner: Mutex::new(SendBox(inner)),
            queued: Mutex::new(0),
            queued_cv: Condvar::new(),
            busy: AtomicBool::new(false),
            threaded: AtomicBool::new(threaded),
            stats: RenderStats::default(),
        });
        let (tx, rx) = mpsc::channel::<Job>();
        let sh = shared.clone();
        std::thread::Builder::new()
            .name("render".into())
            .spawn(move || {
                while let Ok(job) = rx.recv() {
                    // Metal objects made on this thread are autoreleased here too.
                    objc2::rc::autoreleasepool(|_| {
                        let mut g = sh.inner.lock().unwrap();
                        {
                            let mut q = sh.queued.lock().unwrap();
                            *q -= 1;
                            sh.queued_cv.notify_all();
                        }
                        match job {
                            Job::Frame(SendBox((clear, commands, cache))) => {
                                let t = Instant::now();
                                g.0.submit_frame(clear, commands, cache);
                                sh.note_submit(t.elapsed());
                            }
                            Job::After(f) => f(&mut g.0),
                        }
                        drop(g);
                        if *sh.queued.lock().unwrap() == 0 {
                            sh.busy.store(false, Ordering::Release);
                        }
                    });
                }
            })
            .expect("render thread");
        Self {
            shared,
            tx,
            viewport,
            name,
        }
    }

    fn threaded(&self) -> bool {
        self.shared.threaded.load(Ordering::Relaxed)
    }

    fn enqueue(&self, job: Job) {
        *self.shared.queued.lock().unwrap() += 1;
        self.shared.busy.store(true, Ordering::Release);
        self.tx.send(job).expect("render thread gone");
    }

    /// Runs `f` on the backend after everything queued so far (inline when not threaded).
    pub fn after(&self, f: impl FnOnce(&mut Wgpu) + Send + 'static) {
        if self.threaded() {
            self.enqueue(Job::After(Box::new(f)));
        } else {
            f(&mut self.lock().0);
        }
    }

    /// The backend, once every queued job has at least started; counts the wait as main-thread blocking.
    pub fn lock(&self) -> MutexGuard<'_, SendBox<Wgpu>> {
        let t = Instant::now();
        {
            let mut q = self.shared.queued.lock().unwrap();
            while *q > 0 {
                q = self.shared.queued_cv.wait(q).unwrap();
            }
        }
        let g = self.shared.inner.lock().unwrap();
        let us = t.elapsed().as_micros() as u64;
        if us >= 100 {
            let s = &self.shared.stats;
            s.main_blocked_us.fetch_add(us, Ordering::Relaxed);
            s.main_blocked_n.fetch_add(1, Ordering::Relaxed);
            s.main_blocked_max_us.fetch_max(us, Ordering::Relaxed);
        }
        g
    }

    /// Waits until the render thread has finished everything queued.
    #[cfg_attr(not(feature = "diag"), allow(dead_code))]
    pub fn wait_idle(&self) {
        drop(self.lock());
    }
}

impl RenderBackend for ThreadedBackend {
    fn viewport_dimensions(&self) -> ViewportDimensions {
        self.viewport
    }
    fn set_viewport_dimensions(&mut self, dimensions: ViewportDimensions) {
        self.viewport = dimensions;
        self.lock().0.set_viewport_dimensions(dimensions)
    }
    fn register_shape(&mut self, shape: DistilledShape, bitmap_source: &dyn BitmapSource) -> ShapeHandle {
        self.lock().0.register_shape(shape, bitmap_source)
    }
    fn register_shape_with_scale(
        &mut self,
        shape: DistilledShape,
        bitmap_source: &dyn BitmapSource,
        scale: f32,
    ) -> ShapeHandle {
        self.lock().0.register_shape_with_scale(shape, bitmap_source, scale)
    }
    fn render_offscreen(
        &mut self,
        handle: BitmapHandle,
        commands: CommandList,
        quality: StageQuality,
        bounds: PixelRegion,
    ) -> Option<Box<dyn SyncHandle>> {
        self.lock().0.render_offscreen(handle, commands, quality, bounds)
    }
    fn apply_filter(
        &mut self,
        source: BitmapHandle,
        source_point: (u32, u32),
        source_size: (u32, u32),
        destination: BitmapHandle,
        dest_point: (i32, i32),
        filter: Filter,
    ) -> Option<Box<dyn SyncHandle>> {
        self.lock()
            .0
            .apply_filter(source, source_point, source_size, destination, dest_point, filter)
    }
    fn is_filter_supported(&self, filter: &Filter) -> bool {
        self.lock().0.is_filter_supported(filter)
    }
    fn is_offscreen_supported(&self) -> bool {
        true
    }
    fn submit_frame(&mut self, clear: Color, commands: CommandList, cache_entries: Vec<BitmapCacheEntry>) {
        if self.threaded() {
            self.enqueue(Job::Frame(SendBox((clear, commands, cache_entries))));
        } else {
            let mut g = self.lock();
            let t = Instant::now();
            g.0.submit_frame(clear, commands, cache_entries);
            self.shared.note_submit(t.elapsed());
        }
    }
    fn create_empty_texture(&mut self, width: NonZeroU32, height: NonZeroU32) -> Result<BitmapHandle, Error> {
        self.lock().0.create_empty_texture(width, height)
    }
    fn register_bitmap(&mut self, bitmap: Bitmap<'_>) -> Result<BitmapHandle, Error> {
        self.lock().0.register_bitmap(bitmap)
    }
    fn update_texture(&mut self, handle: &BitmapHandle, bitmap: Bitmap<'_>, region: PixelRegion) -> Result<(), Error> {
        self.lock().0.update_texture(handle, bitmap, region)
    }
    fn create_context3d(&mut self, profile: Context3DProfile) -> Result<Box<dyn Context3D>, Error> {
        self.lock().0.create_context3d(profile)
    }
    fn debug_info(&self) -> Cow<'static, str> {
        self.lock().0.debug_info()
    }
    fn memory_usage(&self) -> Option<RenderMemoryUsage> {
        self.lock().0.memory_usage()
    }
    fn name(&self) -> &'static str {
        self.name
    }
    fn set_quality(&mut self, quality: StageQuality) {
        self.lock().0.set_quality(quality)
    }
    fn compile_pixelbender_shader(&mut self, shader: PixelBenderShader) -> Result<PixelBenderShaderHandle, Error> {
        self.lock().0.compile_pixelbender_shader(shader)
    }
    fn run_pixelbender_shader(
        &mut self,
        handle: PixelBenderShaderHandle,
        arguments: &[PixelBenderShaderArgument],
        target: &PixelBenderTarget,
    ) -> Result<PixelBenderOutput, Error> {
        self.lock().0.run_pixelbender_shader(handle, arguments, target)
    }
    fn resolve_sync_handle(&mut self, handle: Box<dyn SyncHandle>, with_rgba: RgbaBufRead) -> Result<(), Error> {
        self.lock().0.resolve_sync_handle(handle, with_rgba)
    }
}

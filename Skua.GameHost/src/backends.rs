//! The Ruffle backends the Game Host supplies: ExternalInterface over the Bridge, AS3 logs, a headless
//! navigator, the main-thread future spawner, and Ruffle/wgpu logs as 'L' frames.

use crate::{Msg, bridge, frame, xml};
use ruffle_core::backend::log::LogBackend;
use ruffle_core::backend::navigator::OwnedFuture;
use ruffle_core::context::UpdateContext;
use ruffle_core::external::{ExternalInterfaceProvider, Value};
use ruffle_frontend_utils::backends::navigator::{FutureSpawner, NavigatorInterface};
use std::collections::HashMap;
use std::path::Path;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::Sender;
use std::time::{Duration, Instant};
use url::Url;

/// Events ('E') sent so far.
pub static EVENTS_SENT: AtomicU64 = AtomicU64::new(0);

pub struct BridgeExternalInterface {
    /// Test hook for the lifecycle check (`SKUA_GAMEHOST_PANIC_ON`): panic inside Ruffle's call stack
    /// when AS3 calls `ExternalInterface.call(name)`.
    pub panic_on: Option<String>,
}

impl ExternalInterfaceProvider for BridgeExternalInterface {
    fn call_method(&self, _ctx: &mut UpdateContext<'_>, name: &str, args: &[Value]) -> Value {
        if self.panic_on.as_deref() == Some(name) {
            panic!("injected panic on ExternalInterface.call(\"{name}\")");
        }
        EVENTS_SENT.fetch_add(1, Ordering::Relaxed);
        bridge::send(frame::encode(b'E', xml::invoke(name, args).as_bytes()));
        // No Game Client -> Engine call uses a return value.
        Value::Undefined
    }

    fn on_callback_available(&self, name: &str) {
        bridge::send(frame::encode(b'X', name.as_bytes()));
    }

    fn get_id(&self) -> Option<String> {
        Some("flash".into())
    }
}

pub struct BridgeLog;

impl LogBackend for BridgeLog {
    fn avm_trace(&self, message: &str) {
        bridge::send(frame::encode(b'F', message.as_bytes()));
    }

    fn avm_warning(&self, message: &str) {
        bridge::send(frame::encode(b'F', format!("[warning] {message}").as_bytes()));
    }
}

#[derive(Clone)]
pub struct HeadlessNavigatorInterface;

impl NavigatorInterface for HeadlessNavigatorInterface {
    fn navigate_to_website(&self, url: Url) {
        tracing::warn!("navigate_to_website ignored: {url}");
    }

    fn open_file(&self, path: &Path) -> impl Future<Output = std::io::Result<std::fs::File>> + Send {
        let path = path.to_owned();
        async move { std::fs::File::open(path) }
    }

    /// Sockets need no prompt.
    async fn confirm_socket(&self, _host: &str, _port: u16) -> bool {
        true
    }
}

/// Runs navigator futures on the main thread, like the desktop's winit executor.
pub struct MainThreadSpawner(pub Sender<Msg>);

impl<E: std::error::Error + 'static> FutureSpawner<E> for MainThreadSpawner {
    fn spawn(&self, future: OwnedFuture<(), E>) {
        let future = async {
            if let Err(e) = future.await {
                tracing::error!("Async error: {e}");
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

/// Identical log lines within this window are collapsed into one.
const REPEAT_WINDOW: Duration = Duration::from_secs(10);
/// Past this many remembered lines, forget those whose window has passed.
const MAX_SEEN: usize = 256;

/// Forwards tracing events at WARN and above as 'L' frames, collapsing identical lines within 10 s.
/// Uncaught AS3 errors arrive here as ERROR from `ruffle_core::avm2` and are also sent as 'F'.
#[derive(Default)]
pub struct FrameLayer {
    seen: Mutex<HashMap<String, (Instant, u64)>>,
}

impl<S: tracing::Subscriber> tracing_subscriber::Layer<S> for FrameLayer {
    fn on_event(&self, event: &tracing::Event<'_>, _ctx: tracing_subscriber::layer::Context<'_, S>) {
        let level = *event.metadata().level();
        if level > tracing::Level::WARN {
            return;
        }
        struct Message(String);
        impl tracing::field::Visit for Message {
            fn record_debug(&mut self, field: &tracing::field::Field, value: &dyn std::fmt::Debug) {
                if field.name() == "message" {
                    self.0 = format!("{value:?}");
                }
            }
        }
        let mut msg = Message(String::new());
        event.record(&mut msg);
        let mut text = format!("[{}] {}", event.metadata().target(), msg.0);
        // Every uncaught AS3 error reaches the flash log; only the 'L' copy is collapsed.
        if event.metadata().target() == "ruffle_core::avm2" && level == tracing::Level::ERROR {
            bridge::send(frame::encode(b'F', format!("[uncaught] {text}").as_bytes()));
        }
        {
            let mut seen = self.seen.lock().unwrap();
            let now = Instant::now();
            match seen.get_mut(&text) {
                Some((first, n)) if now.duration_since(*first) < REPEAT_WINDOW => {
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
                    // Lines that vary (URLs, ids) would otherwise pile up for the life of the host.
                    if seen.len() >= MAX_SEEN {
                        seen.retain(|_, (first, _)| now.duration_since(*first) < REPEAT_WINDOW);
                    }
                    seen.insert(text.clone(), (now, 0));
                }
            }
        }
        bridge::log(
            if level == tracing::Level::ERROR {
                frame::LOG_ERROR
            } else {
                frame::LOG_WARN
            },
            &text,
        );
    }
}

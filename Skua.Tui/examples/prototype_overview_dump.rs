//! PROTOTYPE: prints each Overview variant for one account's live Engine. `SKUA_TUI_PROTOTYPE=1 cargo run --example prototype_overview_dump -- <account>`
use std::time::Duration;

use ratatui::Terminal;
use ratatui::backend::TestBackend;
use skua_tui::app::{App, Tab};
use skua_tui::poller::Poller;
use skua_tui::ui::prototype_overview as proto;

fn main() {
    let account = std::env::args().nth(1).unwrap_or_default();
    let dir = std::path::PathBuf::from(std::env::var("HOME").unwrap()).join("Library/Application Support/Skua");
    proto::start(dir.clone());
    let mut app = App::new(dir.clone());
    app.filter = account;
    app.tab = Tab::Overview;
    let mut poller = Poller::new(dir, Duration::from_secs(5));
    app.set_snapshot(poller.poll(&app.focus()));
    std::thread::sleep(Duration::from_secs(6));
    app.set_snapshot(poller.poll(&app.focus()));
    for _ in 0..proto::VARIANTS.len() {
        let mut terminal = Terminal::new(TestBackend::new(150, 40)).unwrap();
        terminal.draw(|frame| skua_tui::ui::draw(frame, &app)).unwrap();
        let buffer = terminal.backend().buffer();
        for y in 0..40 {
            let line: String = (0..150).map(|x| buffer[(x, y)].symbol().to_owned()).collect();
            println!("{}", line.trim_end());
        }
        proto::cycle(1);
    }
}

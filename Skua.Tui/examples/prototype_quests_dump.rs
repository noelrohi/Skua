//! PROTOTYPE: prints each Quests variant for one account's live Engine. `cargo run --example prototype_quests_dump -- <account>`
use std::time::Duration;

use ratatui::Terminal;
use ratatui::backend::TestBackend;
use skua_tui::app::{App, Tab};
use skua_tui::poller::Poller;

fn main() {
    let account = std::env::args().nth(1).unwrap_or_default();
    let dir = std::path::PathBuf::from(std::env::var("HOME").unwrap()).join("Library/Application Support/Skua");
    let mut app = App::new(dir.clone());
    app.filter = account;
    app.tab = Tab::Quests;
    let mut poller = Poller::new(dir, Duration::from_secs(5));
    app.set_snapshot(poller.poll(&app.focus()));
    app.set_snapshot(poller.poll(&app.focus()));
    for _ in 0..skua_tui::ui::prototype_quests::VARIANTS.len() {
        let mut terminal = Terminal::new(TestBackend::new(150, 34)).unwrap();
        terminal.draw(|frame| skua_tui::ui::draw(frame, &app)).unwrap();
        let buffer = terminal.backend().buffer();
        for y in 0..34 {
            let line: String = (0..150).map(|x| buffer[(x, y)].symbol().to_owned()).collect();
            println!("{}", line.trim_end());
        }
        skua_tui::ui::prototype_quests::cycle(1);
    }
}

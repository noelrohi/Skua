use std::io;
use std::sync::mpsc;
use std::time::Duration;

use ratatui::crossterm::event::{self, DisableMouseCapture, EnableMouseCapture, Event};
use ratatui::crossterm::execute;
use skua_tui::actions::{self, Runner};
use skua_tui::app::App;
use skua_tui::chat::Follower;
use skua_tui::discovery;
use skua_tui::poller;

const USAGE: &str = "usage: skua-tui

A terminal UI for the windowless Engines under the Skua data folder (SKUA_DIR, else ~/Library/Application Support/Skua),
with the Skua Manager's accounts by group. It reads the accounts and never changes them.

E starts a windowless Engine as skua auto-starts one: SKUA_ENGINE, else the skua-engine next to the skua on PATH.
H starts the Hook Runner, the skua on PATH's `skua hooks`; skua-tui never runs a hook itself.";

fn main() -> io::Result<()> {
    if let Some(arg) = std::env::args().nth(1) {
        if arg == "-h" || arg == "--help" {
            println!("{USAGE}");
            return Ok(());
        }
        eprintln!("skua-tui: unknown argument '{arg}'\n\n{USAGE}");
        std::process::exit(2);
    }

    let mut app = App::new(discovery::default_skua_dir());
    let (focus_tx, snapshots) = poller::spawn(app.skua_dir.clone(), Duration::from_secs(1));
    let runner = Runner::new(app.skua_dir.clone());
    let mut follower = Follower::new(app.skua_dir.clone());
    let (outcome_tx, outcomes) = mpsc::channel();
    let mut shown = skua_tui::picture::Shown::default();
    let mut terminal = ratatui::init();
    // Clicks and the wheel come to skua-tui; the terminal's own selection still works with option (or shift) held.
    execute!(io::stdout(), EnableMouseCapture)?;
    let result = (|| -> io::Result<()> {
        let mut focus = app.focus();
        while !app.quit {
            if let Some(snapshot) = snapshots.try_iter().last() {
                app.set_snapshot(snapshot);
            }
            for outcome in outcomes.try_iter() {
                app.on_outcome(outcome);
            }
            follower.sync(&mut app);
            if app.focus() != focus {
                focus = app.focus();
                _ = focus_tx.send(focus.clone());
            }
            terminal.draw(|frame| skua_tui::ui::draw(frame, &app))?;
            let place = app.hits.borrow().picture.clone();
            let picture = app
                .snapshot
                .as_ref()
                .and_then(|s| s.detail.picture.as_ref())
                .and_then(|p| p.as_ref().ok());
            shown.sync(
                &mut io::stdout(),
                place
                    .as_ref()
                    .zip(picture)
                    .map(|((engine, frame, area), p)| (engine.as_str(), *frame, p.png.as_str(), *area)),
            )?;
            if event::poll(Duration::from_millis(100))? {
                match event::read()? {
                    Event::Key(key) => app.on_key(key),
                    Event::Mouse(mouse) => app.on_mouse(mouse),
                    _ => {}
                }
            }
            for job in app.jobs.drain(..) {
                actions::spawn(&runner, job, outcome_tx.clone());
            }
        }
        Ok(())
    })();
    _ = shown.sync(&mut io::stdout(), None);
    _ = execute!(io::stdout(), DisableMouseCapture);
    ratatui::restore();
    result
}

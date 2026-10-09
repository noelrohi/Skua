//! What a run has changed in the inventory: the inventory now against the inventory as the run started, from `script status` or its
//! `script.started` event, else as skua-tui first read it.

use std::collections::HashMap;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde_json::Value;

use crate::dto::{HeldItem, Inventory, LogEntry};

/// The event a run starts with, carrying the inventory as it began (`EventTypes.ScriptStarted`); a relogin's restart of the same run sends
/// it again with `restart: true`.
pub const SCRIPT_STARTED: &str = "script.started";
/// What the player held once in game, for a run that started before (`EventTypes.ScriptHeld`): a Script that logs in itself.
pub const SCRIPT_HELD: &str = "script.held";
/// The event a run ends with (`EventTypes.ScriptStopped`).
pub const SCRIPT_STOPPED: &str = "script.stopped";
/// How long an item that just changed stays fresh.
pub const FRESH: Duration = Duration::from_secs(6);
/// How long an item is measured from its first rise before it gives a rate, the 10 minutes the Engine's goal waits by default: a farm
/// loop gains in bursts, so its first minutes swing widely.
pub const RATE_AFTER: Duration = Duration::from_secs(600);
/// A read older than this is too old to tell what just changed against: another tab showed meanwhile.
const RECENT: Duration = Duration::from_secs(5);

/// Now, in epoch ms, as the Engine stamps its entries.
pub fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_millis() as i64)
}

/// The inventory's changes since a run started, as the Overview's Bags shows them.
#[derive(Debug, Clone, Default)]
pub struct Bags {
    start: Option<Start>,
    now: Option<Read>,
    /// When each item's count last changed between two recent reads.
    changed: HashMap<i64, Instant>,
    /// What each item's rate counts from, once it has risen in the run.
    rose: HashMap<i64, Rise>,
}

/// The newest read, by item id, and when it was read.
#[derive(Debug, Clone)]
struct Read {
    at: Instant,
    at_ms: i64,
    items: HashMap<i64, Held>,
}

/// Where an item's rate counts from: the count it held, and when, just before it first rose; epoch ms.
#[derive(Debug, Clone, Copy)]
pub struct Rise {
    pub at_ms: i64,
    pub qty: i64,
}

/// One item as a read held it.
#[derive(Debug, Clone)]
struct Held {
    name: String,
    qty: i64,
    max: i64,
}

/// What the changes count from.
#[derive(Debug, Clone)]
pub struct Start {
    /// The run it is the start of; `None` when it is skua-tui's first read.
    pub run: Option<i64>,
    /// When the run started, else when its inventory was first read; epoch ms.
    pub at_ms: i64,
    /// When the run ended, once its `script.stopped` has been seen; epoch ms.
    pub ended_ms: Option<i64>,
    /// By item id, its name and count; `None` until a read fills it in for a run that started while the player wasn't playing.
    items: Option<HashMap<i64, (String, i64)>>,
}

impl Start {
    /// How long the run has gone, or went.
    pub fn elapsed_sec(&self, now_ms: i64) -> f64 {
        (self.end_ms(now_ms) - self.at_ms) as f64 / 1000.0
    }

    /// When the run ended, else `now_ms`.
    pub fn end_ms(&self, now_ms: i64) -> i64 {
        self.ended_ms.unwrap_or(now_ms)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    /// A stack that rose and isn't full.
    Filling,
    /// A stack that rose to its max.
    Filled,
    /// An item of one that wasn't held.
    New,
    /// Fewer than at the start: spent, sold or banked.
    Spent,
}

/// One item held in another count than at the start.
#[derive(Debug, Clone)]
pub struct Change {
    pub name: String,
    pub start: i64,
    pub now: i64,
    pub max: i64,
    pub kind: Kind,
    /// Whether it changed within `FRESH`.
    pub fresh: bool,
    /// What its rate counts from, once it has risen in the run.
    pub rose: Option<Rise>,
}

impl Change {
    /// How many more than at the start; fewer is negative.
    pub fn delta(&self) -> i64 {
        self.now - self.start
    }

    /// Whether the run held one of it and holds none now: mostly a dud Unidentified turned in at Swindle's.
    pub fn is_single(&self) -> bool {
        self.start == 1 && self.now == 0
    }

    /// Its gain an hour since it first rose, until `end_ms`; `None` until it has been measured for `RATE_AFTER`.
    pub fn per_hour(&self, end_ms: i64) -> Option<f64> {
        let rose = self.rose?;
        let sec = (end_ms - rose.at_ms) as f64 / 1000.0;
        let gain = self.now - rose.qty;
        (gain > 0 && sec >= RATE_AFTER.as_secs_f64()).then(|| gain as f64 * 3600.0 / sec)
    }
}

impl Bags {
    pub fn start(&self) -> Option<&Start> {
        self.start.as_ref()
    }

    /// Whether the count needs run `number`'s start, as `script status` gives it: it counts from another run's, or from no run's.
    pub fn wants_run(&self, number: i64) -> bool {
        self.start.as_ref().is_none_or(|s| s.run != Some(number))
    }

    /// Run `number`'s start, from `script status`: when it started, in epoch ms, and what the player held then, `None` until they were in
    /// game. However long the run has gone, the count starts from it, not from skua-tui's first read.
    pub fn on_run(&mut self, number: i64, at_ms: i64, held: Option<&[HeldItem]>) {
        self.start = Some(Start {
            run: Some(number),
            at_ms,
            ended_ms: None,
            items: held.map(|items| items.iter().map(|i| (i.id, (i.name.clone(), i.qty))).collect()),
        });
        self.changed.clear();
        self.rose.clear();
    }

    /// A run's `script.started` starts the count over, from the inventory it carries, else from the next read; a relogin's restart of
    /// the run being counted leaves the count alone. Its `script.stopped` stops the clock.
    pub fn on_entry(&mut self, entry: &LogEntry) {
        let data = entry.data.as_ref();
        let run = entry.run.or_else(|| data?.get("run")?.as_i64());
        match entry.event_type.as_deref() {
            Some(SCRIPT_STARTED) => {
                // The run being counted, its restart after a relogin included, or a start no newer than the run's: an older run's, read
                // again after selecting the account again.
                if self
                    .start
                    .as_ref()
                    .is_some_and(|s| s.run.is_some() && (s.run == run || entry.ts <= s.at_ms))
                {
                    return;
                }
                self.start = Some(Start {
                    run,
                    at_ms: entry.ts,
                    ended_ms: None,
                    items: held(data),
                });
                self.changed.clear();
                self.rose.clear();
            }
            // A run that started before the player was in game counts, and is timed, from what they held once they were.
            Some(SCRIPT_HELD) => {
                if let Some(start) = self
                    .start
                    .as_mut()
                    .filter(|s| s.run.is_some() && s.run == run && s.items.is_none())
                {
                    start.items = held(data);
                    start.at_ms = entry.ts;
                }
            }
            Some(SCRIPT_STOPPED) => {
                if let Some(start) = self.start.as_mut().filter(|s| s.run.is_some() && s.run == run) {
                    start.ended_ms = Some(entry.ts);
                }
            }
            _ => {}
        }
    }

    /// A read of the inventory at `now_ms`; it fills in a start that has no inventory yet, or is the start when no run's is known.
    pub fn on_inventory(&mut self, inventory: &Inventory, now_ms: i64) {
        let held: HashMap<i64, Held> = inventory
            .items
            .iter()
            .map(|i| {
                let item = Held {
                    name: i.name.clone(),
                    qty: i.qty,
                    max: i.max_stack,
                };
                (i.id, item)
            })
            .collect();
        let counts = || held.iter().map(|(id, h)| (*id, (h.name.clone(), h.qty))).collect();
        match &mut self.start {
            None => {
                self.start = Some(Start {
                    run: None,
                    at_ms: now_ms,
                    ended_ms: None,
                    items: Some(counts()),
                })
            }
            // A run that started while the player wasn't playing counts, and is timed, from this read.
            Some(start) if start.items.is_none() => {
                start.items = Some(counts());
                start.at_ms = now_ms;
            }
            Some(start) => {
                // Against the read before, else, for the run's first read, the run's start. An item missing from it can't tell.
                let (base_ms, base): (i64, HashMap<i64, i64>) = match &self.now {
                    Some(read) if read.at_ms >= start.at_ms => {
                        (read.at_ms, read.items.iter().map(|(id, h)| (*id, h.qty)).collect())
                    }
                    _ => (
                        start.at_ms,
                        start.items.iter().flatten().map(|(id, (_, qty))| (*id, *qty)).collect(),
                    ),
                };
                // Over a longer gap, as while another tab showed, it rose at some time in it: its rate counts from this read.
                let recent = now_ms - base_ms < RECENT.as_millis() as i64;
                for (id, h) in &held {
                    match base.get(id) {
                        Some(&qty) if h.qty > qty && recent => {
                            self.rose.entry(*id).or_insert(Rise { at_ms: base_ms, qty })
                        }
                        Some(&qty) if h.qty > qty => self.rose.entry(*id).or_insert(Rise {
                            at_ms: now_ms,
                            qty: h.qty,
                        }),
                        _ => continue,
                    };
                }
            }
        }
        if let Some(Read { at, items: before, .. }) = &self.now
            && at.elapsed() < RECENT
        {
            let qty = |items: &HashMap<i64, Held>, id: &i64| items.get(id).map(|h| h.qty);
            let changed: Vec<i64> = held
                .keys()
                .chain(before.keys())
                .filter(|id| qty(&held, id) != qty(before, id))
                .copied()
                .collect();
            for id in changed {
                self.changed.insert(id, Instant::now());
            }
        }
        self.now = Some(Read {
            at: Instant::now(),
            at_ms: now_ms,
            items: held,
        });
    }

    /// Every item held in another count than at the start, the most recently changed first.
    pub fn changes(&self) -> Vec<Change> {
        let (Some(start), Some(Read { items: now, .. })) = (&self.start, &self.now) else {
            return Vec::new();
        };
        let Some(began) = &start.items else {
            return Vec::new();
        };
        let mut ids: Vec<i64> = now.keys().chain(began.keys()).copied().collect();
        ids.sort_unstable();
        ids.dedup();
        let mut changes: Vec<(Option<Instant>, Change)> = ids
            .into_iter()
            .filter_map(|id| {
                let (name, qty, max) = match (now.get(&id), began.get(&id)) {
                    (Some(held), _) => (held.name.clone(), held.qty, held.max),
                    (None, Some((name, _))) => (name.clone(), 0, 0),
                    (None, None) => return None,
                };
                let start = began.get(&id).map_or(0, |(_, qty)| *qty);
                let kind = match qty - start {
                    0 => return None,
                    d if d < 0 => Kind::Spent,
                    _ if max <= 1 => Kind::New,
                    _ if qty >= max => Kind::Filled,
                    _ => Kind::Filling,
                };
                let changed = self.changed.get(&id).copied();
                let change = Change {
                    name,
                    start,
                    now: qty,
                    max,
                    kind,
                    fresh: changed.is_some_and(|at| at.elapsed() < FRESH),
                    rose: self.rose.get(&id).copied(),
                };
                Some((changed, change))
            })
            .collect();
        changes.sort_by(|(a, x), (b, y)| b.cmp(a).then(y.delta().cmp(&x.delta())).then(x.name.cmp(&y.name)));
        changes.into_iter().map(|(_, change)| change).collect()
    }
}

/// The inventory an event's `inventory` lists, by item id; `None` when it has none, as while the player wasn't in game.
fn held(data: Option<&Value>) -> Option<HashMap<i64, (String, i64)>> {
    let items = data?.get("inventory")?.as_array()?;
    Some(
        items
            .iter()
            .filter_map(|i| {
                let name = i.get("name")?.as_str()?.to_owned();
                Some((i.get("id")?.as_i64()?, (name, i.get("qty")?.as_i64()?)))
            })
            .collect(),
    )
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    fn inventory(items: &[(i64, &str, i64, i64)]) -> Inventory {
        serde_json::from_value(json!({
            "usedSlots": items.len(),
            "totalSlots": 160,
            "items": items.iter().map(|(id, name, qty, max)| json!({
                "id": id, "name": name, "qty": qty, "maxStack": max, "category": "Item", "equipped": false
            })).collect::<Vec<_>>()
        }))
        .unwrap()
    }

    fn entry(event_type: &str, run: i64, ts: i64, data: Value) -> LogEntry {
        serde_json::from_value(
            json!({ "seq": 1, "ts": ts, "kind": "events", "run": run, "type": event_type, "data": data }),
        )
        .unwrap()
    }

    fn started(run: i64, restart: bool, items: Option<&[(i64, &str, i64)]>) -> LogEntry {
        let inventory = items.map(|items| {
            items
                .iter()
                .map(|(id, name, qty)| json!({ "id": id, "name": name, "qty": qty }))
                .collect::<Vec<_>>()
        });
        entry(
            SCRIPT_STARTED,
            run,
            // A later run starts later.
            1_000_000 + run * 100_000,
            json!({ "run": run, "script": "Nation/Materials/0MaxBags.cs", "restart": restart, "inventory": inventory,
                "temp": [], "bank": null }),
        )
    }

    fn kinds(bags: &Bags) -> Vec<(String, Kind, i64)> {
        let mut changes: Vec<_> = bags
            .changes()
            .into_iter()
            .map(|c| (c.name.clone(), c.kind, c.delta()))
            .collect();
        changes.sort_by(|a, b| a.0.cmp(&b.0));
        changes
    }

    #[test]
    fn counts_from_the_runs_start_what_filled_filled_up_is_new_or_was_spent() {
        let mut bags = Bags::default();
        bags.on_entry(&started(
            4,
            false,
            Some(&[
                (1, "Diamond of Nulgath", 800),
                (2, "Dark Crystal Shard", 990),
                (3, "Unidentified 10", 760),
                (4, "Voucher of Nulgath", 1),
            ]),
        ));
        bags.on_inventory(
            &inventory(&[
                (1, "Diamond of Nulgath", 870, 1000),
                (2, "Dark Crystal Shard", 1000, 1000),
                (3, "Unidentified 10", 752, 1000),
                (5, "Unidentified 16", 1, 1),
            ]),
            2_000_000,
        );

        assert_eq!(bags.start().and_then(|s| s.run), Some(4));
        assert_eq!(
            kinds(&bags),
            vec![
                ("Dark Crystal Shard".into(), Kind::Filled, 10),
                ("Diamond of Nulgath".into(), Kind::Filling, 70),
                ("Unidentified 10".into(), Kind::Spent, -8),
                ("Unidentified 16".into(), Kind::New, 1),
                ("Voucher of Nulgath".into(), Kind::Spent, -1),
            ]
        );
    }

    #[test]
    fn without_a_runs_start_it_counts_from_the_first_read_and_marks_what_changed_since_fresh() {
        let mut bags = Bags::default();
        bags.on_inventory(
            &inventory(&[(1, "Diamond of Nulgath", 800, 1000), (2, "Gem of Nulgath", 5, 1000)]),
            1_000,
        );
        assert!(bags.changes().is_empty());
        assert_eq!(bags.start().map(|s| (s.run, s.at_ms)), Some((None, 1_000)));

        bags.on_inventory(
            &inventory(&[(1, "Diamond of Nulgath", 803, 1000), (2, "Gem of Nulgath", 5, 1000)]),
            2_000,
        );

        let changes = bags.changes();
        assert_eq!(changes.len(), 1);
        assert_eq!(
            (changes[0].name.as_str(), changes[0].delta(), changes[0].fresh),
            ("Diamond of Nulgath", 3, true)
        );
    }

    #[test]
    fn a_new_run_starts_the_count_over_but_a_relogins_restart_of_it_does_not() {
        let mut bags = Bags::default();
        bags.on_entry(&started(1, false, Some(&[(1, "Diamond of Nulgath", 700)])));
        bags.on_inventory(&inventory(&[(1, "Diamond of Nulgath", 800, 1000)]), 2_000_000);

        bags.on_entry(&started(1, true, Some(&[(1, "Diamond of Nulgath", 800)])));
        assert_eq!(kinds(&bags), vec![("Diamond of Nulgath".into(), Kind::Filling, 100)]);

        bags.on_entry(&started(2, false, Some(&[(1, "Diamond of Nulgath", 800)])));
        assert_eq!(bags.start().and_then(|s| s.run), Some(2));
        assert!(bags.changes().is_empty());
    }

    #[test]
    fn the_events_read_again_after_selecting_the_account_again_keep_the_count() {
        let mut bags = Bags::default();
        let older = started(1, false, Some(&[(1, "Diamond of Nulgath", 100)]));
        let mut current = started(2, false, Some(&[(1, "Diamond of Nulgath", 700)]));
        current.ts += 60_000;
        bags.on_entry(&older);
        bags.on_entry(&current);
        bags.on_inventory(&inventory(&[(1, "Diamond of Nulgath", 750, 1000)]), 2_000_000);

        bags.on_entry(&older);
        bags.on_entry(&current);

        assert_eq!(bags.start().and_then(|s| s.run), Some(2));
        assert_eq!(kinds(&bags), vec![("Diamond of Nulgath".into(), Kind::Filling, 50)]);
    }

    #[test]
    fn a_rate_counts_from_the_read_before_the_first_rise_or_after_a_gap_from_the_read_that_saw_it() {
        let mut bags = Bags::default();
        let mut start = started(
            1,
            false,
            Some(&[(1, "Diamond of Nulgath", 100), (2, "Gem of Nulgath", 10)]),
        );
        start.ts = 0;
        bags.on_entry(&start);
        let read = |bags: &mut Bags, diamonds, gems: Option<i64>, at_ms| {
            let mut items = vec![(1, "Diamond of Nulgath", diamonds, 1000)];
            items.extend(gems.map(|g| (2, "Gem of Nulgath", g, 1000)));
            bags.on_inventory(&inventory(&items), at_ms);
        };
        let rose = |bags: &Bags, name: &str| {
            let change = bags.changes().into_iter().find(|c| c.name == name)?;
            change.rose.map(|r| (r.at_ms, r.qty))
        };

        read(&mut bags, 100, Some(10), 1_000);
        read(&mut bags, 120, Some(10), 3_000);
        assert_eq!(rose(&bags, "Diamond of Nulgath"), Some((1_000, 100)));

        // A minute without a read: the Gems rose at some time in it, so their rate counts from the read that saw them higher.
        read(&mut bags, 130, Some(15), 63_000);
        assert_eq!(rose(&bags, "Gem of Nulgath"), Some((63_000, 15)));
        assert_eq!(rose(&bags, "Diamond of Nulgath"), Some((1_000, 100)));
        // 30 Diamonds in the 10 minutes since the read before they rose.
        let diamonds = bags
            .changes()
            .into_iter()
            .find(|c| c.name == "Diamond of Nulgath")
            .unwrap();
        assert_eq!(diamonds.per_hour(601_000), Some(180.0));
        assert_eq!(diamonds.per_hour(600_000), None);
    }

    #[test]
    fn an_item_missing_from_the_read_before_has_not_risen() {
        let mut bags = Bags::default();
        let mut start = started(
            1,
            false,
            Some(&[(1, "Diamond of Nulgath", 100), (2, "Gem of Nulgath", 10)]),
        );
        start.ts = 0;
        bags.on_entry(&start);
        bags.on_inventory(&inventory(&[(1, "Diamond of Nulgath", 100, 1000)]), 1_000);
        bags.on_inventory(
            &inventory(&[(1, "Diamond of Nulgath", 100, 1000), (2, "Gem of Nulgath", 12, 1000)]),
            3_000,
        );
        bags.on_inventory(
            &inventory(&[(1, "Diamond of Nulgath", 100, 1000), (2, "Gem of Nulgath", 14, 1000)]),
            5_000,
        );

        let gems = bags.changes().into_iter().find(|c| c.name == "Gem of Nulgath").unwrap();
        assert_eq!(gems.rose.map(|r| (r.at_ms, r.qty)), Some((3_000, 12)));
    }

    #[test]
    fn a_run_started_while_not_playing_counts_from_the_next_read_and_its_end_stops_the_clock() {
        let mut bags = Bags::default();
        bags.on_entry(&started(3, false, Some(&[(1, "Diamond of Nulgath", 500)])));
        bags.on_entry(&started(4, false, None));
        bags.on_inventory(&inventory(&[(1, "Diamond of Nulgath", 600, 1000)]), 1_500_000);
        assert_eq!(bags.start().map(|s| s.run), Some(Some(4)));
        assert!(
            bags.changes().is_empty(),
            "run 4 counts from this read, not run 3's start"
        );

        bags.on_entry(&entry(SCRIPT_STOPPED, 4, 1_600_000, json!({ "run": 4 })));
        // Timed from the read it counts from, to the run's end.
        assert_eq!(bags.start().map(|s| s.elapsed_sec(9_000_000)), Some(100.0));
    }

    #[test]
    fn a_runs_start_from_script_status_counts_from_its_start_however_long_ago_and_its_events_keep_it() {
        let mut bags = Bags::default();
        assert!(bags.wants_run(4));
        let held = [
            HeldItem {
                id: 1,
                name: "Diamond of Nulgath".into(),
                qty: 800,
            },
            HeldItem {
                id: 2,
                name: "Gem of Nulgath".into(),
                qty: 5,
            },
        ];

        bags.on_run(4, 1_000_000, Some(&held));
        // The same run's script.started, read later from the tail, keeps the count.
        bags.on_entry(&started(4, false, Some(&[(1, "Diamond of Nulgath", 1)])));
        bags.on_inventory(
            &inventory(&[(1, "Diamond of Nulgath", 870, 1000), (2, "Gem of Nulgath", 5, 1000)]),
            9_000_000,
        );

        assert!(!bags.wants_run(4));
        assert!(bags.wants_run(5));
        assert_eq!(bags.start().map(|s| (s.run, s.at_ms)), Some((Some(4), 1_000_000)));
        assert_eq!(kinds(&bags), vec![("Diamond of Nulgath".into(), Kind::Filling, 70)]);
    }

    #[test]
    fn a_run_started_before_the_player_was_in_game_counts_from_its_script_held() {
        let mut bags = Bags::default();
        bags.on_run(4, 1_000_000, None);
        let held = entry(
            SCRIPT_HELD,
            4,
            1_500_000,
            json!({ "run": 4, "script": "Nation/Materials/0MaxBags.cs", "inventory": [{ "id": 1, "name": "Diamond of Nulgath", "qty": 800 }],
                "temp": [], "bank": null }),
        );

        bags.on_entry(&held);
        bags.on_inventory(&inventory(&[(1, "Diamond of Nulgath", 810, 1000)]), 2_000_000);

        assert_eq!(bags.start().map(|s| s.at_ms), Some(1_500_000));
        assert_eq!(kinds(&bags), vec![("Diamond of Nulgath".into(), Kind::Filling, 10)]);
    }
}

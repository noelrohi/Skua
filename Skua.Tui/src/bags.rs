//! What a run has gained: the inventory now against the inventory as the run started, from its `script.started` event, else as
//! skua-tui first read it.

use std::collections::HashMap;
use std::time::{Duration, Instant};

use serde_json::Value;

use crate::dto::{Inventory, LogEntry};

/// How long an item that just changed stays fresh.
pub const FRESH: Duration = Duration::from_secs(6);

#[derive(Debug, Clone, Default)]
pub struct Bags {
    start: Option<Start>,
    /// What the newest read held, by item id: its name, count and max stack.
    now: HashMap<i64, (String, i64, i64)>,
    /// Whether `now` has been read.
    read: bool,
    /// When each item's count last changed between two reads.
    changed: HashMap<i64, Instant>,
}

/// The inventory the gains count from.
#[derive(Debug, Clone)]
pub struct Start {
    /// The run whose `script.started` it came from; `None` when it is skua-tui's first read.
    pub run: Option<i64>,
    /// Epoch ms.
    pub at_ms: i64,
    /// By item id: its name and count.
    items: HashMap<i64, (String, i64)>,
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

#[derive(Debug, Clone)]
pub struct Gain {
    pub name: String,
    pub start: i64,
    pub now: i64,
    pub max: i64,
    pub kind: Kind,
    /// Whether it changed within `FRESH`.
    pub fresh: bool,
}

impl Gain {
    pub fn delta(&self) -> i64 {
        self.now - self.start
    }
}

impl Bags {
    pub fn start(&self) -> Option<&Start> {
        self.start.as_ref()
    }

    /// A `script.started` that carries the inventory starts the count over from it.
    pub fn on_entry(&mut self, entry: &LogEntry) {
        if entry.event_type.as_deref() != Some("script.started") {
            return;
        }
        let Some(items) = entry
            .data
            .as_ref()
            .and_then(|d| d.get("inventory"))
            .and_then(Value::as_array)
        else {
            return;
        };
        let items = items
            .iter()
            .filter_map(|i| {
                let name = i.get("name")?.as_str()?.to_owned();
                Some((i.get("id")?.as_i64()?, (name, i.get("qty")?.as_i64()?)))
            })
            .collect();
        self.start = Some(Start {
            run: entry.run.or_else(|| entry.data.as_ref()?.get("run")?.as_i64()),
            at_ms: entry.ts,
            items,
        });
        self.changed.clear();
    }

    /// A read of the inventory at `now_ms`; the first starts the count when no run's start has.
    pub fn on_inventory(&mut self, inventory: &Inventory, now_ms: i64) {
        let now: HashMap<i64, (String, i64, i64)> = inventory
            .items
            .iter()
            .map(|i| (i.id, (i.name.clone(), i.qty, i.max_stack)))
            .collect();
        if self.start.is_none() {
            self.start = Some(Start {
                run: None,
                at_ms: now_ms,
                items: now
                    .iter()
                    .map(|(id, (name, qty, _))| (*id, (name.clone(), *qty)))
                    .collect(),
            });
        }
        if self.read {
            let ids = now.keys().chain(self.now.keys());
            let changed: Vec<i64> = ids
                .filter(|id| now.get(*id).map(|i| i.1) != self.now.get(*id).map(|i| i.1))
                .copied()
                .collect();
            for id in changed {
                self.changed.insert(id, Instant::now());
            }
        }
        self.now = now;
        self.read = true;
    }

    /// Every item held in another count than at the start, the most recently changed first.
    pub fn gains(&self) -> Vec<Gain> {
        let Some(start) = &self.start else {
            return Vec::new();
        };
        let mut ids: Vec<i64> = self.now.keys().chain(start.items.keys()).copied().collect();
        ids.sort_unstable();
        ids.dedup();
        let mut gains: Vec<(Option<Instant>, Gain)> = ids
            .into_iter()
            .filter_map(|id| {
                let (name, now, max) = match (self.now.get(&id), start.items.get(&id)) {
                    (Some((name, qty, max)), _) => (name.clone(), *qty, *max),
                    (None, Some((name, _))) => (name.clone(), 0, 0),
                    (None, None) => return None,
                };
                let begun = start.items.get(&id).map_or(0, |(_, qty)| *qty);
                let kind = match now - begun {
                    0 => return None,
                    d if d < 0 => Kind::Spent,
                    _ if max <= 1 => Kind::New,
                    _ if now >= max => Kind::Filled,
                    _ => Kind::Filling,
                };
                let changed = self.changed.get(&id).copied();
                Some((
                    changed,
                    Gain {
                        name,
                        start: begun,
                        now,
                        max,
                        kind,
                        fresh: changed.is_some_and(|at| at.elapsed() < FRESH),
                    },
                ))
            })
            .collect();
        gains.sort_by(|(a, x), (b, y)| b.cmp(a).then(y.delta().cmp(&x.delta())).then(x.name.cmp(&y.name)));
        gains.into_iter().map(|(_, gain)| gain).collect()
    }
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

    fn started(run: i64, items: &[(i64, &str, i64)]) -> LogEntry {
        serde_json::from_value(json!({
            "seq": 1, "ts": 1_000_000, "kind": "events", "run": run, "type": "script.started",
            "data": { "run": run, "script": "Nation/Materials/0MaxBags.cs", "restart": false,
                "inventory": items.iter().map(|(id, name, qty)| json!({ "id": id, "name": name, "qty": qty })).collect::<Vec<_>>(),
                "temp": [], "bank": null }
        }))
        .unwrap()
    }

    fn kinds(bags: &Bags) -> Vec<(String, Kind, i64)> {
        let mut gains: Vec<_> = bags
            .gains()
            .into_iter()
            .map(|g| (g.name.clone(), g.kind, g.delta()))
            .collect();
        gains.sort_by(|a, b| a.0.cmp(&b.0));
        gains
    }

    #[test]
    fn counts_from_the_runs_start_what_filled_filled_up_is_new_or_was_spent() {
        let mut bags = Bags::default();
        bags.on_entry(&started(
            4,
            &[
                (1, "Diamond of Nulgath", 800),
                (2, "Dark Crystal Shard", 990),
                (3, "Unidentified 10", 760),
                (4, "Voucher of Nulgath", 1),
            ],
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
        assert!(bags.gains().is_empty());
        assert_eq!(bags.start().map(|s| (s.run, s.at_ms)), Some((None, 1_000)));

        bags.on_inventory(
            &inventory(&[(1, "Diamond of Nulgath", 803, 1000), (2, "Gem of Nulgath", 5, 1000)]),
            2_000,
        );

        let gains = bags.gains();
        assert_eq!(gains.len(), 1);
        assert_eq!(
            (gains[0].name.as_str(), gains[0].delta(), gains[0].fresh),
            ("Diamond of Nulgath", 3, true)
        );
    }

    #[test]
    fn a_new_run_starts_the_count_over() {
        let mut bags = Bags::default();
        bags.on_entry(&started(1, &[(1, "Diamond of Nulgath", 700)]));
        bags.on_inventory(&inventory(&[(1, "Diamond of Nulgath", 800, 1000)]), 2_000_000);
        bags.on_entry(&started(2, &[(1, "Diamond of Nulgath", 800)]));

        assert_eq!(bags.start().and_then(|s| s.run), Some(2));
        assert!(bags.gains().is_empty());
    }
}

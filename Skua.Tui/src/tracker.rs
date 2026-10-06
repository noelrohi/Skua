//! PROTOTYPE: what the inventory gained since the run started, from the run's `script.started` snapshot (or, without one, the first
//! inventory this skua-tui read), and each pickup as it arrives.

use std::collections::HashMap;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use serde_json::Value;

use crate::dto::{Inventory, LogEntry};

/// How many pickups the feed keeps.
const MAX_PICKUPS: usize = 60;

#[derive(Debug, Clone, Default)]
pub struct Tracker {
    pub start: Option<Baseline>,
    /// Newest last.
    pub pickups: Vec<Pickup>,
    /// What the last read held, by item id: name, qty, max stack.
    last: HashMap<i64, (String, i64, i64)>,
    /// When each item's count last changed.
    pub changed: HashMap<i64, Instant>,
}

#[derive(Debug, Clone)]
pub struct Baseline {
    pub run: Option<i64>,
    /// Epoch ms.
    pub at_ms: i64,
    pub qty: HashMap<i64, i64>,
    /// Whether it came from the run's `script.started`, rather than the first read.
    pub from_event: bool,
}

#[derive(Debug, Clone)]
pub struct Pickup {
    pub at_ms: i64,
    pub name: String,
    pub delta: i64,
    pub qty: i64,
    pub max: i64,
}

/// One item's change since the start.
#[derive(Debug, Clone)]
pub struct Gain {
    pub id: i64,
    pub name: String,
    pub start: i64,
    pub now: i64,
    pub max: i64,
    pub changed: Option<Instant>,
}

impl Gain {
    pub fn delta(&self) -> i64 {
        self.now - self.start
    }
}

pub fn now_ms() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_millis() as i64)
}

impl Tracker {
    /// A `script.started` event starts the tracker over from its snapshot.
    pub fn on_event(&mut self, entry: &LogEntry) {
        if entry.event_type.as_deref() != Some("script.started") {
            return;
        }
        let Some(items) = entry.data.as_ref().and_then(|d| d.get("inventory")).and_then(Value::as_array) else {
            return;
        };
        let qty = items
            .iter()
            .filter_map(|i| Some((i.get("id")?.as_i64()?, i.get("qty")?.as_i64()?)))
            .collect();
        self.start = Some(Baseline {
            run: entry.run,
            at_ms: entry.ts,
            qty,
            from_event: true,
        });
        self.pickups.clear();
        self.changed.clear();
    }

    pub fn on_inventory(&mut self, inventory: &Inventory) {
        let now: HashMap<i64, (String, i64, i64)> = inventory
            .items
            .iter()
            .map(|i| (i.id, (i.name.clone(), i.qty, i.max_stack)))
            .collect();
        if self.start.is_none() {
            self.start = Some(Baseline {
                run: None,
                at_ms: now_ms(),
                qty: now.iter().map(|(id, (_, q, _))| (*id, *q)).collect(),
                from_event: false,
            });
        }
        if !self.last.is_empty() {
            let at_ms = now_ms();
            for (id, (name, qty, max)) in &now {
                let before = self.last.get(id).map_or(0, |(_, q, _)| *q);
                if *qty != before {
                    self.changed.insert(*id, Instant::now());
                    self.pickups.push(Pickup {
                        at_ms,
                        name: name.clone(),
                        delta: qty - before,
                        qty: *qty,
                        max: *max,
                    });
                }
            }
            for (id, (name, qty, max)) in &self.last {
                if !now.contains_key(id) {
                    self.changed.insert(*id, Instant::now());
                    self.pickups.push(Pickup {
                        at_ms,
                        name: name.clone(),
                        delta: -qty,
                        qty: 0,
                        max: *max,
                    });
                }
            }
            let excess = self.pickups.len().saturating_sub(MAX_PICKUPS);
            self.pickups.drain(..excess);
        }
        self.last = now;
    }

    /// Every item whose count differs from the start, newest change first.
    pub fn gains(&self) -> Vec<Gain> {
        let Some(start) = &self.start else {
            return Vec::new();
        };
        let mut gains: Vec<Gain> = self
            .last
            .iter()
            .map(|(id, (name, qty, max))| Gain {
                id: *id,
                name: name.clone(),
                start: start.qty.get(id).copied().unwrap_or(0),
                now: *qty,
                max: *max,
                changed: self.changed.get(id).copied(),
            })
            .filter(|g| g.delta() != 0)
            .collect();
        gains.sort_by(|a, b| b.changed.cmp(&a.changed).then(b.delta().cmp(&a.delta())));
        gains
    }

    /// Seconds since the start.
    pub fn elapsed_sec(&self) -> f64 {
        self.start.as_ref().map_or(0.0, |s| (now_ms() - s.at_ms) as f64 / 1000.0)
    }
}

//! The Inventory tab's shelves: the game's item categories gathered into a few, so a full bag splits into tabs that fit on one line.

use crate::dto::Item;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Shelf {
    #[default]
    All,
    Weapons,
    Classes,
    Gear,
    Items,
    QuestItems,
    Other,
}

impl Shelf {
    pub const ALL: [Shelf; 7] = [
        Shelf::All,
        Shelf::Weapons,
        Shelf::Classes,
        Shelf::Gear,
        Shelf::Items,
        Shelf::QuestItems,
        Shelf::Other,
    ];

    pub fn title(self) -> &'static str {
        match self {
            Shelf::All => "All",
            Shelf::Weapons => "Weapons",
            Shelf::Classes => "Classes",
            Shelf::Gear => "Gear",
            Shelf::Items => "Items",
            Shelf::QuestItems => "Quest items",
            Shelf::Other => "Other",
        }
    }

    /// The shelf the game's `category` goes on: every weapon type on Weapons, armour, helms, capes and pets on Gear.
    pub fn of(category: &str) -> Shelf {
        match category {
            "Sword" | "Axe" | "Dagger" | "Gun" | "HandGun" | "Rifle" | "Bow" | "Mace" | "Polearm" | "Staff"
            | "Wand" | "Whip" => Shelf::Weapons,
            "Class" => Shelf::Classes,
            "Armor" | "Helm" | "Cape" | "Pet" | "Necklace" => Shelf::Gear,
            "Item" | "Resource" => Shelf::Items,
            "Quest Item" => Shelf::QuestItems,
            _ => Shelf::Other,
        }
    }

    pub fn holds(self, item: &Item) -> bool {
        self == Shelf::All || Shelf::of(&item.category) == self
    }

    /// The shelves that hold something, with how much: All always, the rest only when they hold an item.
    pub fn counts(items: &[Item]) -> Vec<(Shelf, usize)> {
        Shelf::ALL
            .into_iter()
            .map(|shelf| (shelf, items.iter().filter(|i| shelf.holds(i)).count()))
            .filter(|(shelf, n)| *shelf == Shelf::All || *n > 0)
            .collect()
    }
}

//! Where the Engines and the Skua Manager's accounts are, by the same rules as `Skua.Control`'s `EngineEndpoint` and `ManagerAccounts`.

use std::path::{Path, PathBuf};

use serde::Deserialize;

/// Overrides the Skua data folder, as in `EngineEndpoint.SkuaDirVariable`.
pub const SKUA_DIR_VARIABLE: &str = "SKUA_DIR";

/// The Skua Manager's account list; it never holds a password.
pub const MANAGER_FILE: &str = "Skua.manager.json";

/// `SKUA_DIR`, else `~/Library/Application Support/Skua`.
pub fn default_skua_dir() -> PathBuf {
    match std::env::var_os(SKUA_DIR_VARIABLE) {
        Some(dir) if !dir.is_empty() => std::path::absolute(&dir).unwrap_or_else(|_| PathBuf::from(dir)),
        _ => {
            let home = std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default();
            home.join("Library/Application Support/Skua")
        }
    }
}

/// `[a-z0-9-]{1,16}`, as `EngineName.IsValid` and `Accounts.IsValidName`.
pub fn is_valid_name(name: &str) -> bool {
    (1..=16).contains(&name.len())
        && name
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

pub fn socket_path(skua_dir: &Path, name: &str) -> PathBuf {
    skua_dir.join("engines").join(format!("{name}.sock"))
}

/// The names of the sockets in `<SkuaDIR>/engines`, sorted; an Engine may or may not answer on each.
pub fn engine_names(skua_dir: &Path) -> Vec<String> {
    let Ok(entries) = std::fs::read_dir(skua_dir.join("engines")) else {
        return Vec::new();
    };
    let mut names: Vec<String> = entries
        .filter_map(|e| e.ok())
        .filter_map(|e| e.file_name().to_str()?.strip_suffix(".sock").map(str::to_owned))
        .filter(|name| is_valid_name(name))
        .collect();
    names.sort();
    names
}

/// An account the Skua Manager lists. `name` is also the Engine Name of the app the Manager launches for it.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ManagedAccount {
    pub name: String,
    pub username: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ManagedGroup {
    pub name: String,
    pub usernames: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ManagerAccounts {
    pub accounts: Vec<ManagedAccount>,
    pub groups: Vec<ManagedGroup>,
}

/// Reads the Manager's list, read-only; no file is an empty list.
pub fn load_accounts(skua_dir: &Path) -> Result<ManagerAccounts, String> {
    let path = skua_dir.join(MANAGER_FILE);
    let text = match std::fs::read_to_string(&path) {
        Ok(text) => text,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(ManagerAccounts::default()),
        Err(e) => return Err(format!("Can't read {}: {e}", path.display())),
    };
    let mut accounts: ManagerAccounts =
        serde_json::from_str(&text).map_err(|e| format!("Can't read {}: {e}", path.display()))?;
    accounts
        .accounts
        .retain(|a| is_valid_name(&a.name) && !a.username.is_empty());
    Ok(accounts)
}

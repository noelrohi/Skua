//! Where the Engines and the Skua Manager's accounts are, by the same rules as `Skua.Control`'s `EngineEndpoint` and `ManagerAccounts`.

use std::os::fd::AsRawFd;
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

/// Core's settings, where `client.TestAccountService` names the Active Account's Keychain service.
pub const SETTINGS_FILE: &str = "Skua.settings.json";

/// The Test Account's Keychain service, the setting's default (`AccountSetting.DefaultService`).
const TEST_ACCOUNT_SERVICE: &str = "skua-test-account";

/// A named account's Keychain service is this and its name (`Accounts.ServicePrefix`).
const ACCOUNT_SERVICE_PREFIX: &str = "skua-account-";

/// The Keychain service a windowless Engine's login reads its account from, as `AccountSetting.Read` reads it: the setting, else the
/// Test Account's. Core reads the file's keys ignoring case. Keychain itself is never read.
pub fn active_account_service(skua_dir: &Path) -> String {
    fn get<'a>(value: &'a serde_json::Value, key: &str) -> Option<&'a serde_json::Value> {
        value
            .as_object()?
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(key))
            .map(|(_, v)| v)
    }
    std::fs::read_to_string(skua_dir.join(SETTINGS_FILE))
        .ok()
        .and_then(|text| serde_json::from_str::<serde_json::Value>(&text).ok())
        .and_then(|root| Some(get(get(&root, "client")?, "TestAccountService")?.as_str()?.to_owned()))
        .filter(|service| !service.trim().is_empty())
        .unwrap_or_else(|| TEST_ACCOUNT_SERVICE.into())
}

/// The name of the account stored under `service`, as `Accounts.NameOf`: `test` for the Test Account, None for a service set by hand.
pub fn account_of_service(service: &str) -> Option<String> {
    if service == TEST_ACCOUNT_SERVICE {
        return Some("test".into());
    }
    service
        .strip_prefix(ACCOUNT_SERVICE_PREFIX)
        .filter(|name| is_valid_name(name))
        .map(str::to_owned)
}

/// `[a-z0-9-]{1,16}`, as `EngineName.IsValid` and `Accounts.IsValidName`.
pub fn is_valid_name(name: &str) -> bool {
    (1..=16).contains(&name.len())
        && name
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

/// The lock a Hook Runner holds while it runs (`HookRunner.LockPath`).
pub fn hook_runner_lock(skua_dir: &Path) -> PathBuf {
    skua_dir.join("hooks.lock")
}

/// The folder of Hooks: executables named after the event type each runs for (`HookRunner.HooksDir`).
pub fn hooks_dir(skua_dir: &Path) -> PathBuf {
    skua_dir.join("hooks")
}

/// Whether a Hook Runner (`skua hooks`) runs for this data folder.
pub fn hook_runner_running(skua_dir: &Path) -> bool {
    lock_held(&hook_runner_lock(skua_dir))
}

/// Whether a process holds `lock`: .NET takes it with `flock(LOCK_EX | LOCK_NB)`, as `EngineLock.IsHeld` probes it.
pub fn lock_held(lock: &Path) -> bool {
    let Ok(file) = std::fs::File::open(lock) else {
        return false;
    };
    // SAFETY: flock only acts on the descriptor, which `file` keeps open; closing it releases the probe's lock.
    let taken = unsafe { libc::flock(file.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } == 0;
    !taken && std::io::Error::last_os_error().raw_os_error() == Some(libc::EWOULDBLOCK)
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

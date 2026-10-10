//! Where the game's local SharedObjects live (#250, ADR 0008).

use ruffle_core::backend::storage::{MemoryStorageBackend, StorageBackend};
use ruffle_frontend_utils::backends::storage::DiskStorageBackend;
use std::path::Path;

/// The game's SharedObjects, such as `AQLite_Data` with its Favorites: one `.sol` file each in `folder` (the Engine Name's game storage
/// folder, which the Engine passes as `--storage`), where the next run reads them back; without one, in memory for this run only.
pub fn open(folder: Option<&Path>) -> Box<dyn StorageBackend> {
    match folder {
        Some(folder) => Box::new(DiskStorageBackend::new(folder.to_path_buf())),
        None => Box::new(MemoryStorageBackend::new()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    /// The key Ruffle gives the game's `SharedObject.getLocal("AQLite_Data", "/")` (its Favorites and options): skua.swf is the root
    /// movie, loaded from a file.
    const AQLITE_DATA: &str = "localhost//AQLite_Data";

    struct TempDir(PathBuf);

    impl TempDir {
        fn new(tag: &str) -> TempDir {
            let dir = std::env::temp_dir().join(format!("skua-gamehost-{tag}-{}", std::process::id()));
            let _ = std::fs::remove_dir_all(&dir);
            TempDir(dir)
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn a_shared_object_flushed_in_one_run_is_read_in_the_next() {
        let skua_dir = TempDir::new("next-run");
        let farm = skua_dir.0.join("engines/game-storage/farm");

        assert!(open(Some(&farm)).put(AQLITE_DATA, b"favs"));

        assert_eq!(open(Some(&farm)).get(AQLITE_DATA).as_deref(), Some(&b"favs"[..]));
    }

    #[test]
    fn two_engine_names_keep_separate_stores() {
        let skua_dir = TempDir::new("two-names");
        let farm = skua_dir.0.join("engines/game-storage/farm");
        let alt1 = skua_dir.0.join("engines/game-storage/alt1");

        open(Some(&farm)).put(AQLITE_DATA, b"farm's favs");
        open(Some(&alt1)).put(AQLITE_DATA, b"alt1's favs");

        assert_eq!(open(Some(&farm)).get(AQLITE_DATA).as_deref(), Some(&b"farm's favs"[..]));
        assert_eq!(open(Some(&alt1)).get(AQLITE_DATA).as_deref(), Some(&b"alt1's favs"[..]));
    }

    #[test]
    fn without_a_folder_nothing_outlives_the_run() {
        assert!(open(None).put(AQLITE_DATA, b"favs"));

        assert_eq!(open(None).get(AQLITE_DATA), None);
    }
}

# Engines share the compile cache under a lock file: compiles take it shared, the cleanup exclusively

Every Engine of a data folder compiles Scripts into one cache, `Scripts/Cached-Scripts`, and each Engine is its own process (ADR 0001). Until #269, entries were deleted by whoever happened to touch them. A Scripts update wiped the whole folder whenever it downloaded anything. A compile deleted the other versions of an include, and any entry it couldn't read, including one another process was still writing in place. The cleanup ran at the start of every compile. When a party ran `script start` on four Engines at once, three failed with `Could not find file '.../Cached-Scripts/<key>-GetScrolls.dll'`.

Now the cache works by these rules (`ScriptCompileCache`):

- **Keys, not invalidation.** An entry's name is `<key>-<name>.dll`. An include's key covers its source, the sources of its transitive includes and the Skua build. A Script's key covers its source and the keys of the include entries it compiled against. A change makes a new entry, so nothing has to delete the old one when it happens.
- **Whole writes.** An entry is written to a temp file next to it and renamed into place, so a reader sees the old file or the new one, never part of one. An entry that doesn't read counts as a miss and is compiled again, never deleted.
- **One lock file, `Cached-Scripts/.lock`.** A compile holds it shared from its first include to the Script's own entry. The cleanup (expired, superseded and excess entries, and temp files a crashed process left) runs only if it can take the lock exclusively, without waiting, so only while no process compiles. A compile waits for a running cleanup, for up to 2 minutes. Each process tries the cleanup after a compile ends, at most every 5 minutes.
- **Nothing reads the cache after a compile.** Every include a Script uses is loaded into its load context during the compile, so a running Script never looks an assembly up in the cache, where other versions of it sit and the cleanup may delete any of them.

The lock is a `FileStream`. On Unix, .NET takes `FileShare.None` as an exclusive `flock` and any other `FileShare` as a shared one. On Windows, a handle with `FileShare.None` refuses every other handle, and the reverse. The operating system releases the lock when a process dies, so a crashed Engine never leaves the cache locked.

## Considered Options

- **Keep the wipe, but under the lock.** Rejected: an update would have to wait until no Engine compiles, and every Engine would then recompile everything, as now.
- **A named mutex for the whole cache.** Rejected: a mutex is exclusive. Engines compiling at once would queue behind each other, when only the cleanup needs to be alone. The compiler still takes a named mutex per entry, so two Engines don't compile the same entry at once.
- **Never delete entries.** Rejected: every change to the Scripts adds entries, and the cache would grow without bound.
- **Reference counts or access times per entry.** Rejected: more state shared between processes, and its own races, for no more than the lock gives.

## Consequences

- Superseded entries stay on disk until a cleanup runs while every Engine is idle. That is at most every 5 minutes per process, so the folder briefly holds more than one version of an include after an update.
- A cleanup never waits for compiles, so Engines that compile all the time put it off. It runs at the next quiet moment.
- A change to the key format makes every cached Script compile once again.
- A Script update writes each `.cs` file the same way, through a temp file and a rename, so an Engine compiling while another updates never reads half of one. It can still read some includes from before the update and some from after. That only matters when the Script Source changed in a way the two versions can't compile together.

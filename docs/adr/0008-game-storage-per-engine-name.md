# The Game Host keeps the game's local storage on disk, one store per Engine Name

The game keeps some state only in Flash's local SharedObjects. Since AQW client 5.0 that includes the Favorites (`AQLite_Data`, keyed by character inside), alongside its options, key binds, the ignore list and the login form's remembered name. Until #250 the Mac Game Host built its Ruffle player without storage, so Ruffle used its `MemoryStorageBackend`: every SharedObject was lost when the Game Host exited, and a restart forgot every Favorite.

Now every Engine starts its Game Host with `--storage=<SkuaDIR>/engines/game-storage/<name>`, its Engine Name's folder (`EngineEndpoint.GameStorageDir`). The Game Host hands that folder to Ruffle's own `DiskStorageBackend` (`ruffle_frontend_utils`, already a dependency), which writes one `.sol` file per SharedObject, in Flash's format. No change to the Ruffle fork was needed. skua.swf is the root movie, loaded from a file, and the game opens its SharedObjects with the local path `/`, so `AQLite_Data` is `localhost/AQLite_Data.sol` in that folder, whichever build folder the SWF runs from. The headless `skua-engine` and the Mac App pass it alike. Without `--storage` (the stress suite, a hand-run Game Host) the store stays in memory.

We chose one store per Engine Name because it is the only key the Game Host knows when it starts, and the only one with exactly one writer at a time.

## Considered Options

- **One store per account.** Rejected: the Game Host and its storage start before any login, and the game opens `AQLite_Data` and `AQWUserPref` while it loads, not when someone logs in. An Engine can also log out and into another account without restarting its Game Host. The game already keys the Favorites by character inside the store, so one store holds several characters' Favorites without mixing them. Where the Skua Manager launches an app per account, the Engine Name is the account name (ADR 0006), so in practice the store is per account there.
- **One store per machine, or per data folder** (Ruffle desktop's default, a single shared folder). Rejected: Engines run side by side, and each holds its own copy of a SharedObject in memory and flushes the whole of it. The last Engine to flush would overwrite the others' Favorites. `DiskStorageBackend` truncates a file before it writes it and takes no lock, so one Engine could also read another's half-written store.
- **Ruffle's default folder outside the data folder.** Rejected: test sandboxes and a second `SKUA_DIR` would share it, and it wouldn't go with the data folder when it is moved or deleted.

## Consequences

- One process holds an Engine Name at a time (the flock lock, ADR 0001 and ADR 0006), so each store has one writer.
- A Favorite starred under one Engine Name isn't seen under another, even for the same character. This matches the game, which keeps Favorites per device ("Favorite" in the glossary).
- The store sits under `engines/`, which only the user may enter. If a player ticks the game's own "remember password" on its login form in the Game View, the game saves the password in `AQWUserPref.sol` there, as Flash does on Windows. The Engine's own logins don't go through that form, so they never save one.
- Ruffle's writes aren't atomic. A Game Host killed mid-flush can leave a truncated `.sol`, which the game then reads as empty. Making writes atomic would mean a backend of our own or a Ruffle change.
- Nothing removes the folder of an Engine Name that is no longer used. Deleting a folder resets that Engine Name's game options and Favorites.
- An older `skua-gamehost` rejects `--storage`. The Engine and the Game Host are built and shipped together (ADR 0004), so they never mismatch.

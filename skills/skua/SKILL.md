---
name: skua
description: Drive Skua Engines, which play AQW accounts, with the skua CLI. Use when asked how the Engines or accounts are doing, to run or watch a Script, to farm or unlock something on an account, to set up Butlers, or to check quest or item progress.
---

# Skua

An Engine plays one AQW account and runs Scripts on it; `skua` drives it. The installed binary is the authority on syntax: `skua --help` and `skua <command> --help`. Add `--json` to any command to read its result as JSON.

Every command takes `--engine <name>`, the Engine Name; without it, a command talks to the Engine named `default`. A command auto-starts an Engine where none runs, so a mistyped name starts a new, empty one: `skua --engine <name> engine status` looks without starting.

Paths below are in the data folder, `<SkuaDIR>`: `~/Library/Application Support/Skua` on macOS, or `$SKUA_DIR` when set.

## Engines

- **List**: `skua engine list` shows every Engine in the data folder, in the Mac App or windowless, with its status, and starts none. With `--json` it is an array of `{engine, status}`, `status` null for an Engine that isn't running.
- **Party**: `skua engine list --brief` gives each Engine one line: class, HP, map·cell, target, Script, run time, kills/min and deaths. Watch several accounts with it, one call for all of them; `--json` carries the same fields.
- **Accounts**: an Engine the Skua Manager launched, or the TUI started, is named after its account.
- **Mac App or windowless**: `status` says `in the Skua app` for an Engine in the Mac App, which stops only with its app; `engine stop` refuses it. A windowless one starts with `skua --engine <name> engine start` and stops with `engine stop`. Stopping an Engine stops its Script: ask the developer first.
- **Log in**: `skua --engine <name> login [server] --account <name>` logs that account in. From a shell, `skua login` acts with the developer's rights, so log in only the accounts the developer names; otherwise use the Test Account, `--account test`.
- **Memory**: each logged-in Engine runs its own Game Host. To free memory, `logout` the Engines not needed, then `engine stop` them.
- **Logged out**: the game logs an idle account out after about 40 minutes, and a newer `skua` installed mid-run replaces an idle Engine at its next command, which comes back logged out. When a Script stops, log the account back in before starting the next.
- **Relogin Hook**: offer the developer [`docs/hooks/game.disconnected`](https://github.com/noelrohi/Skua/blob/master/docs/hooks/game.disconnected). Copied into `<SkuaDIR>/hooks/` and made executable, with `skua hooks` running, it logs an account back in when the game logs it out unexpectedly, and leaves it out after `skua logout` or `engine stop`.

## Progress

- **The run**: `skua --engine <name> status --json`. Its `script.run` has `kills`, `killsPerMin`, `deaths`, `questIdleSec` (seconds since a requirement of an accepted quest rose) and `goal` (the quest, the item it buys, the material it farms with its count). A run whose quests idle for 10 minutes with no kills is stuck; one that still kills is grinding a rare drop.
- **Quests**: `skua --engine <name> quests active --json` gives each requirement of the accepted quests its `have` and `qty`, `inBank`, `gainPerHour` and `idleSec` (since its count rose). For a quest not accepted or not loaded, use `eval`.
- **eval**: pass the code on stdin through a quoted heredoc, so item names with apostrophes survive the shell:
  ```sh
  skua --engine <name> eval - <<'EOF'
  return Bot.Quests.HasBeenCompleted(8741);
  EOF
  ```
- **Quest done**: `Bot.Quests.HasBeenCompleted(id)` reads the quest value, as CoreBots' `isCompletedBefore` does. The game's `world.isQuestComplete` reported a finished quest as not done.
- **What a quest needs**: `AcceptRequirements` to accept it, `Requirements` to turn it in:
  ```csharp
  var q = Bot.Quests.EnsureLoad(8741);
  Bot.Bank.Load();
  return q.Requirements.Select(r => $"{r.Name} {Bot.Inventory.GetQuantity(r.ID) + Bot.TempInv.GetQuantity(r.ID) + Bot.Bank.GetQuantity(r.ID)}/{r.Quantity}");
  ```
- **What an unlock chain needs**: read the Script's method for it, which lists every step in order, e.g. `HerosValiance()` in `Enhancement/UnlockForgeEnhancements.cs`. The Scripts are in `<SkuaDIR>/Scripts`; `skua scripts search <words>` finds one.
- **Monsters and drops**: an item's AQW wiki page, `https://aqwwiki.wikidot.com/<item-name>` (lower case, spaces as dashes), names the monster and map that drop it and the quests that use it. Judge drop rates from the Script's log as the run goes.
- **Tough bosses**: `publicRoom: true` or `publicRoom: Core.PublicDifficult` on a Script's `HuntMonster` or `KillMonster` marks a boss the Script expects other players to help with: offer to bring more accounts.

## Running Scripts

- **Start**: `skua --engine <name> script start <path> --option <key>=<value> ...`, with `<path>` in the Script Source, e.g. `Farm/Leveling.cs`. `script options <path>` lists the keys, only while no Script runs on that Engine.
- **Wait**: `skua --engine <name> script wait --timeout 600` returns when the run ends or a Question is pending. `skua --engine <name> dialogs` lists the Questions; `dialogs answer <id> <choice>` answers one.
- **Classes**: CoreBots swaps classes from `<SkuaDIR>/options/CBO_Storage(<username>).txt`, lines such as `SoloClassSelect: <class>` (also `FarmClassSelect`, `DodgeClassSelect`, `BossClassSelect`). A Script reads it as it starts, so edit it while that account's Script is stopped, after a backup.
- **Equip**: the game server can take a few seconds to equip an item, so check the equip again before acting on it. When it hasn't landed 10 s after the request, the Script log says `Equipping <item> failed: it still isn't equipped 10 s after the request (map <map>)`.

## Butlers

`Tools/Butlerv4/Butlerv4.cs` makes an account follow and fight beside a leader, which runs any Script. Its plugin, `LeaderButlerSyncv2.dll`, connects them: `Tools/Butlerv4/DownloadDLL.cs` puts it in `<SkuaDIR>/plugins`, which an Engine loads as it starts. The leader's Script log shows `[LeaderButlerSync] Butler '<name>' connected`.

- **Shared config**: every account reads the same `<SkuaDIR>/options/Butler1.cfg`, so a Butler's `script start --option` rewrites it for all of them. Back it up, then pass both on each Butler's start: `--option Leader1Name=<leader> --option Leader1Butlers=<a>,<b>`, by the accounts' usernames.
- **Drops**: a Butler rejects every drop. When it needs the leader's quest items too, add them on the Butler with `eval`: `Bot.Drops.Add("<item>"); Bot.Drops.Pickup("<item>");`.

## Watching a run

- **Follow**: `skua --engine <name> logs script -f --tail 20` prints the newest 20 lines, then follows, like `tail -f -n 20`. When a follow ends, start it again.
- **Filter**: CoreBots' lines read `[HH:mm:ss] (<method>) <message>`: `Killing <monster> for item`, `Farming <item> (n/m)`, `Enhancement Unlocked`, `is now Rank 10`, and `Script ran for` at the end. Match errors as a whole word, `\b[Ee]rror\b`, since monster names contain "Terror".
- **Deaths**: `deaths` in `status --json` counts the run's; `skua --engine <name> logs events` has each `player.death`. A death per kill means the account is under-geared for that boss: offer to bring more accounts.

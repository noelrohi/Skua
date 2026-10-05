---
name: skua
description: Drive Skua Engines, which play AQW accounts, with the skua CLI. Use when asked how the Engines or accounts are doing, to run or watch a Script, to farm or unlock something on an account, to set up Butlers, to check quest or item progress, or to test an Ultra Comp.
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
- **Quests**: `skua --engine <name> quests active --json` gives each requirement of the accepted quests its `have` and `qty`, `inBank`, `gainPerHour` and `idleSec` (since its count rose). A quest's `status` is `completable` when it is ready to turn in; a daily, weekly or monthly quest has `repeat` and `repeatDone` (turned in since the game reset it); `lastRejection` is the game server's reason for refusing its last turn-in. For a quest not accepted or not loaded, use `eval`.
- **Turn-ins**: `skua --engine <name> quests complete <id>` turns a quest in and prints the game server's answer, exiting with 1 when it refuses. Every turn-in, a Script's too, is a `quest.completed` or `quest.rejected` event (`{id, name, reason}`), and a refusal is also a Script log line.
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
- **Script Reports**: a Script that calls `Bot.Report(name, data)` records a structured result as a `script.report` event, `{run, script, name, data}` with `data` as JSON (`{error}` when it couldn't be serialized; a string cut to 64 KB when over it), plus a `[report] <name>` Script log line. Follow them with `skua --engine <name> logs events -f` and read `data` rather than scraping log lines. `run` and `script` are null for a report from `eval`.
- **Deaths**: `deaths` in `status --json` counts the run's; `skua --engine <name> logs events` has each `player.death`. A death per kill means the account is under-geared for that boss: offer to bring more accounts.

## Testing a Comp

A **Comp** is the developer's named strategy for one Ultra: 4 classes, each with its **Role**, its **Loadout** (enhancements, potions, scroll) and when it taunts. The **Party Layout** says which account plays which class. An **Attempt** is one fight, from engaging the boss to its kill or a **Wipe**, the whole party dead. The [Ultras v3 Playbook](https://github.com/noelrohi/Scripts/blob/Skua/docs/Skua/Ultras%20v3%20Playbook.md) in the Scripts fork has each boss's Comps and results table. To test a Comp:

1. **As written**: run the Comp exactly as the developer wrote it. A tweak worth trying goes in the result's note; the Comp's code stays as it is.
2. **Potions**: the Script buys each Loadout's potions, except Unstable Malevolence Elixir, which the potion buyer can't make. Check with `eval` that the account playing a class whose Loadout has it holds some.
3. **Options**: the boss's `<Boss> comp` and `<Boss> layout` options, e.g. `UltraNulgathComp=dot-lr-ap-loo` and `UltraNulgathLayout=<account>=<class>; ...`, are `DoAllUltras` options that a boss Script reads. No command sets them: while no `DoAllUltras` runs, back up `<SkuaDIR>/options/DoAllUltras.cfg` and set its `Options:UltraNulgathComp=` and `Options:UltraNulgathLayout=` lines. All four Engines read that one file. A kill spends the boss's quest for its period: before any test of a weekly boss, warn the developer and wait for their go-ahead.
4. **Start and follow**: on each of the 4 Engines, `skua --engine <name> script start Ultrasv3/IndividualUltras/<boss script>`, e.g. `6UltraNulgathv3.cs`, and follow its `logs events -f` for the `script.report` events named `ultra.attempt`. Their `data` is `{boss, comp, class, role, startedAt, endedAt, outcome, bossHp, bossMaxHp, deaths: [{atSec}]}`, `outcome` being `kill`, `wipe` or `stopped`. A Script Report names no account; the Engine it came from does. Merge the four accounts' reports into one Attempt by boss, Comp and `startedAt`.
5. **Stop**: at the first `kill`, or the session's third `wipe`, `skua --engine <name> script stop` on every Engine. A boss Script refights after a Wipe on its own.
6. **Results**: add one row per Attempt to the boss's results table in the playbook: date, Comp, Party Layout as `alt1=<class>; ...`, outcome, duration (`endedAt` minus `startedAt`), deaths as `<Role> @ <atSec>`, and a note. The playbook is public: write the accounts as `alt1`…`alt4`, the same alt for the same account in every row.

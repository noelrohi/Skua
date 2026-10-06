# Commands that need no Engine start none; skua runs the Script Source code in its own process

`skua scripts search|list|update|new|source` and `skua scripts check` (#215) run in the `skua` process against the data folder, through the Engine's own code (`Skua.Engine`'s `DataFolderScripts` and `ScriptCheck`), so they start no Engine. `status` connects only to an Engine that runs, and otherwise fails with `EngineUnavailable`, saying how to start it. `engine list` and `account` already started none. Every other command drives an Engine and keeps auto-starting it. This amends ADR 0001, under which every CLI command was a client of an Engine it auto-started.

- **Refusals.** An update or a change of the Script Source is refused while any Engine of the data folder runs a Script, since they all share its Scripts; an Engine refused only while its own Script ran. An Engine whose `status` doesn't answer in time is let be. The Engine keeps its `scripts_*` operations for MCP, the Mac App and `script start`'s update, with its own Scripts slot.
- **`scripts update --verify`.** A plain update stays incremental: when the Script Source's head is the synced commit it makes one request and downloads nothing. `--verify` also hashes every local Script against `scripts.json` and downloads each one that differs or is missing, whatever was synced last, besides the Scripts changed since the last synced commit, so a `scripts.json` entry nobody regenerated doesn't hide a change. It measures a file as `ScriptInfo.Outdated` does, which is how the Scripts generator measures it (#220): the size and SHA-256 of its UTF-8 text without U+200B and U+FEFF. It is a flag, not the default, because it replaces a Script edited on disk, and `script start` updates before every start.
- **`scripts check`.** It compiles copies of the Script and its includes, with `#line` directives, in a temporary folder made the process's data folder: errors point at the original files and lines, and nothing is written next to the Scripts or into the data folder's compile cache. A file in a Scripts checkout (a folder with `scripts.json`) compiles against that checkout's includes. It accepts exactly what the Engine compiles: it finds includes with Core's own `ScriptDirectives.Resolve`, in the Scripts folder or else as a path of their own (an absolute path outside it is staged too), and reads `//cs_` lines as Core does, anywhere in the Script and only before an include's first `using`. An include that doesn't exist is skipped, as Core skips it, with a warning at its line, which leads the errors when the Script doesn't compile.

## Considered Options

- **Route each `scripts` command through a running Engine of the data folder, and run it locally only when none runs.** Rejected: two paths with different refusals for one command.
- **A one-shot `skua-engine` mode the CLI runs as a child.** Rejected: a second command line to keep in step, for code `skua` can call directly.
- **`--verify` as the default.** Rejected, see above.

## Consequences

- `skua` references `Skua.Engine`, and so Core and Roslyn; they load only for the commands that use them, and `install-macos.sh` already ships them next to `skua`.
- `scripts check` sets `SKUA_DIR` in its own process before Core reads it, so it must stay its own command, run before anything else reads Core's data folder.
- An Engine can start a Script while the CLI updates the Scripts; the check before the update doesn't hold any Engine's Scripts slot.

# Skua

Skua is a third-party client for AdventureQuest Worlds that plays the game on a player's behalf by running Scripts.

## Language

### Running the game

**Game Client**:
Skua's own SWF together with the AQW game it loads; the thing that actually plays.
_Avoid_: SWF, flash, client (alone)

**Game Host**:
The runtime that executes the Game Client.
_Avoid_: Flash player, player, container

**Bridge**:
The two-way channel over which the Engine and the Game Client call each other.
_Avoid_: ExternalInterface, flash call, IPC

### Driving the game

**Engine**:
Skua's core, running without any UI: it owns game state, runs Scripts and accepts commands.
_Avoid_: bot, core, daemon, backend

**Control Surface**:
Anything that drives the Engine from outside its process, such as a CLI, an MCP server or a GUI.
_Avoid_: frontend, UI, client

**Engine Name**:
The short name that identifies one Engine on a Mac, so several Engines can run side by side; the default is `default`.
_Avoid_: instance, profile, session

**Script**:
A C# program, compiled and run by the Engine, that plays the game toward some goal.
_Avoid_: bot, plugin

**Script Source**:
The repository the Engine fetches Scripts from.
_Avoid_: script repo, scripts folder

**Script Dialog**:
A message a Script raises for a human; it is either a Notice or a Question.
_Avoid_: message box, popup, prompt

**Notice**:
An OK-only Script Dialog; it never waits for an answer.
_Avoid_: info box, alert

**Question**:
A Script Dialog offering a choice (yes/no or named buttons) that waits for a Control Surface to answer it until it times out.
_Avoid_: prompt, confirm, pending dialog

**Test Account**:
An AQW account reserved for automated sessions; agents never log in with any other account.
_Avoid_: alt, bot account

**Active Account**:
The account in Keychain that a developer's login uses, chosen with `skua account`; by default the Test Account. An agent's login uses it only if it was added with `--allow-agents`, and the Test Account otherwise.
_Avoid_: profile, current user

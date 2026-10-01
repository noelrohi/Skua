# Skua for Mac

What's new in Skua on macOS, newest first. To install or update, follow [Install on macOS](./BUILD.md#install-on-macos).

---

## October 1, 2026

### Fixes
* Skua no longer jumps in front of the app you're using while a Script runs. A window a Script opens, such as its options, waits until you click Skua, and a notification tells you it's there.

---

## September 29, 2026

### Fixes
* The game no longer sometimes stops answering right after Skua starts.

---

## September 28, 2026: the Mac App

### Playing in the window
* **Skua.app**: Skua is now a Mac app, with the game in its window. Click and type in the game as you would in a browser.
* The game is sharp on Retina displays, shows its hand cursor over buttons, and takes ⌘C and ⌘V in chat.
* Log in from the window, and see your account, map and Script in its status bar.
* Closing the window keeps the game and your Script running. Click Skua in the Dock to bring the window back; ⌘Q quits, and asks first while a Script runs.
* When a Script stops, hits an error or relogs while Skua isn't in front, you get a macOS notification.
* Window › Top Most keeps any Skua window above the others, and is remembered.
* The title shows Skua's version and, if you turn it on, your username.

### Scripts
* Load, start and stop Scripts from the Scripts window, with the Script's log beside it. Search Scripts finds one to download.
* Script options and the CoreBots options edit as on Windows.
* Scripts come from a Mac-ready copy of Skua's Scripts, updated from them every day, since many of the originals need Windows.
* At start-up Skua updates your Scripts, skill sets, junk items and quest data, as the Windows app does. Logging in no longer waits for it.
* Reset Scripts downloads every Script again, and Open in VSCode opens one Script in VS Code.
* When a Script asks a question, it shows on a sheet in the window. Its notices go to a list with a badge, and never hold the Script up.

### Every panel from Windows
* Options (Game, Application and Themes), HotKeys, Skills, Runtime, Fast Travel, Current Drops, Auto and Jump.
* Tools and Bank: the Loader, the Grabber, Junk Items, Stats, the Console and Plugins.
* Packets: the Spammer, the Logger and the Interceptor.
* The **Bot Window** (the **+** button, or Window › Bot Window) shows every panel in one window, with a search.
* About, Change Logs and GitHub login are in the Skua menu. Your GitHub token is kept in Keychain.
* Application Options that do nothing on macOS are hidden.
* Change Logs shows this page first, then Skua's own change log.

### Skua Manager
* Keep your accounts, with their passwords in Keychain, and launch a Skua app for each account or group. Open it from the Manager menu.
* **Running** lists every Skua app with its game and Script. Bring one to the front, or stop it.
* **Import Windows list…** brings over the Windows Manager's accounts.

### Command line
* One command installs Skua and the `skua` command. Updating never stops a running Script.
* `skua` sets up your accounts, lists Scripts and what's new in them, keeps Scripts up to date, and shows a Script's progress live.

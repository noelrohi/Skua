# On Linux, accounts live in owner-only files in the data folder instead of Keychain

Linux has no Keychain (#293), and the Linux target is a server: an Ubuntu VM where a scheduled job runs `skua login` and `skua script start` with nobody logged in. So on Linux, `Keychain` keeps each generic password as a JSON file of its account, comment and password, `<SkuaDIR>/credentials/<service>`, mode 0600 in a folder of mode 0700 (`CredentialFiles`). Everything above it stays as ADR 0005 has it: `skua account add|use|remove`, the active account in `Skua.settings.json`, the `--allow-agents` comment the Engine reads at each agent's login, and credentials never crossing the Control Surface. A file is written whole to a temporary file and renamed into place, so the Engine never reads half of one. macOS keeps Keychain, and `SKUA_SECURITY_TOOL` still selects a `security` tool on either system, as the tests' fake does.

## Considered Options

- **libsecret (`secret-tool`, the freedesktop Secret Service).** Rejected: it needs a D-Bus session and an unlocked keyring, which a scheduled job on a headless server has neither of.
- **One file for every account.** Rejected: two `skua account` commands at once would race to rewrite it, and one would lose its change. A file per service needs no lock.
- **Encrypting the files.** Rejected: the key would sit next to them, readable by the same user, so it adds nothing over the file mode.

## Consequences

- Any process running as the user can read the passwords, as on any Linux server without a keyring. Keychain at least asks before `security` reads one. The `--allow-agents` flag can be edited the same way, so on Linux it is a convention between the developer and their agents, not a barrier.
- The CLI's messages and help still say "Keychain": on Linux that means these files.
- A service must be a plain file name (`[A-Za-z0-9_-][A-Za-z0-9._-]{0,127}`); every service `skua account` makes is one.

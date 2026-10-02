# Agents log in with the Test Account unless the active account allows them; accounts are stored outside the Control Surface

`skua account add|use|remove` keeps a developer's accounts in Keychain and chooses the Active Account, which the CLI's `login` uses. An agent's login, which MCP's `login` tool makes with `asAgent`, uses the Test Account instead, unless the active account was added with `--allow-agents`. That flag is stored with the account, as its Keychain item's comment, which the Engine reads at each agent's login without reading the password. So an agent reaches another account only when a developer both allowed it and left it active. When the active account doesn't allow agents, the agent's login doesn't refuse: it logs the Test Account in, relogging if another account plays, and its reply names the username. This amends ADR 0002, under which `login` could only ever reach the Test Account.

Credentials still never cross the Control Surface. The CLI alone writes Keychain (and, since #93, the Skua Manager, through the same code), with the password on `security`'s standard input, and no RPC or MCP tool sets, switches or reads an account. The Engine reads the active account's service from `Skua.settings.json`, and the account from Keychain, at every login, so a switch needs no Engine restart.

## Considered Options

- **Only the Test Account, as before.** Rejected: #63 asks for a developer's own accounts, and a hand-edited setting already allowed any service.
- **Account tools on the Control Surface** (`account_use` over RPC and MCP). Rejected: an agent could then switch itself off the Test Account.
- **Refusing an agent's login while a non-allowed account is active.** Rejected: agents would stall whenever a developer last used their own account, whereas the Test Account is always theirs to use.
- **The flag in Skua.settings.json.** Rejected: any process can edit that file. The Keychain item is where the account lives, and replacing it takes its password.

## Consequences

- `asAgent` is the caller's word. An agent that runs `skua login` from a shell logs in as a developer would, so agents use MCP, or the CLI only with the Test Account active.
- The Test Account keeps its reserved name `test` and service `skua-test-account`, which the live tests use. `account add` changes it only with `--test` or `--name test --replace`, and `account remove` deletes it only by name.
- `login` may name an account (#175), so a Control Surface such as the TUI logs each Engine in as its own account. Naming one doesn't make it active. An agent may name only the Test Account or one added with `--allow-agents`, and naming any other is refused rather than replaced with the Test Account, since the agent asked for it.
- An app the Skua Manager launched pins its Engine's account (ADR 0006). This rule applies there with the pinned account in place of the active one.

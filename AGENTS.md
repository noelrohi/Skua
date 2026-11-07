## Public repo

This fork is public. Game accounts are private: in anything that leaves this machine (commits, code, issues, PRs, comments), name them `alt1`…`altN`, consistent within one document.

## Tests

Run the Mac tests as `.github/workflows/macos.yml` does, in the foreground: `dotnet test Skua.MacOS.slnf -c Release --no-build --blame-hang-timeout 5m`. The Avalonia suite takes about 2 minutes, so a run past 5 is hung. Avalonia tests share one Script runner: a test that starts a Script first waits until none is running, since Start otherwise opens a modal "stop it?" prompt that blocks the run.

## Agent skills

### Issue tracker

GitHub Issues on the fork `noelrohi/Skua` (never upstream `auqw/Skua`). See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: root `GLOSSARY.md` + `docs/adr/`. See `docs/agents/domain.md`.

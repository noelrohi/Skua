# Ruffle patches ride on a noelrohi/ruffle fork branch, pinned by rev and tag

The Game Host's Ruffle patches live as commits on the `skua` branch of a fork, `noelrohi/ruffle`: one commit per fix, on top of a pinned upstream commit. `Skua.GameHost/Cargo.toml` takes the Ruffle crates from that fork by full `rev`, and every series we publish also gets an immutable tag (`skua-<yyyymmdd>-<base7>`, with a letter after the date for a second series on the same day and base, as in `skua-20260927b-a1277c0`), so that force-pushing `skua` on a rebase never makes an old Skua commit unbuildable. The Cargo project lives in this repo as `Skua.GameHost/` and is built by `dotnet build` of `Skua.App.Engine` on macOS. We chose a fork because the series is large (a ~1.8k-line port of Ruffle PR #24590 plus six smaller fixes), gets rebased on every pin bump, and is meant to shrink by offering fixes upstream. Git's own rebase tooling and per-commit review suit that better than patch files.

## Considered Options

- **Vendored patch files in `Skua.GameHost/patches/`, applied to a checkout before building.** Rejected: Cargo can't apply patches itself, so every build (local and CI) needs an apply step and a local checkout. Rebasing means round-tripping through `git am` and `format-patch`, and the prototype's files had already drifted from their commits (missing hunks, clashing numbers).
- **`[patch]` sections pointing at git refs of loose branches.** Rejected: it's the same fork without a named, reviewable series, and `[patch]` indirection hides which Ruffle actually ships.
- **Referencing the `skua` branch by name.** Rejected: it's reproducible only through `Cargo.lock`, `cargo update` jumps series silently, and a rebase orphans the revs older Skua commits name.
- **The Game Host in its own repo, or inside the fork.** Rejected: the Bridge framing changes in lockstep with the C# Engine, and the fork should hold only Ruffle changes.

## Consequences

- Production has no `[patch]` section: wgpu-hal and gc-arena are stock. Diagnostics (GC and render census, counters) live on a `skua-diag` branch stacked on `skua`, and are built only with a local override.
- A new patch is a new commit and tag at the same base. Bumping the upstream base happens only on need, and is gated by Ruffle's tests, an offline stress suite and one live smoke.
- The #24590 port is carried until upstream lands an equivalent fix. It dominates rebase cost.
- `Skua.GameHost/README.md` maps the prototype-era patch numbers (#6, #13, #14, #17) to commits, and lists which commits are upstreaming candidates.
- Losing the fork's tags would break old builds, so tags are never deleted.

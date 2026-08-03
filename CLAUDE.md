# TaskSpaces — session briefing

Windows tray utility: named workspaces (Work / Personal / YouTube) backed by
Windows virtual desktops; one-click switching (taskbar natively shows only the
active group's windows); rules auto-assign new windows; windows renameable to
short taskbar names; durable per-workspace rosters; state persists across
reboots.

## Current state (as of 2026-08-03)

- **The app is built and running.** Branch `taskspaces-v1`, HEAD `381ee4a`,
  55 commits ahead of `main`, ~9.5k lines. `main` still contains only the
  design docs and plans — all implementation lives on the branch.
- Work happens **directly in this repo**. The old
  `.claude/worktrees/taskspaces-v1` git worktree was removed on 2026-08-03;
  don't recreate it. It split session history under a second project key
  (making past sessions invisible to `/resume`) and hid the code from the
  main checkout.
- Tests: **119/119 Core unit tests green**. The 5 tests in
  `TaskSpaces.Windows.Tests` are all `[Trait("Category","Integration")]` and
  mutate *real* virtual desktops — run them deliberately, never as part of a
  routine check while Petre is working. Routine command:
  `dotnet test TaskSpaces.sln --filter "Category!=Integration"`.
- **PR IS ON HOLD — never push, never open a PR until Petre says so.** Nothing
  has been pushed; `origin/main` is still at the design-doc commit. Petre is
  live-testing the app throughout.

## Where the context lives (read in this order)

- Specs: `docs/superpowers/specs/2026-08-01-taskspaces-design.md` (original),
  `docs/superpowers/specs/2026-08-01-switcher-roster-design.md` (amended 5×).
- Plans: `docs/superpowers/plans/2026-08-01-taskspaces-implementation.md`
  (9 tasks), `docs/superpowers/plans/2026-08-02-switcher-roster-implementation.md`
  (11 tasks).
- SDD ledgers: `.superpowers/sdd/*/progress.md` — gitignored, but they carry
  every ruling, root cause and *deferred minor*. Read these before assuming
  something was overlooked; it was probably decided.
- Manual test script: `docs/superpowers/notes/manual-test-script.md`.
- Virtual desktop COM findings: `docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md`.

## Awaiting Petre (live testing — these are the open threads)

- **Drag-and-drop retest, both directions.** `DnDTrace` is still flag-gated and
  writing to `%TEMP%\taskspaces-dnd.log`. **Remove DnDTrace once Petre
  confirms** — it is temporary diagnostic scaffolding, not a feature.
- **Floating bar verify**: rows for Sparrow / GEPHA / Main all visible after the
  `7633d35` fix (root cause was the design's own exclusions, not a bug).
- **The mystery "Unplaced" window**: the `381ee4a` hover info line was added
  specifically to identify it (shows title · process · group · `was:` original).
- Manual pass, items 34–40.
- Integration/PR decision.

## Key decisions already made (don't relitigate)

- Build on Windows' own virtual desktops, NOT manual ShowWindow(SW_HIDE) —
  hidden windows can be orphaned by a crash; virtual desktops can't lose windows.
- User-friendly GUI first; hotkeys are optional accelerators. Tiling WMs
  (komorebi/GlazeWM) were explicitly rejected as "too much".
- Switcher form factor — **resolved**: tray hover-peek panel (menu on click)
  *plus* a separate always-on-top translucent floating icon bar showing windows
  from every workspace, grouped with dim workspace labels.
- Content-based membership identity = exe path + args; Chromium browsers keyed
  by `--profile-directory`; **firefox deliberately excluded** (its args are too
  generic). Don't "fix" this.
- Original non-goals are partly superseded by Petre's later requests: window
  renaming and the floating bar are both **shipped**. Still out of scope:
  tiling/layouts, browser tab restore, sync.
- Parked post-merge backlog (non-blocking, deliberate): roster / persisted-rename
  GC policy is unbounded by design (revisit ~month 2); Unplaced-row JumpTo
  activate-only fallback; pending-tier-3 misroute comment.

## Open questions

- Product name ("TaskSpaces" is still a working name).

# TaskSpaces — Switcher Panel, Pinning & Workspace Roster

**Date:** 2026-08-01
**Status:** Approved via brainstorming with Petre; extends the v1 design
(`2026-08-01-taskspaces-design.md`) after hands-on testing of the v1 build.
**Resolves:** v1's open question #1 (switcher UI form factor → tray-summoned panel).

## What this adds

Four user-visible capabilities on top of TaskSpaces v1:

1. **Switcher panel** — one surface showing *all* windows across *all* workspaces,
   taskbar-style, without switching desktops first. Left-click the tray icon → panel.
2. **Pin to all workspaces** — per-window, user-decided (RDP manager: always pinned;
   Beeper: "depends, I need to be able to say"). Uses Windows' native
   pin-to-all-desktops, so pinned windows follow the user everywhere.
3. **Workspace visibility** — the Manage window's Windows tab shows which workspace
   each window is actually on (ground truth from the OS, not our bookkeeping).
4. **Workspace roster + Start** — a workspace lists the apps that *belong* to it even
   when they aren't running (dimmed), and a ▶ Start button launches them all with
   their recorded command lines.

## Core model decision: membership is per-window, content-based

The same app may belong to workspace A or B **depending on what it is showing**
(Rider on solution X → Work; Rider on solution Y → Personal). Consequences,
confirmed with Petre:

- Rules stay content-based (title regex, browser profile) — already true in v1.
- **Roster identity = exe path + command line** (browsers: path + profile), NOT exe
  path alone. `rider64.exe X.sln` and `rider64.exe Y.sln` are distinct entries in
  distinct workspaces. If the identical path+args later lands in a different
  workspace, the entry moves there (last placement wins; nothing belongs to two).
- **Late placement:** when an *unplaced* window's title changes (bare Rider loads a
  solution), workspace rules re-run against the new title. Placed windows are never
  re-placed by title changes — a browser must not teleport on tab switch. Once
  placed, only the user moves it.
- **Observability limit (accepted):** content-based membership works where content
  shows in the title or command line. An app opened bare and navigated internally is
  routable only once its title changes (late placement), and its roster entry
  relaunches it bare — the app's own session restore (VS Code, Rider) supplies the
  content. That is the app's memory, not ours.
- **Future rule kind (explicitly out of scope now):** UI Automation matchers (e.g.
  browser URL, editor document path) — the credible way to peek beyond
  title/command-line. The `RuleMatchKind` enum extends without redesign; UIA needs
  its own spike (per-app tree quirks) before any commitment. Injection/hooking and
  process-memory approaches are rejected on principle.

## Components / changes

### IVirtualDesktopService (+3 members)

`Result Pin(WindowHandle)`, `Result Unpin(WindowHandle)`,
`Result<bool> IsPinned(WindowHandle)` — thin wrappers over the COM wrapper's native
pin support. Same Result discipline, same integration-test trait as the rest.

### WorkspaceManager

- **`WindowsByWorkspace()`** — snapshot query powering both the panel and the
  Windows tab: for each known window, pinned-check first (pinned windows are on all
  desktops), then ask the OS which desktop it is on (`DesktopOf`), map desktop →
  workspace. Groups: **Pinned**, one per workspace (running windows + not-running
  roster entries), **Unassigned** (windows on desktops no workspace owns).
- **Roster lifecycle** (replaces v1's ephemeral inventory semantics): an entry is
  added/updated when a window is placed into a workspace (rule, late placement,
  manual move, pending-launch). Entries **survive window close** — that is the
  point — and leave only via user removal or workspace deletion. Command-line
  capture extends from browsers-only to every window that gets placed (~10ms WMI
  lookup, once per window).
- **`PinWindow` / `UnpinWindow`** pass-throughs. Interaction rule: *Send to
  workspace* on a pinned window unpins it first — moving something to one workspace
  is an explicit statement it should not be on all of them. Pin state lives in the
  OS only; pinned windows are excluded from rosters (re-pinning after reboot is a
  non-goal).
- **`StartWorkspace(Guid)`** — launch every not-running roster entry with its
  recorded command line, register pending placements (which outrank rules), switch
  to the workspace. "Not-running" matches on path+args, so Rider-on-Y running in
  Personal does not suppress starting Rider-on-X for Work.
- **Late placement** as described above: TitleChanged on an unplaced window re-runs
  workspace rules.

### Switcher panel (TaskSpaces.App)

- Summoned by **left-click** on the tray icon (right-click keeps the existing menu);
  borderless, topmost, anchored near the tray; dismissed by Esc, focus loss, or a
  completed jump. Contents rebuilt on every open; live-refresh via StateChanged
  while open.
- Group header = workspace name + running count + **▶ Start** (enabled when the
  roster has not-running entries); clicking the header switches to that workspace.
  Current workspace highlighted.
- Rows: app icon + display title (short rename shown where applied). Running rows
  normal; roster-only rows dimmed with "not running" affordance.
- **Click a running row → jump**: switch desktop if needed, restore if minimized,
  focus, close panel. Click a dimmed row → launch that one entry; the panel stays
  open (the row transitions to running as the window arrives — visible feedback,
  and Petre can start several apps in a row).
- **Right-click, running row:** Pin to all workspaces / Unpin · Send to →
  [workspaces] · Rename… · Restore title.
  **Right-click, dimmed row:** Start · Remove from workspace.
  An **Add app…** action per group lets Petre roster an exe manually: file picker
  for the exe plus an optional arguments textbox (that pair IS the roster identity).

### Manage window

Windows tab gains a **Workspace** column (workspace name, "Pinned", or "—") from the
same `WindowsByWorkspace()` snapshot; Refresh re-queries.

### Rehydration prompt

No longer a separate concept: at startup, workspaces whose rosters have not-running
entries are offered ("start them?") through the same roster/Start machinery and the
same already-running filter. The v1 prompt's per-workspace opt-in stays.

## Persistence

`AppState.Inventory` (workspaceId → entries) carries roster entries
(path, command line, last title). Existing on-disk files remain readable — the shape
is unchanged; only the lifecycle (no removal on window close) and the dedupe key
(path+args) change. Enum values continue to serialize as names.

## Error handling & testing

- Result everywhere; panel failures surface like the rest of the UI. Launch failures
  during Start are per-entry and non-fatal (best-effort batch, v1 rehydrator rule).
- Pure logic (grouping, roster dedupe/move, late-placement gating, not-running
  matching) → xunit. Pin/Unpin → manual-trait integration tests. Panel behavior →
  manual test script additions.

## Non-goals

- Always-on-top persistent bar (revisit only if the on-demand panel proves
  insufficient in daily use).
- DWM thumbnail previews; global hotkeys (still optional accelerators, later).
- Re-pinning pinned windows after reboot; UIA matchers (future, spiked separately);
  per-app integrations (browser extensions, IDE plugins).

## Next step

`superpowers:writing-plans` against this spec, after Petre reviews it.

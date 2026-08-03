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
5. **Persistent, self-healing renames** *(added 2026-08-02 during testing)* — manual
   window renames survive app restarts (persisted as process + original title → short
   name) and a periodic sweep (~5s) re-asserts every active rename even if a
   title-change event was missed. Event-driven re-apply stays the primary mechanism;
   the sweep is the safety net Petre asked for ("applying those renamed titles every
   several seconds"). Known limit: after a restart, a window whose natural title has
   since changed (browser navigated elsewhere) no longer matches its recorded original
   title and stays un-renamed until renamed again — window identity across restarts is
   fundamentally heuristic.
6. **Polish requirements from testing** *(2026-08-02)*: app icons on every window row
   (switcher panel AND Manage → Windows tab); left-click on the tray icon opens the
   switcher panel (Manage stays on the right-click menu); renamed windows display both
   names (short name + original title); windows on desktops that are not TaskSpaces
   workspaces are grouped under **their desktop's actual name** (e.g. "Desktop 1"),
   not a flat "Unassigned" — including the current desktop.

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
  roster entries), then one group per **non-workspace desktop, labeled with that
  desktop's actual name** (from the OS desktop list — "Desktop 1", etc.), so
  uncategorized windows always sit under the name of the desktop they are on,
  including the current one. Rows carry the applied short name AND the original
  title when renamed.
- **Rename persistence + sweep** — `RenameWindow` records a `PersistedRename`
  (process name, title-at-rename, short name); `RestoreTitle` removes it. A public
  `ReapplyRenames()` re-asserts all active renames (ledger windows whose current
  title drifted, plus persisted renames matching windows not yet in the ledger) —
  called once after Start() and every ~5 seconds by an App-side timer. Event-driven
  NAMECHANGE re-apply remains the fast path; the sweep is the safety net.
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
- Rows: app icon (extracted from the exe, cached; also used by the Windows tab) +
  display title. Renamed windows show both names — short name prominent, original
  title dimmed beside it. Running rows normal; roster-only rows dimmed with "not
  running" affordance.
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

`AppState` additionally gains `PersistedRenames` — a list of
`(ProcessName, OriginalTitle, ShortName)` records. A manual rename adds one (keyed by
the window's process and its title at rename time); *Restore title* removes it. On
startup and in the periodic sweep, any window whose process+title exactly matches a
persisted rename gets the short name applied (which also seeds the runtime ledger, so
event-driven re-apply takes over from there). Missing `PersistedRenames` in an older
state.json deserializes as empty — no migration needed.

## Error handling & testing

- Result everywhere; panel failures surface like the rest of the UI. Launch failures
  during Start are per-entry and non-fatal (best-effort batch, v1 rehydrator rule).
- Pure logic (grouping, roster dedupe/move, late-placement gating, not-running
  matching) → xunit. Pin/Unpin → manual-trait integration tests. Panel behavior →
  manual test script additions.

## Tray interaction & hotkeys *(added 2026-08-02 during testing)*

Petre's requests while using the panel:

- **Hover to peek**: hovering the tray icon (~400 ms) opens the switcher panel
  WITHOUT stealing focus (`ShowActivated=false`); it hides itself once the cursor
  leaves the panel area (small grace margin, ~250 ms polling). Clicking inside the
  peeked panel activates it, after which normal focus/dismiss behavior applies.
- **Menu on click**: left-clicking the tray icon opens the tray menu (same as
  right-click; H.NotifyIcon `MenuActivation = LeftOrRightClick`). The panel is
  reached by hover or hotkey, not by click.
- **Global hotkeys** (hardcoded v1, RegisterHotKey): **Ctrl+Alt+Left/Right** cycles
  TaskSpaces workspaces in their defined order (wrapping; skips non-workspace
  desktops; if the current desktop is not a workspace, goes to the first/last
  workspace); **Ctrl+Alt+1..9** switches directly to workspace N (1-based, defined
  order). Registration failures (another app owns the chord) surface once as a
  warning — never a crash, never silent. Native Win+Ctrl+arrows remain untouched.

## Drag-and-drop window management *(added 2026-08-02, second testing round)*

Petre: "that plus sign makes no sense, i'd much rather have the ability to drag
windows around" and "this windows [tab] should have spaces, windows underneath each
space, and let me drag and drop them, similar to the [hover panel]".

- The switcher panel's **＋ Add app button is removed** (manual roster-add stays
  reachable via the workspace header's right-click menu — one entry, out of the way).
- **Window rows are draggable** — drop onto a workspace group moves the window there
  (AssignWindow semantics, incl. unpin-first); drop onto the 📌 Pinned group pins it.
  *(Amended, fourth testing round:)* dropping onto an **unbound-desktop group** (e.g.
  "Main") also works — `MoveToDesktop`: unpin first, move to that desktop, drop the
  workspace membership, and mark the window **detached** so rules don't drag it back on
  its next title change (a browser retitles constantly, so without this the drag would
  undo itself within seconds). Detachment is live-only state, like every other
  membership fact; the workspace's roster entry is untouched (▶ Start still relaunches
  the app). The "Unplaced" catch-all group is never a drop target — it is not a real
  desktop.
- The Manage window's **Windows tab is restructured to the same grouped view** as the
  panel: workspace/desktop headers with their windows underneath, same drag-and-drop,
  same right-click menu (Pin/Unpin · Send to · Rename… · Restore title). One shared
  control (`WindowGroupsView`) backs both surfaces so they cannot drift. The tab's
  bottom action bar (Send-to combo, rename textbox/buttons) is removed — actions live
  on the rows; Refresh and "Start with Windows" remain.

Also from this testing round (bug, under investigation as part of the same task):
windows on unbound/default desktops reportedly missing from the panel's
non-workspace section — root-cause first (suspects: unnamed default desktops return
"" from the API making the group header unrecognizable; silent per-window omission
when the desktop query fails), then fix with evidence.

## Floating icon bar *(added 2026-08-02, third testing round — supersedes the
"always-on-top persistent bar" non-goal below; Petre asked for it explicitly)*

A small always-on-top, borderless, translucent window showing **only app icons**,
grouped one compact row per group: 📌 pinned row on top — *(amended 2026-08-03:)*
**always rendered, even when empty**, because it is the only way to pin the FIRST
window (hiding it when empty left the drop target unreachable) — then EVERY
workspace (an empty one shows just its click-to-switch label), then unbound desktops
that have windows, labeled with the desktop's real name and click-to-switch too.
*(Amended in the fourth testing round — the original "unbound desktops excluded — it
is a workspace bar" rule collapsed the bar to a single row on Petre's machine, where
most windows live on the unbound "Main" desktop; he asked to "show tabs from all
workspaces". The "Unplaced" catch-all group keeps a plain, non-clickable label — it
is not a real desktop. **Amended 2026-08-03: the bar no longer renders "Unplaced" at
all** — see "Unplaced leaves the bar" below; the switcher panel still does.)*
Clicking an icon **jumps to that window**
(switch workspace if needed + focus — existing JumpTo). Its position and
visibility persist in state.json (`FloatingBar` init-property on AppState — older
files load with it hidden/default, no migration). Toggled from the tray menu
("Show floating bar", checkable). Right-click on the bar → small menu (Hide bar).
Live-refreshes via StateChanged like the other surfaces. No roster (not-running)
entries — it is a jump-and-arrange surface for live windows.

*Fourth testing round, both asked for by Petre after living with the bar:*

- **Hover info line** ("add a small panel, when i hover over any icon, i want to see
  what it is"): a reserved single line at the bottom of the bar showing the hovered
  window's full title plus, dimmed, its process, its group, and the original title when
  we renamed it. It is part of the bar's own window rather than a popup or tooltip —
  this window is topmost, layered (`AllowsTransparency`) and pinned to all desktops,
  the setup where separate-HWND popups misbehave most — with height reserved (a hint
  when nothing is hovered) and a fixed text width, so a hover never resizes or moves a
  bar that sits in the bottom-right corner. During a drag the same line reads out the
  drop target ("→ move to GEPHA").
- **Icons are drag sources** ("i also want to be able to drag them around across
  tabs"): dragging an icon onto another row moves the window through the same
  AssignWindow / PinWindow / MoveToDesktop paths as the switcher panel, sharing the drag
  payload format so a drag can even cross between the two surfaces. The row under the
  cursor highlights. One consequence, deliberate: a left-drag that starts **on an icon**
  now moves the window, so the bar itself is moved by dragging anywhere else — its row
  labels, the padding, the info line (the earlier "drag from anywhere" fix, narrowed by
  exactly the icon area). The idle info line names both gestures, since neither is
  discoverable on an icon-only surface.

## Startup redistribution, per-icon rename & bar cleanups *(added 2026-08-03, fifth
testing round)*

Petre, after living with the bar: "right clicking on the icon should give me option to
customize that one - tab rename", and "when starting up, all those apps, when started,
should be redistributed to the correct workspaces, is that already in place?" — it was
not, see below.

### Per-icon right-click menu — naming only

Each bar icon gets its own context menu with exactly two entries: **Rename…** (the tab
rename) and **Restore title**, the latter only when TaskSpaces actually renamed that
window. Both reuse the existing ledger-backed `RenameWindow` / `RestoreTitle`, so a
rename made from the bar persists across restarts and is re-applied by the ~5s sweep
exactly like one made from the panel.

Deliberately NOT in this menu — both offered and rejected by Petre: **Send to ▸
workspace** ("i can drag and drop, no need for this") and **Pin / Unpin** ("another drag
to the pinned section"). The rule that falls out, which future surfaces should follow:
**drag expresses movement and pinning; right-click expresses naming.** Naming is the one
operation a drag cannot express, which is precisely why it earns a menu. Same reasoning
that removed the duplicate bar-drag handler in round 3 as "pure redundancy".

Unpinning stays reachable by dragging an icon out of 📌 onto any workspace or desktop
row — both `AssignWindow` and `MoveToDesktop` unpin first. Known, accepted gap: there is
no gesture for "unpin but leave the window where it is", because unpinning always
arrives with a destination. For a window that is on every desktop by definition,
requiring a destination is reasonable.

The bar's background right-click menu (Hide floating bar) is unaffected: WPF gives the
innermost element's menu precedence, so an icon's menu wins on an icon and the
background's wins on bare bar.

### Unplaced leaves the bar

The "Unplaced" catch-all — windows whose desktop the COM API refuses to resolve, on
Petre's machine "Windows Input Experience" — is **no longer rendered on the floating
bar**, and is unchanged in the switcher panel. Rationale: the bar's contract is "click an
icon, go to that window", and an Unplaced row cannot honour it, since `Guid.Empty` is
neither a switch target nor a drop target. A permanent row on a permanently-visible
surface that can never be acted upon is noise. The catch-all itself is NOT removed — it
exists so that a window the API loses track of can never become invisible (the Task 10
defect) — and the panel, which is the auditing surface, keeps showing it. **Bar =
actionable, panel = complete.**

### Startup redistribution of already-running windows

Nothing today places a window that was already open when TaskSpaces started: `Start()`
records the snapshot into `knownWindows` and subscribes for FUTURE events, while
placement happens only in `OnAppeared`. Two confirmed consequences — workspace rules
never touch pre-existing windows, and roster membership (the app's own record of what
belongs where) is never used to place a running window at all.

New: a **startup placement sweep**. A pure function in Core — same shape and
testability as `OverviewBuilder`, every OS fact arriving as data, no COM call inside —
takes the windows, their current desktops, the workspaces, the inventory and the rules,
and returns the list of moves to make. `Start()` applies them after reconcile.

*Superseded later the same day — see "Last placement IS the rule" below, which reverses
the rules-vs-roster precedence and folds in the lost-pin defect.*

Rulings:

- ~~**Rules first, then roster identity.**~~ Reversed below: last placement wins.
- **Only windows currently on unbound / plain desktops are moved.** Anything already on
  a workspace-bound desktop is left alone. After a reboot that costs nothing (desktops
  do not survive reboots), but when TaskSpaces merely restarts mid-session it stops the
  sweep from yanking windows Petre placed by hand.
- **Ambiguous roster matches are skipped, never guessed**: if one identity appears in two
  workspaces' rosters, no move is made.
- Identity is the existing `RosterIdentity` (exe path + args; Chromium keyed by
  `--profile-directory`; firefox deliberately excluded), so "VS Code with corne-config"
  and "VS Code with TaskSpaces" resolve to different workspaces.
- **Pinned windows are never swept** — they are on all desktops by definition, matching
  the existing `AutoPlaceable` rule.

### Last placement IS the rule *(added 2026-08-03, same round — supersedes the
rules-first ruling above)*

Reported defect: "move Beeper to pinned, close the app, open again, it is incorrectly
placed in GEPHA workspace."

**Root cause.** Pin state lives only in the OS and is keyed to the **window handle**
(`PinWindow` persists nothing — by the original design, which also listed re-pinning as a
non-goal). Beeper is an Electron app that closes to tray, destroying and recreating its
HWND; the pin dies with the old handle, and the recreated window appears on whatever
desktop is current — GEPHA. Its *roster* entry is in Sparrow, so the roster was never the
source of the wrong group. The defect is a hole in the data model, not stray logic: there
is nowhere for "pinned" to be recorded.

**Petre's ruling:** "last placement in the workspace should be where it's placed when
started" and "last placement beats rules, i'd say, last placement is the rule. what else
is a rule?"

**Precedence, replacing the rules-first ruling above:**

1. **Pending launch** — we just started this app for a workspace, we know where it goes.
2. **Last placement** — an explicit act by Petre. Wins over rules: a standing guess must
   never yank back a window he moved by hand.
3. **Rules** — first-sight classification ONLY, for windows with no recorded placement.
4. **Roster inventory** — the workspace-membership half of last placement (see below).

**Why rules survive at all**, given the ruling: a rule is the only thing that can decide
for a window with NO history — either never seen, or seen but *indistinguishable by
identity because the difference is in the content*. Two Chrome windows on one profile
share path+args and differ only in title; `RosterIdentity` cannot split them, a title
regex can. That is the founding content-based-membership decision. The other rule kinds
are weaker or redundant: process-name rules put every window of an app in one workspace
(exactly what content-based membership rejected), and browser-profile matching is already
how `RosterIdentity` keys Chromium windows. So rules are demoted to the fallback they
now are, not deleted. (Petre's `WorkspaceRules` is empty today, so the demotion costs
nothing.) The Rules tab arguably now reads as more central than it is — relabel later.

**Storage — smaller than first designed.** `Place()` already calls `RosterAdd`, so
identity → workspace IS persisted in `Inventory`, merely stored inverted and never
consulted for placement. So no new map is needed for workspace placements. Only the two
placements the structure cannot express are new, each an `AppState` init-property
(no migration, same pattern as `PersistedRenames` / `FloatingBar`):

- **`PinnedApps`** — identities that were pinned to all workspaces. Re-applied when a
  matching window appears, which is what makes a pin survive an Electron HWND recycle.
- **`DetachedApps`** — identities deliberately dragged onto a plain desktop, so a restart
  does not let a rule or the roster reclaim them (today `detached` is live-only).

Deliberately NOT `Guid.Empty` as a sentinel key inside `Inventory`: `Guid.Empty` already
means the "Unplaced" catch-all elsewhere, and overloading it would collide.

**Applied in two places**, since windows arrive by two routes: the startup sweep (windows
already open, which `Start()` currently only records) and `OnAppeared` (windows that show
up later — the path that actually repairs Beeper on every tray-restore).

### Active-window highlight

Petre: "active window should be highlighted in the floating window." On an icon-only bar
showing three identical VS Code glyphs, "which one am I in" is otherwise unanswerable.

- `WindowMonitor` gains a hook on **`EVENT_SYSTEM_FOREGROUND`** and a new
  `WindowEventKind.Activated`. Its own hook, not a widened range: that event is 0x0003
  while the object events are 0x800x, so one span would subscribe us to everything between.
- Only windows we already track raise it. `WINEVENT_SKIPOWNPROCESS` already excludes our
  own windows, so clicking the bar never steals the highlight; focus moving to an
  untracked window emits nothing, leaving the highlight on the last real window — the
  useful answer to "which window am I in".
- Purely presentational: `Activated` places, renames and persists **nothing**. Focus is not
  a statement about where a window belongs.
- Pulses only on an actual CHANGE of active window. Alt-tabbing fires foreground events
  continuously and each pulse costs one `DesktopOf` COM call per known window per open
  surface, so de-duplicating is what stops a burst of alt-tabs becoming a burst of COM sweeps.
- **Seeded at startup** from `IWindowMonitor.Foreground()` (`GetForegroundWindow`, filtered
  to tracked windows). Foreground events only report CHANGES, so without seeding nothing is
  highlighted from launch until the user next switches windows — indistinguishable from the
  feature being broken.
- Carried as `WindowRow.IsActive` so the bar and the panel highlight from the same fact and
  cannot disagree. The bar draws a dim background plus a brighter outline, and keeps
  `BorderThickness = 1` on EVERY icon (transparent when inactive) so gaining the highlight
  cannot nudge a `SizeToContent` bar across the screen.

### Workspace reordering

Petre: "i need to be able to move workspaces up or down in the manage window." Order is not
cosmetic — `State.Workspaces` order drives the floating bar's rows, the switcher panel's
groups AND which workspace `Ctrl+Alt+1..9` selects — so persisting this one list is all the
other surfaces need. `MoveWorkspace(id, delta)` with ↑/↓ buttons on the Workspaces tab; an
out-of-range move SUCCEEDS as a no-op, because ↑ on the top row should do nothing rather
than raise an error at someone who clicked it. Selection follows the moved workspace, so ↑
can be clicked repeatedly.

### Invariant: shared brushes must be frozen

Found while testing the highlight. A `Freezable` that is still mutable takes on the thread
affinity of whoever created it, and a `static readonly` brush is created once per process on
whichever thread first touches the class — so assigning it to a control on any other thread
throws "Cannot use a DependencyObject that belongs to a different thread than its parent
Freezable" during Arrange. It surfaced as order-dependent test failures (each bar test
passing alone, failing in a suite, since every test gets a fresh STA thread), but the same
hazard applies to any future surface built off the UI thread. Every shared brush in
`FloatingBar` and `WindowGroupsView` is now frozen, which also lets WPF skip change-tracking
on values that never change. Same reasoning as `IconCache`'s frozen bitmaps.

### Invariant: a rebuild must never be re-entered

From the defect Petre screenshotted this round (every bar row rendered twice): any
surface that clears its host panel and then repopulates it must not be re-entrant.
`WindowsByWorkspace()` makes COM calls; COM calls on an STA thread pump the message
queue; WinEvent callbacks are delivered on that thread; `stateChanged` is a synchronous
Rx `Subject`; and `Dispatcher.Invoke` runs inline when already on the dispatcher thread.
A pulse can therefore land between the clear and the repopulate, leaving the nested
rebuild's rows in place and the outer rebuild's appended after them. `FloatingBar` and
`WindowGroupsView` both guard now: a pulse arriving mid-rebuild is deferred and
re-queued via `BeginInvoke` — queued rather than looped, so heavy window churn cannot
spin the UI thread through back-to-back rebuilds.

## Non-goals

- ~~Always-on-top persistent bar~~ (superseded above, 2026-08-02).
- DWM thumbnail previews; global hotkeys (still optional accelerators, later).
- Re-pinning pinned windows after reboot; UIA matchers (future, spiked separately);
  per-app integrations (browser extensions, IDE plugins).

## Next step

`superpowers:writing-plans` against this spec, after Petre reviews it.

# TaskSpaces: session briefing

Windows tray utility. It **names Windows' own virtual desktops** and adds what they lack:
an always-on-top floating bar showing every desktop's windows at once, `Win+Ctrl+Tab` to
switch between them Alt+Tab style, per-desktop colours, short taskbar names for windows,
and memory of where you last put each window. State persists across reboots.

Terminology, and keep it straight: a **workspace** is a virtual desktop you have named.
Unnamed ones still exist and the bar shows them separately, so the two words are not
interchangeable: "virtual desktop" is the familiar anchor, "workspace" is the app's
object.

## Current state (as of 2026-08-11)

- **Shipped and in daily use.** Everything is on `main`; **v1.8.0** is released. One branch
  per topic, PR, merge, and no long-lived feature branch. Sixty PRs merged so far.
- **Tests: about 630 green**, the great majority in `TaskSpaces.Core.Tests` and a few dozen
  in `TaskSpaces.Windows.Tests` (which constructs real WPF windows on an STA thread per
  test). Do not trust this number to the digit; run the suite. The routine command, and the
  default: `dotnet test TaskSpaces.sln --filter "Category!=Integration"`
- **Integration tests mutate the real machine**. They create/switch/delete real virtual
  desktops, and one spawns `winver`. Run them deliberately, never as a routine check while
  Petre is working.
- Work happens **directly in this repo**. Do not create git worktrees; a previous one split
  session history under a second project key and hid the code from the main checkout.

## How Petre works, and what that means for you

- **He live-tests continuously** and reports in short fragments, often a screenshot with
  three words. Restart the app after a change so he can see it:
  `src/TaskSpaces.App/bin/Debug/net10.0-windows10.0.19041.0/TaskSpaces.App.exe`. Stop the
  running instance first or the build cannot overwrite it.
- **Never push to `main`, never merge.** Branch, PR, wait to be told it merged. Pushing a
  feature branch in order to open a PR is fine; anything beyond that needs asking.
- **Measure, do not theorise.** Every wrong answer this project has produced was a guess,
  and throwaway probes have settled what reasoning got wrong. Two probe traps worth
  remembering:
  - Call `SetProcessDpiAwarenessContext(-4)` before reading any window geometry from
    PowerShell, or Windows virtualises the numbers and every rect is wrong.
  - Declare P/Invokes `CharSet.Unicode`. A `FindWindowW` without it silently returns 0 and
    the probe "proves" whatever you hoped.
- He asks to be pushed back on. Do it with reasons, then do what he decides.

## Where the context lives

- Specs: `docs/superpowers/specs/*.md`. **Both are behind reality**. Read them for
  original intent, not current behaviour. The code comments are the accurate record; they
  are long on purpose and carry the reasoning behind decisions that look arbitrary.
- Plans: `docs/superpowers/plans/*.md` (all tasks complete).
- Virtual-desktop COM findings: `docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md`.
- Manual test script: `docs/superpowers/notes/manual-test-script.md`.
- SDD ledgers: `.superpowers/sdd/*/progress.md`: gitignored, carry every ruling and
  deferred minor from the original build.

## Decisions already made, do not relitigate

- **Built on Windows' virtual desktops**, never `ShowWindow(SW_HIDE)`. Hidden windows can be
  orphaned by a crash; a virtual desktop cannot lose one. This is also why the app is
  Windows-only by design rather than by omission.
- **The floating bar is the main surface** and is always on. The tray switcher panel and
  Manage's Windows tab were both deleted once the bar covered their jobs. Left-click the
  tray opens Manage; right-click gives Manage, Check for updates (#110) and Exit, plus an
  update item while one is available.
- **Exactly one global hotkey**: `Win+Ctrl+Tab` (configurable on Manage → Shortcuts),
  walking workspaces most-recently-used first, committing on modifier release. It was
  chosen by trying every `*+Tab` chord against `RegisterHotKey`: the only one with both
  its forward and reverse halves free, and it neighbours Windows' own `Win+Ctrl+←/→`.
  `Ctrl+Alt+arrows` and `Ctrl+Alt+1..9` were removed: eleven exclusive chords was too high
  a price, and the digits bound by *list position*, so reordering silently changed them. If
  keyboard direct-jump returns it must bind a NAMED chord to a workspace id
  (`Workspace.Shortcut` and `Chord` exist for it), never to an index.
- **No launching, no restore prompt.** `RehydratePrompt`, `AppLauncher`,
  `PendingPlacements`, `StartWorkspace` and friends are deleted. "no, bad, don't want
  this". The **roster stays**: it is the workspace half of placement memory.
- **Membership identity = exe path + args.** Chromium browsers key on
  `--profile-directory` plus `--app-id` (so a PWA is not the browser); Firefox is
  deliberately excluded because its args are too generic. Do not "fix" that.
- **Placement memory stands down when another live window shares the identity.** Four
  browser windows on one profile are one identity, so "put it back" has no single answer; a
  new one stays where it was opened. Memory restores an app *coming back*; it does not herd
  extra windows of one already open.
- **Own windows appear in the bar** (`WINEVENT_SKIPOWNPROCESS` is not used). Three
  carve-outs stop the app placing, renaming or rostering itself, and the bar's own hwnd
  opts out via `WindowMonitor.Ignore`.
- Bar rows: icons from the **left** edge, the workspace name in a **fixed 58 DIP gutter on
  the right** (same on every row, wrapping inside it), lane tinted per workspace by
  *position*. Every row is laid out over **every known screen** in reading order, one equal
  lane each with a hairline between, so a screen's region is at the same x on every row and
  the boundary cannot wander. Auto and shared-width gutters were both tried and rejected:
  each row then split a different width in half and the hairlines disagreed (#70, #99, #103).
- **Workspaces can be grouped** (#83, #84): a group has one colour settable from any of its
  rows, one caption, a shared lane and a bracket down its left edge. A nested workspace
  really gets its parent's windows.
- **The ordinal colour band groups windows by their ARTWORK**, fingerprinted from the icon's
  pixels (#105). Three cheaper keys were measured wrong on Petre's machine and the reasoning
  is in `IconCache.ArtworkKeyOf`: process name, the command line's `--app-id`, and the
  window's AppUserModelID. Read that comment before replacing it with something cheaper.
- **The band carries a dark rim** for the same reason the attention dot does: no palette is
  legible on artwork nobody chose (sky blue vanished on an Edge icon).

## Instrumentation, and start with it

Every wrong answer in this project has been a guess, and every right one has come from a
measurement. Three traces exist for that reason, all behind ONE switch: create the marker
file `%APPDATA%\TaskSpaces\trace.on` and restart. The log is
`%TEMP%\taskspaces-trace.log`, appended across restarts, and its first line names which
switch turned it on. An empty log, or one with no `--- trace on` line, was not recording.

- **Clicks** (`ClickTrace`, #48): press, release, row-up, switch, drag-start, drop. Its own
  file's comment carries a reading guide and five worked examples of lost clicks, each with
  the sequence that named the cause. A lost click is an ABSENCE; the missing line names the
  mechanism.
- **Row geometry** (`FloatingBar.DumpRowGeometry`): the bar's width and, per line, the
  columns it was actually given plus where each monitor mark sits. Four rounds of "put the
  hairline in the middle" were argued from screenshots; this settled the last two in a line
  each.
- **Bands and icons** (`FloatingBar.DumpBands`, `IconCache.Trace`): which windows share an
  ordinal family and by what key, and which source answered for each icon at what size.

The last two write only when their output CHANGES, so the log stays readable with tracing on
all day. Leave them in; this is the third visit to two of these bugs.

## Traps that have already cost time

- **The window list only ever loses windows.** `HIDE` does not mean gone, and
  `WINEVENT_OUTOFCONTEXT` events get dropped under load. `WindowMonitor.Resync` on the 5s
  sweep repairs the drift; its gone-for-real half must key off `IsWindow`, never "absent
  from the candidate list", or a tray-minimised window loses its rename ledger entry.
- **Never cache a lookup failure that can succeed later.** A window's icon arrives
  asynchronously, and caching the first null kept a placeholder for the window's whole life.
- **`ShowWindowAsync` is asynchronous**, and the name is the whole trap. A move issued right
  after an un-maximize ran while the window was still maximized, was ignored, and the
  re-maximize put it back: "i drag the vscode window in ec workspace to the left and it does
  nothing". Wait for the state to settle, with a bounded wait, never a synchronous
  `ShowWindow` into another process's thread.
- **A minimized window's rectangle is nonsense.** `GetWindowRect` reports the iconic
  rectangle at -32000,-32000, so anything computed from it is garbage and `SetWindowPos`
  writes a position nothing reads. Its real rectangle is `rcNormalPosition` in
  `WINDOWPLACEMENT`, in workspace coordinates rather than screen ones (#107).
- **A Chromium PWA shares the browser's process** when the browser was already running, so
  it shares its command line too. Anything per-window has to come from the WINDOW.
- **Topmost is a shared band, not a rank.** The taskbar and the Start menu are ordinary
  topmost windows, so the bar re-asserts `HWND_TOPMOST` on foreground change *and* on a 1s
  timer (a second taskbar click changes no foreground window, so the event alone is not
  enough). It is NOT a z-band problem and `uiAccess` is not needed, and that was claimed three
  times and was wrong each time.
- **Re-entrancy in the bar's rebuild.** COM calls pump the message queue on STA threads and
  `Dispatcher.Invoke` runs inline, so a rebuild can re-enter itself and double every row.
  Guarded; keep it that way.
- **Static WPF brushes must be frozen** or they take thread affinity and throw.
- An **exact-title rename lapses** the moment the app rewrites its own title. Prefer
  "Rename all *app* windows", which keys on the process.

## Open threads

- The README's hero screenshot is many versions behind: it predates groups, the caption
  gutter and the monitor hairlines, so it no longer shows what the bar looks like. It cannot
  be recaptured while Petre's own bar shows a workspace named after a colleague. **No real
  people's names in this repo**, screenshots and commit messages included.
- Commit `033f2bd` still carries a colleague's name in its message and is now on `main`, so
  fixing it means rewriting published history. Parked; Petre's call.
- No UI removes a roster entry (that lived on the deleted Windows tab), so a wrong one needs
  hand-editing `%APPDATA%\TaskSpaces\state.json`.
- `TitleToken.cs` is written and tested but wired to nothing: the "open the editor, then
  load a folder" late-placement idea.
- `Workspace.Color` has a picker now (#97: the palette, plus a Custom… entry opening
  Windows' own `ChooseColorW`); the default still comes from `WorkspacePalette` by position.
- The bar's icon lanes wrap against the bar's own width, so at a narrow width busy rows grow
  TALLER rather than wider. The three-icon floor is divided by the number of screens sharing
  the line, or it clips instead of wrapping (#58 and its regression in 1.8.0).
- Product name is still a working name.

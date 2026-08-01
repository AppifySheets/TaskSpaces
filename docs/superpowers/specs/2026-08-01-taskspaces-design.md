# TaskSpaces — Design Document

**Date:** 2026-08-01
**Status:** Approved concept, pre-implementation. UI form factor still open (see Open Questions).

## What this is

A user-friendly Windows utility that groups running applications into named workspaces
(e.g. **Work**, **Personal**, **YouTube**) and lets you switch between them with one click.
When you switch to a workspace, the taskbar shows **only that group's windows** — everything
else keeps running but disappears from view. Workspace definitions and contents survive reboots.

## Motivation

The user liked [TaskBarRenamer](https://github.com/kwaschny/TaskBarRenamer) but wants more:
grouping of taskbar icons into contexts, switching between those contexts, and persistence
across restarts — all with a friendlier UX than Windows virtual desktops / Task View.

### Research: why nothing off-the-shelf fits

| Tool | What it does | Why it's not enough |
|---|---|---|
| PowerToys Workspaces | One-click relaunch of saved app layouts; survives reboot | Launches apps; doesn't switch between live groups; other groups stay visible |
| Stardock Groupy 2 ($10) | Tabs windows together like browser tabs | Organizes within one desktop; no context switching |
| TaskbarGroups (tjackenpacken) | Taskbar flyout folders of *launcher shortcuts* | No live-window management; unmaintained |
| komorebi / GlazeWM | Named workspaces, keyboard-driven | Tiling WMs — too invasive, rejected by user ("too much") |
| Dexpot / VirtuaWin / Actual Virtual Desktops | Rule-based virtual desktop managers | Dated or abandoned |
| [awaescher/StageManager](https://github.com/awaescher/StageManager) | C# Stage Manager clone: scenes that show/hide window groups | Feasibility study, not a product — but proves the hard parts work in C# |

**Conclusion:** the polished version of this tool does not exist. Market gap.

## Core insight (architecture keystone)

The Windows 11 taskbar **natively shows only the windows of the current virtual desktop**.
So if each workspace is backed by a Windows virtual desktop, "switch workspace → taskbar
shows only that group's windows" comes for free, with zero shell hacks. Windows keep running
off-screen; switching back is instant.

Everything clunky about virtual desktops is the *chrome around them*, which is what
TaskSpaces replaces:

- No visible names / slow modal Task View → **always-available named switcher**
- Apps land on whatever desktop is active → **rules engine** auto-assigns windows to workspaces
- Assignments lost on reboot → **persistence + rehydration**

## Requirements

1. Named workspaces (Work, Personal, YouTube, …) — user-defined, editable.
2. One-click switching; on switch, only that workspace's windows appear on the taskbar.
3. Rules to auto-assign windows to workspaces by process name, window title, and
   (where detectable) browser profile. New windows fly to their group automatically.
4. Manual override: send any window to any workspace (hotkey and/or context menu).
5. Persistence across reboots:
   - Workspace definitions and rules always persist (JSON on disk).
   - Each workspace remembers what was in it; after restart, optionally relaunch a
     workspace's apps back into their group ("rehydrate").
6. User-friendly above all: no config files required, no keyboard-first workflow,
   GUI for everything. Hotkeys are optional accelerators, not the primary interface.

### Non-goals (YAGNI)

- Window tiling/layout management (PowerToys FancyZones exists).
- Renaming window titles (TaskBarRenamer exists).
- Restoring browser tab sets (browser's own session restore handles this).
- Cross-machine sync.

## Architecture

C# / .NET (latest LTS), WinUI 3 (or WPF if WinUI friction is too high) tray application.

### Components

1. **VirtualDesktopService** — wraps the virtual desktop COM API via a maintained C#
   wrapper (e.g. Slions/VirtualDesktop lineage). Creates/renames/switches desktops,
   moves windows between them. Isolates the undocumented-API risk in one place —
   the COM interfaces shift between Windows builds, so this service owns version
   detection and failure modes.
2. **WindowMonitor** — enumerates top-level windows and listens for creation/destruction
   (WinEvent hooks, surfaced as RX observables). Emits `WindowAppeared(hwnd, process, title)`.
3. **RulesEngine** — pure function: window metadata + rule list → target workspace.
   Rules matched in order: process name, title regex, browser profile. Immutable rule
   records, functional style.
4. **PersistenceStore** — JSON under `%APPDATA%\TaskSpaces\`. Stores workspace
   definitions, rules, and per-workspace window inventory (process path + args where
   recoverable) snapshotted periodically for rehydration.
5. **Switcher UI** — the user-facing surface. Form factor TBD (Open Questions):
   floating always-on-top pill, tray flyout, or docked bar. Shows workspace names +
   window counts; click to switch; right-click a window to reassign.
6. **Rehydrator** — after reboot, recreates desktops for each workspace and optionally
   relaunches its recorded apps into it (per-workspace opt-in, like a browser's
   "restore session?" prompt).

### Data flow

```
WindowAppeared (WindowMonitor, RX)
  → RulesEngine.Match(window, rules) : Result<Workspace>
  → VirtualDesktopService.MoveToDesktop(hwnd, workspace.DesktopId)
  → PersistenceStore.RecordMembership(...)

User clicks workspace in Switcher UI
  → VirtualDesktopService.Switch(workspace.DesktopId)
  → taskbar updates natively
```

### Error handling

- CSharpFunctionalExtensions `Result`/`Result<T>` for expected failures (rule mismatch,
  window vanished before move, desktop not found); exceptions only for the truly
  exceptional (COM API shape unrecognized on this Windows build).
- If the virtual desktop API is unavailable/unrecognized after an OS update, degrade
  gracefully: switcher still lists workspaces but shows a "compatibility" banner rather
  than crashing; no window moves attempted.
- Never lose a window: all mutations go through virtual desktops (windows stay in
  Alt-Tab and Task View at worst). No ShowWindow(SW_HIDE) — a crash could orphan
  hidden windows. This is the reason Architecture B (manual hide/show) was rejected.

### Testing

- RulesEngine and persistence: pure, fully unit-testable (xunit).
- VirtualDesktopService: thin integration tests behind a manual/CI-skipped trait
  (mutates real desktops).
- UI: manual test script per release; automation later if warranted.

## Open questions

1. **Switcher UI form factor** — floating pill vs. tray flyout vs. docked bar.
   To be decided with visual mockups before UI implementation.
2. Product name — "TaskSpaces" is the working name; revisit before any public release.
3. Browser-profile detection depth (Chrome/Edge command line inspection) — v1 or later.

## Next step

Invoke the `superpowers:writing-plans` skill against this spec to produce the
implementation plan, then build incrementally: VirtualDesktopService spike first
(riskiest bit), then WindowMonitor + RulesEngine, then minimal switcher UI,
then persistence/rehydration.

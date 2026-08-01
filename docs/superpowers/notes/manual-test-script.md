# TaskSpaces manual test script (per release)

Setup: build + run TaskSpaces.App; open Task View to observe desktops.

1.  Tray icon visible; menu lists workspaces + Manage… + Exit.
2.  Add workspace "Work" -> desktop "Work" exists in Task View.
3.  Add rule ProcessName=notepad -> Work; open Notepad -> it lands on Work's desktop
    (disappears from current taskbar; visible after switching to Work).
4.  Tray menu -> Work: taskbar shows only Work's windows.
5.  Windows tab: select a window, Send to workspace -> it moves.
6.  Windows tab: rename a browser window to "Amy related" -> taskbar shows "Amy related";
    navigate to another page -> short name comes back within ~a second.
7.  Restore title -> original title returns.
8.  Rules tab: add rename rule TitleRegex "Remote Desktop" -> "RDP"; open mstsc -> renamed.
9.  Rename workspace -> desktop renames in Task View. Remove workspace -> desktop closes,
    its windows merge into the previous desktop (Windows behavior), nothing is lost.
10. Exit via tray -> all renamed titles restored; desktops remain (by design).
11. Restart app -> workspaces re-bind to the same desktops (no duplicates).
12. Invalid regex in Rules tab -> message box; rules not saved; nothing crashes.
13. Start-with-Windows checkbox -> HKCU\...\Run entry appears/disappears (regedit).
14. Put Notepad in Work via a rule; exit the app (inventory persists); close Notepad;
    relaunch the app -> prompt lists "Work (1 app(s))"; Restore relaunches Notepad and
    it lands on Work's desktop.

## Results (2026-08-01)

Status: **pending human execution.** An agent cannot observe the desktop, Task View,
tray menu rendering, or perform interactive window renaming — none of the 13 checks
above can be validated without a human at the keyboard, so none are marked pass/fail
here to avoid fabricating results.

What an automated smoke run *could* verify stands in as a partial, non-visual proxy for
step 1's non-crash half and none of the others:

- `dotnet build src/TaskSpaces.App` succeeds (Debug, x64).
- `dotnet run --project src/TaskSpaces.App` launches, stays alive and responding for
  15+ seconds with no unhandled-exception output on the console.
- `%APPDATA%\TaskSpaces\state.json` is created on first run with the empty `AppState`
  shape (`Workspaces: []`, `WorkspaceRules: []`, `RenameRules: []`, `Inventory: {}`),
  confirming `VirtualDesktopService.Initialize()` succeeded (not compatibility mode on
  this machine/build) and `WorkspaceManager.Start()` completed its reconcile-and-persist
  path without throwing.
- The process was stopped cleanly (`Stop-Process`) with no crash dialog.

Task 9 (rehydration) smoke check, added in this session, same non-visual proxy, re-run
with a corrected observation method (fix round 1 — the first pass here mis-checked
`%APPDATA%\TaskSpaces\state.json` through a Bash-tool `powershell -Command "...$env:APPDATA..."`
invocation, where the surrounding POSIX shell pre-expands `$env` to empty before
`powershell` ever sees the string, silently mangling the path to `:APPDATA\...` and making
every check against it report "absent" regardless of the real file. Re-checked properly
via the PowerShell tool directly, with an explicit `$p = Join-Path $env:APPDATA ...`):
on this build/machine, both at Task 8 and at HEAD (Task 9), the app starts normally —
NOT in compatibility mode — and `%APPDATA%\TaskSpaces\state.json`'s `LastWriteTime` bumps
on every run (confirmed: `19:56:15` before this round's run, `20:00:30` after, with
content `{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}`), i.e.
`VirtualDesktopService.Initialize()` succeeds and `WorkspaceManager.Start()` completes its
reconcile-and-persist path normally. `RehydratePrompt.HasAnythingToRestore` is guarded by
`!compatibilityMode` and by an empty `Inventory`; with the inventory empty here, no prompt
should appear, and none did: the process's `MainWindowTitle` stayed empty (a shown
`RehydratePrompt` would carry the title "TaskSpaces — Restore workspaces?") throughout the
15+ second observation window, and there was no crash. This does NOT exercise the
Restore/Skip button paths or an actual relaunch — that needs the human steps in item 14
above.

| # | Check | Result |
|---|-------|--------|
| 1 | Tray icon + menu contents | pending human execution |
| 2 | Add workspace creates desktop | pending human execution |
| 3 | ProcessName rule auto-places Notepad | pending human execution |
| 4 | Tray switch shows only that workspace's windows | pending human execution |
| 5 | Manual "Send to workspace" | pending human execution |
| 6 | Rename + re-apply after title change | pending human execution |
| 7 | Restore title | pending human execution |
| 8 | TitleRegex rename rule (mstsc) | pending human execution |
| 9 | Rename/remove workspace, desktop merge | pending human execution |
| 10 | Exit restores titles, desktops persist | pending human execution |
| 11 | Restart re-binds without duplicating desktops | pending human execution |
| 12 | Invalid regex rejected with message box | pending human execution |
| 13 | Start-with-Windows registry toggle | pending human execution |
| 14 | Rehydration prompt lists app counts; Restore relaunches into the right workspace | pending human execution (non-crash proxy verified: no prompt window shown when inventory is empty) |

Re-run this script by hand before each release and replace the table above with actual
pass/fail results plus notes on the Windows build tested.

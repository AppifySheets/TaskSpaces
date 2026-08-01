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

Re-run this script by hand before each release and replace the table above with actual
pass/fail results plus notes on the Windows build tested.

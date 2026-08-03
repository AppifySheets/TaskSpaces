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
15. Left-click tray icon -> switcher panel opens near the tray, dark-themed, one group
    per workspace with window counts; current workspace bold. **Superseded by Task 9
    (items 25-26 below): left-click now opens the tray MENU; the panel is reached by
    hover or hotkey.** Re-verify panel appearance/grouping via item 25's hover instead.
16. Panel: click a window row on another workspace -> lands on that workspace with the
    window focused; panel closes.
17. Panel: right-click a window -> Pin to all workspaces -> window follows across every
    workspace and appears in the 📌 Pinned group. Unpin reverses it. Send to a
    workspace from a pinned window unpins it.
18. Panel: windows on a non-workspace desktop appear under that desktop's own name
    (e.g. "Desktop 1"), including the current desktop.
19. Panel: renamed windows show "ShortName · was: Original Title"; icons show on every
    row (panel AND Manage window's Windows tab).
20. Roster: close an app that was in a workspace -> it stays listed, dimmed
    "(not running)"; click it -> it relaunches with its original command line and lands
    back in its workspace. ▶ on the header starts everything missing and switches.
21. Add app… -> pick an exe + optional args -> appears dimmed in the group;
    Remove from workspace deletes it.
22. Rename persistence: manually rename a window, exit TaskSpaces, relaunch ->
    within ~5s the window is renamed again (same title as before rename required).
23. Rename sweep: rename a browser window, navigate tabs rapidly -> the short name
    reasserts within ~5s even if an occasional title flip slips through.
24. Restart TaskSpaces with workspace apps still open -> restore prompt does NOT
    offer duplicates (only genuinely-missing apps listed).
25. Hover the tray icon for about half a second -> the switcher panel peeks open
    WITHOUT taking focus (taskbar/whatever app you were using stays highlighted as
    active); move the mouse away from the panel -> it hides itself again. Hover once
    more and click a row/button INSIDE the peeked panel first -> from then on it
    behaves like a normal clicked-open panel (stays open, responds to child dialogs,
    Deactivate-hides when you click elsewhere).
26. Left-click the tray icon -> the tray MENU opens (same items/behavior as
    right-click); the switcher panel does NOT open from a left-click anymore.
27. Ctrl+Alt+Right / Ctrl+Alt+Left cycles through TaskSpaces workspaces in the order
    they're defined (not the native Windows desktop order), wrapping from last back
    to first (and vice versa), and skipping any plain OS desktop that isn't a
    workspace.
28. With at least two workspaces defined, Ctrl+Alt+2 jumps straight to the second
    workspace (by defined order); Ctrl+Alt+9 with fewer than 9 workspaces does nothing
    (no crash, no message box).
29. If another application already owns one of these chords (e.g. some machines bind
    Ctrl+Alt+Left/Right to a display-rotate shortcut), TaskSpaces shows exactly ONE
    warning message box at startup listing the chord(s) it could not register, and
    every other hotkey it DID register still works normally.
30. Panel/Windows tab: drag a window row onto another workspace's group -> it moves
    there (and is unpinned first if it was pinned); drag a row onto the 📌 Pinned
    group -> it pins (follows every workspace) instead of moving.
31. Manage window's Windows tab shows the exact same grouped view as the panel (same
    groups, same running/roster rows); drag-and-drop works identically there; each
    row's right-click menu covers rename, restore title, pin/unpin, and send to
    workspace.
32. Windows on an unbound/default OS desktop appear grouped under a sensibly-derived
    name for that desktop (e.g. "Desktop 1"), including whichever such desktop is
    currently active; any oddball shell/system windows that don't belong to a real
    desktop grouping appear under an "Unplaced" catch-all group.
33. The old ＋ button is gone from the workspace header; adding an app to a workspace
    is done via "Add app…" in the workspace header's right-click context menu.
34. Tray menu -> "Show floating bar" -> a small translucent, always-on-top bar appears
    with one icon row per group: Pinned first if anything is pinned, then EVERY
    workspace (an empty one shows just its label), then each unbound desktop that has
    windows, then "Unplaced" if any window's desktop can't be resolved; click an
    icon -> lands on that window (switches workspace if needed, focuses it); the bar
    stays open after the click. Click a row's dim LABEL -> switches to that
    workspace/desktop (the 📌 Pinned and "Unplaced" labels are not clickable).
35. Drag the bar somewhere else by pressing a row LABEL or the padding/info line (not
    an icon) -> it moves with the cursor; restart the app -> the bar reappears in that
    same spot. Toggle it off/on from the tray menu (checkmark reflects state) ->
    restart the app -> it stays hidden/shown exactly as last left. Right-click the bar
    -> "Hide floating bar" also hides it and persists that.
36. With the bar showing, open a new app that belongs to a workspace -> its icon
    appears in that workspace's row live, without reopening the bar; close it -> the
    icon disappears.
37. Hover any bar icon -> the info line at the bottom of the bar names that window:
    full title, then dimmed process · group (· "was: <original title>" if TaskSpaces
    renamed it). Move off the icon -> the line returns to its hint text. The bar must
    NOT resize or jump position while hovering different icons.
38. Drag a bar icon onto another row -> the row highlights and the info line reads
    "→ move to <group>" while hovering it; dropping moves that window there (workspace
    row = move + unpin if pinned; 📌 row = pin; unbound-desktop row = move out of every
    workspace). Dropping onto its own row does nothing. The "Unplaced" row never
    highlights and never accepts a drop.
39. Drag a rule-matched browser window out of its workspace onto the "Main" (unbound)
    row, then browse to a different page so its title changes -> it STAYS on Main
    (rules must not drag it back). Its app still appears in the old workspace's
    not-running roster / ▶ Start list.
40. A single CLICK on a bar icon (no movement) still jumps to that window — the new
    drag behavior must not swallow plain clicks.

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
| 15 | Superseded by Task 9 (see items 25-26): re-verify panel appearance/grouping via item 25's hover instead of left-click | n/a — superseded, see item 25 |
| 16 | Panel: click a window row -> jumps workspace + focuses, panel closes | pending human execution |
| 17 | Panel: pin/unpin via context menu; pinned group; send-to unpins | pending human execution |
| 18 | Panel: non-workspace desktop windows grouped under desktop name | pending human execution |
| 19 | Panel: renamed-window "was: ..." display; icons on every row | pending human execution |
| 20 | Roster: closed app stays dimmed "(not running)"; click relaunches; ▶ starts all | pending human execution |
| 21 | Add app… roster entry; Remove from workspace | pending human execution |
| 22 | Rename persistence across restart (sweep adopts persisted rename) | pending human execution |
| 23 | Rename sweep reasserts short name after title drift (~5s) | pending human execution |
| 24 | Restart with apps still open -> no duplicate restore offers | pending human execution |
| 25 | Hover peeks panel without stealing focus; moving away hides it; click-inside graduates it to normal behavior | pending human execution |
| 26 | Left-click tray icon opens the menu (not the panel) | pending human execution |
| 27 | Ctrl+Alt+Right/Left cycles workspaces in defined order, wrapping, skipping non-workspace desktops | pending human execution |
| 28 | Ctrl+Alt+2 (etc.) jumps directly to that workspace by defined order | pending human execution |
| 29 | A chord already owned by another app produces exactly one startup warning; other hotkeys keep working | pending human execution |
| 30 | Drag a window row onto another workspace's group -> moves (unpins if pinned); onto 📌 Pinned -> pins | pending human execution |
| 31 | Windows tab shows the same grouped view; drag works identically; right-click menu covers rename/restore/pin/send | pending human execution |
| 32 | Windows on unbound/default desktops group under a sensibly-named group (e.g. "Desktop 1"), including the current desktop; oddball shell windows appear under "Unplaced" | pending human execution |
| 33 | ＋ button is gone; Add app… lives in the workspace header's right-click menu | pending human execution |
| 34 | Floating bar shows icon rows per workspace, translucent, always on top; click icon -> lands on that window | pending human execution |
| 35 | Drag bar -> position survives app restart; tray toggle hides/shows it, state survives restart | pending human execution |
| 36 | Windows opening/closing update the bar live | pending human execution |

Re-run this script by hand before each release and replace the table above with actual
pass/fail results plus notes on the Windows build tested.

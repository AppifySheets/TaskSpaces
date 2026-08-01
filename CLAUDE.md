# TaskSpaces — session briefing

Windows utility: named workspaces (Work / Personal / YouTube) backed by Windows
virtual desktops; one-click switching (taskbar natively shows only the active
group's windows); rules auto-assign new windows; state persists across reboots.

## Current state

- Design approved by Petre, written up in
  `docs/superpowers/specs/2026-08-01-taskspaces-design.md` — **read it first**.
- No code yet. Next step per the spec: invoke `superpowers:writing-plans` to
  produce the implementation plan, then start with a VirtualDesktopService spike
  (the virtual desktop COM API is the riskiest part).

## Key decisions already made (don't relitigate)

- Build on Windows' own virtual desktops, NOT manual ShowWindow(SW_HIDE) — hidden
  windows can be orphaned by a crash; virtual desktops can't lose windows.
- User-friendly GUI first; hotkeys are optional accelerators. Tiling WMs
  (komorebi/GlazeWM) were explicitly rejected as "too much".
- Non-goals: tiling/layouts, window renaming, browser tab restore, sync.

## Open questions

- Switcher UI form factor (floating pill vs. tray flyout vs. docked bar) — decide
  with visual mockups before building UI.
- Product name ("TaskSpaces" is a working name).

# TaskSpaces

Named workspaces for the Windows taskbar. Group your running apps into contexts —
**Work**, **Personal**, **YouTube** — and switch between them with one click.
Switch to a workspace and the taskbar shows only that group's windows; everything
else keeps running out of sight. Workspaces survive reboots.

> **Status:** design phase. No code yet — see the
> [design document](docs/superpowers/specs/2026-08-01-taskspaces-design.md).

## Why

Windows virtual desktops can already separate contexts, but the experience around
them is clunky: no visible names, slow Task View, apps open on whatever desktop is
active, and nothing survives a restart. Existing tools each cover a slice
(PowerToys Workspaces relaunches layouts, Groupy tabs windows, TaskbarGroups groups
shortcuts) but nothing does *live context switching with persistence*. TaskSpaces
fills that gap.

## How it works (planned)

- Each workspace is backed by a real Windows virtual desktop, so taskbar filtering
  is native — no shell hacks, windows never get lost.
- A small always-available switcher shows your workspace names and window counts;
  one click switches context.
- A rules engine auto-assigns new windows to the right workspace (by process,
  title, browser profile).
- Workspace contents are persisted; after a reboot, a workspace can be rehydrated —
  its apps relaunched back into their group.

## Tech

C# / .NET, WinUI 3 tray application, virtual desktop COM API, RX for window events,
CSharpFunctionalExtensions for railway-style error handling.

*<sub>Collaboration by Claude</sub>*

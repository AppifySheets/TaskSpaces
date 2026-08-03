<p align="center">
  <img src="docs/branding/taskspaces-mark-256.png" width="128" alt="TaskSpaces" />
</p>

<h1 align="center">TaskSpaces</h1>

<p align="center"><em>One click switches context — the taskbar follows.</em></p>

Named workspaces for the Windows taskbar. Group your running apps into contexts —
**Work**, **Personal**, **YouTube** — and switch between them with one click.
Switch to a workspace and the taskbar shows only that group's windows; everything
else keeps running out of sight. Workspaces survive reboots.

## Install

**Requirements:** Windows 11 (build 22000 or newer) on x64. Nothing else — the
download bundles the .NET runtime, so there is no framework to install first.

1. Download `TaskSpaces-1.0.0-win-x64.exe` from the
   [latest release](https://github.com/AppifySheets/TaskSpaces/releases/latest).
2. Put it somewhere permanent — `C:\Users\<you>\Programs\TaskSpaces\` is a good
   choice. **Not** your Downloads folder: see the note below.
3. Double-click it. Nothing appears to happen — TaskSpaces opens no window at
   startup, it goes straight to the notification area (the tray). Look for the
   tiled icon there. (It does have windows: **Manage** and the switcher panel open
   when you ask for them.)
4. Right- or left-click the tray icon to open the menu, then **Manage…** to create
   your first workspace.
5. In Manage, tick **Start TaskSpaces with Windows** if you want it always running.

There is no installer yet, and no admin rights are needed. TaskSpaces writes
nothing outside your own user profile:

| What | Where |
|---|---|
| Your workspaces, rules and window names | `%APPDATA%\TaskSpaces\state.json` |
| "Start with Windows", when enabled | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |

> **Why the file's location matters.** "Start with Windows" records the *path* of the
> exe you ran it from. If you later move or delete that file, Windows will keep
> trying to launch it from the old place and TaskSpaces will silently stop starting.
> Pick a home for it before enabling that option.

**To uninstall:** untick "Start with Windows", exit from the tray menu, delete the
exe, and — if you want your settings gone too — delete `%APPDATA%\TaskSpaces`.

### "Windows protected your PC"

The executable is **not code-signed yet**, so SmartScreen will warn you the first
time you run it. Click **More info → Run anyway**. If that is not acceptable in your
environment, build it from source instead:

```powershell
git clone https://github.com/AppifySheets/TaskSpaces.git
cd TaskSpaces
dotnet publish src/TaskSpaces.App/TaskSpaces.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o artifacts/publish
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). The result is a
single ~74 MB exe in `artifacts/publish`.

## Using it

- **Tray icon** — hover it to peek at every window across every workspace; click for
  the menu.
- **Switcher panel** — click a row to jump straight to that window, wherever it
  lives. Drag rows between workspaces. Right-click for pin, send-to, rename and
  restore-title.
- **Floating bar** — an always-on-top, icon-only strip with one row per workspace.
  Click an icon to jump to that window, drag icons between rows to move windows,
  right-click an icon to rename it, hover to see what it actually is. The focused
  window's icon is highlighted. Toggle it from the tray menu.
- **Hotkeys** — `Ctrl+Alt+←/→` cycles workspaces, `Ctrl+Alt+1…9` jumps to one
  directly.
- **Renaming** — give a window a short name so the taskbar shows `RDP` instead of
  `Remote Desktop Manager [_Richard - fhd]`. Names persist across restarts and are
  re-applied when an app rewrites its own title.
- **Rosters** — a workspace remembers which apps belong to it even when they are
  closed, so ▶ Start relaunches them all.

## Why

**→ [The longer answer, with the research behind it](docs/why-taskspaces.md)** — what the
evidence on context switching and task resumption actually says, which widely-quoted
statistic to distrust, and where the argument for this app is weak.

Windows virtual desktops can already separate contexts, but the experience around
them is clunky: no visible names, slow Task View, apps open on whatever desktop is
active, and nothing survives a restart. Existing tools each cover a slice
(PowerToys Workspaces relaunches layouts, Groupy tabs windows, TaskbarGroups groups
shortcuts) but nothing does *live context switching with persistence*. TaskSpaces
fills that gap.

## How it works

- Each workspace is backed by a real Windows virtual desktop, so taskbar filtering
  is native — no shell hacks, and a crash can never lose a window. (Hiding windows
  with `ShowWindow` was considered and rejected for exactly that reason.)
- Where a window belongs is remembered by **what it is**, not by its window handle:
  identity is its executable path plus arguments, so `rider64 A.sln` and
  `rider64 B.sln` are different things. Chromium browsers key on their profile.
- **Your last placement wins.** Drag a window somewhere and that is where it goes
  next time; rules only decide for windows with no history.
- Rules can auto-assign windows you have never placed, by process name, title
  regex or browser profile.
- Everything is persisted, so a reboot can relaunch a workspace's apps back into
  their group.

## Tech

C# / .NET 10, WPF tray application (Fluent theming, follows your system light/dark
mode), the Windows virtual desktop COM API via Slions.VirtualDesktop, WinEvent hooks
plus RX for window lifecycle events, and CSharpFunctionalExtensions for
railway-style error handling.

The domain is deliberately COM-free and fully unit-tested — 186 tests, of which 179
run without touching Windows at all.

## Development

```powershell
dotnet build TaskSpaces.sln
dotnet test TaskSpaces.sln --filter "Category!=Integration"
```

The `Category=Integration` tests are excluded from that command on purpose: they
create, switch and delete **real** virtual desktops on the machine running them. Run
them deliberately, not while you are working.

*<sub>Collaboration by Claude</sub>*

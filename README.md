<p align="center">
  <img src="docs/branding/taskspaces-mark-256.png" width="128" alt="TaskSpaces" />
</p>

<h1 align="center">TaskSpaces</h1>

<p align="center"><em>One click switches context. The taskbar follows.</em></p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2011%20%C2%B7%20x64-0078D4" alt="Platform: Windows 11, x64" />
  <img src="https://img.shields.io/github/v/release/AppifySheets/TaskSpaces" alt="Latest release" />
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" />
</p>

> **Windows only, by design rather than by omission.** TaskSpaces is built directly on
> Windows' virtual-desktop COM API, the taskbar's native per-desktop window filtering, and
> WinEvent hooks. macOS and Linux organise windows on fundamentally different models, so
> there is no port waiting to be written. If you are not on Windows 11, this is not the tool
> for you.

Named workspaces for the Windows taskbar. Group your running apps into contexts
(**Work**, **Personal**, **YouTube**) and switch between them with one click.
Switch to a workspace and the taskbar shows only that group's windows; everything
else keeps running out of sight. Workspaces survive reboots.

## Install

**Requirements:** Windows 11 (build 22000 or newer) on x64. Nothing else: the
download bundles the .NET runtime, so there is no framework to install first.

1. Download `TaskSpaces-1.0.0-win-x64.exe` from the
   [latest release](https://github.com/AppifySheets/TaskSpaces/releases/latest).
2. Put it somewhere permanent. `C:\Users\<you>\Programs\TaskSpaces\` is a good
   choice. **Not** your Downloads folder: see the note below.
3. Double-click it. Nothing appears to happen, because TaskSpaces opens no window at
   startup; it goes straight to the notification area (the tray). Look for the
   tiled icon there.
4. **Left-click the tray icon** to open Manage, the main window, and create your first
   workspace. (Right-click gives you just Manage and Exit.)
5. In Manage, tick **Start TaskSpaces with Windows** if you want it always running,
   and **Show floating bar** for the always-on-top strip.

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
exe, and, if you want your settings gone too, delete `%APPDATA%\TaskSpaces`.

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

- **Tray icon.** Left-click opens **Manage**, the main window. Right-click gives you
  just Manage and Exit.
- **Floating bar.** An always-on-top, icon-only strip with one row per workspace, and
  the surface you will actually live in. Click an icon to jump to that window wherever
  it lives, drag icons between rows to move windows between workspaces, click a row
  label to switch to it, right-click an icon to rename it, hover to see what an icon
  actually is. The focused window's icon is highlighted. Turn it on and off in Manage.
- **Manage.** Workspaces (add, rename, remove, reorder) and naming patterns. Opened by
  left-clicking the tray icon.
- **Hotkeys.** `Ctrl+Alt+←/→` cycles workspaces, `Ctrl+Alt+1…9` jumps to one
  directly.
- **Renaming.** Give a window a short name so the taskbar shows `RDP` instead of
  `Remote Desktop Manager [server-01 - fhd]`. Names persist across restarts and are
  re-applied when an app rewrites its own title.
- **Rosters.** A workspace remembers which apps belong to it even when they are
  closed, and offers to relaunch them after a reboot.

## Why this matters

Switching between projects is expensive, and the expensive part is not the switch.
It is **rebuilding the context you had before**. Here is what the research actually
says about that, including the parts that argue against this app.

### The unit people think in is the project, not the window

[González and Mark](https://dl.acm.org/doi/10.1145/985692.985707) followed analysts,
developers and managers through their working days and found that people organise work
into **"working spheres"**: thematically connected units, each with its own documents,
tools and people. Workers spent roughly **three minutes on a single event** before
switching, and a little over two minutes on any one document or tool.

A taskbar shows you windows. Your head is organised by spheres. That mismatch is the gap.
Mark's later work found the fragmentation getting finer still: average time on a single
screen fell from about **2.5 minutes in 2004 to roughly 47 seconds**.

### Resuming is the costly half, and it is mostly *searching*

The most directly relevant study is
[Parnin and Rugaber's](https://link.springer.com/article/10.1007/s11219-010-9104-9)
analysis of **10,000 recorded programming sessions from 86 developers**, plus a survey of
414 more. Two numbers stand out:

- only **10%** of sessions resume programming activity within a minute of an interruption;
- only **7%** involve *no* navigation to other locations before editing resumes.

After a switch, most of the time goes on re-finding things rather than doing the work.
That is the cost a workspace tool can actually attack.

### Your windows are external memory, not clutter

The same study names the strategy developers invent for themselves, **cue priming**:
deliberately leaving the last edited window open, or highlighting the relevant lines, so
that returning to the task triggers recall.

This is the crux of the whole argument. **A window arrangement is externalised mental
state.** Every open window is a deliberate cue about where you were. So anything that
scatters the arrangement destroys the cue, and anything that preserves it preserves the
context for free. TaskSpaces does not help you rebuild context; it stops the context
being demolished.

### Which is what virtual desktops were invented for, in 1986

[Henderson and Card](https://dl.acm.org/doi/10.1145/24054.24056) built *Rooms* at Xerox
PARC to attack what they named **"window thrashing"**: the state where the screen is too
small for the work, so the user "must expend considerable effort to keep desired windows
visible". Their fix was multiple virtual workspaces, exploiting the fact that window
access clusters by task.

Forty years on, Windows ships virtual desktops that solve the *space* problem and barely
touch the *context* problem: no visible names, no memory of what belongs where, and
nothing survives a reboot. Existing tools each cover a slice (PowerToys Workspaces
relaunches layouts, Groupy tabs windows, TaskbarGroups groups shortcuts), but none does
*live context switching with persistence*.

### Attention residue: why unfinished work keeps charging you

[Leroy](https://ideas.repec.org/a/eee/jobhdp/v109y2009i2p168-181.html) found that when
people switch tasks, part of their attention stays with the previous one, and they
perform measurably worse on the new task as a result. The effect is strongest when the
previous task was **unfinished**, time-pressured or emotionally engaging, and it does not
fade after a moment's adjustment.

Fifteen taskbar buttons from four projects are fifteen reminders of things you have not
finished. **Stated honestly:** Leroy studied cognitive residue, not taskbars. That
visible unfinished work sustains residue is a reasonable inference, not a measured
finding.

### How that maps onto what the app does

| The research says | What TaskSpaces does |
|---|---|
| Work is organised in *spheres*, not windows | Workspaces are named, first-class things you switch between |
| Only 10% of resumptions are fast; the rest are re-finding | A switch restores an entire context at once, with nothing to re-find |
| People leave windows open as recall cues | The cue *is* the workspace, preserved and persisted |
| Window thrashing wastes effort keeping the right windows visible | Other contexts' windows are on another desktop, natively filtered out of the taskbar |
| Unfinished work stays costly while it is in view | Other projects are genuinely out of sight, not merely minimised |
| Fragmentation is getting finer | A switch is one click or one hotkey |

And the features that exist because manual organisation decays the moment it needs
upkeep: **placement memory** (where you last put a window is where it goes next time,
keyed to what the app *is* rather than to a window handle), **rosters and rehydration**
(a workspace remembers its apps even when they are closed), and **renaming** (`RDP` is
parsed faster than `Remote Desktop Manager [server-01 - fhd]`, and retrieval cues work
better when they are legible).

### Who it is for, and who it is not

The benefit scales with **how many unrelated contexts you hold at once**, not with how
hard you work: several projects with distinct toolchains, switching on someone else's
schedule, work and personal life on one machine, contexts that live for days.

If you work on one thing at a time and close it when you are done, this solves a problem
you do not have.

### Where the argument is weak

1. **Cheaper switching is not less switching.** The research suggests *frequency* drives
   the stress. This lowers the cost per switch and may even encourage more of them.
2. **No study measures TaskSpaces.** Everything above is adjacent research on
   interruption, resumption and window management; the step from "resumption is mostly
   re-finding" to "therefore this helps" is mechanism-level reasoning, not evidence about
   this product.
3. **Windows already provides the mechanism.** The claim is not that this invents context
   isolation, only that it makes it nameable, persistent and automatic.

### Sources

- Victor M. González and Gloria Mark, ["Constant, constant, multi-tasking craziness": Managing multiple working spheres](https://dl.acm.org/doi/10.1145/985692.985707), CHI 2004. Working spheres, and the ~3-minutes-per-event figure.
- Gloria Mark, Victor M. González and Justin Harris, [No Task Left Behind? Examining the Nature of Fragmented Work](https://ics.uci.edu/~gmark/CHI2005.pdf), CHI 2005. The fragmentation of knowledge work.
- Gloria Mark, *Attention Span* (Hanover Square Press, 2023). The 2.5-minutes-to-47-seconds figure.
- Chris Parnin and Spencer Rugaber, [Resumption strategies for interrupted programming tasks](https://link.springer.com/article/10.1007/s11219-010-9104-9), Software Quality Journal 19(1), 2011 ([PDF](http://www.chrisparnin.me/pdf/parnin-sqj11.pdf)). The 10% and 7% resumption figures, and cue priming.
- D. Austin Henderson and Stuart K. Card, [Rooms: the use of multiple virtual workspaces to reduce space contention in a window-based graphical user interface](https://dl.acm.org/doi/10.1145/24054.24056), ACM Transactions on Graphics 5(3), 1986 ([PDF](http://rivcons.com/wp-content/uploads/1987/Rooms-TOG.pdf)). Window thrashing.
- Sophie Leroy, [Why is it so hard to do my work? The challenge of attention residue when switching between work tasks](https://ideas.repec.org/a/eee/jobhdp/v109y2009i2p168-181.html), Organizational Behavior and Human Decision Processes 109(2), 2009. Attention residue.

## How it works

- Each workspace is backed by a real Windows virtual desktop, so taskbar filtering
  is native, with no shell hacks, and a crash can never lose a window. (Hiding windows
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

The domain is deliberately COM-free and fully unit-tested: 186 tests, of which 179
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

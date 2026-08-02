# Switcher Panel, Pinning & Workspace Roster Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A tray-summoned switcher panel showing all windows across all workspaces (jump/pin/rename/start from one place), per-window pin-to-all-workspaces, durable content-based workspace rosters with one-click workspace start, persistent self-healing renames, and workspace visibility + icons in the Manage window.

**Architecture:** Extends the existing v1 codebase in place. New pure Core logic (roster identity, overview builder, persisted renames, start/jump orchestration) lands TDD-first behind the existing fakes; the Windows layer gains three thin wrappers (pin, activate, batch command lines); the WPF app gains the SwitcherPanel window and small helpers (icon cache, app launcher, prompt dialog). Spec: `docs/superpowers/specs/2026-08-01-switcher-roster-design.md` — read it first.

**Tech Stack:** unchanged — .NET 10, WPF (Fluent ThemeMode=System already enabled), Slions.VirtualDesktop, System.Reactive, CSharpFunctionalExtensions 3.7.0, System.Management, xunit.

## Global Constraints

- **Petre has put the PR on hold: NEVER push, never create a PR. Commit locally on branch `worktree-taskspaces-v1` only.** Do not create new branches.
- Every commit message ends with the line: `*Collaboration by Claude*`
- Existing suite is 63 Core tests + 3 Windows integration tests (Category=Integration, mutate real desktops — run only where a task says so). Default verification: `dotnet test --filter "Category!=Integration"`. All existing tests stay green except where a task explicitly says a v1 test's behavior is superseded (Task 4 updates exactly one).
- CSharpFunctionalExtensions 3.7.0: `Maybe.Execute` is obsolete → use `.Tap`. Result/Maybe for expected failures/absences; exceptions only for the exceptional.
- Functional style, immutable records, expression bodies, `var`, no braces on single-statement `if`, `private` implied, ample intention comments.
- Never `ShowWindow(SW_HIDE)` anywhere; `SW_RESTORE` for un-minimizing is fine.
- Fire-and-forget Result discards in the event pipeline carry an explicit comment (existing convention in `WorkspaceManager`).
- The manual test script (`docs/superpowers/notes/manual-test-script.md`) gains items per UI task; never fabricate pass results — mark "pending human execution".
- The app may be running while you work (Petre tests it). Before a smoke run: `Stop-Process -Name TaskSpaces.App -Force -ErrorAction SilentlyContinue`, rebuild Release, relaunch `src/TaskSpaces.App/bin/Release/net10.0-windows10.0.19041.0/TaskSpaces.App.exe`, verify alive ~10s, and LEAVE IT RUNNING at the end of UI tasks (Petre keeps testing).

## File Structure

```
src/TaskSpaces.Core/
  Abstractions/IVirtualDesktopService.cs      # MODIFY: +Pin/Unpin/IsPinned/CurrentDesktop (Task 1)
  Abstractions/IWindowActivator.cs            # NEW (Task 5): jump plumbing seam
  Abstractions/IAppLauncher.cs                # NEW (Task 5): launch seam (Core can't Process.Start)
  Rehydration/CommandLines.cs                 # NEW (Task 2): args-of-command-line (from Rehydrator)
  Rehydration/RosterIdentity.cs               # NEW (Task 2): path+args / path+profile identity
  Rehydration/PendingPlacements.cs            # MODIFY (Task 2): identity tiebreak
  Rehydration/RehydrationFilter.cs            # DELETE (Task 6): subsumed by NotRunningRoster
  Persistence/PersistedRename.cs              # NEW (Task 3)
  Persistence/AppState.cs                     # MODIFY (Task 3): +PersistedRenames init property
  Overview/Overview.cs                        # NEW (Task 5): WindowRow/WorkspaceGroup/DesktopGroup/Overview
  Overview/OverviewBuilder.cs                 # NEW (Task 5): pure grouping
  WorkspaceManager.cs                         # MODIFY (Tasks 2,3,4,5)
src/TaskSpaces.Windows/
  Desktops/VirtualDesktopService.cs           # MODIFY (Task 1): pin + current desktop
  Monitoring/NativeMethods.cs                 # MODIFY (Task 6): activate + cursor P/Invoke
  Monitoring/WindowInfoFactory.cs             # MODIFY (Task 2): cmdline for ALL windows + batch query
  Monitoring/WindowMonitor.cs                 # MODIFY (Task 2): snapshot uses batch query
  Activation/WindowActivator.cs               # NEW (Task 6)
src/TaskSpaces.App/
  AppLauncher.cs                              # NEW (Task 6): IAppLauncher via Process.Start
  Rehydrator.cs                               # DELETE (Task 6): logic moves to AppLauncher/manager
  RehydratePrompt.xaml.cs                     # MODIFY (Task 6): NotRunningRoster + StartRosterEntry
  IconCache.cs                                # NEW (Task 7): exe → frozen 16px ImageSource
  PromptDialog.xaml(.cs)                      # NEW (Task 7): tiny text-input dialog
  SwitcherPanel.xaml(.cs)                     # NEW (Task 7): THE panel
  App.xaml.cs                                 # MODIFY (Task 7): left-click, sweep timer, panel wiring
  ManageWindow.xaml(.cs)                      # MODIFY (Task 8): icons + Workspace column + both names
tests/TaskSpaces.Core.Tests/                  # new test files per task; Fakes.cs extended (Task 1, 5)
tests/TaskSpaces.Windows.Tests/               # +pin integration test (Task 1), +activator (Task 6)
```

Dependency order: 1 and 2 are independent; 3 and 4 need 2; 5 needs 1+2+4; 6 needs 5; 7 needs 3+5+6; 8 needs 5+7 (IconCache).

---

### Task 1: Pin + current-desktop support in the desktop service

**Files:**
- Modify: `src/TaskSpaces.Core/Abstractions/IVirtualDesktopService.cs`
- Modify: `src/TaskSpaces.Windows/Desktops/VirtualDesktopService.cs`
- Modify: `tests/TaskSpaces.Core.Tests/Fakes.cs` (FakeDesktops)
- Test: `tests/TaskSpaces.Windows.Tests/VirtualDesktopServiceTests.cs` (add one integration test)

**Interfaces:**
- Consumes: existing `IVirtualDesktopService`, `WindowHandle`; spike doc confirms the wrapper exposes `VirtualDesktop.PinWindow(IntPtr)` / `UnpinWindow(IntPtr)` / `IsPinnedWindow(IntPtr)` and `VirtualDesktop.Current` (see `docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md`, "Not exercised by this spike" section).
- Produces (binding — Tasks 5+ consume): `Result Pin(WindowHandle window); Result Unpin(WindowHandle window); Result<bool> IsPinned(WindowHandle window); Result<Guid> CurrentDesktop();` on `IVirtualDesktopService`; `FakeDesktops.PinnedWindows : HashSet<WindowHandle>` and `FakeDesktops.CurrentDesktopId : Guid` (settable) in tests.

- [ ] **Step 1: Extend the interface**

Append inside `IVirtualDesktopService` (after `DesktopOf`):

```csharp
    // Pin = "this window exists on ALL desktops" (Windows-native). Per-window and
    // user-decided (spec: RDP manager always pinned; Beeper "when I say"). Pin state
    // lives in the OS only — nothing persisted, nothing to reconcile after reboot.
    Result Pin(WindowHandle window);
    Result Unpin(WindowHandle window);
    Result<bool> IsPinned(WindowHandle window);

    // The desktop the user is looking at right now — the overview needs it to mark
    // the current workspace and to skip a no-op Switch when jumping.
    Result<Guid> CurrentDesktop();
```

- [ ] **Step 2: Implement in VirtualDesktopService**

Append inside the class (before `Find`), matching the file's existing Result.Try style:

```csharp
    public Result Pin(WindowHandle window) =>
        Result.Try(() => VirtualDesktop.PinWindow(window.Value),
            e => $"Could not pin window {window.Value} (it may have closed): {e.Message}");

    public Result Unpin(WindowHandle window) =>
        Result.Try(() => VirtualDesktop.UnpinWindow(window.Value),
            e => $"Could not unpin window {window.Value}: {e.Message}");

    public Result<bool> IsPinned(WindowHandle window) =>
        Result.Try(() => VirtualDesktop.IsPinnedWindow(window.Value),
            e => $"Could not query pin state of window {window.Value}: {e.Message}");

    public Result<Guid> CurrentDesktop() =>
        Result.Try(() => VirtualDesktop.Current.Id,
            e => $"Could not determine the current desktop: {e.Message}");
```

If member names differ from the wrapper's actual surface, fix against IntelliSense and record the corrections in your report (the spike doc lists them as `PinWindow`/`UnpinWindow`/`IsPinnedWindow`).

- [ ] **Step 3: Extend FakeDesktops**

In `tests/TaskSpaces.Core.Tests/Fakes.cs`, add to `FakeDesktops`:

```csharp
    public HashSet<WindowHandle> PinnedWindows { get; } = [];
    public Guid CurrentDesktopId { get; set; } = Guid.NewGuid();

    public Result Pin(WindowHandle w) { PinnedWindows.Add(w); return Result.Success(); }
    public Result Unpin(WindowHandle w) { PinnedWindows.Remove(w); return Result.Success(); }
    public Result<bool> IsPinned(WindowHandle w) => PinnedWindows.Contains(w);
    public Result<Guid> CurrentDesktop() => CurrentDesktopId;
```

- [ ] **Step 4: Build + run unit suite**

Run: `dotnet build` then `dotnet test --filter "Category!=Integration"`
Expected: clean build, 63/63 (no behavior change yet — the fake just satisfies the interface).

- [ ] **Step 5: Add the pin integration test**

Append to `tests/TaskSpaces.Windows.Tests/VirtualDesktopServiceTests.cs` (inside the existing `[Trait("Category", "Integration")]` class):

```csharp
    [Fact]
    public void Pin_roundtrip_on_a_real_window()
    {
        var service = new VirtualDesktopService();
        Assert.True(service.Initialize().IsSuccess);

        var winver = System.Diagnostics.Process.Start("winver.exe");
        try
        {
            while (winver.MainWindowHandle == 0) { Thread.Sleep(100); winver.Refresh(); }
            var handle = new TaskSpaces.Core.Domain.WindowHandle(winver.MainWindowHandle);

            Assert.False(service.IsPinned(handle).Value);
            Assert.True(service.Pin(handle).IsSuccess);
            Assert.True(service.IsPinned(handle).Value);
            output.WriteLine("pinned OK — check visually: winver should now follow desktop switches");
            Assert.True(service.Unpin(handle).IsSuccess);
            Assert.False(service.IsPinned(handle).Value);

            Assert.True(service.CurrentDesktop().IsSuccess);
        }
        finally { if (!winver.HasExited) winver.Kill(); }
    }
```

Run: `dotnet test tests/TaskSpaces.Windows.Tests --filter "Category=Integration"` (live desktop/window mutation authorized)
Expected: PASS (now 4 integration tests).

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: pin-to-all-desktops and current-desktop queries in desktop service

*Collaboration by Claude*"
```

---

### Task 2: Roster identity — command lines for all windows, shared helpers, smarter pending placement

Membership is per-window and content-based (spec): identity = exe path + arguments (browsers: path + profile). This task builds the identity primitives and widens command-line capture from browsers-only to every window.

**Files:**
- Create: `src/TaskSpaces.Core/Rehydration/CommandLines.cs`, `src/TaskSpaces.Core/Rehydration/RosterIdentity.cs`
- Modify: `src/TaskSpaces.Core/Rehydration/PendingPlacements.cs`, `src/TaskSpaces.Core/WorkspaceManager.cs` (RegisterPendingLaunch signature), `src/TaskSpaces.Windows/Monitoring/WindowInfoFactory.cs`, `src/TaskSpaces.Windows/Monitoring/WindowMonitor.cs` (Snapshot), `src/TaskSpaces.App/Rehydrator.cs` (use the shared helpers)
- Test: `tests/TaskSpaces.Core.Tests/CommandLinesTests.cs`, `tests/TaskSpaces.Core.Tests/RosterIdentityTests.cs`, `tests/TaskSpaces.Core.Tests/PendingPlacementsTests.cs` (add one)

**Interfaces:**
- Consumes: `InventoryEntry`, `WindowInfo`, `BrowserProfile.FromCommandLine` (Core.Rules), existing `PendingPlacements`.
- Produces (binding):
  - `CommandLines.ArgumentsOf(string? commandLine, string processPath) : string`
  - `RosterIdentity.Of(string processPath, string? commandLine) : string`; `RosterIdentity.Of(InventoryEntry) : string`; `RosterIdentity.Of(WindowInfo) : Maybe<string>`; `RosterIdentity.IsRunning(InventoryEntry, IEnumerable<WindowInfo>) : bool`
  - `PendingPlacements.Add(int processId, string processPath, Guid workspaceId, DateTimeOffset now, string? commandLine = null)`
  - `WorkspaceManager.RegisterPendingLaunch(int processId, string processPath, Guid workspaceId, string? commandLine = null)`
  - `WindowInfoFactory.FromHwnd(nint hwnd, IReadOnlyDictionary<uint, string>? commandLines = null)`; `WindowInfoFactory.AllCommandLines() : IReadOnlyDictionary<uint, string>`

- [ ] **Step 1: Write failing tests**

`tests/TaskSpaces.Core.Tests/CommandLinesTests.cs`:

```csharp
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

public class CommandLinesTests
{
    [Theory]
    [InlineData("\"C:\\Tools\\app.exe\" --flag value", @"C:\Tools\app.exe", "--flag value")]
    [InlineData(@"C:\Tools\app.exe --flag", @"C:\Tools\app.exe", "--flag")]
    [InlineData("\"C:\\Tools\\app.exe\"", @"C:\Tools\app.exe", "")]
    [InlineData(null, @"C:\Tools\app.exe", "")]
    [InlineData("", @"C:\Tools\app.exe", "")]
    [InlineData(@"D:\other\thing.exe --x", @"C:\Tools\app.exe", "")] // unknown exe prefix -> bare
    public void Extracts_arguments(string? commandLine, string path, string expected) =>
        Assert.Equal(expected, CommandLines.ArgumentsOf(commandLine, path));
}
```

`tests/TaskSpaces.Core.Tests/RosterIdentityTests.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

public class RosterIdentityTests
{
    static WindowInfo Window(string path, string? cmd) =>
        new(new WindowHandle(1), 42, System.IO.Path.GetFileNameWithoutExtension(path), path, "t", cmd);

    [Fact]
    public void Same_app_different_content_is_different_identity() =>
        Assert.NotEqual(
            RosterIdentity.Of(@"C:\rider\rider64.exe", "\"C:\\rider\\rider64.exe\" C:\\repos\\X\\X.sln"),
            RosterIdentity.Of(@"C:\rider\rider64.exe", "\"C:\\rider\\rider64.exe\" C:\\repos\\Y\\Y.sln"));

    [Fact]
    public void Identity_is_case_insensitive_and_quote_insensitive() =>
        Assert.Equal(
            RosterIdentity.Of(@"C:\Rider\Rider64.exe", "\"C:\\Rider\\Rider64.exe\" C:\\Repos\\X\\X.sln"),
            RosterIdentity.Of(@"c:\rider\rider64.exe", @"c:\rider\rider64.exe c:\repos\x\x.sln"));

    [Fact]
    public void Browser_identity_is_profile_not_full_args()
    {
        // Chromium browsers spray session-specific args; only the profile identifies content.
        var a = RosterIdentity.Of(@"C:\chrome\chrome.exe", "\"C:\\chrome\\chrome.exe\" --profile-directory=\"Profile 2\" --restore-session");
        var b = RosterIdentity.Of(@"C:\chrome\chrome.exe", "\"C:\\chrome\\chrome.exe\" --profile-directory=\"Profile 2\" --flag-switches-begin");
        Assert.Equal(a, b);
        Assert.NotEqual(a, RosterIdentity.Of(@"C:\chrome\chrome.exe", "\"C:\\chrome\\chrome.exe\" --profile-directory=Default"));
    }

    [Fact]
    public void Window_without_process_path_has_no_identity() =>
        Assert.True(RosterIdentity.Of(Window(@"C:\a.exe", null) with { ProcessPath = null }).HasNoValue);

    [Fact]
    public void IsRunning_matches_identity_not_just_path()
    {
        var entry = new InventoryEntry(@"C:\rider\rider64.exe", "\"C:\\rider\\rider64.exe\" X.sln", "X");
        var otherContent = Window(@"C:\rider\rider64.exe", "\"C:\\rider\\rider64.exe\" Y.sln");
        Assert.False(RosterIdentity.IsRunning(entry, [otherContent]));
        Assert.True(RosterIdentity.IsRunning(entry, [otherContent, Window(@"C:\rider\rider64.exe", "\"C:\\rider\\rider64.exe\" X.sln")]));
    }
}
```

Append to `tests/TaskSpaces.Core.Tests/PendingPlacementsTests.cs`:

```csharp
    [Fact]
    public void Two_pendings_same_exe_different_args_route_by_args()
    {
        var personal = Guid.NewGuid();
        var pending = PendingPlacements.Empty
            .Add(500, @"C:\rider\rider64.exe", Work, T0, "\"C:\\rider\\rider64.exe\" X.sln")
            .Add(501, @"C:\rider\rider64.exe", personal, T0, "\"C:\\rider\\rider64.exe\" Y.sln");

        // Window arrives with a pid we never launched (IDE splash handed off), but its
        // command line identifies which pending launch it belongs to.
        var window = new WindowInfo(new WindowHandle(0x10), 999, "rider64", @"C:\rider\rider64.exe", "Y", "\"C:\\rider\\rider64.exe\" Y.sln");
        Assert.Equal(personal, pending.Match(window, T0.AddSeconds(5)).WorkspaceId.Value);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/TaskSpaces.Core.Tests --filter "CommandLines|RosterIdentity|PendingPlacements"`
Expected: FAIL — `CommandLines`/`RosterIdentity` don't exist; new PendingPlacements overload missing.

- [ ] **Step 3: Implement Core pieces**

`src/TaskSpaces.Core/Rehydration/CommandLines.cs` (logic lifted verbatim from `Rehydrator.StripExecutable` — it moves here so roster identity and the launcher share one definition):

```csharp
namespace TaskSpaces.Core.Rehydration;

// A recorded command line is the ORIGINAL full line ("exe" args...). The exe part is
// noise for both relaunching (ProcessStartInfo takes args separately) and identity
// (quoting differs between captures) — this strips it, leaving only the arguments.
public static class CommandLines
{
    public static string ArgumentsOf(string? commandLine, string processPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";
        var trimmed = commandLine.TrimStart();
        // Quoted form: "C:\path\app.exe" args   Unquoted form: C:\path\app.exe args
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            return close < 0 ? "" : trimmed[(close + 1)..].TrimStart();
        }
        return trimmed.StartsWith(processPath, StringComparison.OrdinalIgnoreCase)
            ? trimmed[processPath.Length..].TrimStart()
            : ""; // command line doesn't start with the known exe — safer to treat as bare
    }
}
```

`src/TaskSpaces.Core/Rehydration/RosterIdentity.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Rehydration;

// THE content-based membership key (spec: "every app may belong to workspace A or B,
// depending on what's being shown"). rider64.exe X.sln and rider64.exe Y.sln are
// different identities; two chrome windows of the same profile are the same identity.
public static class RosterIdentity
{
    // Chromium browsers spray session-specific arguments (--restore-session, flag
    // switches...) that vary run to run — only --profile-directory identifies content.
    static readonly IReadOnlySet<string> Browsers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "vivaldi", "opera" };

    public static string Of(string processPath, string? commandLine)
    {
        var exe = Path.GetFileNameWithoutExtension(processPath);
        var content = Browsers.Contains(exe)
            ? BrowserProfile.FromCommandLine(commandLine).Map(p => $"profile:{p}").GetValueOrDefault("")
            : CommandLines.ArgumentsOf(commandLine, processPath);
        return $"{processPath.ToLowerInvariant()}|{content.ToLowerInvariant()}";
    }

    public static string Of(InventoryEntry entry) => Of(entry.ProcessPath, entry.CommandLine);

    // A window with no readable process path (elevated) can't be identified or relaunched.
    public static Maybe<string> Of(WindowInfo window) =>
        window.ProcessPath is null ? Maybe<string>.None : Of(window.ProcessPath, window.CommandLine);

    // "Running anywhere counts": Rider-on-X sitting in ANOTHER workspace still means
    // Start must not launch a duplicate of it.
    public static bool IsRunning(InventoryEntry entry, IEnumerable<WindowInfo> windows) =>
        windows.Any(w => Of(w).Map(id => id == Of(entry)).GetValueOrDefault(false));
}
```

`PendingPlacements.cs` — change the record, `Add`, and `Match`:

```csharp
    sealed record Pending(int ProcessId, string ProcessPath, Guid WorkspaceId, DateTimeOffset LaunchedAt, string? CommandLine);
```

```csharp
    public PendingPlacements Add(int processId, string processPath, Guid workspaceId, DateTimeOffset now, string? commandLine = null) =>
        new(entries.Add(new Pending(processId, processPath, workspaceId, now, commandLine)));

    // Match priority: exact pid -> content identity (path+args — separates two launches
    // of the same exe with different solutions) -> bare path (browsers hand the window
    // to an existing process AND rewrite their args, so identity may not survive).
    public (PendingPlacements Remaining, Maybe<Guid> WorkspaceId) Match(WindowInfo window, DateTimeOffset now)
    {
        var alive = entries.RemoveAll(p => now - p.LaunchedAt > Ttl);
        var hit = alive.FirstOrDefault(p => p.ProcessId == window.ProcessId)
                  ?? alive.FirstOrDefault(p => window.ProcessPath is not null
                        && RosterIdentity.Of(p.ProcessPath, p.CommandLine) == RosterIdentity.Of(window.ProcessPath, window.CommandLine))
                  ?? alive.FirstOrDefault(p => p.ProcessPath.Equals(window.ProcessPath, StringComparison.OrdinalIgnoreCase));
        return hit is null
            ? (new PendingPlacements(alive), Maybe<Guid>.None)
            : (new PendingPlacements(alive.Remove(hit)), hit.WorkspaceId);
    }
```

`WorkspaceManager.RegisterPendingLaunch` — widen (signature stays compatible with existing callers):

```csharp
    // Rehydrator/StartWorkspace tell us "pid X (path Y, args Z) belongs to workspace W,
    // expect it soon". The command line lets two same-exe launches route separately.
    public void RegisterPendingLaunch(int processId, string processPath, Guid workspaceId, string? commandLine = null) =>
        pending = pending.Add(processId, processPath, workspaceId, now(), commandLine);
```

`Rehydrator.cs` — two line-level changes: replace the `StripExecutable(entry.CommandLine, entry.ProcessPath)` call with `CommandLines.ArgumentsOf(entry.CommandLine, entry.ProcessPath)` (add `using TaskSpaces.Core.Rehydration;`), delete the now-unused private `StripExecutable`, and pass the command line through: `manager.RegisterPendingLaunch(process.Id, entry.ProcessPath, workspaceId, entry.CommandLine);`

- [ ] **Step 4: Widen command-line capture in the Windows layer**

`WindowInfoFactory.cs` — delete the `Browsers` set (it lives in `RosterIdentity` now) and replace `FromHwnd` + add `AllCommandLines`:

```csharp
    public static Maybe<WindowInfo> FromHwnd(nint hwnd, IReadOnlyDictionary<uint, string>? commandLines = null)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return Maybe<WindowInfo>.None;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = TryPath(process);
            // Command line for EVERY window now (roster identity is path+args, not just
            // browser profiles). Per-event single WMI lookup ~10ms — fine at human window-
            // opening rates; the startup snapshot passes a prefetched batch instead.
            var commandLine = commandLines is not null
                ? commandLines.GetValueOrDefault(pid)
                : TryCommandLine(pid);
            return new WindowInfo(new WindowHandle(hwnd), (int)pid, process.ProcessName, path, TitleOf(hwnd), commandLine);
        }
        catch (ArgumentException) { return Maybe<WindowInfo>.None; } // process already gone
    }

    // One WMI round-trip for ALL processes — the startup snapshot enumerates dozens of
    // windows; per-window queries there would cost seconds on the dispatcher thread.
    public static IReadOnlyDictionary<uint, string> AllCommandLines()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
            return searcher.Get().Cast<ManagementBaseObject>()
                .Where(o => o["CommandLine"] is string { Length: > 0 })
                .ToDictionary(o => (uint)o["ProcessId"], o => (string)o["CommandLine"]);
        }
        catch (Exception) { return new Dictionary<uint, string>(); } // best-effort, like TryCommandLine
    }
```

`WindowMonitor.Snapshot()` — prefetch once (adjust the existing body):

```csharp
    public IReadOnlyList<WindowInfo> Snapshot()
    {
        var commandLines = WindowInfoFactory.AllCommandLines(); // one WMI query, not one per window
        return TopLevelWindows.Enumerate()
            .Select(h => WindowInfoFactory.FromHwnd(h, commandLines))
            .Where(m => m.HasValue).Select(m => m.Value)
            .ToList();
    }
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test --filter "Category!=Integration"` and `dotnet build`
Expected: all green (63 + 11 new = 74), clean build.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: content-based roster identity; command lines captured for all windows

*Collaboration by Claude*"
```

---

### Task 3: Persistent renames + re-apply sweep (TDD)

Petre: renames must survive app restarts and be re-asserted "every several seconds" even if a title-change event was missed.

**Files:**
- Create: `src/TaskSpaces.Core/Persistence/PersistedRename.cs`
- Modify: `src/TaskSpaces.Core/Persistence/AppState.cs`, `src/TaskSpaces.Core/WorkspaceManager.cs`
- Test: `tests/TaskSpaces.Core.Tests/PersistedRenameTests.cs`

**Interfaces:**
- Consumes: `RenameLedger`, `IWindowTitles` (FakeTitles has `Titles` dict; `Get` returns `""` for unknown handles), `AppState`, fakes.
- Produces (binding):
  - `record PersistedRename(string ProcessName, string OriginalTitle, string ShortName)`
  - `AppState.PersistedRenames : IReadOnlyList<PersistedRename>` (init property, defaults `[]` — old state.json files load unchanged)
  - `WorkspaceManager.ReapplyRenames() : void` (Task 7's App timer calls it every ~5s; `Start()` calls it once)

- [ ] **Step 1: Write failing tests**

`tests/TaskSpaces.Core.Tests/PersistedRenameTests.cs`:

```csharp
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class PersistedRenameTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static WindowInfo Chrome(nint hwnd = 0x10, string title = "Some Page - Chrome") =>
        new(new WindowHandle(hwnd), 100, "chrome", @"C:\chrome.exe", title, null);

    [Fact]
    public void Manual_rename_is_persisted_with_the_original_title()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.True(manager.RenameWindow(new WindowHandle(0x10), "Amy related").IsSuccess);

        var persisted = store.Stored.PersistedRenames.Single();
        Assert.Equal(("chrome", "Some Page - Chrome", "Amy related"), (persisted.ProcessName, persisted.OriginalTitle, persisted.ShortName));
    }

    [Fact]
    public void Restore_removes_the_persisted_rename()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        manager.RenameWindow(new WindowHandle(0x10), "Amy related");

        Assert.True(manager.RestoreTitle(new WindowHandle(0x10)).IsSuccess);

        Assert.Empty(store.Stored.PersistedRenames);
    }

    [Fact]
    public void Rule_based_renames_are_not_persisted_as_manual_entries()
    {
        var manager = Started();
        manager.SetRules([], [new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related")]);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        Assert.Empty(store.Stored.PersistedRenames); // the rule itself is already persistent
    }

    [Fact]
    public void After_restart_a_matching_window_gets_its_persisted_rename_back()
    {
        // Session 1: rename, then the app "exits" (manager discarded, store survives).
        var first = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        first.RenameWindow(new WindowHandle(0x10), "Amy related");
        titles.Titles.Clear();

        // Session 2: same store; the window is already open with its NATURAL title
        // (RestoreAllTitles put it back on exit), so it arrives via the snapshot.
        var monitor2 = new FakeMonitor();
        monitor2.InitialWindows.Add(Chrome(title: "Some Page - Chrome"));
        var second = new WorkspaceManager(desktops, monitor2, titles, store);
        Assert.True(second.Start().IsSuccess);

        Assert.Equal("Amy related", titles.Titles[new WindowHandle(0x10)]);
    }

    [Fact]
    public void After_restart_a_window_whose_title_drifted_stays_untouched()
    {
        var first = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        first.RenameWindow(new WindowHandle(0x10), "Amy related");
        titles.Titles.Clear();

        var monitor2 = new FakeMonitor();
        monitor2.InitialWindows.Add(Chrome(title: "Completely Different Page - Chrome"));
        var second = new WorkspaceManager(desktops, monitor2, titles, store);
        Assert.True(second.Start().IsSuccess);

        Assert.Empty(titles.Titles); // identity heuristic failed -> hands off (spec's known limit)
    }

    [Fact]
    public void Sweep_reapplies_a_drifted_title_even_without_an_event()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        manager.RenameWindow(new WindowHandle(0x10), "Amy related");

        // The app rewrote its title but the NAMECHANGE event was missed entirely:
        // only the OS-side title (what titles.Get returns) shows the drift.
        titles.Titles[new WindowHandle(0x10)] = "Drifted Page - Chrome";

        manager.ReapplyRenames();

        Assert.Equal("Amy related", titles.Titles[new WindowHandle(0x10)]);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/TaskSpaces.Core.Tests --filter PersistedRename`
Expected: FAIL — `PersistedRename`, `AppState.PersistedRenames`, `ReapplyRenames` don't exist.

- [ ] **Step 3: Implement**

`src/TaskSpaces.Core/Persistence/PersistedRename.cs`:

```csharp
namespace TaskSpaces.Core.Persistence;

// A MANUAL window rename, made durable. Identity across app restarts is heuristic —
// hwnds die with the session, so the best stable key is "same app, same title it had
// when Petre renamed it". A window whose natural title has since changed (browser
// navigated elsewhere) will not match, and deliberately stays untouched.
public sealed record PersistedRename(string ProcessName, string OriginalTitle, string ShortName);
```

`AppState.cs` — add inside the record body (NOT a positional parameter — an init property with a default keeps every existing `new AppState(...)` call site and every pre-existing state.json loading unchanged):

```csharp
    // Manual renames that survive restarts (spec §Persistence). Init property with a
    // default so older state.json files (no such key) deserialize to empty, no migration.
    public IReadOnlyList<PersistedRename> PersistedRenames { get; init; } = [];
```

`WorkspaceManager.cs` — three changes:

Replace `RenameWindow`:

```csharp
    public Result RenameWindow(WindowHandle window, string shortName) =>
        knownWindows.TryGetValue(window, out var info)
            ? ApplyRename(info, shortName)
                // Manual renames persist (spec: survive restarts). Rule-based renames never
                // pass through here — the rule itself is already durable. Keyed by process +
                // the title the window had before ANY rename (ledger's original).
                .Tap(() =>
                {
                    var original = ledger.OriginalTitle(window).GetValueOrDefault(info.Title);
                    Persist(State with
                    {
                        PersistedRenames =
                        [
                            .. State.PersistedRenames.Where(r => !(r.ProcessName.Equals(info.ProcessName, StringComparison.OrdinalIgnoreCase) && r.OriginalTitle == original)),
                            new PersistedRename(info.ProcessName, original, shortName),
                        ],
                    });
                })
            : Result.Failure("Window no longer exists.");
```

Replace `RestoreTitle`:

```csharp
    public Result RestoreTitle(WindowHandle window) =>
        ledger.OriginalTitle(window)
            .ToResult("Window was never renamed.")
            .Bind(original => titles.Set(window, original)
                .Tap(() =>
                {
                    ledger = ledger.Remove(window);
                    // Also forget the durable form, else the sweep would re-rename it seconds later.
                    var processName = knownWindows.TryGetValue(window, out var info) ? info.ProcessName : null;
                    var remaining = State.PersistedRenames
                        .Where(r => !(r.OriginalTitle == original && (processName is null || r.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))))
                        .ToList();
                    if (remaining.Count != State.PersistedRenames.Count)
                        Persist(State with { PersistedRenames = remaining });
                }));
```

Add `ReapplyRenames` (after `RestoreAllTitles`) and call it at the end of `Start()`'s `.Tap(...)` block (after the `subscription = ...` line, add `ReapplyRenames();`):

```csharp
    // The safety-net sweep (Petre: "applying those renamed titles every several
    // seconds"). Event-driven NAMECHANGE re-apply is the fast path; this catches missed
    // events AND adopts persisted renames after a restart. App calls it on a ~5s timer.
    public void ReapplyRenames()
    {
        // 1. Active renames whose on-screen title drifted without us hearing about it.
        //    Fire-and-forget Sets: a hung/closed window just misses this sweep round.
        ledger.Handles.ToList().ForEach(h =>
            titles.Get(h).Tap(current =>
            {
                if (ledger.NeedsReapply(h, current))
                    ledger.AppliedName(h).Tap(name => { titles.Set(h, name); });
            }));

        // 2. Persisted renames not yet active this session (the restart case): adopt any
        //    window whose process + current title exactly match a recorded rename.
        knownWindows.Values
            .Where(w => ledger.AppliedName(w.Handle).HasNoValue)
            .ToList()
            .ForEach(w => State.PersistedRenames
                .TryFirst(r => r.ProcessName.Equals(w.ProcessName, StringComparison.OrdinalIgnoreCase) && r.OriginalTitle == w.Title)
                .Tap(r => { ApplyRename(w, r.ShortName); }));
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: all green (74 + 6 = 80). If `Manual_rename_and_restore_roundtrip` (v1) fails, your RestoreTitle rewrite broke the Set-before-ledger ordering — fix the implementation, not the test.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: manual renames persist across restarts and re-apply on a sweep

*Collaboration by Claude*"
```

---

### Task 4: Durable roster lifecycle + late placement (TDD)

**Files:**
- Modify: `src/TaskSpaces.Core/WorkspaceManager.cs`
- Test: `tests/TaskSpaces.Core.Tests/RosterTests.cs`; Modify: `tests/TaskSpaces.Core.Tests/WorkspaceManagerTests.cs` (one superseded test)

**Interfaces:**
- Consumes: `RosterIdentity` (Task 2), existing manager internals.
- Produces (binding):
  - Roster survives window close; identity moves between workspaces on re-placement.
  - `WorkspaceManager.RemoveRosterEntry(Guid workspaceId, InventoryEntry entry) : Result`
  - `WorkspaceManager.AddRosterEntry(Guid workspaceId, string exePath, string? arguments) : Result<InventoryEntry>`
  - Late placement: unplaced windows re-run workspace rules on TitleChanged.
  - `RemoveWorkspace` prunes `memberships` (fixes the deferred phantom-inventory minor).

- [ ] **Step 1: Update the one superseded v1 test**

In `WorkspaceManagerTests.cs`, the v1 test `Disappeared_window_leaves_inventory` asserts the old ephemeral-inventory behavior. Replace it (same location):

```csharp
    [Fact]
    public void Disappeared_window_keeps_its_roster_entry()
    {
        // Superseded v1 behavior: inventory used to be "currently running members" and
        // emptied on close. The roster spec inverts this on purpose — a workspace lists
        // what BELONGS to it even when it isn't running (that's what ▶ Start launches).
        var (manager, work) = StartedWithWorkWorkspace();
        manager.SetRules([new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "chrome")], []);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Chrome()));
        Assert.Contains(store.Stored.Inventory[work.Id], e => e.ProcessPath == @"C:\chrome.exe");
    }
```

- [ ] **Step 2: Write the new failing tests**

`tests/TaskSpaces.Core.Tests/RosterTests.cs`:

```csharp
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class RosterTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    (WorkspaceManager Manager, Workspace Work, Workspace Personal) Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        var work = manager.AddWorkspace("Work").Value;
        var personal = manager.AddWorkspace("Personal").Value;
        return (manager, work, personal);
    }

    static WindowInfo Rider(nint hwnd, string sln, string title) =>
        new(new WindowHandle(hwnd), 100, "rider64", @"C:\rider\rider64.exe", title, $"\"C:\\rider\\rider64.exe\" {sln}");

    [Fact]
    public void Same_identity_moves_between_workspaces_never_duplicates()
    {
        var (manager, work, personal) = Started();
        var window = Rider(0x10, "X.sln", "X");

        manager.AssignWindow(NextAppeared(window), work.Id);
        Assert.Single(store.Stored.Inventory[work.Id]);

        manager.AssignWindow(window.Handle, personal.Id);
        Assert.Empty(store.Stored.Inventory[work.Id]);           // moved, not copied
        Assert.Single(store.Stored.Inventory[personal.Id]);
    }

    [Fact]
    public void Different_content_same_app_rosters_in_different_workspaces()
    {
        var (manager, work, personal) = Started();
        manager.AssignWindow(NextAppeared(Rider(0x10, "X.sln", "X")), work.Id);
        manager.AssignWindow(NextAppeared(Rider(0x11, "Y.sln", "Y")), personal.Id);
        Assert.Single(store.Stored.Inventory[work.Id]);
        Assert.Single(store.Stored.Inventory[personal.Id]);
    }

    [Fact]
    public void Manual_add_and_remove_roster_entry()
    {
        var (manager, work, _) = Started();
        var added = manager.AddRosterEntry(work.Id, @"C:\Tools\gitextensions.exe", "browse C:\\repos\\X");
        Assert.True(added.IsSuccess);
        Assert.Single(store.Stored.Inventory[work.Id]);

        Assert.True(manager.RemoveRosterEntry(work.Id, added.Value).IsSuccess);
        Assert.Empty(store.Stored.Inventory[work.Id]);
    }

    [Fact]
    public void Late_placement_moves_an_unplaced_window_when_its_title_changes()
    {
        var (manager, work, _) = Started();
        manager.SetRules([new WorkspaceRule(work.Id, RuleMatchKind.TitleRegex, "TaskSpaces")], []);

        var bare = Rider(0x10, "", "JetBrains Rider");            // opened bare: no rule matches
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, bare));
        Assert.Empty(desktops.WindowPlacements);

        // Petre loads the solution -> Rider rewrites its title -> NOW the rule matches.
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, bare with { Title = "TaskSpaces – rider" }));
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[bare.Handle]);
    }

    [Fact]
    public void Placed_windows_are_never_re_placed_by_title_changes()
    {
        var (manager, work, personal) = Started();
        manager.SetRules([new WorkspaceRule(personal.Id, RuleMatchKind.TitleRegex, "Sparrow")], []);

        var window = Rider(0x10, "X.sln", "TaskSpaces – rider");
        manager.AssignWindow(NextAppeared(window), work.Id);      // Petre put it in Work by hand

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, window with { Title = "Sparrow – rider" }));
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[window.Handle]); // stayed put
    }

    [Fact]
    public void RemoveWorkspace_prunes_memberships_no_phantom_roster_resurrection()
    {
        var (manager, work, _) = Started();
        var window = Rider(0x10, "X.sln", "X");
        manager.AssignWindow(NextAppeared(window), work.Id);

        Assert.True(manager.RemoveWorkspace(work.Id).IsSuccess);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, window));

        Assert.False(store.Stored.Inventory.ContainsKey(work.Id)); // deleted stays deleted
    }

    WindowHandle NextAppeared(WindowInfo window)
    {
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        return window.Handle;
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/TaskSpaces.Core.Tests --filter "Roster|Disappeared_window_keeps"`
Expected: FAIL — `AddRosterEntry`/`RemoveRosterEntry` missing; move/survive/late-placement semantics absent.

- [ ] **Step 4: Implement in WorkspaceManager**

Replace `PersistInventory(Guid)` with roster operations:

```csharp
    // Roster (spec): a workspace lists the apps that BELONG to it even when they are
    // not running. An entry is added/updated when a window is PLACED here and SURVIVES
    // the window closing; identity = path+args (browser: path+profile), and the same
    // identity landing in another workspace MOVES (a window can't belong to two —
    // last placement wins). Entries leave only via user removal or workspace deletion.
    void RosterAdd(WindowInfo window, Guid workspaceId)
    {
        if (window.ProcessPath is null) return; // elevated/inaccessible: nothing relaunchable to remember
        AddEntry(workspaceId, new InventoryEntry(window.ProcessPath, window.CommandLine,
            ledger.OriginalTitle(window.Handle).GetValueOrDefault(window.Title)));
    }

    void AddEntry(Guid workspaceId, InventoryEntry entry)
    {
        var identity = RosterIdentity.Of(entry);
        var inventory = State.Inventory.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<InventoryEntry>)kv.Value.Where(e => RosterIdentity.Of(e) != identity).ToList());
        inventory[workspaceId] = [.. inventory.GetValueOrDefault(workspaceId, []), entry];
        Persist(State with { Inventory = inventory });
    }

    public Result<InventoryEntry> AddRosterEntry(Guid workspaceId, string exePath, string? arguments) =>
        Result.FailureIf(string.IsNullOrWhiteSpace(exePath), "Executable path required")
            .Bind(() => Workspace(workspaceId))
            .Map(_ => new InventoryEntry(
                exePath,
                string.IsNullOrWhiteSpace(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}",
                Path.GetFileNameWithoutExtension(exePath)))
            .Tap(entry => AddEntry(workspaceId, entry));

    public Result RemoveRosterEntry(Guid workspaceId, InventoryEntry entry)
    {
        var identity = RosterIdentity.Of(entry);
        var current = State.Inventory.GetValueOrDefault(workspaceId, []);
        var remaining = current.Where(e => RosterIdentity.Of(e) != identity).ToList();
        if (remaining.Count == current.Count) return Result.Failure("That app is no longer in this workspace's list.");
        var inventory = State.Inventory.ToDictionary(kv => kv.Key, kv => kv.Value);
        inventory[workspaceId] = remaining;
        Persist(State with { Inventory = inventory });
        return Result.Success();
    }
```

Add `using TaskSpaces.Core.Rehydration;` if not present. Update `Place` (roster instead of PersistInventory):

```csharp
    Result Place(WindowInfo window, Guid workspaceId) =>
        Workspace(workspaceId)
            .Bind(w => w.DesktopId is { } desktopId
                ? desktops.MoveWindow(window.Handle, desktopId)
                : Result.Failure("Workspace has no desktop (compatibility mode)."))
            .Tap(() =>
            {
                memberships[window.Handle] = workspaceId;
                RosterAdd(window, workspaceId);
            });
```

Update `OnHidden` and `OnDisappeared` — the roster no longer reacts to windows leaving (update the OnHidden comment's "in inventory" wording accordingly):

```csharp
    void OnHidden(WindowInfo window)
    {
        knownWindows.Remove(window.Handle);
        memberships.Remove(window.Handle); // roster entry stays — that's the point (spec)
    }

    void OnDisappeared(WindowInfo window)
    {
        knownWindows.Remove(window.Handle);
        ledger = ledger.Remove(window.Handle);
        memberships.Remove(window.Handle); // roster entry stays — ▶ Start relaunches it
    }
```

Add late placement at the END of `OnTitleChanged` (after the rename if/else):

```csharp
        // Late placement (spec): a window that appeared bare may only now reveal what it
        // is showing — Rider loading a solution rewrites its title. Only UNPLACED windows
        // are eligible: once placed (rule, launch, or hand), a title change must never
        // teleport a window between workspaces (browsers rewrite titles every tab switch).
        if (!memberships.ContainsKey(window.Handle))
            RulesEngine.MatchWorkspace(window, State.WorkspaceRules)
                .Tap(workspaceId => { Place(window, workspaceId); }); // fire-and-forget, as above
```

Update `RemoveWorkspace` — add memberships pruning inside the final `.Tap`:

```csharp
            .Tap(w =>
            {
                // Prune live bookkeeping too: without this, a later Disappeared for one of
                // this workspace's windows would resurrect a phantom inventory key.
                memberships.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()
                    .ForEach(h => memberships.Remove(h));
                Persist(State with
                {
                    Workspaces = State.Workspaces.Where(x => x.Id != id).ToList(),
                    WorkspaceRules = State.WorkspaceRules.Where(r => r.WorkspaceId != id).ToList(),
                    Inventory = State.Inventory.Where(kv => kv.Key != id).ToDictionary(kv => kv.Key, kv => kv.Value),
                });
            });
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test --filter "Category!=Integration"`
Expected: all green (80 + 7 new = 87; the superseded test replaced 1:1). The v1 test `Appeared_window_matching_rule_is_moved_and_inventoried` must still pass — RosterAdd covers it.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: durable content-based workspace rosters and late placement

*Collaboration by Claude*"
```

---

### Task 5: Overview query, jump, pin semantics, start-workspace (TDD)

**Files:**
- Create: `src/TaskSpaces.Core/Abstractions/IWindowActivator.cs`, `src/TaskSpaces.Core/Abstractions/IAppLauncher.cs`, `src/TaskSpaces.Core/Overview/Overview.cs`, `src/TaskSpaces.Core/Overview/OverviewBuilder.cs`
- Modify: `src/TaskSpaces.Core/WorkspaceManager.cs`, `tests/TaskSpaces.Core.Tests/Fakes.cs`
- Test: `tests/TaskSpaces.Core.Tests/OverviewTests.cs`

**Interfaces:**
- Consumes: Task 1 (Pin/IsPinned/CurrentDesktop on service + fakes), Task 2 (RosterIdentity), Task 4 (roster).
- Produces (binding — Tasks 6/7/8 consume exactly these):
  - `interface IWindowActivator { Result Activate(WindowHandle window); }`
  - `interface IAppLauncher { Maybe<int> Launch(InventoryEntry entry); }`
  - `record WindowRow(WindowInfo Window, Maybe<string> OriginalTitle)`
  - `record WorkspaceGroup(Workspace Workspace, bool IsCurrent, IReadOnlyList<WindowRow> Running, IReadOnlyList<InventoryEntry> NotRunning)`
  - `record DesktopGroup(Guid DesktopId, string Name, bool IsCurrent, IReadOnlyList<WindowRow> Windows)`
  - `record Overview(IReadOnlyList<WindowRow> Pinned, IReadOnlyList<WorkspaceGroup> Workspaces, IReadOnlyList<DesktopGroup> OtherDesktops)`
  - Manager: `Result<Overview> WindowsByWorkspace()`, `Result PinWindow(WindowHandle)`, `Result UnpinWindow(WindowHandle)`, `Result JumpTo(WindowHandle, IWindowActivator)`, `IReadOnlyList<InventoryEntry> NotRunningRoster(Guid)`, `Result StartRosterEntry(Guid, InventoryEntry, IAppLauncher)`, `Result StartWorkspace(Guid, IAppLauncher)`
  - Fakes: `FakeActivator { List<WindowHandle> Activated }`, `FakeLauncher { List<InventoryEntry> Launched }` (returns pids 9000, 9001, …)

- [ ] **Step 1: Write the abstractions and overview types**

`src/TaskSpaces.Core/Abstractions/IWindowActivator.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// Bring a window to the foreground (un-minimizing if needed). Windows-layer concern
// (SetForegroundWindow); abstracted so JumpTo is testable with a fake.
public interface IWindowActivator
{
    Result Activate(WindowHandle window);
}
```

`src/TaskSpaces.Core/Abstractions/IAppLauncher.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Abstractions;

// Launch a roster entry (Process.Start lives in the App layer — Core stays pure).
// Maybe: launching is best-effort; None = "didn't happen" (moved exe, denied, ...).
public interface IAppLauncher
{
    Maybe<int> Launch(InventoryEntry entry);
}
```

`src/TaskSpaces.Core/Overview/Overview.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Overview;

// One row per live window. OriginalTitle is present when WE renamed it — the UI shows
// both names (Petre: "show me what the new name is vs the original title").
public sealed record WindowRow(WindowInfo Window, Maybe<string> OriginalTitle);

// A workspace's slice of the world: live windows + roster entries not running anywhere.
public sealed record WorkspaceGroup(Workspace Workspace, bool IsCurrent, IReadOnlyList<WindowRow> Running, IReadOnlyList<InventoryEntry> NotRunning);

// A desktop that is NOT a TaskSpaces workspace still has a name ("Desktop 1") — its
// windows group under that name, never under a generic "Unassigned" (Petre's ask).
public sealed record DesktopGroup(Guid DesktopId, string Name, bool IsCurrent, IReadOnlyList<WindowRow> Windows);

public sealed record Overview(IReadOnlyList<WindowRow> Pinned, IReadOnlyList<WorkspaceGroup> Workspaces, IReadOnlyList<DesktopGroup> OtherDesktops);
```

`src/TaskSpaces.Core/Overview/OverviewBuilder.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Overview;

// Pure: all OS facts (pin states, desktop-of, desktop list, current) arrive as data,
// so every grouping rule is unit-testable without a single COM call.
public static class OverviewBuilder
{
    public static Core.Overview.Overview Build(
        AppState state,
        IReadOnlyList<WindowInfo> windows,
        Func<WindowHandle, Maybe<string>> originalTitleOf,
        ISet<WindowHandle> pinned,
        IReadOnlyDictionary<WindowHandle, Guid> desktopOf,
        IReadOnlyList<DesktopInfo> desktops,
        Guid currentDesktopId)
    {
        WindowRow Row(WindowInfo w) => new(w, originalTitleOf(w.Handle));

        var pinnedRows = windows.Where(w => pinned.Contains(w.Handle)).Select(Row).ToList();

        List<WindowRow> OnDesktop(Guid desktopId) => windows
            .Where(w => !pinned.Contains(w.Handle) && desktopOf.TryGetValue(w.Handle, out var d) && d == desktopId)
            .Select(Row).ToList();

        var workspaceGroups = state.Workspaces
            .Select(ws => new WorkspaceGroup(
                ws,
                ws.DesktopId == currentDesktopId,
                ws.DesktopId is { } id ? OnDesktop(id) : [],
                state.Inventory.GetValueOrDefault(ws.Id, []).Where(e => !RosterIdentity.IsRunning(e, windows)).ToList()))
            .ToList();

        var claimed = state.Workspaces.Where(w => w.DesktopId is not null).Select(w => w.DesktopId!.Value).ToHashSet();
        var otherDesktops = desktops
            .Where(d => !claimed.Contains(d.Id))
            .Select(d => new DesktopGroup(d.Id, d.Name, d.Id == currentDesktopId, OnDesktop(d.Id)))
            .Where(g => g.Windows.Count > 0) // an empty unbound desktop is noise, not information
            .ToList();

        return new(pinnedRows, workspaceGroups, otherDesktops);
    }
}
```

- [ ] **Step 2: Extend fakes**

Append to `tests/TaskSpaces.Core.Tests/Fakes.cs`:

```csharp
public sealed class FakeActivator : IWindowActivator
{
    public List<WindowHandle> Activated { get; } = [];
    public Result Activate(WindowHandle w) { Activated.Add(w); return Result.Success(); }
}

public sealed class FakeLauncher : IAppLauncher
{
    public List<InventoryEntry> Launched { get; } = [];
    int nextPid = 9000;
    public Maybe<int> Launch(InventoryEntry entry) { Launched.Add(entry); return nextPid++; }
}
```

(Add `using TaskSpaces.Core.Persistence;` to Fakes.cs if missing.)

- [ ] **Step 3: Write failing tests**

`tests/TaskSpaces.Core.Tests/OverviewTests.cs`:

```csharp
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class OverviewTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeActivator activator = new();
    readonly FakeLauncher launcher = new();

    (WorkspaceManager Manager, Workspace Work) Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, manager.AddWorkspace("Work").Value);
    }

    static WindowInfo App(nint hwnd, string name = "notepad", string? path = @"C:\notepad.exe", string title = "Notes") =>
        new(new WindowHandle(hwnd), 100, name, path, title, path is null ? null : $"\"{path}\"");

    WindowHandle Appear(WindowInfo w) { monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, w)); return w.Handle; }

    [Fact]
    public void Overview_groups_pinned_workspace_and_other_desktop_windows()
    {
        var (manager, work) = Started();

        var inWork = Appear(App(0x10));
        manager.AssignWindow(inWork, work.Id);
        desktops.WindowPlacements[inWork] = work.DesktopId!.Value;

        var pinnedW = Appear(App(0x11, name: "mstsc", path: @"C:\mstsc.exe", title: "RDP Manager"));
        manager.PinWindow(pinnedW);

        var elsewhere = Appear(App(0x12, name: "paint", path: @"C:\paint.exe", title: "Doodle"));
        var strayDesktop = desktops.Create("Desktop 1").Value;   // an OS desktop no workspace owns
        desktops.WindowPlacements[elsewhere] = strayDesktop.Id;
        desktops.CurrentDesktopId = strayDesktop.Id;             // Petre is currently ON it

        var overview = manager.WindowsByWorkspace().Value;

        Assert.Equal(pinnedW, overview.Pinned.Single().Window.Handle);
        var workGroup = overview.Workspaces.Single(g => g.Workspace.Id == work.Id);
        Assert.Equal(inWork, workGroup.Running.Single().Window.Handle);
        Assert.False(workGroup.IsCurrent);
        var other = overview.OtherDesktops.Single();
        Assert.Equal(("Desktop 1", true), (other.Name, other.IsCurrent)); // named by the desktop, current flagged
        Assert.Equal(elsewhere, other.Windows.Single().Window.Handle);
    }

    [Fact]
    public void Overview_shows_original_title_for_renamed_windows()
    {
        var (manager, work) = Started();
        var h = Appear(App(0x10, title: "myserver - Remote Desktop"));
        manager.AssignWindow(h, work.Id);
        desktops.WindowPlacements[h] = work.DesktopId!.Value;
        manager.RenameWindow(h, "RDP");

        var row = manager.WindowsByWorkspace().Value.Workspaces.Single().Running.Single();
        Assert.Equal("myserver - Remote Desktop", row.OriginalTitle.Value);
    }

    [Fact]
    public void NotRunning_uses_identity_and_running_anywhere_suppresses()
    {
        var (manager, work) = Started();
        manager.AddRosterEntry(work.Id, @"C:\rider\rider64.exe", "X.sln");
        manager.AddRosterEntry(work.Id, @"C:\rider\rider64.exe", "Y.sln");

        // X.sln is running — in NO workspace at all — Y.sln is not.
        Appear(new WindowInfo(new WindowHandle(0x20), 7, "rider64", @"C:\rider\rider64.exe", "X", "\"C:\\rider\\rider64.exe\" X.sln"));

        var notRunning = manager.NotRunningRoster(work.Id);
        Assert.Contains("Y.sln", notRunning.Single().CommandLine);
    }

    [Fact]
    public void StartWorkspace_launches_only_missing_registers_pending_and_switches()
    {
        var (manager, work) = Started();
        manager.AddRosterEntry(work.Id, @"C:\Tools\gitextensions.exe", "browse");
        Appear(App(0x10, name: "devenv", path: @"C:\devenv.exe"));
        manager.AddRosterEntry(work.Id, @"C:\devenv.exe", null); // this identity is bare-devenv...
        // ...but the live window's command line is also bare "C:\devenv.exe" -> running.

        Assert.True(manager.StartWorkspace(work.Id, launcher).IsSuccess);

        Assert.Equal(@"C:\Tools\gitextensions.exe", launcher.Launched.Single().ProcessPath);
        Assert.Equal([work.DesktopId!.Value], desktops.Switches.TakeLast(1).ToArray());

        // The launched app's window arrives -> pending placement routes it to Work,
        // even though no rule matches it.
        Appear(new WindowInfo(new WindowHandle(0x30), 9000, "gitextensions", @"C:\Tools\gitextensions.exe", "GE", "\"C:\\Tools\\gitextensions.exe\" browse"));
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[new WindowHandle(0x30)]);
    }

    [Fact]
    public void JumpTo_switches_to_the_windows_desktop_then_activates()
    {
        var (manager, work) = Started();
        var h = Appear(App(0x10));
        desktops.WindowPlacements[h] = work.DesktopId!.Value;
        desktops.CurrentDesktopId = Guid.NewGuid(); // somewhere else

        Assert.True(manager.JumpTo(h, activator).IsSuccess);

        Assert.Contains(work.DesktopId!.Value, desktops.Switches);
        Assert.Equal([h], activator.Activated);
    }

    [Fact]
    public void JumpTo_pinned_window_activates_without_switching()
    {
        var (manager, _) = Started();
        var h = Appear(App(0x10));
        manager.PinWindow(h);
        var before = desktops.Switches.Count;

        Assert.True(manager.JumpTo(h, activator).IsSuccess);

        Assert.Equal(before, desktops.Switches.Count);
        Assert.Equal([h], activator.Activated);
    }

    [Fact]
    public void Assigning_a_pinned_window_unpins_it_first()
    {
        var (manager, work) = Started();
        var h = Appear(App(0x10));
        manager.PinWindow(h);

        Assert.True(manager.AssignWindow(h, work.Id).IsSuccess);

        Assert.Empty(desktops.PinnedWindows); // "put it in Work" = "not everywhere anymore"
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[h]);
    }

    [Fact]
    public void Appeared_pinned_window_is_not_auto_placed_by_rules()
    {
        var (manager, work) = Started();
        manager.SetRules([new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "notepad")], []);
        desktops.PinnedWindows.Add(new WindowHandle(0x10)); // pinned before we ever saw it

        Appear(App(0x10));

        Assert.Empty(desktops.WindowPlacements); // pinned = on ALL desktops; rules keep out
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test tests/TaskSpaces.Core.Tests --filter Overview`
Expected: FAIL — manager members missing.

- [ ] **Step 5: Implement the manager members**

Add to `WorkspaceManager.cs` (usings: `TaskSpaces.Core.Overview`):

```csharp
    // --- overview / switcher-facing operations -----------------------------------

    // Ground truth for "which workspace is this window in": ASK THE OS which desktop
    // it is on (memberships only knows what WE placed). Pinned first — pinned windows
    // are on all desktops, DesktopOf is meaningless for them.
    public Result<Core.Overview.Overview> WindowsByWorkspace() =>
        desktops.GetDesktops().Bind(live => desktops.CurrentDesktop().Map(current =>
        {
            var windows = knownWindows.Values.ToList();
            var pinned = windows
                .Where(w => desktops.IsPinned(w.Handle).GetValueOrDefault(false))
                .Select(w => w.Handle).ToHashSet();
            var desktopOf = windows
                .Where(w => !pinned.Contains(w.Handle))
                .Select(w => (w.Handle, Desktop: desktops.DesktopOf(w.Handle)))
                .Where(x => x.Desktop.IsSuccess) // closed mid-query: just not shown this round
                .ToDictionary(x => x.Handle, x => x.Desktop.Value);
            return OverviewBuilder.Build(State, windows, h => ledger.OriginalTitle(h), pinned, desktopOf, live, current);
        }));

    public Result PinWindow(WindowHandle window) =>
        desktops.Pin(window).Tap(() => stateChanged.OnNext(Unit.Default));

    public Result UnpinWindow(WindowHandle window) =>
        desktops.Unpin(window).Tap(() => stateChanged.OnNext(Unit.Default));

    // Jump = what clicking a taskbar button does, but across workspaces: land on the
    // window's desktop (skip the no-op switch), then bring it to the foreground.
    public Result JumpTo(WindowHandle window, IWindowActivator activator) =>
        desktops.IsPinned(window).Bind(pinned => pinned
            ? activator.Activate(window) // pinned windows are already wherever Petre is
            : desktops.DesktopOf(window)
                .Bind(desktopId => desktops.CurrentDesktop()
                    .Bind(current => desktopId == current ? Result.Success() : desktops.Switch(desktopId)))
                .Bind(() => activator.Activate(window)));

    // "Not running" checks ALL known windows, not just this workspace's — Rider-on-X
    // sitting in another workspace still means Start must not launch a duplicate.
    public IReadOnlyList<InventoryEntry> NotRunningRoster(Guid workspaceId) =>
        State.Inventory.GetValueOrDefault(workspaceId, [])
            .Where(e => !RosterIdentity.IsRunning(e, knownWindows.Values))
            .ToList();

    public Result StartRosterEntry(Guid workspaceId, InventoryEntry entry, IAppLauncher launcher) =>
        launcher.Launch(entry)
            .ToResult($"Could not launch {entry.ProcessPath} (moved or uninstalled?)")
            .Tap(pid => RegisterPendingLaunch(pid, entry.ProcessPath, workspaceId, entry.CommandLine))
            .Bind(_ => Result.Success());

    // ▶ Start: launch everything missing (best-effort per entry — one bad exe never
    // aborts the batch, v1 rehydrator rule), then take Petre there.
    public Result StartWorkspace(Guid workspaceId, IAppLauncher launcher) =>
        Workspace(workspaceId).Bind(_ =>
        {
            foreach (var entry in NotRunningRoster(workspaceId))
                StartRosterEntry(workspaceId, entry, launcher); // per-entry Result deliberately dropped
            return Switch(workspaceId);
        });
```

Replace `AssignWindow` (unpin-first rule) and add the shared auto-place guard:

```csharp
    public Result AssignWindow(WindowHandle window, Guid workspaceId) =>
        knownWindows.TryGetValue(window, out var info)
            // Explicitly moving a pinned window to ONE workspace is a statement that it
            // should no longer be on ALL of them — unpin first, then place (spec).
            ? desktops.IsPinned(window)
                .Bind(pinned => pinned ? desktops.Unpin(window) : Result.Success())
                .Bind(() => Place(info, workspaceId))
            : Result.Failure("Window no longer exists.");

    // Rules (and late placement) only touch windows that are neither placed nor pinned:
    // pinned windows live on ALL desktops — moving one to a workspace desktop would
    // silently defeat the pin Petre set by hand.
    bool AutoPlaceable(WindowHandle handle) =>
        !memberships.ContainsKey(handle)
        && !desktops.IsPinned(handle).GetValueOrDefault(false);
```

In `OnAppeared`, wrap the placement block:

```csharp
        if (AutoPlaceable(window.Handle))
            placement.Or(RulesEngine.MatchWorkspace(window, State.WorkspaceRules))
                .Tap(workspaceId => { Place(window, workspaceId); });
```

And change Task 4's late-placement guard in `OnTitleChanged` from `if (!memberships.ContainsKey(window.Handle))` to `if (AutoPlaceable(window.Handle))`.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test --filter "Category!=Integration"`
Expected: all green (87 + 8 = 95).

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "feat: overview query, jump, pin semantics and start-workspace in the manager

*Collaboration by Claude*"
```

---

### Task 6: Windows/App plumbing — activator, launcher, prompt rewiring

**Files:**
- Modify: `src/TaskSpaces.Windows/Monitoring/NativeMethods.cs`
- Create: `src/TaskSpaces.Windows/Activation/WindowActivator.cs`, `src/TaskSpaces.App/AppLauncher.cs`
- Delete: `src/TaskSpaces.App/Rehydrator.cs`, `src/TaskSpaces.Core/Rehydration/RehydrationFilter.cs`, `tests/TaskSpaces.Core.Tests/RehydrationFilterTests.cs` (semantics live in `NotRunningRoster`, pinned by Task 5's tests)
- Modify: `src/TaskSpaces.App/RehydratePrompt.xaml.cs`
- Test: `tests/TaskSpaces.Windows.Tests/WindowActivatorTests.cs`

**Interfaces:**
- Consumes: `IWindowActivator`/`IAppLauncher` (Task 5), `CommandLines` (Task 2), `manager.NotRunningRoster`/`StartRosterEntry` (Task 5).
- Produces: `WindowActivator : IWindowActivator` (TaskSpaces.Windows.Activation), `AppLauncher : IAppLauncher` (TaskSpaces.App) — Task 7 consumes both.

- [ ] **Step 1: NativeMethods additions**

Append to `NativeMethods`:

```csharp
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(nint hwnd, int cmdShow);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

    public const int SW_RESTORE = 9; // NEVER SW_HIDE anywhere in this codebase (spec)
```

- [ ] **Step 2: WindowActivator**

`src/TaskSpaces.Windows/Activation/WindowActivator.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.Windows.Activation;

using static NativeMethods;

// "Bring it to me": restore if minimized, then foreground. Called from a click inside
// OUR focused panel, which is exactly the situation where Windows grants
// SetForegroundWindow permission — outside that, it degrades to a taskbar flash,
// which is acceptable best-effort behavior, not an error worth failing loudly on.
public sealed class WindowActivator : IWindowActivator
{
    public Result Activate(WindowHandle window) =>
        Result.Try(() =>
        {
            if (IsIconic(window.Value)) ShowWindowAsync(window.Value, SW_RESTORE);
            SetForegroundWindow(window.Value);
        }, e => $"Could not activate window {window.Value}: {e.Message}");
}
```

- [ ] **Step 3: AppLauncher (Rehydrator's successor)**

`src/TaskSpaces.App/AppLauncher.cs`:

```csharp
using System.Diagnostics;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.App;

// The one Process.Start in the app (Core stays pure behind IAppLauncher). Launching a
// remembered app is best-effort: a moved/uninstalled/permission-denied exe returns
// None — never an exception — so one bad roster entry can't abort a Start batch or
// crash the UI (the hard-won lesson from v1's Rehydrator, whose logic this inherits).
public sealed class AppLauncher : IAppLauncher
{
    public Maybe<int> Launch(InventoryEntry entry)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(entry.ProcessPath)
            {
                Arguments = CommandLines.ArgumentsOf(entry.CommandLine, entry.ProcessPath),
                UseShellExecute = true,
            });
            return process is null ? Maybe<int>.None : process.Id;
        }
        catch (Exception) { return Maybe<int>.None; }
    }
}
```

Delete `src/TaskSpaces.App/Rehydrator.cs`.

- [ ] **Step 4: Rewire RehydratePrompt onto the roster machinery**

Replace the body of `RehydratePrompt.xaml.cs` (class content; XAML unchanged):

```csharp
using System.Windows;
using System.Windows.Controls;
using TaskSpaces.Core;

namespace TaskSpaces.App;

// Per-workspace opt-in "restore session?" at startup — now just a veneer over the
// roster: "these workspaces have apps that aren't running; start them?" The same
// NotRunningRoster filter powers the switcher's ▶ Start, so behavior can't drift.
public partial class RehydratePrompt : Window
{
    readonly WorkspaceManager manager;
    readonly AppLauncher launcher = new();
    readonly List<(CheckBox Box, Guid WorkspaceId)> checks = [];

    public RehydratePrompt(WorkspaceManager manager)
    {
        this.manager = manager;
        InitializeComponent();
        manager.State.Workspaces
            .Select(w => (Workspace: w, Missing: manager.NotRunningRoster(w.Id)))
            .Where(x => x.Missing.Count > 0)
            .ToList()
            .ForEach(x =>
            {
                var box = new CheckBox { Content = $"{x.Workspace.Name} ({x.Missing.Count} app(s))", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
                checks.Add((box, x.Workspace.Id));
                WorkspaceChecklist.Items.Add(box);
            });
    }

    public static bool HasAnythingToRestore(WorkspaceManager manager) =>
        manager.State.Workspaces.Any(w => manager.NotRunningRoster(w.Id).Count > 0);

    void OnRestore(object s, RoutedEventArgs e)
    {
        // StartRosterEntry per entry, NOT StartWorkspace: restoring three workspaces
        // must not desktop-switch three times. Entries are re-read at click time via
        // NotRunningRoster, which also makes a workspace removed while this modeless
        // prompt was open a harmless no-op (empty list).
        checks.Where(c => c.Box.IsChecked == true)
            .ToList()
            .ForEach(c => manager.NotRunningRoster(c.WorkspaceId).ToList()
                .ForEach(entry => manager.StartRosterEntry(c.WorkspaceId, entry, launcher)));
        Close();
    }

    void OnSkip(object s, RoutedEventArgs e) => Close();
}
```

Delete `src/TaskSpaces.Core/Rehydration/RehydrationFilter.cs` and `tests/TaskSpaces.Core.Tests/RehydrationFilterTests.cs` — `NotRunningRoster` (Task 5, identity-based, tested) subsumes them.

- [ ] **Step 5: Activator integration test**

`tests/TaskSpaces.Windows.Tests/WindowActivatorTests.cs`:

```csharp
using System.Diagnostics;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Activation;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

[Trait("Category", "Integration")]
public class WindowActivatorTests(ITestOutputHelper output)
{
    [Fact]
    public void Activates_a_real_window()
    {
        var winver = Process.Start("winver.exe");
        try
        {
            while (winver.MainWindowHandle == 0) { Thread.Sleep(100); winver.Refresh(); }
            var result = new WindowActivator().Activate(new WindowHandle(winver.MainWindowHandle));
            output.WriteLine($"activate: {result.IsSuccess}");
            Assert.True(result.IsSuccess);
        }
        finally { if (!winver.HasExited) winver.Kill(); }
    }
}
```

- [ ] **Step 6: Verify**

Run: `dotnet test --filter "Category!=Integration"` (expect 95 minus the 4 deleted RehydrationFilter tests = 91, all green), `dotnet test tests/TaskSpaces.Windows.Tests --filter "Category=Integration"` (5 tests, live mutation authorized), `dotnet build` (0 warnings).

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "feat: window activator and app launcher; rehydrate prompt rides the roster

*Collaboration by Claude*"
```

---

### Task 7: The switcher panel

**Files:**
- Create: `src/TaskSpaces.App/IconCache.cs`, `src/TaskSpaces.App/PromptDialog.xaml`, `src/TaskSpaces.App/PromptDialog.xaml.cs`, `src/TaskSpaces.App/SwitcherPanel.xaml`, `src/TaskSpaces.App/SwitcherPanel.xaml.cs`
- Modify: `src/TaskSpaces.App/App.xaml.cs`, `docs/superpowers/notes/manual-test-script.md`

**Interfaces:**
- Consumes: `manager.WindowsByWorkspace/JumpTo/PinWindow/UnpinWindow/AssignWindow/RenameWindow/RestoreTitle/StartWorkspace/StartRosterEntry/AddRosterEntry/RemoveRosterEntry/Switch/ReapplyRenames/StateChanged`, `WindowActivator`, `AppLauncher`, `NativeMethods.GetCursorPos`, `IconCache`.
- Produces: `IconCache.For(string? processPath) : ImageSource?` (Task 8 reuses it); the panel itself.

- [ ] **Step 1: IconCache**

`src/TaskSpaces.App/IconCache.cs`:

```csharp
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskSpaces.App;

// exe path -> small frozen ImageSource, cached forever (exe icons don't change while
// an app runs, and the cache is tiny). Frozen so rows on any thread can share it.
public static class IconCache
{
    static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string? processPath)
    {
        if (processPath is null) return null;
        if (cache.TryGetValue(processPath, out var hit)) return hit;
        ImageSource? source = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
            if (icon is not null)
            {
                source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                source.Freeze();
            }
        }
        catch (Exception) { /* missing/odd exe: a row without an icon beats no row */ }
        cache[processPath] = source;
        return source;
    }
}
```

- [ ] **Step 2: PromptDialog (rename + manual-add arguments input)**

`src/TaskSpaces.App/PromptDialog.xaml`:

```xml
<Window x:Class="TaskSpaces.App.PromptDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="360" SizeToContent="Height" WindowStartupLocation="CenterScreen"
        ResizeMode="NoResize" ShowInTaskbar="False" Topmost="True">
    <StackPanel Margin="12">
        <TextBlock x:Name="PromptText" TextWrapping="Wrap" Margin="0,0,0,8"/>
        <TextBox x:Name="Input" />
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="OK" Padding="16,4" IsDefault="True" Click="OnOk"/>
            <Button Content="Cancel" Padding="12,4" Margin="8,0,0,0" IsCancel="True"/>
        </StackPanel>
    </StackPanel>
</Window>
```

`src/TaskSpaces.App/PromptDialog.xaml.cs`:

```csharp
using System.Windows;
using CSharpFunctionalExtensions;

namespace TaskSpaces.App;

// The one text-input dialog the app needs (rename a window; arguments for Add app…).
public partial class PromptDialog : Window
{
    public PromptDialog() => InitializeComponent();

    public static Maybe<string> Ask(string title, string prompt, string initial = "")
    {
        var dialog = new PromptDialog { Title = title };
        dialog.PromptText.Text = prompt;
        dialog.Input.Text = initial;
        dialog.Input.SelectAll();
        dialog.Input.Focus();
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Input.Text)
            ? dialog.Input.Text.Trim()
            : Maybe<string>.None;
    }

    void OnOk(object s, RoutedEventArgs e) => DialogResult = true;
}
```

- [ ] **Step 3: The panel**

`src/TaskSpaces.App/SwitcherPanel.xaml`:

```xml
<Window x:Class="TaskSpaces.App.SwitcherPanel"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" ResizeMode="NoResize" ShowInTaskbar="False" Topmost="True"
        SizeToContent="WidthAndHeight" MaxWidth="420" Deactivated="OnDeactivated"
        KeyDown="OnKeyDown">
    <Border BorderThickness="1" Padding="8" CornerRadius="6"
            BorderBrush="{DynamicResource {x:Static SystemColors.ActiveBorderBrushKey}}">
        <ScrollViewer MaxHeight="640" VerticalScrollBarVisibility="Auto">
            <StackPanel x:Name="GroupsHost" MinWidth="300"/>
        </ScrollViewer>
    </Border>
</Window>
```

`src/TaskSpaces.App/SwitcherPanel.xaml.cs` — the panel builds its rows in code (three row shapes, all needing dynamic context menus — templating this in XAML would be strictly worse):

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Microsoft.Win32;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Windows.Activation;

namespace TaskSpaces.App;

// The switcher: every window across every workspace in one place (spec) — the answer
// to "I need to see all windows, similar to taskbar, without changing desktop first".
// One instance lives for the app's lifetime; each summon rebuilds content fresh.
public partial class SwitcherPanel : Window
{
    readonly WorkspaceManager manager;
    readonly WindowActivator activator = new();
    readonly AppLauncher launcher = new();

    public SwitcherPanel(WorkspaceManager manager)
    {
        this.manager = manager;
        InitializeComponent();
        // Live refresh while open: windows appear/close and renames land as Petre watches.
        manager.StateChanged.Subscribe(_ => Dispatcher.Invoke(() => { if (IsVisible) Rebuild(); }));
    }

    public void Summon(double screenX, double screenY)
    {
        Rebuild();
        Left = Math.Max(0, screenX - 320);   // hug the tray corner, stay on-screen
        Top = Math.Max(0, screenY - 24 - 660);
        Show();
        Activate();
    }

    void OnDeactivated(object? s, EventArgs e) => Hide();
    void OnKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Hide(); }

    void Rebuild()
    {
        GroupsHost.Children.Clear();
        manager.WindowsByWorkspace()
            .Tap(overview =>
            {
                if (overview.Pinned.Count > 0)
                    AddGroup("📌 Pinned", isCurrent: false, header: null, overview.Pinned.Select(r => RunningRow(r, pinned: true)));
                overview.Workspaces.ToList().ForEach(g => AddGroup(
                    $"{g.Workspace.Name} ({g.Running.Count})", g.IsCurrent, WorkspaceHeader(g),
                    g.Running.Select(r => RunningRow(r, pinned: false)).Concat(g.NotRunning.Select(e => RosterRow(g.Workspace.Id, e)))));
                overview.OtherDesktops.ToList().ForEach(g => AddGroup(
                    $"{g.Name} ({g.Windows.Count})", g.IsCurrent, header: null, g.Windows.Select(r => RunningRow(r, pinned: false))));
            })
            .TapError(err => GroupsHost.Children.Add(new TextBlock { Text = err, Margin = new Thickness(4) }));
    }

    // --- group scaffolding -------------------------------------------------------

    void AddGroup(string title, bool isCurrent, UIElement? header, IEnumerable<UIElement> rows)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(header ?? new TextBlock { Text = title, FontWeight = isCurrent ? FontWeights.Bold : FontWeights.SemiBold, Margin = new Thickness(4, 2, 4, 2) });
        rows.ToList().ForEach(r => panel.Children.Add(r));
        GroupsHost.Children.Add(panel);
    }

    // Workspace headers are interactive: click = switch there; ▶ = start missing apps;
    // ＋ = manually roster an exe. Bold marks the workspace Petre is on right now.
    UIElement WorkspaceHeader(WorkspaceGroup group)
    {
        var header = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

        var start = new Button { Content = "▶", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Start {group.Workspace.Name}: launch its {group.NotRunning.Count} not-running app(s) and switch there", Visibility = group.NotRunning.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
        start.Click += (_, _) => Report(manager.StartWorkspace(group.Workspace.Id, launcher)).Tap(Hide);
        DockPanel.SetDock(start, Dock.Right);

        var add = new Button { Content = "＋", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Add app… (roster an exe in this workspace)" };
        add.Click += (_, _) => OnAddApp(group.Workspace.Id);
        DockPanel.SetDock(add, Dock.Right);

        var name = new Button { Content = $"{group.Workspace.Name} ({group.Running.Count})", FontWeight = group.IsCurrent ? FontWeights.Bold : FontWeights.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Switch to this workspace" };
        name.Click += (_, _) => Report(manager.Switch(group.Workspace.Id)).Tap(Hide);

        header.Children.Add(start);
        header.Children.Add(add);
        header.Children.Add(name);
        return header;
    }

    // --- rows ----------------------------------------------------------------------

    UIElement RunningRow(WindowRow row, bool pinned)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = IconCache.For(row.Window.ProcessPath);
        if (icon is not null) content.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = row.Window.Title, FontWeight = row.OriginalTitle.HasValue ? FontWeights.SemiBold : FontWeights.Normal });
        // Renamed window: short name prominent, original title dimmed beside it (spec).
        row.OriginalTitle.Tap(original => content.Children.Add(new TextBlock { Text = $"  ·  was: {original}", Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis }));

        var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(16, 2, 4, 2), ToolTip = row.Window.Title };
        button.Click += (_, _) => Report(manager.JumpTo(row.Window.Handle, activator)).Tap(Hide);
        button.ContextMenu = RunningMenu(row, pinned);
        return button;
    }

    ContextMenu RunningMenu(WindowRow row, bool pinned)
    {
        var menu = new ContextMenu();

        var pin = new MenuItem { Header = pinned ? "Unpin from all workspaces" : "Pin to all workspaces" };
        pin.Click += (_, _) => Report(pinned ? manager.UnpinWindow(row.Window.Handle) : manager.PinWindow(row.Window.Handle));
        menu.Items.Add(pin);

        var sendTo = new MenuItem { Header = "Send to" };
        manager.State.Workspaces.ToList().ForEach(w =>
        {
            var item = new MenuItem { Header = w.Name };
            item.Click += (_, _) => Report(manager.AssignWindow(row.Window.Handle, w.Id));
            sendTo.Items.Add(item);
        });
        menu.Items.Add(sendTo);
        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => PromptDialog.Ask("Rename window", "Short name to show on the taskbar:", row.Window.Title)
            .Tap(shortName => Report(manager.RenameWindow(row.Window.Handle, shortName)));
        menu.Items.Add(rename);

        var restore = new MenuItem { Header = "Restore title", IsEnabled = row.OriginalTitle.HasValue };
        restore.Click += (_, _) => Report(manager.RestoreTitle(row.Window.Handle));
        menu.Items.Add(restore);
        return menu;
    }

    // Roster-only entry: the app BELONGS here but isn't running — dimmed, click to launch.
    // The panel stays open on purpose: the row flips to running as the window arrives,
    // and Petre can start several apps in a row (spec).
    UIElement RosterRow(Guid workspaceId, InventoryEntry entry)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.55 };
        var icon = IconCache.For(entry.ProcessPath);
        if (icon is not null) content.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = $"{entry.Title}  (not running)", FontStyle = FontStyles.Italic });

        var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(16, 2, 4, 2), ToolTip = entry.CommandLine ?? entry.ProcessPath };
        button.Click += (_, _) => Report(manager.StartRosterEntry(workspaceId, entry, launcher));

        var menu = new ContextMenu();
        var startOne = new MenuItem { Header = "Start" };
        startOne.Click += (_, _) => Report(manager.StartRosterEntry(workspaceId, entry, launcher));
        menu.Items.Add(startOne);
        var remove = new MenuItem { Header = "Remove from workspace" };
        remove.Click += (_, _) => Report(manager.RemoveRosterEntry(workspaceId, entry));
        menu.Items.Add(remove);
        button.ContextMenu = menu;
        return button;
    }

    void OnAddApp(Guid workspaceId)
    {
        var picker = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Add app to workspace" };
        if (picker.ShowDialog() != true) return;
        var arguments = PromptDialog.Ask("Arguments", "Optional command-line arguments (path+args identify WHAT the app shows):").GetValueOrDefault("");
        Report(manager.AddRosterEntry(workspaceId, picker.FileName, arguments).Map(_ => true));
    }

    static Result Report(Result result) => result.TapError(err => MessageBox.Show(err, "TaskSpaces"));
    static Result<T> Report<T>(Result<T> result) => result.TapError(err => MessageBox.Show(err, "TaskSpaces"));
}
```

- [ ] **Step 4: Wire into App.xaml.cs**

Add fields and startup wiring (after the tray icon creation block):

```csharp
    SwitcherPanel? switcherPanel;
```

```csharp
        // Left-click on the tray icon = the switcher panel (Petre: "clicking the tray
        // icon should open the window"); right-click keeps the menu. Created lazily so
        // compatibility mode without desktops still has a functional (if empty) panel.
        trayIcon.TrayLeftMouseUp += (_, _) =>
        {
            switcherPanel ??= new SwitcherPanel(manager);
            TaskSpaces.Windows.Monitoring.NativeMethods.GetCursorPos(out var cursor);
            switcherPanel.Summon(cursor.X, cursor.Y);
        };

        // Rename safety-net sweep (spec §5): event-driven re-apply is the fast path;
        // every 5s this re-asserts drifted titles and adopts persisted renames.
        if (!compatibilityMode)
        {
            var sweep = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            sweep.Tick += (_, _) => manager.ReapplyRenames();
            sweep.Start();
        }
```

`NativeMethods` is `internal` to TaskSpaces.Windows — make it `public static class NativeMethods` if the compiler objects (it's P/Invoke declarations, nothing secret), or add `GetCursorPos` access via a tiny public helper in TaskSpaces.Windows; prefer making the class public and note it in your report.

- [ ] **Step 5: Build, run, smoke, script**

Run: `dotnet build` (0 warnings), `dotnet test --filter "Category!=Integration"` (all green), then the smoke cycle from Global Constraints (stop app → rebuild Release → relaunch → alive 10s → leave running).

Append to `docs/superpowers/notes/manual-test-script.md` (all pending human execution):

```markdown
15. Left-click tray icon -> switcher panel opens near the tray, dark-themed, one group
    per workspace with window counts; current workspace bold.
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
```

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: tray-summoned switcher panel - jump, pin, rename, roster start

*Collaboration by Claude*"
```

---

### Task 8: Windows tab — icons, workspace column, both-names display

**Files:**
- Modify: `src/TaskSpaces.App/ManageWindow.xaml`, `src/TaskSpaces.App/ManageWindow.xaml.cs`, `docs/superpowers/notes/manual-test-script.md` (item 19 covers it — no new item needed)

**Interfaces:**
- Consumes: `manager.WindowsByWorkspace()` (Task 5), `IconCache` (Task 7).
- Produces: nothing new — UI only.

- [ ] **Step 1: Row type + Reload changes**

The Windows tab currently binds `WindowList.ItemsSource` to `manager.KnownWindows` (`IReadOnlyList<WindowInfo>`) with columns bound to `ProcessName`/`Title`, and Reload preserves the selected window by `Handle`. Introduce a row projection in `ManageWindow.xaml.cs`:

```csharp
    // Windows-tab row: the window + which workspace it is ACTUALLY on (ground truth
    // via the overview — "Pinned" for pinned, the desktop's own name for desktops no
    // workspace owns) + both titles when renamed.
    public sealed record WindowTabRow(WindowInfo Window, string Workspace, Maybe<string> OriginalTitle)
    {
        public ImageSource? Icon => IconCache.For(Window.ProcessPath);
        public string DisplayTitle => OriginalTitle.HasValue ? $"{Window.Title}  ·  was: {OriginalTitle.Value}" : Window.Title;
    }

    IReadOnlyList<WindowTabRow> WindowRows() =>
        manager.WindowsByWorkspace()
            .Map(o => (IReadOnlyList<WindowTabRow>)
                [.. o.Pinned.Select(r => new WindowTabRow(r.Window, "Pinned", r.OriginalTitle)),
                 .. o.Workspaces.SelectMany(g => g.Running.Select(r => new WindowTabRow(r.Window, g.Workspace.Name, r.OriginalTitle))),
                 .. o.OtherDesktops.SelectMany(g => g.Windows.Select(r => new WindowTabRow(r.Window, g.Name, r.OriginalTitle)))])
            .GetValueOrDefault([.. manager.KnownWindows.Select(w => new WindowTabRow(w, "—", Maybe<string>.None))]); // compat mode fallback
```

In `Reload()`: set `WindowList.ItemsSource = WindowRows();` and adapt the selected-window preservation to match on `row.Window.Handle` (capture `(WindowList.SelectedItem as WindowTabRow)?.Window.Handle` before rebinding, re-select the row whose `Window.Handle` matches after). In `WithSelectedWindow`, unwrap: `WindowList.SelectedItem is WindowTabRow row ? action(row.Window) : ...`. Add usings: `System.Windows.Media`, `CSharpFunctionalExtensions` (already present), `TaskSpaces.Core.Domain` (already present).

- [ ] **Step 2: XAML columns**

Replace the Windows tab's `GridView` columns with:

```xml
<GridView>
    <GridViewColumn Header="" Width="28">
        <GridViewColumn.CellTemplate>
            <DataTemplate>
                <Image Source="{Binding Icon}" Width="16" Height="16"/>
            </DataTemplate>
        </GridViewColumn.CellTemplate>
    </GridViewColumn>
    <GridViewColumn Header="Process" Width="110" DisplayMemberBinding="{Binding Window.ProcessName}"/>
    <GridViewColumn Header="Title" Width="330" DisplayMemberBinding="{Binding DisplayTitle}"/>
    <GridViewColumn Header="Workspace" Width="110" DisplayMemberBinding="{Binding Workspace}"/>
</GridView>
```

- [ ] **Step 3: Verify + smoke**

Run: `dotnet build` (0 warnings), `dotnet test --filter "Category!=Integration"` (all green), then the smoke cycle (stop → rebuild Release → relaunch → alive → leave running for Petre).

- [ ] **Step 4: Commit**

```powershell
git add -A
git commit -m "feat: Windows tab shows icons, actual workspace, and both names for renamed windows

*Collaboration by Claude*"
```

---

### Task 9: Tray interaction & hotkeys (added 2026-08-02 — Petre's testing feedback)

Spec section "Tray interaction & hotkeys". Hover the tray icon → panel peeks without stealing focus; left-click → menu; Ctrl+Alt+Left/Right cycles workspaces; Ctrl+Alt+1..9 direct switch.

**Files:**
- Modify: `src/TaskSpaces.Windows/Monitoring/NativeMethods.cs` (RegisterHotKey/UnregisterHotKey + modifiers)
- Modify: `src/TaskSpaces.Core/WorkspaceManager.cs` (+CycleWorkspace, +SwitchToIndex)
- Create: `src/TaskSpaces.App/HotkeyService.cs`
- Modify: `src/TaskSpaces.App/SwitcherPanel.xaml.cs` (peek mode), `src/TaskSpaces.App/App.xaml.cs` (MenuActivation, hover wiring, hotkey wiring)
- Test: `tests/TaskSpaces.Core.Tests/OverviewTests.cs` (cycle/index tests appended)
- Modify: `docs/superpowers/notes/manual-test-script.md`

**Interfaces:**
- Consumes: manager.Switch, desktops.CurrentDesktop (Task 1), SwitcherPanel.Summon (Task 7), NativeMethods (public).
- Produces: `WorkspaceManager.CycleWorkspace(int direction) : Result` (direction ±1; wraps; current desktop not a workspace → first for +1 / last for -1; no workspaces → failure); `WorkspaceManager.SwitchToIndex(int index) : Result` (0-based internally, failure when out of range); `HotkeyService : IDisposable` (registers on construction with a message-only HwndSource, invokes callbacks on WM_HOTKEY, unregisters on Dispose, exposes `IReadOnlyList<string> Failures` for chords another app owns).

- [ ] **Step 1 (TDD): cycle/index tests in OverviewTests.cs** — Cycle_wraps_in_workspace_order (two workspaces; CurrentDesktopId = ws1's desktop → Cycle(+1) switches to ws2; again → wraps to ws1); Cycle_from_non_workspace_desktop_goes_to_first (CurrentDesktopId = random guid → Cycle(+1) → first workspace; Cycle(-1) → last); Cycle_with_no_workspaces_fails; SwitchToIndex_out_of_range_fails. Run → FAIL.
- [ ] **Step 2: implement CycleWorkspace/SwitchToIndex in WorkspaceManager**

```csharp
    // Ctrl+Alt+arrows (spec §Tray interaction): cycle through OUR workspaces in their
    // defined order — unlike native Win+Ctrl+arrows, which walks every OS desktop
    // including unbound ones. Wrapping; a non-workspace current desktop enters the
    // ring at the edge matching travel direction.
    public Result CycleWorkspace(int direction) =>
        State.Workspaces.Count == 0
            ? Result.Failure("No workspaces to cycle through.")
            : desktops.CurrentDesktop().Bind(current =>
            {
                var index = State.Workspaces.ToList().FindIndex(w => w.DesktopId == current);
                var next = index < 0
                    ? (direction > 0 ? 0 : State.Workspaces.Count - 1)
                    : (index + direction + State.Workspaces.Count) % State.Workspaces.Count;
                return Switch(State.Workspaces[next].Id);
            });

    // Ctrl+Alt+1..9: direct switch by defined order (hotkey digit - 1).
    public Result SwitchToIndex(int index) =>
        index >= 0 && index < State.Workspaces.Count
            ? Switch(State.Workspaces[index].Id)
            : Result.Failure($"No workspace #{index + 1}.");
```

Run tests → PASS. Commit (`feat: workspace cycling and direct-switch for hotkeys` + trailer).
- [ ] **Step 3: NativeMethods** — `RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk)`, `UnregisterHotKey(nint hwnd, int id)`, `MOD_CONTROL = 0x2, MOD_ALT = 0x1, WM_HOTKEY = 0x0312, VK_LEFT = 0x25, VK_RIGHT = 0x27` (digits use char codes '1'..'9' = 0x31..0x39).
- [ ] **Step 4: HotkeyService** (App) — ctor takes `Action cyclePrev, Action cycleNext, Action<int> switchTo`; creates `new HwndSource(new HwndSourceParameters("TaskSpacesHotkeys") { WindowStyle = 0, Width = 0, Height = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ })`; AddHook handling WM_HOTKEY by id; registers ids 1 (Ctrl+Alt+Left), 2 (Ctrl+Alt+Right), 10+n (Ctrl+Alt+digit); failed registrations recorded in `Failures` (chord name strings), not thrown; Dispose unregisters all + disposes the source. Ample comments (why message-only window; why failures are warnings).
- [ ] **Step 5: SwitcherPanel peek mode** — add `bool peekMode` + `DispatcherTimer proximityTimer (250ms)`; `public void Peek(double screenX, double screenY)`: if IsVisible return; `ShowActivated = false; peekMode = true; Show(); PositionNear(...); proximityTimer.Start();` (do NOT Activate). Proximity tick: GetCursorPos; if cursor outside panel bounds inflated by 24px DIP margin → Hide(), stop timer, peekMode = false, ShowActivated = true (restore). `OnPreviewMouseDown`: if peekMode → `peekMode = false; proximityTimer.Stop(); Activate();` (from then on Deactivated-hide governs). OnDeactivated keeps its childDialogOpen guard and additionally ignores while peekMode (not focused anyway). Summon() unchanged for hotkey/other callers.
- [ ] **Step 6: App wiring** — `trayIcon.MenuActivation = PopupActivationMode.LeftOrRightClick;` (menu on click — remove the TrayLeftMouseUp panel handler); `trayIcon.TrayMouseMove += ...` starting/restarting a 400ms DispatcherTimer whose Tick summons `switcherPanel.Peek(cursor)` (create panel lazily as before) and stops itself; hotkeys: `hotkeys = new HotkeyService(() => manager.CycleWorkspace(-1), () => manager.CycleWorkspace(+1), n => manager.SwitchToIndex(n));` gated on !compatibilityMode; if `hotkeys.Failures.Count > 0` show ONE warning MessageBox listing them; dispose in ExitApp. Results from hotkey actions are fire-and-forget (comment: a failed switch from a hotkey has no UI to speak through — silent no-op beats a message-box storm on every keypress).
- [ ] **Step 7: manual script items** (pending human execution): 25. hover tray icon ~half a second → panel peeks without taking focus; move mouse away → it hides; click inside it first → it stays and behaves like the clicked-open panel. 26. left-click tray icon → menu opens (same as right-click). 27. Ctrl+Alt+Right/Left cycles workspaces in order, wrapping, skipping plain desktops. 28. Ctrl+Alt+2 jumps to the second workspace. 29. If another app owns a chord, one warning at startup, hotkeys otherwise functional.
- [ ] **Step 8: verify + smoke** — build 0 warnings; suite green (96 + 4 = 100); stop app, rebuild Release, relaunch, alive, LEAVE RUNNING. Commit (`feat: hover-to-peek panel, menu on click, global workspace hotkeys` + trailer).

### Task 10: Drag-and-drop everywhere + shared grouped view + missing-windows bug (added 2026-08-02, second testing round)

Spec section "Drag-and-drop window management". Three parts, one task (they share the same control and the same investigation surface).

**Files:**
- Create: `src/TaskSpaces.App/WindowGroupsView.xaml(.cs)` — the shared grouped view
- Modify: `src/TaskSpaces.App/SwitcherPanel.xaml(.cs)` (host the shared view; remove ＋; keep peek/summon/positioning shell), `src/TaskSpaces.App/ManageWindow.xaml(.cs)` (Windows tab hosts the shared view; delete bottom action bar except Refresh + Start-with-Windows; WindowTabRow/WindowRows() removed if subsumed), `src/TaskSpaces.Core/Overview/OverviewBuilder.cs` + `src/TaskSpaces.Windows/Desktops/VirtualDesktopService.cs` (only as the bug investigation dictates)
- Test: whatever the bug fix needs (Core-level, pinned by test); no UI unit tests

**Interfaces:**
- Consumes: everything Tasks 5-9 produced. Produces: `WindowGroupsView` (UserControl) with a `Bind(WorkspaceManager manager, Action? afterAction = null)` initializer building the grouped rows exactly as SwitcherPanel does today (headers with switch-on-click + ▶ Start; running rows jump-on-click, right-click menu now including "Add app…" moved onto the workspace header's context menu; dimmed roster rows; both-names display; icons).

- [ ] **Step 1 — BUG FIRST (systematic debugging, root cause before UI work):** reproduce the missing-windows report. Instrument (temporarily or via a small diagnostic test/console dump) what `WindowsByWorkspace()` returns on this machine: for every known window, log pinned result, DesktopOf result (success/failure + guid), and which group it landed in; log GetDesktops (ids + names, note EMPTY names). Suspects, in order: (a) unrenamed OS desktops have Name == "" → DesktopGroup header renders as blank/`" (4)"` — if confirmed, fall back to `string.IsNullOrEmpty(d.Name) ? $"Desktop {index+1}" : d.Name` (index = position in GetDesktops order) in OverviewBuilder (pure change + unit test); (b) DesktopOf failures silently omit windows — if confirmed for real windows, add an "Unplaced" catch-all DesktopGroup for known windows with no resolvable desktop (unit test); (c) something else — follow the evidence. Record findings + fix with a test in the report. Commit separately (`fix: ...` + trailer).
- [ ] **Step 2 — WindowGroupsView:** extract SwitcherPanel's group/row building (AddGroup/WorkspaceHeader/RunningRow/RosterRow/RunningMenu + Report helpers + RunChildDialog interaction via an injected owner Window) into the UserControl. ＋ button removed; "Add app…" becomes an item on the workspace header's ContextMenu. Panel keeps: peek/summon shell, positioning, childDialogOpen/proximity logic, StateChanged→rebuild wiring (now delegating content to the view).
- [ ] **Step 3 — drag-and-drop:** running rows set `AllowDrag` behavior: on MouseMove with pressed left button beyond `SystemParameters.MinimumHorizontalDragDistance`, `DragDrop.DoDragDrop(row, windowHandle-as-data, DragDropEffects.Move)`. Workspace group containers + Pinned group container set `AllowDrop = true`; DragOver sets Move effect; Drop → workspace group: `manager.AssignWindow(handle, workspaceId)`; Pinned group: `manager.PinWindow(handle)`; failures via the existing Report path. Guard: dropping a row onto its own group is a no-op (skip the call). Unbound-desktop groups: not drop targets (comment why). A drag must not trigger the row's Click (WPF: click won't fire once DoDragDrop captures — verify behavior, note it).
- [ ] **Step 4 — Manage Windows tab:** replace the flat ListView + bottom bar with the hosted `WindowGroupsView` (Refresh button + Start-with-Windows checkbox stay; Send-to combo, rename textbox, Rename/Restore buttons deleted — the row context menu covers them). Selection-preservation code for the old list goes away with it.
- [ ] **Step 5 — manual script items** (pending human execution): 30. drag a window row onto another workspace in the panel → it moves (and unpins if it was pinned); onto 📌 Pinned → pins. 31. Windows tab shows the same grouped view; drag works there identically; right-click menu covers rename/restore/pin/send. 32. windows on unbound/default desktops appear under a sensibly-named group (e.g. "Desktop 1"), including the current desktop. 33. ＋ is gone; Add app… lives in the workspace header right-click menu.
- [ ] **Step 6 — verify + smoke:** build 0 warnings; suite green (102 + bug-fix tests); stop app, rebuild Release, relaunch, alive, LEAVE RUNNING. Commit (`feat: drag-and-drop window management + shared grouped view` + trailer).

## After this plan

- Petre executes manual-test-script items 15–33 (plus any remaining 1–14).
- PR remains ON HOLD until Petre says otherwise.
- Future (spec'd, not planned): UIA rule kind (browser URL / document path) — spike first.

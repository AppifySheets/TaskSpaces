# TaskSpaces v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows tray app that groups running windows into named workspaces backed by Windows virtual desktops, auto-assigns new windows by rules, renames windows to short taskbar names, and persists/rehydrates across reboots.

**Architecture:** Pure, unit-tested core (domain records, rules engine, rename ledger, orchestrator, JSON persistence) behind small interfaces; a thin Windows layer (virtual-desktop COM wrapper, WinEvent hooks, WM_SETTEXT) implements those interfaces; a WPF tray app composes everything. The riskiest dependency — the undocumented virtual-desktop COM API — is spiked first on this exact machine (Windows 11 build 26200 / 25H2) before anything is built on it.

**Tech Stack:** .NET 10 (LTS, SDK 10.0.203 installed), C# latest, WPF + H.NotifyIcon.Wpf (tray), Slions.VirtualDesktop 6.9.2 (virtual desktop COM wrapper), System.Reactive, CSharpFunctionalExtensions, System.Text.Json, System.Management (WMI command lines), xunit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-01-taskspaces-design.md`. Read it before starting any task.
- Windows x64 only (`<PlatformTarget>x64</PlatformTarget>` in Windows-facing projects); dev machine is Windows 11 Pro build 26200 (25H2).
- Never hide windows via `ShowWindow(SW_HIDE)` — all visibility changes go through virtual desktops (spec §Error handling; a crash must never orphan a window).
- CSharpFunctionalExtensions `Result`/`Result<T>`/`Maybe<T>` for expected failures and absences; exceptions only for the truly exceptional (e.g. COM API shape unrecognized).
- Functional style: immutable `record` types, `IReadOnlyList`/`ImmutableDictionary`, lambdas/LINQ over loops, expression bodies (`=>`) for one-liners, `var` everywhere, no braces on single-statement `if`, `private` keyword implied (omit it).
- RX (`IObservable<T>`) instead of .NET events on all public surfaces we own.
- Ample intention comments in all source code — a human or agent must understand *why* without reading every line.
- Tests: xunit. Use `ITestOutputHelper` for test logging (never a throwaway console program). Tests that mutate real desktops/windows carry `[Trait("Category", "Integration")]` and are excluded from the default run: `dotnet test --filter "Category!=Integration"`.
- Persistence root: `%APPDATA%\TaskSpaces\state.json` (base directory injectable for tests).
- All work on a feature branch (e.g. `feature/taskspaces-v1`); never push to `main`; PR at the end. Commit messages end with the line: `*Collaboration by Claude*`
- Out of scope for this plan (deliberate, per spec): dedicated switcher surface (form factor undecided — needs visual mockups first; tray menu provides switching in the interim), global hotkeys (optional accelerators, later), browser-tab restore, tiling, sync.

## File Structure

```
TaskSpaces.sln
Directory.Build.props                     # shared: LangVersion, Nullable, ImplicitUsings
spikes/VirtualDesktopSpike/               # Task 1: throwaway console proof (kept in repo for reference)
src/TaskSpaces.Core/                      # net10.0, no Windows deps — fully unit-testable
  Domain/WindowHandle.cs                  # readonly record struct wrapping HWND
  Domain/WindowInfo.cs                    # window metadata snapshot
  Domain/Workspace.cs                     # workspace definition
  Domain/WindowEvent.cs                   # monitor event + kind enum
  Rules/RuleMatchKind.cs
  Rules/WorkspaceRule.cs
  Rules/RenameRule.cs
  Rules/BrowserProfile.cs                 # --profile-directory extraction
  Rules/RulesEngine.cs                    # pure matching functions
  Renaming/RenameLedger.cs                # immutable original-title bookkeeping
  Rehydration/PendingPlacements.cs        # pid/path → workspace map for relaunched apps
  Persistence/InventoryEntry.cs
  Persistence/AppState.cs                 # everything that goes to disk
  Persistence/IPersistenceStore.cs
  Persistence/JsonPersistenceStore.cs
  Abstractions/DesktopInfo.cs
  Abstractions/IVirtualDesktopService.cs
  Abstractions/IWindowMonitor.cs
  Abstractions/IWindowTitles.cs
  WorkspaceManager.cs                     # orchestrator: events × rules → moves/renames/persist
src/TaskSpaces.Windows/                   # net10.0-windows10.0.19041.0, x64
  Desktops/VirtualDesktopService.cs       # wraps Slions.VirtualDesktop, isolates COM risk
  Monitoring/NativeMethods.cs             # P/Invoke: WinEvent hooks, window queries, WM_SETTEXT
  Monitoring/TopLevelWindows.cs           # enumeration + taskbar-eligibility filter
  Monitoring/WindowInfoFactory.cs         # hwnd → WindowInfo (process, title, command line)
  Monitoring/WindowMonitor.cs             # WinEvent hooks → IObservable<WindowEvent>
  Renaming/Win32WindowTitles.cs           # WM_SETTEXT / GetWindowText
src/TaskSpaces.App/                       # net10.0-windows10.0.19041.0, WPF, x64
  App.xaml / App.xaml.cs                  # composition root, tray icon, lifecycle
  TrayMenu.cs                             # context menu built from workspaces
  ManageWindow.xaml(.cs)                  # workspaces / rules / windows tabs
  RehydratePrompt.xaml(.cs)               # "restore session?" checklist
  StartupRegistration.cs                  # HKCU Run key toggle
tests/TaskSpaces.Core.Tests/              # unit tests (pure, fast)
tests/TaskSpaces.Windows.Tests/           # integration tests, Category=Integration
docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md   # Task 1 findings
```

Dependency direction: `App → Windows → Core` and `App → Core`. Core references only CSharpFunctionalExtensions, System.Reactive, System.Collections.Immutable.

---

### Task 1: Solution scaffold + virtual-desktop spike (riskiest first)

The virtual-desktop COM API is undocumented and shifts between Windows builds. Slions.VirtualDesktop 6.9.2 (Apr 2025) documents support up to build 26100 (24H2); this machine is 26200 (25H2 — an enablement package on the 24H2 servicing branch, so the COM GUIDs are *probably* identical). This spike proves it before anything is built on it. A spike is exploratory — no TDD here; the deliverable is a findings document plus a runnable proof.

**Files:**
- Create: `TaskSpaces.sln`, `Directory.Build.props`, `.gitignore`
- Create: `spikes/VirtualDesktopSpike/VirtualDesktopSpike.csproj`, `spikes/VirtualDesktopSpike/Program.cs`
- Create: `docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: written findings that Task 4 depends on — the exact working member names of the wrapper (`VirtualDesktop.GetDesktops/Create/FromHwnd/FromId/MoveToDesktop/Current/IsSupported`, `desktop.Switch/Remove/Name`, `VirtualDesktop.CurrentChanged`), whether any `Configure()`-style initialization call is required, and whether the library works on build 26200 under `net10.0-windows10.0.19041.0`.

- [ ] **Step 1: Scaffold solution**

```powershell
git checkout -b feature/taskspaces-v1
dotnet new gitignore
dotnet new sln -n TaskSpaces
dotnet new console -n VirtualDesktopSpike -o spikes/VirtualDesktopSpike
dotnet sln add spikes/VirtualDesktopSpike
dotnet add spikes/VirtualDesktopSpike package Slions.VirtualDesktop
```

Create `Directory.Build.props` at repo root:

```xml
<Project>
  <!-- Shared compiler settings for every project in the solution. -->
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Edit `spikes/VirtualDesktopSpike/VirtualDesktopSpike.csproj` properties:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <!-- net10 app consuming the package's net8.0-windows assets — part of what this spike verifies. -->
  <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
  <PlatformTarget>x64</PlatformTarget>
</PropertyGroup>
```

- [ ] **Step 2: Write the spike program**

`spikes/VirtualDesktopSpike/Program.cs`:

```csharp
using System.Diagnostics;
using WindowsDesktop;

// SPIKE — is Slions.VirtualDesktop usable on THIS machine (Win11 build 26200 / 25H2)?
// The library runtime-compiles COM interop matched to the OS build; 6.9.2 documents
// builds up to 26100. Each numbered check prints OK/FAIL so a partial run still tells
// us exactly which capability broke. Findings go to
// docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md.

Console.WriteLine($"OS build: {Environment.OSVersion.Version}");
Console.WriteLine($"1. IsSupported: {VirtualDesktop.IsSupported}");

var desktops = VirtualDesktop.GetDesktops();
Console.WriteLine($"2. Enumerate: {desktops.Length} desktop(s): {string.Join(", ", desktops.Select(d => $"'{d.Name}'"))}");

var original = VirtualDesktop.Current;
var created = VirtualDesktop.Create();
created.Name = "TaskSpaces spike";
Console.WriteLine($"3. Create+rename: {created.Id} '{created.Name}'");

// Switch away and back — visually confirms the taskbar swap that the whole product rides on.
created.Switch();
await Task.Delay(1500);
Console.WriteLine($"4. Switch: current == created ? {VirtualDesktop.Current.Id == created.Id}");
original.Switch();
await Task.Delay(500);

// Guinea-pig window: winver is a classic same-process dialog, so MainWindowHandle is
// reliable (Win11 notepad hands off to a packaged process and would lie to us here).
var winver = Process.Start("winver.exe");
while (winver.MainWindowHandle == 0) { await Task.Delay(100); winver.Refresh(); }
var hwnd = winver.MainWindowHandle;

VirtualDesktop.MoveToDesktop(hwnd, created);
var found = VirtualDesktop.FromHwnd(hwnd);
Console.WriteLine($"5. Move window: on created desktop ? {found?.Id == created.Id}");
Console.WriteLine($"6. FromId roundtrip: {VirtualDesktop.FromId(created.Id)?.Id == created.Id}");

// Event check — Task 4 exposes this as an RX observable.
VirtualDesktop.CurrentChanged += (_, e) => Console.WriteLine($"7. CurrentChanged fired: -> {e.NewDesktop.Id}");
created.Switch();
await Task.Delay(1500);
original.Switch();
await Task.Delay(500);

winver.Kill();
created.Remove();
Console.WriteLine("8. Removed spike desktop — check Task View that no stray desktop remains.");
```

- [ ] **Step 3: Build and run; adapt until conclusive**

Run: `dotnet run --project spikes/VirtualDesktopSpike`

Expected: all checks print OK/true. This is a spike, so adapt to reality:
- Compile errors on member names → fix against the package's actual API (IntelliSense / decompile), and record every corrected name — Task 4 is written against your findings.
- Exception mentioning initialization/configuration → look for a `VirtualDesktop.Configure(...)` member and call it first; record the requirement.
- Exception about unsupported OS build → the library doesn't know 26200. Contingencies in order: (a) check https://github.com/Slion/VirtualDesktop issues/releases for 25H2 support; (b) the library resolves COM interface IDs per build — apply its documented override mechanism (README) to map 26200 to the 26100 IDs, which 25H2 almost certainly shares; (c) if the library is a dead end, vendor `VirtualDesktop11-24H2.cs` from https://github.com/MScholtes/VirtualDesktop (MIT) into `src/TaskSpaces.Windows/Desktops/` — its API differs, so revisit Task 4's implementation (not its interface) accordingly.
- If failures look net10-related, retarget the spike to `net8.0-windows10.0.19041.0` to A/B test; if net8 works and net10 doesn't, the whole solution ships on net8 (still LTS until Nov 2026) — record the decision.

- [ ] **Step 4: Write the findings document**

`docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md` — verdict (works / works-with-config / vendored fallback), per-check results, exact confirmed API member names, required initialization, chosen TFM, and any 26200-specific configuration. **Task 4's implementer reads this file before writing any code.**

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "spike: prove virtual desktop COM wrapper on Win11 build 26200

*Collaboration by Claude*"
```

---

### Task 2: Core domain + RulesEngine (TDD)

Pure domain records and the pure matching functions — no I/O, no Windows.

**Files:**
- Create: `src/TaskSpaces.Core/TaskSpaces.Core.csproj`
- Create: `src/TaskSpaces.Core/Domain/WindowHandle.cs`, `Domain/WindowInfo.cs`, `Domain/Workspace.cs`, `Domain/WindowEvent.cs`
- Create: `src/TaskSpaces.Core/Rules/RuleMatchKind.cs`, `Rules/WorkspaceRule.cs`, `Rules/RenameRule.cs`, `Rules/BrowserProfile.cs`, `Rules/RulesEngine.cs`
- Create: `tests/TaskSpaces.Core.Tests/TaskSpaces.Core.Tests.csproj`, `tests/TaskSpaces.Core.Tests/RulesEngineTests.cs`, `tests/TaskSpaces.Core.Tests/BrowserProfileTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (used by every later task):
  - `readonly record struct WindowHandle(nint Value)`
  - `record WindowInfo(WindowHandle Handle, int ProcessId, string ProcessName, string? ProcessPath, string Title, string? CommandLine)`
  - `record Workspace(Guid Id, string Name, Guid? DesktopId)`
  - `record WindowEvent(WindowEventKind Kind, WindowInfo Window)`; `enum WindowEventKind { Appeared, TitleChanged, Disappeared }`
  - `enum RuleMatchKind { ProcessName, TitleRegex, BrowserProfile }`
  - `record WorkspaceRule(Guid WorkspaceId, RuleMatchKind Kind, string Pattern)`
  - `record RenameRule(RuleMatchKind Kind, string Pattern, string ShortName)`
  - `RulesEngine.MatchWorkspace(WindowInfo, IReadOnlyList<WorkspaceRule>) : Maybe<Guid>`
  - `RulesEngine.MatchRename(WindowInfo, IReadOnlyList<RenameRule>) : Maybe<string>`
  - `BrowserProfile.FromCommandLine(string?) : Maybe<string>`

- [ ] **Step 1: Create projects**

```powershell
dotnet new classlib -n TaskSpaces.Core -o src/TaskSpaces.Core
dotnet new xunit -n TaskSpaces.Core.Tests -o tests/TaskSpaces.Core.Tests
dotnet sln add src/TaskSpaces.Core tests/TaskSpaces.Core.Tests
dotnet add tests/TaskSpaces.Core.Tests reference src/TaskSpaces.Core
dotnet add src/TaskSpaces.Core package CSharpFunctionalExtensions
dotnet add src/TaskSpaces.Core package System.Reactive
dotnet add src/TaskSpaces.Core package System.Collections.Immutable
```

Delete the template `Class1.cs` / `UnitTest1.cs`. Core targets plain `net10.0` (keep it Windows-free so tests run anywhere).

- [ ] **Step 2: Write domain records**

`src/TaskSpaces.Core/Domain/WindowHandle.cs`:

```csharp
namespace TaskSpaces.Core.Domain;

// Typed HWND. A raw nint invites passing the wrong integer; the struct costs nothing
// and makes signatures self-documenting.
public readonly record struct WindowHandle(nint Value);
```

`src/TaskSpaces.Core/Domain/WindowInfo.cs`:

```csharp
namespace TaskSpaces.Core.Domain;

// Immutable snapshot of a top-level window at the moment an event fired.
// CommandLine is only populated for browser processes (WMI is expensive) —
// it exists solely so BrowserProfile rules can inspect --profile-directory.
public sealed record WindowInfo(
    WindowHandle Handle,
    int ProcessId,
    string ProcessName,     // e.g. "chrome" (no extension)
    string? ProcessPath,    // null when inaccessible (elevated process)
    string Title,
    string? CommandLine);
```

`src/TaskSpaces.Core/Domain/Workspace.cs`:

```csharp
namespace TaskSpaces.Core.Domain;

// A named group of windows, backed 1:1 by a Windows virtual desktop.
// DesktopId is the *live* desktop's GUID — persisted so we can re-bind to the same
// desktop after an app restart, but desktops don't survive reboots, so reconcile
// logic (WorkspaceManager) may re-create the desktop and update this.
public sealed record Workspace(Guid Id, string Name, Guid? DesktopId);
```

`src/TaskSpaces.Core/Domain/WindowEvent.cs`:

```csharp
namespace TaskSpaces.Core.Domain;

public enum WindowEventKind { Appeared, TitleChanged, Disappeared }

// What WindowMonitor emits. TitleChanged matters twice: rename rules may now match,
// and apps that rewrite their own titles must have our short name re-applied.
public sealed record WindowEvent(WindowEventKind Kind, WindowInfo Window);
```

- [ ] **Step 3: Write failing RulesEngine + BrowserProfile tests**

`tests/TaskSpaces.Core.Tests/RulesEngineTests.cs`:

```csharp
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class RulesEngineTests
{
    static readonly Guid Work = Guid.NewGuid();
    static readonly Guid Personal = Guid.NewGuid();

    static WindowInfo Window(string process = "notepad", string title = "Untitled", string? commandLine = null) =>
        new(new WindowHandle(1), 42, process, null, title, commandLine);

    [Fact]
    public void First_matching_rule_wins_in_list_order()
    {
        var rules = new[]
        {
            new WorkspaceRule(Work, RuleMatchKind.TitleRegex, "Unt.*"),
            new WorkspaceRule(Personal, RuleMatchKind.ProcessName, "notepad"),
        };
        Assert.Equal(Work, RulesEngine.MatchWorkspace(Window(), rules).Value);
    }

    [Fact]
    public void Process_name_match_is_case_insensitive() =>
        Assert.Equal(Work, RulesEngine.MatchWorkspace(
            Window(process: "NOTEPAD"),
            [new WorkspaceRule(Work, RuleMatchKind.ProcessName, "notepad")]).Value);

    [Fact]
    public void Title_regex_matches_anywhere_in_title() =>
        Assert.Equal(Work, RulesEngine.MatchWorkspace(
            Window(title: "Sparrow-SLIP39 - Visual Studio"),
            [new WorkspaceRule(Work, RuleMatchKind.TitleRegex, "sparrow")]).Value);

    [Fact]
    public void Browser_profile_rule_matches_profile_directory() =>
        Assert.Equal(Personal, RulesEngine.MatchWorkspace(
            Window(process: "chrome", commandLine: "\"C:\\chrome.exe\" --profile-directory=\"Profile 2\""),
            [new WorkspaceRule(Personal, RuleMatchKind.BrowserProfile, "Profile 2")]).Value);

    [Fact]
    public void No_matching_rule_returns_none() =>
        Assert.True(RulesEngine.MatchWorkspace(
            Window(),
            [new WorkspaceRule(Work, RuleMatchKind.ProcessName, "chrome")]).HasNoValue);

    [Fact]
    public void Invalid_regex_is_treated_as_no_match_not_an_exception() =>
        Assert.True(RulesEngine.MatchWorkspace(
            Window(),
            [new WorkspaceRule(Work, RuleMatchKind.TitleRegex, "([unclosed")]).HasNoValue);

    [Fact]
    public void Rename_rules_produce_the_short_name()
    {
        var rules = new[] { new RenameRule(RuleMatchKind.TitleRegex, "Remote Desktop", "RDP") };
        Assert.Equal("RDP", RulesEngine.MatchRename(Window(title: "myserver - Remote Desktop Connection"), rules).Value);
    }

    [Fact]
    public void Rename_without_match_returns_none() =>
        Assert.True(RulesEngine.MatchRename(Window(), []).HasNoValue);
}
```

`tests/TaskSpaces.Core.Tests/BrowserProfileTests.cs`:

```csharp
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class BrowserProfileTests
{
    [Theory]
    [InlineData("chrome.exe --profile-directory=Default", "Default")]
    [InlineData("chrome.exe --profile-directory=\"Profile 2\" --restore-session", "Profile 2")]
    [InlineData("msedge.exe --no-first-run --profile-directory=Work", "Work")]
    public void Extracts_profile_directory(string commandLine, string expected) =>
        Assert.Equal(expected, BrowserProfile.FromCommandLine(commandLine).Value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notepad.exe C:\\notes.txt")]
    public void No_profile_returns_none(string? commandLine) =>
        Assert.True(BrowserProfile.FromCommandLine(commandLine).HasNoValue);
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/TaskSpaces.Core.Tests`
Expected: FAIL — compile errors (`RulesEngine`, `BrowserProfile` don't exist yet).

- [ ] **Step 5: Implement rules types**

`src/TaskSpaces.Core/Rules/RuleMatchKind.cs`:

```csharp
namespace TaskSpaces.Core.Rules;

// How a rule inspects a window. Matched in the user's list order — first hit wins —
// so specific rules (a title regex) can sit above broad ones (a process name).
public enum RuleMatchKind { ProcessName, TitleRegex, BrowserProfile }
```

`src/TaskSpaces.Core/Rules/WorkspaceRule.cs`:

```csharp
namespace TaskSpaces.Core.Rules;

// "Windows matching Pattern belong to workspace WorkspaceId."
public sealed record WorkspaceRule(Guid WorkspaceId, RuleMatchKind Kind, string Pattern);
```

`src/TaskSpaces.Core/Rules/RenameRule.cs`:

```csharp
namespace TaskSpaces.Core.Rules;

// "Windows matching Pattern get taskbar name ShortName" — the TaskBarRenamer feature.
public sealed record RenameRule(RuleMatchKind Kind, string Pattern, string ShortName);
```

`src/TaskSpaces.Core/Rules/BrowserProfile.cs`:

```csharp
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Rules;

// Chromium browsers (Chrome/Edge/Brave/Vivaldi) expose the active profile only via
// the process command line: --profile-directory=Default or --profile-directory="Profile 2".
public static partial class BrowserProfile
{
    [GeneratedRegex("""--profile-directory=(?:"(?<q>[^"]+)"|(?<u>\S+))""")]
    private static partial Regex ProfileDirectory();

    public static Maybe<string> FromCommandLine(string? commandLine) =>
        commandLine is not null && ProfileDirectory().Match(commandLine) is { Success: true } m
            ? m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value
            : Maybe<string>.None;
}
```

`src/TaskSpaces.Core/Rules/RulesEngine.cs`:

```csharp
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Rules;

// Pure functions: window metadata + rule list in, decision out. No I/O, no state —
// this is the spec's "RulesEngine" component and the most heavily unit-tested code.
public static class RulesEngine
{
    public static Maybe<Guid> MatchWorkspace(WindowInfo window, IReadOnlyList<WorkspaceRule> rules) =>
        rules.TryFirst(r => Matches(window, r.Kind, r.Pattern)).Map(r => r.WorkspaceId);

    public static Maybe<string> MatchRename(WindowInfo window, IReadOnlyList<RenameRule> rules) =>
        rules.TryFirst(r => Matches(window, r.Kind, r.Pattern)).Map(r => r.ShortName);

    static bool Matches(WindowInfo window, RuleMatchKind kind, string pattern) => kind switch
    {
        RuleMatchKind.ProcessName => window.ProcessName.Equals(pattern, StringComparison.OrdinalIgnoreCase),
        RuleMatchKind.TitleRegex => SafeIsMatch(window.Title, pattern),
        RuleMatchKind.BrowserProfile => BrowserProfile.FromCommandLine(window.CommandLine)
            .Map(p => p.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            .GetValueOrDefault(false),
        _ => false,
    };

    // A user's malformed regex must degrade to "no match", never crash the pipeline.
    // The rule editor UI validates regexes at entry; this is defense in depth.
    static bool SafeIsMatch(string input, string pattern)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { return false; }
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/TaskSpaces.Core.Tests`
Expected: PASS (all RulesEngine + BrowserProfile tests green).

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "feat: core domain records and pure rules engine

*Collaboration by Claude*"
```

---

### Task 3: PersistenceStore (TDD)

Everything that survives a reboot, in one JSON file with atomic writes.

**Files:**
- Create: `src/TaskSpaces.Core/Persistence/InventoryEntry.cs`, `Persistence/AppState.cs`, `Persistence/IPersistenceStore.cs`, `Persistence/JsonPersistenceStore.cs`
- Test: `tests/TaskSpaces.Core.Tests/JsonPersistenceStoreTests.cs`

**Interfaces:**
- Consumes: `Workspace`, `WorkspaceRule`, `RenameRule` from Task 2.
- Produces:
  - `record InventoryEntry(string ProcessPath, string? CommandLine, string Title)`
  - `record AppState(IReadOnlyList<Workspace> Workspaces, IReadOnlyList<WorkspaceRule> WorkspaceRules, IReadOnlyList<RenameRule> RenameRules, IReadOnlyDictionary<Guid, IReadOnlyList<InventoryEntry>> Inventory)` with `static AppState Empty`
  - `interface IPersistenceStore { Result<AppState> Load(); Result Save(AppState state); }`
  - `class JsonPersistenceStore(string baseDirectory) : IPersistenceStore` — file `state.json`; production base dir is `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskSpaces")`

- [ ] **Step 1: Write failing tests**

`tests/TaskSpaces.Core.Tests/JsonPersistenceStoreTests.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public sealed class JsonPersistenceStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"taskspaces-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    static AppState SampleState()
    {
        var work = new Workspace(Guid.NewGuid(), "Work", Guid.NewGuid());
        return new AppState(
            [work, new Workspace(Guid.NewGuid(), "Personal", null)],
            [new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "devenv")],
            [new RenameRule(RuleMatchKind.TitleRegex, "Remote Desktop", "RDP")],
            new Dictionary<Guid, IReadOnlyList<InventoryEntry>>
            {
                [work.Id] = [new InventoryEntry(@"C:\Windows\System32\mstsc.exe", null, "RDP")],
            });
    }

    [Fact]
    public void Roundtrips_full_state()
    {
        var store = new JsonPersistenceStore(dir);
        var state = SampleState();

        Assert.True(store.Save(state).IsSuccess);
        var loaded = store.Load().Value;

        Assert.Equal(state.Workspaces, loaded.Workspaces);
        Assert.Equal(state.WorkspaceRules, loaded.WorkspaceRules);
        Assert.Equal(state.RenameRules, loaded.RenameRules);
        Assert.Equal(state.Inventory.Keys, loaded.Inventory.Keys);
        Assert.Equal(state.Inventory.Values.Single(), loaded.Inventory.Values.Single());
    }

    [Fact]
    public void Missing_file_loads_empty_state_not_failure()
    {
        var loaded = new JsonPersistenceStore(dir).Load();
        Assert.True(loaded.IsSuccess);
        Assert.Empty(loaded.Value.Workspaces);
    }

    [Fact]
    public void Corrupt_file_is_a_failure_not_an_exception()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), "{ not json !!!");
        Assert.True(new JsonPersistenceStore(dir).Load().IsFailure);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = new JsonPersistenceStore(dir);
        store.Save(SampleState());
        Assert.Single(Directory.GetFiles(dir));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/TaskSpaces.Core.Tests --filter JsonPersistenceStore`
Expected: FAIL — compile errors (types don't exist).

- [ ] **Step 3: Implement**

`src/TaskSpaces.Core/Persistence/InventoryEntry.cs`:

```csharp
namespace TaskSpaces.Core.Persistence;

// What we remember about a window for post-reboot rehydration: enough to relaunch
// the app (path + original command line) and to show the user what would come back.
public sealed record InventoryEntry(string ProcessPath, string? CommandLine, string Title);
```

`src/TaskSpaces.Core/Persistence/AppState.cs`:

```csharp
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Persistence;

// The single unit of persistence — everything under %APPDATA%\TaskSpaces\state.json.
// Inventory maps workspace id -> windows last seen in it (for rehydration prompts).
public sealed record AppState(
    IReadOnlyList<Workspace> Workspaces,
    IReadOnlyList<WorkspaceRule> WorkspaceRules,
    IReadOnlyList<RenameRule> RenameRules,
    IReadOnlyDictionary<Guid, IReadOnlyList<InventoryEntry>> Inventory)
{
    public static AppState Empty { get; } = new([], [], [], new Dictionary<Guid, IReadOnlyList<InventoryEntry>>());
}
```

`src/TaskSpaces.Core/Persistence/IPersistenceStore.cs`:

```csharp
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Persistence;

// Seam for tests: WorkspaceManager persists through this, fakes record the calls.
public interface IPersistenceStore
{
    Result<AppState> Load();
    Result Save(AppState state);
}
```

`src/TaskSpaces.Core/Persistence/JsonPersistenceStore.cs`:

```csharp
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Persistence;

public sealed class JsonPersistenceStore(string baseDirectory) : IPersistenceStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    string StatePath => Path.Combine(baseDirectory, "state.json");

    // Missing file is the normal first-run case -> Empty. Unreadable/corrupt file is a
    // real failure the caller must surface (we refuse to silently overwrite user data).
    public Result<AppState> Load() =>
        !File.Exists(StatePath)
            ? AppState.Empty
            : Result.Try(() => JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), Options)
                               ?? throw new JsonException("state.json deserialized to null"),
                         e => $"Could not read {StatePath}: {e.Message}");

    // Write-then-rename so a crash mid-write can never destroy the previous good state.
    public Result Save(AppState state) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            File.Move(tmp, StatePath, overwrite: true);
        }, e => $"Could not write {StatePath}: {e.Message}");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/TaskSpaces.Core.Tests`
Expected: PASS (all tests, including Task 2's).

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: json persistence store with atomic writes

*Collaboration by Claude*"
```

---

### Task 4: VirtualDesktopService

Wrap the COM wrapper behind `IVirtualDesktopService` so the undocumented-API risk lives in exactly one class. **Read `docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md` first** — the code below uses the API names as researched; the spike findings are authoritative where they differ (adjust this implementation, never the interface).

**Files:**
- Create: `src/TaskSpaces.Core/Abstractions/DesktopInfo.cs`, `Abstractions/IVirtualDesktopService.cs`
- Create: `src/TaskSpaces.Windows/TaskSpaces.Windows.csproj`, `src/TaskSpaces.Windows/Desktops/VirtualDesktopService.cs`
- Create: `tests/TaskSpaces.Windows.Tests/TaskSpaces.Windows.Tests.csproj`, `tests/TaskSpaces.Windows.Tests/VirtualDesktopServiceTests.cs`

**Interfaces:**
- Consumes: `WindowHandle` (Task 2); spike findings (Task 1).
- Produces:
  - `record DesktopInfo(Guid Id, string Name)`
  - `interface IVirtualDesktopService` with: `Result Initialize()`, `Result<IReadOnlyList<DesktopInfo>> GetDesktops()`, `Result<DesktopInfo> Create(string name)`, `Result Rename(Guid desktopId, string name)`, `Result Switch(Guid desktopId)`, `Result Remove(Guid desktopId)`, `Result MoveWindow(WindowHandle window, Guid desktopId)`, `Result<Guid> DesktopOf(WindowHandle window)`, `IObservable<Guid> CurrentChanged { get; }`

- [ ] **Step 1: Create projects**

```powershell
dotnet new classlib -n TaskSpaces.Windows -o src/TaskSpaces.Windows
dotnet new xunit -n TaskSpaces.Windows.Tests -o tests/TaskSpaces.Windows.Tests
dotnet sln add src/TaskSpaces.Windows tests/TaskSpaces.Windows.Tests
dotnet add src/TaskSpaces.Windows reference src/TaskSpaces.Core
dotnet add tests/TaskSpaces.Windows.Tests reference src/TaskSpaces.Windows
dotnet add src/TaskSpaces.Windows package Slions.VirtualDesktop
dotnet add src/TaskSpaces.Windows package System.Management
```

Both new csprojs: `<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>` (or the TFM the spike settled on) and `<PlatformTarget>x64</PlatformTarget>`.

- [ ] **Step 2: Write the Core abstraction**

`src/TaskSpaces.Core/Abstractions/DesktopInfo.cs`:

```csharp
namespace TaskSpaces.Core.Abstractions;

// A virtual desktop as Core sees it — id + name, nothing COM-shaped.
public sealed record DesktopInfo(Guid Id, string Name);
```

`src/TaskSpaces.Core/Abstractions/IVirtualDesktopService.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// The ONLY doorway to the undocumented virtual-desktop COM API (spec: isolate the risk).
// Every method returns Result: desktops vanish, windows close mid-move, and the COM
// layer can be entirely unsupported after an OS update — all expected, none fatal.
public interface IVirtualDesktopService
{
    // Probes COM support once at startup. Failure => app runs in "compatibility mode":
    // UI still lists workspaces but shows a banner and attempts no desktop operations.
    Result Initialize();

    Result<IReadOnlyList<DesktopInfo>> GetDesktops();
    Result<DesktopInfo> Create(string name);
    Result Rename(Guid desktopId, string name);
    Result Switch(Guid desktopId);
    Result Remove(Guid desktopId);
    Result MoveWindow(WindowHandle window, Guid desktopId);
    Result<Guid> DesktopOf(WindowHandle window);

    // Fires with the new desktop's id whenever the user switches by ANY means
    // (our UI, Win+Ctrl+arrows, Task View) — keeps the tray menu checkmark honest.
    IObservable<Guid> CurrentChanged { get; }
}
```

- [ ] **Step 3: Implement against Slions.VirtualDesktop**

`src/TaskSpaces.Windows/Desktops/VirtualDesktopService.cs`:

```csharp
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using WindowsDesktop;

namespace TaskSpaces.Windows.Desktops;

// Adapter over Slions.VirtualDesktop (runtime-compiled COM interop, undocumented API).
// See docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md for what was verified
// on Win11 build 26200. Every member call is wrapped: if Windows Update changes the
// COM shape, callers get Result.Failure, not a crash.
public sealed class VirtualDesktopService : IVirtualDesktopService
{
    public Result Initialize() =>
        Result.Try(() =>
        {
            // If the spike found an explicit Configure()/init call is required, it goes
            // here. Touching Current forces the interop compile — fail fast, in one place.
            if (!VirtualDesktop.IsSupported) throw new NotSupportedException("Virtual desktop API not recognized on this Windows build.");
            _ = VirtualDesktop.Current;
        }, e => $"Virtual desktops unavailable: {e.Message}");

    public Result<IReadOnlyList<DesktopInfo>> GetDesktops() =>
        Result.Try<IReadOnlyList<DesktopInfo>>(
            () => VirtualDesktop.GetDesktops().Select(d => new DesktopInfo(d.Id, d.Name)).ToList(),
            e => $"Could not enumerate desktops: {e.Message}");

    public Result<DesktopInfo> Create(string name) =>
        Result.Try(() =>
        {
            var desktop = VirtualDesktop.Create();
            desktop.Name = name;
            return new DesktopInfo(desktop.Id, name);
        }, e => $"Could not create desktop '{name}': {e.Message}");

    public Result Rename(Guid desktopId, string name) =>
        Find(desktopId).Tap(d => d.Name = name).Map(_ => name);

    public Result Switch(Guid desktopId) => Find(desktopId).Tap(d => d.Switch());

    public Result Remove(Guid desktopId) => Find(desktopId).Tap(d => d.Remove());

    public Result MoveWindow(WindowHandle window, Guid desktopId) =>
        Find(desktopId).Bind(d => Result.Try(
            () => VirtualDesktop.MoveToDesktop(window.Value, d),
            e => $"Could not move window {window.Value} (it may have closed): {e.Message}"));

    public Result<Guid> DesktopOf(WindowHandle window) =>
        Result.Try(() => VirtualDesktop.FromHwnd(window.Value), e => e.Message)
            .Ensure(d => d is not null, "Window is not on any desktop (closed or pinned).")
            .Map(d => d!.Id);

    public IObservable<Guid> CurrentChanged { get; } =
        Observable.FromEventPattern<VirtualDesktopChangedEventArgs>(
                h => VirtualDesktop.CurrentChanged += h,
                h => VirtualDesktop.CurrentChanged -= h)
            .Select(e => e.EventArgs.NewDesktop.Id);

    static Result<VirtualDesktop> Find(Guid desktopId) =>
        Result.Try(() => VirtualDesktop.FromId(desktopId), e => e.Message)
            .Ensure(d => d is not null, $"Desktop {desktopId} no longer exists.")
            .Map(d => d!);
}
```

Build: `dotnet build src/TaskSpaces.Windows` — fix member-name drift against spike findings until clean.

- [ ] **Step 4: Write integration tests (manual-run only)**

`tests/TaskSpaces.Windows.Tests/VirtualDesktopServiceTests.cs`:

```csharp
using TaskSpaces.Windows.Desktops;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// MUTATES REAL VIRTUAL DESKTOPS — excluded from normal runs. Execute manually with:
//   dotnet test tests/TaskSpaces.Windows.Tests --filter "Category=Integration"
[Trait("Category", "Integration")]
public class VirtualDesktopServiceTests(ITestOutputHelper output)
{
    [Fact]
    public void Full_lifecycle_create_rename_switch_remove()
    {
        var service = new VirtualDesktopService();
        Assert.True(service.Initialize().IsSuccess);

        var created = service.Create("TaskSpaces IT");
        output.WriteLine($"created: {created.Value.Id}");
        Assert.True(created.IsSuccess);

        Assert.True(service.Rename(created.Value.Id, "TaskSpaces IT2").IsSuccess);
        Assert.Contains(service.GetDesktops().Value, d => d.Name == "TaskSpaces IT2");

        Assert.True(service.Switch(created.Value.Id).IsSuccess);
        Thread.Sleep(1000);                                     // let the shell animate
        Assert.True(service.Remove(created.Value.Id).IsSuccess); // removing current hops back
    }

    [Fact]
    public void Operations_on_missing_desktop_fail_gracefully()
    {
        var service = new VirtualDesktopService();
        Assert.True(service.Initialize().IsSuccess);
        Assert.True(service.Switch(Guid.NewGuid()).IsFailure);
        Assert.True(service.Remove(Guid.NewGuid()).IsFailure);
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/TaskSpaces.Windows.Tests --filter "Category=Integration"` (watch your desktops flip — expected)
Expected: PASS. Then confirm the default run excludes them: `dotnet test --filter "Category!=Integration"` → Windows.Tests reports 0 executed.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: virtual desktop service isolating the COM API risk

*Collaboration by Claude*"
```

---

### Task 5: WindowMonitor

Top-level window lifecycle as an RX stream: WinEvent hooks (out-of-context, no DLL injection) for show/destroy/title-change, plus an `EnumWindows` snapshot for windows that existed before we started.

**Files:**
- Create: `src/TaskSpaces.Core/Abstractions/IWindowMonitor.cs`
- Create: `src/TaskSpaces.Windows/Monitoring/NativeMethods.cs`, `Monitoring/TopLevelWindows.cs`, `Monitoring/WindowInfoFactory.cs`, `Monitoring/WindowMonitor.cs`
- Test: `tests/TaskSpaces.Windows.Tests/WindowMonitorTests.cs`

**Interfaces:**
- Consumes: `WindowInfo`, `WindowEvent`, `WindowEventKind`, `WindowHandle` (Task 2).
- Produces:
  - `interface IWindowMonitor { Result Start(); IObservable<WindowEvent> Events { get; } IReadOnlyList<WindowInfo> Snapshot(); }`
  - `WindowInfoFactory.FromHwnd(nint hwnd) : Maybe<WindowInfo>` (also used by Task 8's Windows tab)
  - `TopLevelWindows.IsTaskbarCandidate(nint hwnd) : bool`

- [ ] **Step 1: Write the Core abstraction**

`src/TaskSpaces.Core/Abstractions/IWindowMonitor.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// Source of truth for "what windows exist". Start() MUST be called on a thread that
// pumps messages (the WPF dispatcher thread) — WinEvent callbacks arrive there.
public interface IWindowMonitor
{
    Result Start();
    IObservable<WindowEvent> Events { get; }
    IReadOnlyList<WindowInfo> Snapshot();
}
```

- [ ] **Step 2: Implement the Windows layer**

`src/TaskSpaces.Windows/Monitoring/NativeMethods.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace TaskSpaces.Windows.Monitoring;

// All P/Invoke in one place. x64-only (GetWindowLongPtr does not exist on 32-bit user32).
internal static class NativeMethods
{
    public delegate void WinEventProc(nint hook, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time);
    public delegate bool EnumWindowsProc(nint hwnd, nint lparam);

    [DllImport("user32.dll")] public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc proc, uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, nint lparam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wparam, string lparam, uint flags, uint timeoutMs, out nint result);

    public const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0, CHILDID_SELF = 0;
    public const uint GA_ROOT = 2;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;
    public const uint DWMWA_CLOAKED = 14;
    public const uint WM_SETTEXT = 0x000C;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
}
```

`src/TaskSpaces.Windows/Monitoring/TopLevelWindows.cs`:

```csharp
namespace TaskSpaces.Windows.Monitoring;

using static NativeMethods;

// Decides which HWNDs the product cares about: roughly "would this show on the taskbar?".
public static class TopLevelWindows
{
    public static bool IsTaskbarCandidate(nint hwnd) =>
        GetAncestor(hwnd, GA_ROOT) == hwnd          // top-level, not a child control
        && IsWindowVisible(hwnd)
        && !IsCloaked(hwnd)                          // UWP ghosts & windows on other desktops still count as visible; cloak check kills true ghosts
        && GetWindowTextLength(hwnd) > 0             // taskbar buttons always have text
        && (!HasExStyle(hwnd, WS_EX_TOOLWINDOW) || HasExStyle(hwnd, WS_EX_APPWINDOW)); // tool windows skip the taskbar unless they opt back in

    public static IReadOnlyList<nint> Enumerate()
    {
        var found = new List<nint>();
        EnumWindows((hwnd, _) => { if (IsTaskbarCandidate(hwnd)) found.Add(hwnd); return true; }, 0);
        return found;
    }

    static bool HasExStyle(nint hwnd, long style) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & style) != 0;

    // Caveat: windows on OTHER virtual desktops report DWM cloaked. Only exclude cloaked
    // windows at initial-snapshot time when they're also invisible; for our purposes a
    // cloaked-but-visible window (other desktop) is still a window we manage.
    static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
        && cloaked != 0
        && cloaked != 2; // DWM_CLOAKED_SHELL: cloaked by the shell = other virtual desktop = keep it
}
```

`src/TaskSpaces.Windows/Monitoring/WindowInfoFactory.cs`:

```csharp
using System.Diagnostics;
using System.Management;
using System.Text;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Windows.Monitoring;

using static NativeMethods;

// hwnd -> immutable WindowInfo snapshot. Anything can vanish between calls
// (window closed, process exited), so the whole thing is Maybe, not exceptions.
public static class WindowInfoFactory
{
    // WMI command-line lookup is slow (~10ms) — only browsers get it, and only because
    // BrowserProfile rules need --profile-directory.
    static readonly IReadOnlySet<string> Browsers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "vivaldi", "opera" };

    public static Maybe<WindowInfo> FromHwnd(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return Maybe<WindowInfo>.None;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = TryPath(process);
            return new WindowInfo(
                new WindowHandle(hwnd), (int)pid, process.ProcessName, path, TitleOf(hwnd),
                Browsers.Contains(process.ProcessName) ? TryCommandLine(pid) : null);
        }
        catch (ArgumentException) { return Maybe<WindowInfo>.None; } // process already gone
    }

    public static string TitleOf(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    // Elevated processes deny module access to non-elevated callers — expected, not an error.
    static string? TryPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    static string? TryCommandLine(uint pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            return searcher.Get().Cast<ManagementBaseObject>().FirstOrDefault()?["CommandLine"] as string;
        }
        catch (ManagementException) { return null; }
    }
}
```

`src/TaskSpaces.Windows/Monitoring/WindowMonitor.cs`:

```csharp
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Windows.Monitoring;

using static NativeMethods;

public sealed class WindowMonitor : IWindowMonitor, IDisposable
{
    readonly Subject<WindowEvent> events = new();
    // Known windows: needed to (a) suppress duplicate SHOW events and (b) emit a full
    // WindowInfo on DESTROY, when the hwnd can no longer be queried.
    readonly Dictionary<nint, WindowInfo> known = new();
    // CRITICAL: the delegate must be kept alive in a field. If the GC collects it,
    // the hook silently dies — the classic SetWinEventHook bug.
    readonly WinEventProc callback;
    readonly List<nint> hooks = [];

    public WindowMonitor() => callback = OnWinEvent;

    public IObservable<WindowEvent> Events => events.AsObservable();

    // Must run on a message-pumping thread (WPF dispatcher): WINEVENT_OUTOFCONTEXT
    // delivers callbacks via the registering thread's message queue.
    public Result Start() =>
        Result.Try(() =>
        {
            // One hook per range: SHOW..HIDE+DESTROY lifecycle, NAMECHANGE for renames.
            hooks.Add(Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE));
            hooks.Add(Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE));
            if (hooks.Any(h => h == 0)) throw new InvalidOperationException("SetWinEventHook failed");
            Snapshot().ToList().ForEach(w => known[w.Handle.Value] = w); // seed before events flow
        }, e => $"Window monitoring unavailable: {e.Message}");

    public IReadOnlyList<WindowInfo> Snapshot() =>
        TopLevelWindows.Enumerate()
            .Select(WindowInfoFactory.FromHwnd)
            .Where(m => m.HasValue).Select(m => m.Value)
            .ToList();

    nint Hook(uint min, uint max) =>
        SetWinEventHook(min, max, 0, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

    void OnWinEvent(nint hook, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == 0) return;

        switch (@event)
        {
            case EVENT_OBJECT_SHOW:
                TryAppear(hwnd);
                break;

            // Note: moving a window to another virtual desktop CLOAKS it (DWM), it does
            // not fire HIDE — so our own desktop moves never produce false Disappeared.
            case EVENT_OBJECT_DESTROY or EVENT_OBJECT_HIDE when known.Remove(hwnd, out var gone):
                events.OnNext(new WindowEvent(WindowEventKind.Disappeared, gone));
                break;

            // Title changed on a window we track -> updated snapshot. WindowRenamer's own
            // WM_SETTEXT also lands here; WorkspaceManager breaks the loop by comparing titles.
            case EVENT_OBJECT_NAMECHANGE when known.TryGetValue(hwnd, out var tracked):
                var updated = tracked with { Title = WindowInfoFactory.TitleOf(hwnd) };
                if (updated.Title == tracked.Title) break; // spurious NAMECHANGE, ignore
                known[hwnd] = updated;
                events.OnNext(new WindowEvent(WindowEventKind.TitleChanged, updated));
                break;

            // A window can become taskbar-worthy late (title set only after SHOW).
            case EVENT_OBJECT_NAMECHANGE:
                TryAppear(hwnd);
                break;
        }
    }

    // Appeared, deduplicated: SHOW fires repeatedly for the same hwnd.
    void TryAppear(nint hwnd)
    {
        if (known.ContainsKey(hwnd) || !TopLevelWindows.IsTaskbarCandidate(hwnd)) return;
        WindowInfoFactory.FromHwnd(hwnd).Execute(info =>
        {
            known[hwnd] = info;
            events.OnNext(new WindowEvent(WindowEventKind.Appeared, info));
        });
    }

    public void Dispose()
    {
        hooks.ForEach(h => UnhookWinEvent(h));
        events.OnCompleted();
    }
}
```

Build: `dotnet build src/TaskSpaces.Windows`
Expected: clean.

- [ ] **Step 3: Write the integration test**

`tests/TaskSpaces.Windows.Tests/WindowMonitorTests.cs`:

```csharp
using System.Diagnostics;
using System.Windows.Threading;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// Spawns a real window (winver) — manual run only:
//   dotnet test tests/TaskSpaces.Windows.Tests --filter "Category=Integration"
[Trait("Category", "Integration")]
public class WindowMonitorTests(ITestOutputHelper output)
{
    [Fact]
    public void Detects_appear_and_disappear_of_a_real_window()
    {
        var appeared = new TaskCompletionSource<WindowInfo>();
        var disappeared = new TaskCompletionSource<WindowInfo>();
        Dispatcher? dispatcher = null;

        // WinEvent hooks need a message pump; give the monitor a dedicated STA thread.
        var thread = new Thread(() =>
        {
            var monitor = new WindowMonitor();
            monitor.Events.Subscribe(e =>
            {
                output.WriteLine($"{e.Kind}: {e.Window.ProcessName} '{e.Window.Title}'");
                if (e.Window.ProcessName == "winver" && e.Kind == WindowEventKind.Appeared) appeared.TrySetResult(e.Window);
                if (e.Window.ProcessName == "winver" && e.Kind == WindowEventKind.Disappeared) disappeared.TrySetResult(e.Window);
            });
            Assert.True(monitor.Start().IsSuccess);
            dispatcher = Dispatcher.CurrentDispatcher;
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var winver = Process.Start("winver.exe");
        Assert.True(appeared.Task.Wait(TimeSpan.FromSeconds(10)), "winver window never appeared");
        winver.Kill();
        Assert.True(disappeared.Task.Wait(TimeSpan.FromSeconds(10)), "winver window never disappeared");
        dispatcher?.InvokeShutdown();
    }
}
```

Add to the test csproj (WPF types for `Dispatcher`): `<UseWPF>true</UseWPF>`.

- [ ] **Step 4: Run it**

Run: `dotnet test tests/TaskSpaces.Windows.Tests --filter "Category=Integration"`
Expected: PASS, with the winver appear/disappear lines in test output.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: window monitor emitting RX lifecycle events via WinEvent hooks

*Collaboration by Claude*"
```

---

### Task 6: Window renaming — RenameLedger (TDD) + Win32WindowTitles

The TaskBarRenamer feature. Split by testability: `RenameLedger` (Core) is pure bookkeeping of original/applied titles — fully unit-tested; `Win32WindowTitles` (Windows) is a two-method WM_SETTEXT wrapper.

**Files:**
- Create: `src/TaskSpaces.Core/Renaming/RenameLedger.cs`, `src/TaskSpaces.Core/Abstractions/IWindowTitles.cs`
- Create: `src/TaskSpaces.Windows/Renaming/Win32WindowTitles.cs`
- Test: `tests/TaskSpaces.Core.Tests/RenameLedgerTests.cs`

**Interfaces:**
- Consumes: `WindowHandle` (Task 2), `NativeMethods` (Task 5).
- Produces:
  - `interface IWindowTitles { Result Set(WindowHandle window, string title); Result<string> Get(WindowHandle window); }`
  - `RenameLedger` (immutable): `static RenameLedger Empty`, `RenameLedger Apply(WindowHandle window, string currentTitle, string shortName)`, `RenameLedger Remove(WindowHandle window)`, `Maybe<string> AppliedName(WindowHandle window)`, `Maybe<string> OriginalTitle(WindowHandle window)`, `bool NeedsReapply(WindowHandle window, string observedTitle)`, `IReadOnlyCollection<WindowHandle> Handles { get; }`

- [ ] **Step 1: Write failing ledger tests**

`tests/TaskSpaces.Core.Tests/RenameLedgerTests.cs`:

```csharp
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Renaming;

namespace TaskSpaces.Core.Tests;

public class RenameLedgerTests
{
    static readonly WindowHandle H = new(0x1234);

    [Fact]
    public void Apply_records_original_title_and_applied_name()
    {
        var ledger = RenameLedger.Empty.Apply(H, "myserver - Remote Desktop Connection", "RDP");
        Assert.Equal("RDP", ledger.AppliedName(H).Value);
        Assert.Equal("myserver - Remote Desktop Connection", ledger.OriginalTitle(H).Value);
    }

    [Fact]
    public void Second_apply_keeps_the_first_original_title()
    {
        // User renames "long title" -> "RDP" -> "Server". Restore must return to
        // "long title", not to the intermediate "RDP".
        var ledger = RenameLedger.Empty.Apply(H, "long title", "RDP").Apply(H, "RDP", "Server");
        Assert.Equal("Server", ledger.AppliedName(H).Value);
        Assert.Equal("long title", ledger.OriginalTitle(H).Value);
    }

    [Fact]
    public void Remove_forgets_the_window()
    {
        var ledger = RenameLedger.Empty.Apply(H, "title", "RDP").Remove(H);
        Assert.True(ledger.AppliedName(H).HasNoValue);
        Assert.Empty(ledger.Handles);
    }

    [Fact]
    public void NeedsReapply_when_app_rewrote_its_own_title()
    {
        // Browser navigated -> Windows fired NAMECHANGE with the browser's new title.
        var ledger = RenameLedger.Empty.Apply(H, "Old Page - Chrome", "Amy related");
        Assert.True(ledger.NeedsReapply(H, "New Page - Chrome"));
    }

    [Fact]
    public void No_reapply_when_observed_title_is_our_own_short_name()
    {
        // Our WM_SETTEXT also fires NAMECHANGE — this check breaks the infinite loop.
        var ledger = RenameLedger.Empty.Apply(H, "Old Page - Chrome", "Amy related");
        Assert.False(ledger.NeedsReapply(H, "Amy related"));
    }

    [Fact]
    public void Untracked_window_never_needs_reapply() =>
        Assert.False(RenameLedger.Empty.NeedsReapply(H, "anything"));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/TaskSpaces.Core.Tests --filter RenameLedger`
Expected: FAIL — `RenameLedger` doesn't exist.

- [ ] **Step 3: Implement the ledger and the titles interface**

`src/TaskSpaces.Core/Renaming/RenameLedger.cs`:

```csharp
using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Renaming;

// Immutable bookkeeping for renamed windows. Knows, per window: the title the app had
// before we touched it (for restore on un-rename/app exit) and the short name we set
// (to detect when the app rewrote its own title and we must re-apply).
public sealed class RenameLedger
{
    sealed record Entry(string OriginalTitle, string AppliedName);

    readonly ImmutableDictionary<WindowHandle, Entry> entries;

    RenameLedger(ImmutableDictionary<WindowHandle, Entry> entries) => this.entries = entries;

    public static RenameLedger Empty { get; } = new(ImmutableDictionary<WindowHandle, Entry>.Empty);

    // First Apply captures the true original; later Applies only change the short name.
    public RenameLedger Apply(WindowHandle window, string currentTitle, string shortName) =>
        new(entries.SetItem(window, entries.TryGetValue(window, out var existing)
            ? existing with { AppliedName = shortName }
            : new Entry(currentTitle, shortName)));

    public RenameLedger Remove(WindowHandle window) => new(entries.Remove(window));

    public Maybe<string> AppliedName(WindowHandle window) =>
        entries.TryGetValue(window, out var e) ? e.AppliedName : Maybe<string>.None;

    public Maybe<string> OriginalTitle(WindowHandle window) =>
        entries.TryGetValue(window, out var e) ? e.OriginalTitle : Maybe<string>.None;

    // True when the app overwrote our short name (observed != applied) — the caller
    // then re-sets the title. Observed == applied means the NAMECHANGE was our own echo.
    public bool NeedsReapply(WindowHandle window, string observedTitle) =>
        entries.TryGetValue(window, out var e) && observedTitle != e.AppliedName;

    public IReadOnlyCollection<WindowHandle> Handles => entries.Keys.ToList();
}
```

`src/TaskSpaces.Core/Abstractions/IWindowTitles.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// Reading/writing window titles, abstracted so WorkspaceManager is testable with fakes.
public interface IWindowTitles
{
    Result Set(WindowHandle window, string title);
    Result<string> Get(WindowHandle window);
}
```

`src/TaskSpaces.Windows/Renaming/Win32WindowTitles.cs`:

```csharp
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.Windows.Renaming;

using static NativeMethods;

// WM_SETTEXT with a timeout: a hung app must never hang TaskSpaces.
// (SetWindowText would block indefinitely on an unresponsive window.)
public sealed class Win32WindowTitles : IWindowTitles
{
    public Result Set(WindowHandle window, string title) =>
        Result.SuccessIf(
            SendMessageTimeout(window.Value, WM_SETTEXT, 0, title, SMTO_ABORTIFHUNG, 2000, out _) != 0,
            $"Window {window.Value} did not accept the title (closed or hung).");

    public Result<string> Get(WindowHandle window) =>
        Result.Success(WindowInfoFactory.TitleOf(window.Value));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/TaskSpaces.Core.Tests` and `dotnet build src/TaskSpaces.Windows`
Expected: PASS / clean build.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: window renaming ledger and WM_SETTEXT title writer

*Collaboration by Claude*"
```

---

### Task 7: WorkspaceManager (TDD)

The orchestrator: window events × rules → desktop moves, renames, inventory, persistence. Pure Core class over the four interfaces — every behavior unit-tested with fakes and an RX `Subject`.

**Files:**
- Create: `src/TaskSpaces.Core/WorkspaceManager.cs`
- Test: `tests/TaskSpaces.Core.Tests/WorkspaceManagerTests.cs`, `tests/TaskSpaces.Core.Tests/Fakes.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–6 (`IVirtualDesktopService`, `IWindowMonitor`, `IWindowTitles`, `IPersistenceStore`, `RulesEngine`, `RenameLedger`, `AppState`).
- Produces (Task 8's UI calls exactly these):
  - `class WorkspaceManager(IVirtualDesktopService desktops, IWindowMonitor monitor, IWindowTitles titles, IPersistenceStore store)`
  - `Result Start()` — loads state, reconciles desktops, seeds snapshot, subscribes to events
  - `AppState State { get; }` / `IObservable<Unit> StateChanged { get; }`
  - `Result Switch(Guid workspaceId)`
  - `Result<Workspace> AddWorkspace(string name)` / `Result RenameWorkspace(Guid id, string name)` / `Result RemoveWorkspace(Guid id)`
  - `Result SetRules(IReadOnlyList<WorkspaceRule> workspaceRules, IReadOnlyList<RenameRule> renameRules)`
  - `Result AssignWindow(WindowHandle window, Guid workspaceId)` (manual override)
  - `Result RenameWindow(WindowHandle window, string shortName)` / `Result RestoreTitle(WindowHandle window)`
  - `void RestoreAllTitles()` (app exit)
  - `void RegisterPendingLaunch(int processId, string processPath, Guid workspaceId)` + ctor param `Func<DateTimeOffset>? clock = null` (Task 9 wires these; declared now so the type is stable)
  - `IReadOnlyList<WindowInfo> KnownWindows { get; }` (for the Windows tab)

- [ ] **Step 1: Write fakes**

`tests/TaskSpaces.Core.Tests/Fakes.cs`:

```csharp
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// In-memory desktop "shell": desktops exist, windows sit on desktops, switches recorded.
public sealed class FakeDesktops : IVirtualDesktopService
{
    public List<DesktopInfo> Desktops { get; } = [];
    public Dictionary<WindowHandle, Guid> WindowPlacements { get; } = [];
    public List<Guid> Switches { get; } = [];
    public Subject<Guid> CurrentChangedSubject { get; } = new();

    public Result Initialize() => Result.Success();
    public Result<IReadOnlyList<DesktopInfo>> GetDesktops() => Result.Success<IReadOnlyList<DesktopInfo>>(Desktops.ToList());
    public Result<DesktopInfo> Create(string name)
    {
        var d = new DesktopInfo(Guid.NewGuid(), name);
        Desktops.Add(d);
        return d;
    }
    public Result Rename(Guid id, string name) =>
        Result.SuccessIf(Desktops.RemoveAll(d => d.Id == id) > 0, "missing").Tap(() => Desktops.Add(new DesktopInfo(id, name)));
    public Result Switch(Guid id) { Switches.Add(id); return Result.Success(); }
    public Result Remove(Guid id) { Desktops.RemoveAll(d => d.Id == id); return Result.Success(); }
    public Result MoveWindow(WindowHandle w, Guid id) { WindowPlacements[w] = id; return Result.Success(); }
    public Result<Guid> DesktopOf(WindowHandle w) =>
        WindowPlacements.TryGetValue(w, out var id) ? id : Result.Failure<Guid>("not placed");
    public IObservable<Guid> CurrentChanged => CurrentChangedSubject.AsObservable();
}

public sealed class FakeMonitor : IWindowMonitor
{
    public Subject<WindowEvent> Subject { get; } = new();
    public List<WindowInfo> InitialWindows { get; } = [];
    public Result Start() => Result.Success();
    public IObservable<WindowEvent> Events => Subject.AsObservable();
    public IReadOnlyList<WindowInfo> Snapshot() => InitialWindows.ToList();
}

public sealed class FakeTitles : IWindowTitles
{
    public Dictionary<WindowHandle, string> Titles { get; } = [];
    public Result Set(WindowHandle w, string title) { Titles[w] = title; return Result.Success(); }
    public Result<string> Get(WindowHandle w) => Titles.TryGetValue(w, out var t) ? t : "";
}

public sealed class FakeStore : IPersistenceStore
{
    public AppState Stored { get; set; } = AppState.Empty;
    public int SaveCount { get; private set; }
    public Result<AppState> Load() => Stored;
    public Result Save(AppState state) { Stored = state; SaveCount++; return Result.Success(); }
}
```

- [ ] **Step 2: Write failing manager tests**

`tests/TaskSpaces.Core.Tests/WorkspaceManagerTests.cs`:

```csharp
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class WorkspaceManagerTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    WorkspaceManager Manager() => new(desktops, monitor, titles, store);

    static WindowInfo Chrome(nint hwnd = 0x10, string title = "Some Page - Chrome") =>
        new(new WindowHandle(hwnd), 100, "chrome", @"C:\chrome.exe", title, "chrome.exe --profile-directory=Default");

    (WorkspaceManager manager, Workspace work) StartedWithWorkWorkspace(params object[] rules)
    {
        var work = new Workspace(Guid.NewGuid(), "Work", null);
        store.Stored = AppState.Empty with
        {
            Workspaces = [work],
            WorkspaceRules = rules.OfType<WorkspaceRule>().ToList(),
            RenameRules = rules.OfType<RenameRule>().ToList(),
        };
        var manager = Manager();
        Assert.True(manager.Start().IsSuccess);
        return (manager, manager.State.Workspaces.Single());
    }

    [Fact]
    public void Start_creates_a_desktop_for_a_workspace_that_has_none()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        Assert.NotNull(work.DesktopId);
        Assert.Contains(desktops.Desktops, d => d.Id == work.DesktopId && d.Name == "Work");
        Assert.Equal(work.DesktopId, store.Stored.Workspaces.Single().DesktopId); // persisted
    }

    [Fact]
    public void Start_rebinds_to_existing_desktop_by_name_instead_of_duplicating()
    {
        var existing = desktops.Create("Work").Value;
        var (_, work) = StartedWithWorkWorkspace();
        Assert.Equal(existing.Id, work.DesktopId);
        Assert.Single(desktops.Desktops);
    }

    [Fact]
    public void Appeared_window_matching_rule_is_moved_and_inventoried()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        manager.SetRules([new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "chrome")], []);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.Equal(work.DesktopId, desktops.WindowPlacements[new WindowHandle(0x10)]);
        Assert.Contains(store.Stored.Inventory[work.Id], e => e.ProcessPath == @"C:\chrome.exe");
    }

    [Fact]
    public void Appeared_window_without_matching_rule_is_left_alone()
    {
        var (manager, _) = StartedWithWorkWorkspace();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        Assert.Empty(desktops.WindowPlacements);
    }

    [Fact]
    public void Rename_rule_applies_short_name_on_appearance()
    {
        var (manager, _) = StartedWithWorkWorkspace(new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        Assert.Equal("Amy related", titles.Titles[new WindowHandle(0x10)]);
    }

    [Fact]
    public void Short_name_is_reapplied_when_app_rewrites_its_title()
    {
        var (manager, _) = StartedWithWorkWorkspace(new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        titles.Titles.Clear(); // forget the first application so we can observe the re-apply

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, Chrome(title: "Other Page - Chrome")));

        Assert.Equal("Amy related", titles.Titles[new WindowHandle(0x10)]);
    }

    [Fact]
    public void Own_echo_titlechange_is_not_reapplied()
    {
        var (manager, _) = StartedWithWorkWorkspace(new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        titles.Titles.Clear();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, Chrome(title: "Amy related")));

        Assert.Empty(titles.Titles); // no write happened — loop is broken
    }

    [Fact]
    public void Manual_rename_and_restore_roundtrip()
    {
        var (manager, _) = StartedWithWorkWorkspace();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.True(manager.RenameWindow(new WindowHandle(0x10), "RDP").IsSuccess);
        Assert.Equal("RDP", titles.Titles[new WindowHandle(0x10)]);

        Assert.True(manager.RestoreTitle(new WindowHandle(0x10)).IsSuccess);
        Assert.Equal("Some Page - Chrome", titles.Titles[new WindowHandle(0x10)]);
    }

    [Fact]
    public void Manual_assignment_moves_window_and_wins_over_rules()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.True(manager.AssignWindow(new WindowHandle(0x10), work.Id).IsSuccess);
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[new WindowHandle(0x10)]);
    }

    [Fact]
    public void Switch_delegates_to_desktop_service()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        Assert.True(manager.Switch(work.Id).IsSuccess);
        Assert.Equal(new[] { work.DesktopId!.Value }, desktops.Switches);
    }

    [Fact]
    public void Disappeared_window_leaves_inventory()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        manager.SetRules([new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "chrome")], []);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Chrome()));
        Assert.Empty(store.Stored.Inventory[work.Id]);
    }

    [Fact]
    public void Workspace_crud_persists()
    {
        var manager = Manager();
        Assert.True(manager.Start().IsSuccess);

        var added = manager.AddWorkspace("YouTube");
        Assert.True(added.IsSuccess);
        Assert.Contains(store.Stored.Workspaces, w => w.Name == "YouTube");

        Assert.True(manager.RenameWorkspace(added.Value.Id, "Video").IsSuccess);
        Assert.Contains(store.Stored.Workspaces, w => w.Name == "Video");
        Assert.Contains(desktops.Desktops, d => d.Name == "Video"); // desktop renamed too

        Assert.True(manager.RemoveWorkspace(added.Value.Id).IsSuccess);
        Assert.Empty(store.Stored.Workspaces);
    }

    [Fact]
    public void RestoreAllTitles_restores_every_renamed_window()
    {
        var (manager, _) = StartedWithWorkWorkspace(new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related"));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        manager.RestoreAllTitles();

        Assert.Equal("Some Page - Chrome", titles.Titles[new WindowHandle(0x10)]);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/TaskSpaces.Core.Tests --filter WorkspaceManager`
Expected: FAIL — `WorkspaceManager` doesn't exist.

- [ ] **Step 4: Implement WorkspaceManager**

`src/TaskSpaces.Core/WorkspaceManager.cs`:

```csharp
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;
using TaskSpaces.Core.Renaming;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core;

// The heart of TaskSpaces: subscribes to window lifecycle events and applies the
// data-flow from the spec —
//   Appeared      -> workspace rule -> move to desktop -> record inventory
//   Appeared      -> rename rule    -> apply short name (ledger keeps the original)
//   TitleChanged  -> renamed window -> re-apply short name (apps rewrite their titles)
//   Disappeared   -> drop from inventory + ledger
// Single-threaded by design: all events arrive on the UI dispatcher thread (WinEvent
// hooks deliver there), and all UI calls originate there too. No locks needed.
public sealed class WorkspaceManager(
    IVirtualDesktopService desktops,
    IWindowMonitor monitor,
    IWindowTitles titles,
    IPersistenceStore store,
    Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
    readonly Subject<Unit> stateChanged = new();
    readonly Dictionary<WindowHandle, WindowInfo> knownWindows = [];
    readonly Dictionary<WindowHandle, Guid> memberships = []; // window -> workspace
    RenameLedger ledger = RenameLedger.Empty;
    PendingPlacements pending = PendingPlacements.Empty;      // rehydration (Task 9)
    IDisposable? subscription;

    public AppState State { get; private set; } = AppState.Empty;
    public IObservable<Unit> StateChanged => stateChanged.AsObservable();
    public IReadOnlyList<WindowInfo> KnownWindows => knownWindows.Values.ToList();

    public Result Start() =>
        store.Load()
            .Tap(s => State = s)
            .Bind(_ => Reconcile())
            .Tap(() =>
            {
                monitor.Snapshot().ToList().ForEach(w => knownWindows[w.Handle] = w);
                subscription = monitor.Events.Subscribe(OnWindowEvent);
            });

    // --- workspace <-> desktop reconciliation -------------------------------------
    // Desktops don't survive reboots and ids go stale across app restarts. For each
    // workspace: keep a still-valid DesktopId; else adopt a live desktop with the same
    // name; else create one. Runs once at startup.
    Result Reconcile() =>
        desktops.GetDesktops().Bind(live =>
        {
            var reconciled = State.Workspaces
                .Select(w => BindDesktop(w, live))
                .ToList();
            return reconciled.Combine()
                .Tap(workspaces => Persist(State with { Workspaces = workspaces.ToList() }));
        });

    Result<Workspace> BindDesktop(Workspace workspace, IReadOnlyList<DesktopInfo> live) =>
        live.Any(d => d.Id == workspace.DesktopId)
            ? workspace
            : live.TryFirst(d => d.Name.Equals(workspace.Name, StringComparison.OrdinalIgnoreCase))
                .Match(
                    adopted => Result.Success(workspace with { DesktopId = adopted.Id }),
                    () => desktops.Create(workspace.Name).Map(created => workspace with { DesktopId = created.Id }));

    // --- event pipeline -------------------------------------------------------------
    void OnWindowEvent(WindowEvent e)
    {
        switch (e.Kind)
        {
            case WindowEventKind.Appeared: OnAppeared(e.Window); break;
            case WindowEventKind.TitleChanged: OnTitleChanged(e.Window); break;
            case WindowEventKind.Disappeared: OnDisappeared(e.Window); break;
        }
    }

    void OnAppeared(WindowInfo window)
    {
        knownWindows[window.Handle] = window;

        // Rehydrated launches win over rules: we KNOW where that app belongs.
        var (remaining, placement) = pending.Match(window, now());
        pending = remaining;
        placement.Or(RulesEngine.MatchWorkspace(window, State.WorkspaceRules))
            .Execute(workspaceId => Place(window, workspaceId));

        RulesEngine.MatchRename(window, State.RenameRules)
            .Execute(shortName => ApplyRename(window, shortName));
    }

    void OnTitleChanged(WindowInfo window)
    {
        var previouslyUnknown = !knownWindows.ContainsKey(window.Handle);
        knownWindows[window.Handle] = window;
        if (previouslyUnknown) { OnAppeared(window); return; } // became taskbar-worthy late

        if (ledger.NeedsReapply(window.Handle, window.Title))
            ledger.AppliedName(window.Handle).Execute(name => titles.Set(window.Handle, name));
        else if (ledger.AppliedName(window.Handle).HasNoValue)
            // Not renamed yet — but the new title may now match a rename rule.
            RulesEngine.MatchRename(window, State.RenameRules)
                .Execute(shortName => ApplyRename(window, shortName));
    }

    void OnDisappeared(WindowInfo window)
    {
        knownWindows.Remove(window.Handle);
        ledger = ledger.Remove(window.Handle);
        if (memberships.Remove(window.Handle, out var workspaceId))
            PersistInventory(workspaceId);
    }

    void Place(WindowInfo window, Guid workspaceId) =>
        Workspace(workspaceId)
            .Bind(w => w.DesktopId is { } desktopId
                ? desktops.MoveWindow(window.Handle, desktopId)
                : Result.Failure("Workspace has no desktop (compatibility mode)."))
            .Tap(() =>
            {
                memberships[window.Handle] = workspaceId;
                PersistInventory(workspaceId);
            });

    void ApplyRename(WindowInfo window, string shortName)
    {
        // Ledger first (captures the original title), then the actual write.
        ledger = ledger.Apply(window.Handle, window.Title, shortName);
        titles.Set(window.Handle, shortName);
    }

    // --- UI-facing operations ---------------------------------------------------

    public Result Switch(Guid workspaceId) =>
        Workspace(workspaceId).Bind(w => w.DesktopId is { } id
            ? desktops.Switch(id)
            : Result.Failure("Workspace has no desktop (compatibility mode)."));

    public Result<Workspace> AddWorkspace(string name) =>
        Result.FailureIf(string.IsNullOrWhiteSpace(name), "Workspace name required")
            .Bind(() => desktops.Create(name))
            .Map(d => new Workspace(Guid.NewGuid(), name, d.Id))
            .Tap(w => Persist(State with { Workspaces = [.. State.Workspaces, w] }));

    public Result RenameWorkspace(Guid id, string name) =>
        Workspace(id)
            .Tap(w => { if (w.DesktopId is { } d) desktops.Rename(d, name); })
            .Tap(w => Persist(State with
            {
                Workspaces = State.Workspaces.Select(x => x.Id == id ? x with { Name = name } : x).ToList(),
            }));

    // Removing a workspace never removes its desktop implicitly — windows live there.
    // The desktop merge behavior (Windows moves its windows to the previous desktop)
    // is exactly what we want, so removal = remove desktop + forget definition.
    public Result RemoveWorkspace(Guid id) =>
        Workspace(id)
            .Tap(w => { if (w.DesktopId is { } d) desktops.Remove(d); })
            .Tap(w => Persist(State with
            {
                Workspaces = State.Workspaces.Where(x => x.Id != id).ToList(),
                WorkspaceRules = State.WorkspaceRules.Where(r => r.WorkspaceId != id).ToList(),
                Inventory = State.Inventory.Where(kv => kv.Key != id).ToDictionary(kv => kv.Key, kv => kv.Value),
            }));

    public Result SetRules(IReadOnlyList<WorkspaceRule> workspaceRules, IReadOnlyList<RenameRule> renameRules)
    {
        Persist(State with { WorkspaceRules = workspaceRules, RenameRules = renameRules });
        return Result.Success();
    }

    public Result AssignWindow(WindowHandle window, Guid workspaceId) =>
        knownWindows.TryGetValue(window, out var info)
            ? Result.Success().Tap(() => Place(info, workspaceId))
            : Result.Failure("Window no longer exists.");

    public Result RenameWindow(WindowHandle window, string shortName) =>
        knownWindows.TryGetValue(window, out var info)
            ? Result.Success().Tap(() => ApplyRename(info, shortName))
            : Result.Failure("Window no longer exists.");

    public Result RestoreTitle(WindowHandle window) =>
        ledger.OriginalTitle(window)
            .ToResult("Window was never renamed.")
            .Bind(original => titles.Set(window, original))
            .Tap(() => ledger = ledger.Remove(window));

    // App exit / crash-avoidance: leave every window exactly as we found it.
    public void RestoreAllTitles() => ledger.Handles.ToList().ForEach(h => RestoreTitle(h));

    // Rehydrator (Task 9) tells us "pid X / path Y belongs to workspace Z, expect it soon".
    public void RegisterPendingLaunch(int processId, string processPath, Guid workspaceId) =>
        pending = pending.Add(processId, processPath, workspaceId, now());

    // --- persistence helpers -----------------------------------------------------

    Result<Workspace> Workspace(Guid id) =>
        State.Workspaces.TryFirst(w => w.Id == id).ToResult($"Workspace {id} not found.");

    void PersistInventory(Guid workspaceId)
    {
        var entries = memberships
            .Where(kv => kv.Value == workspaceId)
            .Select(kv => knownWindows.GetValueOrDefault(kv.Key))
            .Where(w => w?.ProcessPath is not null)
            .Select(w => new InventoryEntry(w!.ProcessPath!, w.CommandLine, ledger.OriginalTitle(w.Handle).GetValueOrDefault(w.Title)))
            .ToList();
        var inventory = State.Inventory.Where(kv => kv.Key != workspaceId)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        inventory[workspaceId] = entries;
        Persist(State with { Inventory = inventory });
    }

    void Persist(AppState next)
    {
        State = next;
        store.Save(next); // small JSON, synchronous write on every mutation is fine for v1
        stateChanged.OnNext(Unit.Default);
    }
}
```

Also create the referenced-but-empty rehydration type now so this compiles (its real tests come in Task 9):

`src/TaskSpaces.Core/Rehydration/PendingPlacements.cs`:

```csharp
using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Rehydration;

// "We just launched pid X (path Y) for workspace Z — when its window appears, place it
// there without consulting rules." Entries expire: a browser may reuse an existing
// process, so a pending entry that never matches must not linger forever.
public sealed class PendingPlacements
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    sealed record Pending(int ProcessId, string ProcessPath, Guid WorkspaceId, DateTimeOffset LaunchedAt);

    readonly ImmutableList<Pending> entries;

    PendingPlacements(ImmutableList<Pending> entries) => this.entries = entries;

    public static PendingPlacements Empty { get; } = new([]);

    public PendingPlacements Add(int processId, string processPath, Guid workspaceId, DateTimeOffset now) =>
        new(entries.Add(new Pending(processId, processPath, workspaceId, now)));

    // Match by pid first (exact), then by process path (browsers hand off to an existing
    // process, so the window's pid won't be the launched pid). Matched entry is consumed.
    public (PendingPlacements Remaining, Maybe<Guid> WorkspaceId) Match(WindowInfo window, DateTimeOffset now)
    {
        var alive = entries.RemoveAll(p => now - p.LaunchedAt > Ttl);
        var hit = alive.FirstOrDefault(p => p.ProcessId == window.ProcessId)
                  ?? alive.FirstOrDefault(p => p.ProcessPath.Equals(window.ProcessPath, StringComparison.OrdinalIgnoreCase));
        return hit is null
            ? (new PendingPlacements(alive), Maybe<Guid>.None)
            : (new PendingPlacements(alive.Remove(hit)), hit.WorkspaceId);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/TaskSpaces.Core.Tests`
Expected: PASS — all manager, ledger, rules, persistence tests green.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: workspace manager orchestrating events, rules, renames, persistence

*Collaboration by Claude*"
```

---

### Task 8: WPF tray application

The composition root and the v1 UI: tray icon with a workspace menu (switching), a Manage window (workspaces / rules / windows tabs, including manual reassign + rename), compatibility banner, start-with-Windows toggle. No MVVM framework — three simple windows don't justify one (comment this in code).

**Files:**
- Create: `src/TaskSpaces.App/TaskSpaces.App.csproj`, `App.xaml`, `App.xaml.cs`, `TrayMenu.cs`, `ManageWindow.xaml`, `ManageWindow.xaml.cs`, `StartupRegistration.cs`
- Create: `docs/superpowers/notes/manual-test-script.md`

**Interfaces:**
- Consumes: `WorkspaceManager` and its full UI-facing surface (Task 7), `VirtualDesktopService` (Task 4), `WindowMonitor` (Task 5), `Win32WindowTitles` (Task 6), `JsonPersistenceStore` (Task 3).
- Produces: `TaskSpaces.App.exe`; `StartupRegistration.IsEnabled/Enable()/Disable()` (also used by nothing else — self-contained).

- [ ] **Step 1: Create the project**

```powershell
dotnet new wpf -n TaskSpaces.App -o src/TaskSpaces.App
dotnet sln add src/TaskSpaces.App
dotnet add src/TaskSpaces.App reference src/TaskSpaces.Core src/TaskSpaces.Windows
dotnet add src/TaskSpaces.App package H.NotifyIcon.Wpf
dotnet add src/TaskSpaces.App package System.Drawing.Common
```

csproj: `<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>`, `<PlatformTarget>x64</PlatformTarget>`, `<UseWPF>true</UseWPF>`. Delete template `MainWindow.xaml(.cs)`.

- [ ] **Step 2: App shell + tray icon**

`App.xaml` (no StartupUri — tray-only app, lives until explicitly exited):

```xml
<Application x:Class="TaskSpaces.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown" />
```

`App.xaml.cs`:

```csharp
using System.Drawing;
using System.Windows;
using H.NotifyIcon;
using TaskSpaces.Core;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Windows.Desktops;
using TaskSpaces.Windows.Monitoring;
using TaskSpaces.Windows.Renaming;

namespace TaskSpaces.App;

// Composition root. Explicit wiring instead of a DI container — five objects don't
// justify one, and the construction ORDER documents the architecture.
public partial class App : Application
{
    TaskbarIcon? trayIcon;
    WorkspaceManager? manager;
    WindowMonitor? monitor;
    bool compatibilityMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskSpaces");
        var desktops = new VirtualDesktopService();
        monitor = new WindowMonitor();
        manager = new WorkspaceManager(desktops, monitor, new Win32WindowTitles(), new JsonPersistenceStore(stateDir));

        // Spec §Error handling: if the COM API is unrecognized (post-Windows-Update),
        // degrade to listing workspaces with a banner — never crash, never move windows.
        compatibilityMode = desktops.Initialize().IsFailure;
        if (!compatibilityMode)
        {
            manager.Start();      // reconcile desktops, seed snapshot, subscribe
            monitor.Start();      // we're on the dispatcher thread — hooks pump here
        }

        trayIcon = new TaskbarIcon
        {
            Icon = SystemIcons.Application, // placeholder until the product name settles
            ToolTipText = compatibilityMode ? "TaskSpaces (compatibility mode)" : "TaskSpaces",
            ContextMenu = TrayMenu.Build(manager, compatibilityMode, OpenManage, ExitApp),
        };
        // Rebuild the menu whenever workspaces change so names/counts stay honest.
        manager.StateChanged.Subscribe(_ => Dispatcher.Invoke(() =>
            trayIcon.ContextMenu = TrayMenu.Build(manager, compatibilityMode, OpenManage, ExitApp)));

        // OS shutdown/logoff: every window is about to close, and each close would fire
        // Disappeared and ERASE the inventory that rehydration needs. Unhook the monitor
        // FIRST so state.json keeps its last-known contents, then put titles back.
        SessionEnding += (_, _) =>
        {
            monitor.Dispose();
            manager.RestoreAllTitles();
        };
    }

    void OpenManage() => new ManageWindow(manager!, compatibilityMode).Show();

    void ExitApp()
    {
        manager?.RestoreAllTitles();  // leave every window as we found it
        monitor?.Dispose();
        trayIcon?.Dispose();
        Shutdown();
    }
}
```

`TrayMenu.cs`:

```csharp
using System.Windows.Controls;
using TaskSpaces.Core;

namespace TaskSpaces.App;

// The tray context menu IS the v1 switcher: one workspace per item, click to switch.
// (The dedicated switcher surface — pill/flyout/bar — is a separate, post-mockup plan.)
public static class TrayMenu
{
    public static ContextMenu Build(WorkspaceManager manager, bool compatibilityMode, Action openManage, Action exit)
    {
        var menu = new ContextMenu();

        if (compatibilityMode)
            menu.Items.Add(new MenuItem
            {
                Header = "⚠ Virtual desktops unavailable on this Windows build",
                IsEnabled = false,
            });

        manager.State.Workspaces.ToList().ForEach(w =>
        {
            var item = new MenuItem { Header = w.Name, IsEnabled = !compatibilityMode };
            item.Click += (_, _) => manager.Switch(w.Id);
            menu.Items.Add(item);
        });

        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "Manage…" };
        manage.Click += (_, _) => openManage();
        menu.Items.Add(manage);
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(exitItem);
        return menu;
    }
}
```

- [ ] **Step 3: Manage window (workspaces / rules / windows) + startup registration**

`StartupRegistration.cs`:

```csharp
using Microsoft.Win32;

namespace TaskSpaces.App;

// "Start with Windows" via HKCU Run — per-user, no admin, trivially reversible.
public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "TaskSpaces";

    public static bool IsEnabled =>
        Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(Name) is not null;

    public static void Enable() =>
        Registry.CurrentUser.CreateSubKey(RunKey).SetValue(Name, $"\"{Environment.ProcessPath}\"");

    public static void Disable() =>
        Registry.CurrentUser.CreateSubKey(RunKey).DeleteValue(Name, throwOnMissingValue: false);
}
```

`ManageWindow.xaml` — a `TabControl` with three tabs; keep XAML plain:

```xml
<Window x:Class="TaskSpaces.App.ManageWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="TaskSpaces — Manage" Width="720" Height="480">
    <DockPanel>
        <TextBlock x:Name="CompatBanner" DockPanel.Dock="Top" Background="#FFF4CE" Padding="8"
                   Text="⚠ Virtual desktops are unavailable on this Windows build. Workspaces are listed but windows will not be moved."
                   Visibility="Collapsed"/>
        <CheckBox x:Name="StartWithWindows" DockPanel.Dock="Bottom" Margin="8"
                  Content="Start TaskSpaces with Windows"
                  Checked="OnStartupToggled" Unchecked="OnStartupToggled"/>
        <TabControl>
            <TabItem Header="Workspaces">
                <DockPanel Margin="8">
                    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,8,0,0">
                        <TextBox x:Name="NewWorkspaceName" Width="200"/>
                        <Button Content="Add" Margin="8,0,0,0" Padding="12,2" Click="OnAddWorkspace"/>
                        <Button Content="Rename" Margin="8,0,0,0" Padding="12,2" Click="OnRenameWorkspace"/>
                        <Button Content="Remove" Margin="8,0,0,0" Padding="12,2" Click="OnRemoveWorkspace"/>
                    </StackPanel>
                    <ListBox x:Name="WorkspaceList" DisplayMemberPath="Name"/>
                </DockPanel>
            </TabItem>
            <TabItem Header="Rules">
                <DockPanel Margin="8">
                    <TextBlock DockPanel.Dock="Top" TextWrapping="Wrap" Margin="0,0,0,8"
                               Text="Rules run top-to-bottom; first match wins. Kinds: ProcessName (exact, e.g. chrome), TitleRegex (.NET regex on the title), BrowserProfile (Chrome/Edge --profile-directory name)."/>
                    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,8,0,0">
                        <Button Content="Save rules" Padding="12,2" Click="OnSaveRules"/>
                    </StackPanel>
                    <StackPanel>
                        <TextBlock Text="Workspace rules" FontWeight="Bold"/>
                        <DataGrid x:Name="WorkspaceRulesGrid" AutoGenerateColumns="True" Height="140" CanUserAddRows="True"/>
                        <TextBlock Text="Rename rules" FontWeight="Bold" Margin="0,8,0,0"/>
                        <DataGrid x:Name="RenameRulesGrid" AutoGenerateColumns="True" Height="140" CanUserAddRows="True"/>
                    </StackPanel>
                </DockPanel>
            </TabItem>
            <TabItem Header="Windows">
                <DockPanel Margin="8">
                    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,8,0,0">
                        <ComboBox x:Name="AssignTarget" Width="160" DisplayMemberPath="Name"/>
                        <Button Content="Send to workspace" Margin="8,0,0,0" Padding="12,2" Click="OnAssignWindow"/>
                        <TextBox x:Name="ShortName" Width="160" Margin="24,0,0,0"/>
                        <Button Content="Rename" Margin="8,0,0,0" Padding="12,2" Click="OnRenameWindow"/>
                        <Button Content="Restore title" Margin="8,0,0,0" Padding="12,2" Click="OnRestoreTitle"/>
                        <Button Content="Refresh" Margin="24,0,0,0" Padding="12,2" Click="OnRefreshWindows"/>
                    </StackPanel>
                    <ListView x:Name="WindowList">
                        <ListView.View>
                            <GridView>
                                <GridViewColumn Header="Process" Width="140" DisplayMemberBinding="{Binding ProcessName}"/>
                                <GridViewColumn Header="Title" Width="420" DisplayMemberBinding="{Binding Title}"/>
                            </GridView>
                        </ListView.View>
                    </ListView>
                </DockPanel>
            </TabItem>
        </TabControl>
    </DockPanel>
</Window>
```

`ManageWindow.xaml.cs` — code-behind wiring every handler to `WorkspaceManager` (rule grids edit `ObservableCollection` copies of mutable row DTOs, "Save rules" maps them back to the immutable records via `SetRules`; each row DTO validates its regex with the same 100 ms-timeout `Regex.IsMatch` guard and shows a message box listing invalid patterns instead of saving):

```csharp
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using CSharpFunctionalExtensions;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.App;

// Code-behind, not MVVM: three windows, no view-state worth abstracting. Every handler
// is a thin adapter onto WorkspaceManager, which owns all behavior (and all the tests).
public partial class ManageWindow : Window
{
    // DataGrid needs mutable rows; the domain records are immutable. These DTOs are the bridge.
    public sealed class WorkspaceRuleRow { public string Workspace { get; set; } = ""; public RuleMatchKind Kind { get; set; } public string Pattern { get; set; } = ""; }
    public sealed class RenameRuleRow { public RuleMatchKind Kind { get; set; } public string Pattern { get; set; } = ""; public string ShortName { get; set; } = ""; }

    readonly WorkspaceManager manager;
    readonly ObservableCollection<WorkspaceRuleRow> workspaceRules = [];
    readonly ObservableCollection<RenameRuleRow> renameRules = [];

    public ManageWindow(WorkspaceManager manager, bool compatibilityMode)
    {
        this.manager = manager;
        InitializeComponent();
        if (compatibilityMode) CompatBanner.Visibility = Visibility.Visible;
        StartWithWindows.IsChecked = StartupRegistration.IsEnabled;
        WorkspaceRulesGrid.ItemsSource = workspaceRules;
        RenameRulesGrid.ItemsSource = renameRules;
        Reload();
    }

    void Reload()
    {
        WorkspaceList.ItemsSource = manager.State.Workspaces;
        AssignTarget.ItemsSource = manager.State.Workspaces;
        WindowList.ItemsSource = manager.KnownWindows;
        workspaceRules.Clear();
        manager.State.WorkspaceRules.ToList().ForEach(r => workspaceRules.Add(new WorkspaceRuleRow
        {
            Workspace = manager.State.Workspaces.FirstOrDefault(w => w.Id == r.WorkspaceId)?.Name ?? "?",
            Kind = r.Kind,
            Pattern = r.Pattern,
        }));
        renameRules.Clear();
        manager.State.RenameRules.ToList().ForEach(r => renameRules.Add(new RenameRuleRow { Kind = r.Kind, Pattern = r.Pattern, ShortName = r.ShortName }));
    }

    void OnAddWorkspace(object s, RoutedEventArgs e) => Report(manager.AddWorkspace(NewWorkspaceName.Text).Map(_ => true)).Tap(Reload);
    void OnRenameWorkspace(object s, RoutedEventArgs e) => WithSelectedWorkspace(w => manager.RenameWorkspace(w.Id, NewWorkspaceName.Text));
    void OnRemoveWorkspace(object s, RoutedEventArgs e) => WithSelectedWorkspace(w => manager.RemoveWorkspace(w.Id));
    void OnAssignWindow(object s, RoutedEventArgs e) =>
        WithSelectedWindow(w => AssignTarget.SelectedItem is Workspace target
            ? manager.AssignWindow(w.Handle, target.Id)
            : Result.Failure("Pick a target workspace first."));
    void OnRenameWindow(object s, RoutedEventArgs e) => WithSelectedWindow(w => manager.RenameWindow(w.Handle, ShortName.Text));
    void OnRestoreTitle(object s, RoutedEventArgs e) => WithSelectedWindow(w => manager.RestoreTitle(w.Handle));
    void OnRefreshWindows(object s, RoutedEventArgs e) => Reload();
    void OnStartupToggled(object s, RoutedEventArgs e)
    {
        if (StartWithWindows.IsChecked == true) StartupRegistration.Enable(); else StartupRegistration.Disable();
    }

    void OnSaveRules(object s, RoutedEventArgs e)
    {
        var invalid = workspaceRules.Where(r => r.Kind == RuleMatchKind.TitleRegex && !IsValidRegex(r.Pattern))
            .Select(r => r.Pattern)
            .Concat(renameRules.Where(r => r.Kind == RuleMatchKind.TitleRegex && !IsValidRegex(r.Pattern)).Select(r => r.Pattern))
            .ToList();
        if (invalid.Count > 0) { MessageBox.Show($"Invalid regex pattern(s):\n{string.Join("\n", invalid)}"); return; }

        var byName = manager.State.Workspaces.ToDictionary(w => w.Name, w => w.Id, StringComparer.OrdinalIgnoreCase);
        var unknown = workspaceRules.Where(r => !byName.ContainsKey(r.Workspace)).Select(r => r.Workspace).ToList();
        if (unknown.Count > 0) { MessageBox.Show($"Unknown workspace(s):\n{string.Join("\n", unknown)}"); return; }

        Report(manager.SetRules(
            workspaceRules.Select(r => new WorkspaceRule(byName[r.Workspace], r.Kind, r.Pattern)).ToList(),
            renameRules.Select(r => new RenameRule(r.Kind, r.Pattern, r.ShortName)).ToList()).Map(() => true)).Tap(Reload);
    }

    static bool IsValidRegex(string pattern)
    {
        try { _ = Regex.IsMatch("", pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); return true; }
        catch (ArgumentException) { return false; }
    }

    Result<bool> WithSelectedWorkspace(Func<Workspace, Result> action) =>
        (WorkspaceList.SelectedItem is Workspace w ? action(w) : Result.Failure("Select a workspace first."))
            .Map(() => true)
            .Tap(Reload)
            .TapError(err => MessageBox.Show(err));

    Result<bool> WithSelectedWindow(Func<WindowInfo, Result> action) =>
        (WindowList.SelectedItem is WindowInfo w ? action(w) : Result.Failure("Select a window first."))
            .Map(() => true)
            .Tap(Reload)
            .TapError(err => MessageBox.Show(err));

    static Result<bool> Report(Result<bool> result) => result.TapError(err => MessageBox.Show(err));
}
```

- [ ] **Step 4: Build and run the app**

Run: `dotnet build src/TaskSpaces.App`, then launch **in the background** so output stays observable: `dotnet run --project src/TaskSpaces.App` (run_in_background).
Expected: tray icon appears; adding a workspace "Work" creates a real virtual desktop named Work (verify in Task View); clicking it in the tray menu switches to it.

- [ ] **Step 5: Write and run the manual test script**

`docs/superpowers/notes/manual-test-script.md`:

```markdown
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
```

Execute it; record results (pass/fail per line) at the bottom of the file.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: WPF tray app with workspace switching, rules and rename management

*Collaboration by Claude*"
```

---

### Task 9: Rehydration (TDD on the logic, manual on the prompt)

After a reboot, desktops are gone but `state.json` remembers each workspace's windows. On startup: recreate desktops (Task 7's reconcile already does), then offer to relaunch each workspace's remembered apps into it.

**Files:**
- Test: `tests/TaskSpaces.Core.Tests/PendingPlacementsTests.cs`
- Create: `src/TaskSpaces.App/RehydratePrompt.xaml`, `RehydratePrompt.xaml.cs`, `src/TaskSpaces.App/Rehydrator.cs`
- Modify: `src/TaskSpaces.App/App.xaml.cs` (show prompt after Start())

**Interfaces:**
- Consumes: `PendingPlacements` (created in Task 7), `WorkspaceManager.RegisterPendingLaunch` (Task 7), `AppState.Inventory` (Task 3).
- Produces: `Rehydrator.Launch(WorkspaceManager manager, Guid workspaceId, IReadOnlyList<InventoryEntry> entries) : int` (count launched).

- [ ] **Step 1: Write failing PendingPlacements tests**

`tests/TaskSpaces.Core.Tests/PendingPlacementsTests.cs`:

```csharp
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

public class PendingPlacementsTests
{
    static readonly Guid Work = Guid.NewGuid();
    static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    static WindowInfo Window(int pid = 500, string? path = @"C:\app.exe") =>
        new(new WindowHandle(0x10), pid, "app", path, "App", null);

    [Fact]
    public void Matches_by_exact_pid_and_consumes_the_entry()
    {
        var pending = PendingPlacements.Empty.Add(500, @"C:\app.exe", Work, T0);
        var (remaining, hit) = pending.Match(Window(pid: 500), T0.AddSeconds(5));
        Assert.Equal(Work, hit.Value);
        Assert.True(remaining.Match(Window(pid: 500), T0.AddSeconds(6)).WorkspaceId.HasNoValue); // consumed
    }

    [Fact]
    public void Falls_back_to_process_path_when_pid_differs()
    {
        // Browsers hand the window to an already-running process — launched pid never appears.
        var pending = PendingPlacements.Empty.Add(500, @"C:\app.exe", Work, T0);
        Assert.Equal(Work, pending.Match(Window(pid: 999), T0.AddSeconds(5)).WorkspaceId.Value);
    }

    [Fact]
    public void Expired_entries_never_match()
    {
        var pending = PendingPlacements.Empty.Add(500, @"C:\app.exe", Work, T0);
        Assert.True(pending.Match(Window(pid: 500), T0.Add(PendingPlacements.Ttl).AddSeconds(1)).WorkspaceId.HasNoValue);
    }

    [Fact]
    public void Unrelated_window_matches_nothing()
    {
        var pending = PendingPlacements.Empty.Add(500, @"C:\app.exe", Work, T0);
        Assert.True(pending.Match(Window(pid: 999, path: @"C:\other.exe"), T0.AddSeconds(5)).WorkspaceId.HasNoValue);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/TaskSpaces.Core.Tests --filter PendingPlacements`
Expected: PASS immediately if Task 7's implementation was faithful — these tests pin the behavior down; if any fail, fix `PendingPlacements`, not the tests. Also add a manager-level test to `WorkspaceManagerTests` proving the end-to-end priority:

```csharp
    [Fact]
    public void Pending_launch_placement_beats_rules()
    {
        var (manager, work) = StartedWithWorkWorkspace();
        var other = manager.AddWorkspace("Other").Value;
        manager.SetRules([new WorkspaceRule(other.Id, RuleMatchKind.ProcessName, "chrome")], []);

        manager.RegisterPendingLaunch(100, @"C:\chrome.exe", work.Id);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.Equal(work.DesktopId, desktops.WindowPlacements[new WindowHandle(0x10)]);
    }
```

Run: `dotnet test tests/TaskSpaces.Core.Tests` — Expected: PASS.

- [ ] **Step 3: Implement Rehydrator + prompt**

`src/TaskSpaces.App/Rehydrator.cs`:

```csharp
using System.Diagnostics;
using TaskSpaces.Core;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.App;

// Relaunches a workspace's remembered apps and tells the manager to expect their
// windows. Failures are per-entry and non-fatal: a moved/uninstalled exe just doesn't
// come back (matching the browser-session-restore mental model from the spec).
public static class Rehydrator
{
    public static int Launch(WorkspaceManager manager, Guid workspaceId, IReadOnlyList<InventoryEntry> entries) =>
        entries.Count(entry => TryLaunch(manager, workspaceId, entry));

    static bool TryLaunch(WorkspaceManager manager, Guid workspaceId, InventoryEntry entry)
    {
        try
        {
            // CommandLine is the ORIGINAL full command line ("exe" args...) — strip the
            // exe part; what remains are the arguments to relaunch with.
            var process = Process.Start(new ProcessStartInfo(entry.ProcessPath)
            {
                Arguments = StripExecutable(entry.CommandLine, entry.ProcessPath),
                UseShellExecute = true,
            });
            if (process is null) return false;
            manager.RegisterPendingLaunch(process.Id, entry.ProcessPath, workspaceId);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    static string StripExecutable(string? commandLine, string processPath)
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
            : ""; // command line doesn't start with the known exe — safer to relaunch bare
    }
}
```

`RehydratePrompt.xaml`:

```xml
<Window x:Class="TaskSpaces.App.RehydratePrompt"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="TaskSpaces — Restore workspaces?" Width="420" SizeToContent="Height"
        WindowStartupLocation="CenterScreen" Topmost="True">
    <DockPanel Margin="12">
        <TextBlock DockPanel.Dock="Top" TextWrapping="Wrap" Margin="0,0,0,8"
                   Text="These workspaces had apps in them before the last shutdown. Relaunch them into their workspaces?"/>
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Restore selected" Padding="12,4" Click="OnRestore"/>
            <Button Content="Skip" Padding="12,4" Margin="8,0,0,0" Click="OnSkip"/>
        </StackPanel>
        <ItemsControl x:Name="WorkspaceChecklist"/>
    </DockPanel>
</Window>
```

`RehydratePrompt.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using TaskSpaces.Core;

namespace TaskSpaces.App;

// Per-workspace opt-in, spec's "restore session?" model. Shown once at startup and
// only when at least one workspace has remembered apps.
public partial class RehydratePrompt : Window
{
    readonly WorkspaceManager manager;
    readonly List<(CheckBox Box, Guid WorkspaceId)> checks = [];

    public RehydratePrompt(WorkspaceManager manager)
    {
        this.manager = manager;
        InitializeComponent();
        manager.State.Workspaces
            .Select(w => (Workspace: w, Entries: manager.State.Inventory.GetValueOrDefault(w.Id)))
            .Where(x => x.Entries is { Count: > 0 })
            .ToList()
            .ForEach(x =>
            {
                var box = new CheckBox { Content = $"{x.Workspace.Name} ({x.Entries!.Count} app(s))", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
                checks.Add((box, x.Workspace.Id));
                WorkspaceChecklist.Items.Add(box);
            });
    }

    public static bool HasAnythingToRestore(WorkspaceManager manager) =>
        manager.State.Workspaces.Any(w => manager.State.Inventory.GetValueOrDefault(w.Id) is { Count: > 0 });

    void OnRestore(object s, RoutedEventArgs e)
    {
        checks.Where(c => c.Box.IsChecked == true)
            .ToList()
            .ForEach(c => Rehydrator.Launch(manager, c.WorkspaceId, manager.State.Inventory[c.WorkspaceId]));
        Close();
    }

    void OnSkip(object s, RoutedEventArgs e) => Close();
}
```

Modify `App.xaml.cs` `OnStartup`, right after the tray icon is created (only outside compatibility mode):

```csharp
        if (!compatibilityMode && RehydratePrompt.HasAnythingToRestore(manager))
            new RehydratePrompt(manager).Show();
```

- [ ] **Step 4: Verify end-to-end manually**

Run: `dotnet test tests/TaskSpaces.Core.Tests` (all green), then the manual pass: run the app, put Notepad in Work via a rule, exit the app (inventory persists), close Notepad, relaunch the app.
Expected: prompt lists "Work (1 app(s))"; Restore relaunches Notepad and it lands on Work's desktop. Append the result to `manual-test-script.md` as item 14.

- [ ] **Step 5: Commit, push branch, open PR**

```powershell
git add -A
git commit -m "feat: post-reboot rehydration with per-workspace restore prompt

*Collaboration by Claude*"
git push -u origin feature/taskspaces-v1
gh pr create --title "TaskSpaces v1: workspaces, rules, renaming, persistence, rehydration" --body "Implements docs/superpowers/specs/2026-08-01-taskspaces-design.md per docs/superpowers/plans/2026-08-01-taskspaces-implementation.md. Spike findings in docs/superpowers/notes/. *Collaboration by Claude*"
```

Do **not** merge — Petre reviews.

---

## After this plan (separate, decided with Petre)

1. **Switcher surface** — produce visual mockups (floating pill / tray flyout / docked bar), Petre picks, then a small follow-up plan implements it against `WorkspaceManager` (the API surface is already sufficient: `State`, `StateChanged`, `Switch`, `CurrentChanged`).
2. **Hotkeys** — optional accelerators once the switcher exists.
3. **Product name + real icon** — before any public release.

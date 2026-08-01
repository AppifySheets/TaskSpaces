# Virtual-desktop COM wrapper spike — findings

Date: 2026-08-01
Machine: Windows 11 Pro, OS build **10.0.26200.0** (25H2, enablement package on the 24H2 servicing branch)
.NET SDK: 10.0.203
Package under test: `Slions.VirtualDesktop` 6.9.2 (April 2025; documents support up to build 26100)

## Verdict

**Works, with two required code-level corrections (not build-related).** The library recognized
and operated correctly on build 26200 with no build-override configuration needed. All 8 checks
in the spike passed, on two independent runs, with the virtual-desktop count returning to the
original baseline (2 desktops: `Main`, `BG`) after each run — no stray desktops left behind, no
orphaned processes.

Neither of the brief's listed build-compatibility contingencies (GitHub issue check, COM
interface ID override, vendoring MScholtes/VirtualDesktop) was needed. The two problems
encountered were unrelated to the OS build: a required initialization call, and a .NET
threading/entry-point gotcha with `[STAThread]` and `async Main`.

## Chosen TFM

`net10.0-windows10.0.19041.0`, `PlatformTarget=x64`, as specified in the brief. **No net8
A/B test was needed** — net10 worked on the first fully-corrected run. The package's
net8.0-windows lib assets loaded and ran fine under a net10 app.

## Per-check results (final run)

```
OS build: 10.0.26200.0
Thread apartment state: STA
0. Configure(): OK
1. IsSupported: True
2. Enumerate: OK - 2 desktop(s): 'Main', 'BG'
3. Create+rename: OK - 37d032e3-126d-48aa-b939-fdd3a868c39c 'TaskSpaces spike'
4. Switch: OK - current == created ? True
5. Move window: OK - on created desktop ? True
6. FromId roundtrip: OK - True
7. CurrentChanged fired: OK - -> 37d032e3-126d-48aa-b939-fdd3a868c39c
7. CurrentChanged fired: OK - -> 813282cd-8da3-434e-8dca-4aff7e00ed9d
SPIKE RUN COMPLETE - see numbered OK/FAIL lines above for the verdict.
8. Removed spike desktop - check Task View that no stray desktop remains.
```

(Line 7 fires twice: once for `created.Switch()`, once for `original.Switch()` — both
`CurrentChanged` invocations carry the correct destination desktop ID, confirming the event
payload is reliable for Task 4's RX wrapper.)

Verbatim console output from the **first** attempt is included below in "Failure transcripts"
because it demonstrates the two real defects found; the second and later runs are clean
repeats of the block above.

## Required initialization

`VirtualDesktop.Configure()` **must be called first**, before `IsSupported`, `GetDesktops()`,
or any other member. This is documented only in the package's XML doc comments (not the
README):

> "Initialize using the default settings. This method should always be called first."

The brief's draft `Program.cs` omitted this call entirely. Skipping it doesn't throw
immediately — it fails later, from inside `Configure()`'s own implicit call chain — because
public members other than `Configure()` don't call it for you.

There is also `Configure(VirtualDesktopConfiguration)` for compiler-behavior overrides
(`SaveCompiledAssembly`, `CompiledAssemblySaveDirectory`) — not needed here, but useful for
Task 4 if runtime-compiled-assembly caching/debugging is ever wanted.

## Two required code corrections (both non-build-related)

### 1. The process must run on an STA thread

`VirtualDesktop.Configure()` internally constructs a WPF `HwndSource` (`ExplorerRestartListenerWindow`)
to listen for `explorer.exe` restarts. WPF's `InputManager` throws unless the constructing
thread is STA:

```
System.InvalidOperationException: The calling thread must be STA, because many UI components require this.
   at System.Windows.Input.InputManager..ctor()
   at System.Windows.Input.InputManager.GetCurrentInputManagerImpl()
   at System.Windows.Interop.HwndMouseInputProvider..ctor(HwndSource source)
   at System.Windows.Interop.HwndSource.Initialize(HwndSourceParameters parameters)
   at WindowsDesktop.Utils.RawWindow.Show(HwndSourceParameters parameters)
   at WindowsDesktop.Utils.TransparentWindow.Show()
   at WindowsDesktop.Utils.ExplorerRestartListenerWindow.Show()
   at WindowsDesktop.VirtualDesktop.InitializeIfNeeded()
   at WindowsDesktop.VirtualDesktop.Configure()
```

A default `dotnet new console` top-level-statements `Program.cs` runs MTA and cannot carry a
`[STAThread]` attribute (top-level statements have no addressable `Main` to attribute).
**Fix: convert to an explicit `Program` class with a `[STAThread]`-attributed `Main`.**
Task 4 (and any future console/tray entry point that touches this library) must NOT use
top-level statements, or must otherwise ensure the entry thread is STA.

### 2. `[STAThread]` is silently ignored on `async Task`/`async Task<int> Main()`

Attributing an `async` `Main` with `[STAThread]` compiles cleanly and produces **no warning**,
but the CLR does not honor it — `Thread.CurrentThread.GetApartmentState()` still reports `MTA`
at runtime. Verified independently with a minimal throwaway repro outside this project
(sync `Main` + `[STAThread]` → `STA`; async `Main` + `[STAThread]` → `MTA`, same SDK/TFM).
This is a well-known but easy-to-miss .NET async/STA interaction; it is not flagged by the
compiler or analyzers.

**Fix: keep `Main` synchronous.** Structure as:

```csharp
internal static class Program
{
    [STAThread]
    static int Main() => RunAsync().GetAwaiter().GetResult();

    static async Task<int> RunAsync() { /* ... await-based logic ... */ }
}
```

Task 4's entry point must follow this same shape (synchronous `[STAThread] Main` blocking on
an async `RunAsync`) wherever it needs both the virtual-desktop API and `await`.

## Confirmed working API member names

All member names anticipated in the brief and in `Program.cs`'s draft matched the real
`WindowsDesktop.VirtualDesktop` public surface exactly — confirmed both from the package's
shipped XML doc (`VirtualDesktop.xml`) and by reflecting directly over
`lib/net8.0-windows10.0.19041/VirtualDesktop.dll`:

- `VirtualDesktop.IsSupported` — static `bool` property
- `VirtualDesktop.GetDesktops()` — static, returns `VirtualDesktop[]`
- `VirtualDesktop.Create()` — static, returns `VirtualDesktop`
- `VirtualDesktop.Current` — static property, returns `VirtualDesktop`
- `VirtualDesktop.FromHwnd(IntPtr)` — static, returns `VirtualDesktop?` (null if window not found)
- `VirtualDesktop.FromId(Guid)` — static, returns `VirtualDesktop?`
- `VirtualDesktop.MoveToDesktop(IntPtr hwnd, VirtualDesktop desktop)` — static, `void`
- `desktop.Switch()` — instance, `void`
- `desktop.Remove()` — instance, `void`; also overload `Remove(VirtualDesktop fallbackDesktop)`
  which explicitly switches to the given fallback (used in the spike's cleanup for a
  deterministic post-removal desktop regardless of what's current)
- `desktop.Name` — instance `string` get/set ("not supported on Windows 10" per XML doc; works fine here on 11)
- `desktop.Id` — instance `Guid` get-only
- `VirtualDesktop.CurrentChanged` — static event, `EventHandler<VirtualDesktopChangedEventArgs>`
- `VirtualDesktopChangedEventArgs.NewDesktop` / `.OldDesktop` — both `VirtualDesktop`, confirmed by reflection

Not exercised by this spike but present and worth knowing for later tasks:
`VirtualDesktop.Created`, `Destroyed`, `DestroyBegin`, `DestroyFailed`, `Moved`, `Renamed`,
`WallpaperChanged`, `Switched`, `RemoteConnected` events; `PinWindow`/`UnpinWindow`/
`IsPinnedWindow`; `PinApplication`/`UnpinApplication`/`IsPinnedApplication`; `GetLeft()`/
`GetRight()`; `WallpaperPath`; `Move(int index)`; `RegisterViewChanged(IntPtr, Action<IntPtr>)`.

No member names needed correction — the brief's assumed API surface was accurate. The two
defects found were both process/threading concerns, not API-shape concerns.

## Build-compatibility internals (why 26200 worked without an override)

Reflecting over the package's internal `WindowsDesktop.Interop` namespace shows it ships
build-specific COM interop implementations for `Build10240`, `Build20348`, `Build22000`,
`Build22621`, and `Build26100`, plus a `WindowsDesktop.Interop.OsBuildSettings` type that
maps an `osBuild` `Version` to an interface-ID override (`SettingsProperty`, i.e. the
app.config override mechanism described in the README). String constants embedded in the DLL
show the known version thresholds: `20348.0000`, `22000.0000`, `22621.2215`, `22621.3155`,
`22631.2428`, `22631.3155`, `26100.0000` — i.e., **26100 is the highest known build**, and the
library evidently selects the highest known provider whose build is `<=` the running OS build
rather than requiring an exact match. Build 26200 (25H2) resolved to the `Build26100` provider
and every COM call succeeded, confirming the brief's hypothesis that 25H2 shares 24H2's GUIDs
on this servicing branch — at least for the interfaces this spike exercised. No
`app.config`/`VirtualDesktopConfiguration` override was necessary.

## Failure transcripts (first attempt — kept for the record)

Attempt 1, top-level-statements + `async Task<int> Main()` (no `[STAThread]` possible):

```
OS build: 10.0.26200.0
0. Configure(): FAIL - System.InvalidOperationException: The calling thread must be STA, because many UI components require this.
   at System.Windows.Input.InputManager..ctor()
   at System.Windows.Input.InputManager.GetCurrentInputManagerImpl()
   at System.Windows.Interop.HwndMouseInputProvider..ctor(HwndSource source)
   at System.Windows.Interop.HwndSource.Initialize(HwndSourceParameters parameters)
   at WindowsDesktop.Utils.RawWindow.Show(HwndSourceParameters parameters)
   at WindowsDesktop.Utils.TransparentWindow.Show()
   at WindowsDesktop.Utils.ExplorerRestartListenerWindow.Show()
   at WindowsDesktop.VirtualDesktop.InitializeIfNeeded()
   at WindowsDesktop.VirtualDesktop.Configure()
   at Program.<Main>$(String[] args) in ...\spikes\VirtualDesktopSpike\Program.cs:line 22
```

Attempt 2, converted to explicit class + `[STAThread] async Task<int> Main()` — **same
failure**, because the attribute is ignored on async Main:

```
OS build: 10.0.26200.0
0. Configure(): FAIL - System.InvalidOperationException: The calling thread must be STA, because many UI components require this.
   ... (identical stack) ...
   at Program.Main() in ...\spikes\VirtualDesktopSpike\Program.cs:line 30
```

Independent minimal repro confirming attempt 2's root cause (outside this project, same SDK):

```csharp
[STAThread] static void Main() { ... }              // GetApartmentState() -> STA
[STAThread] static async Task<int> Main() { ... }   // GetApartmentState() -> MTA (!)
```

Attempt 3 (final, working shape: sync `[STAThread] Main` blocking on `RunAsync()`) — see
"Per-check results" above for full clean output.

## Cleanup verification

- Ran the spike twice back-to-back; `Enumerate` reported the same baseline (`2 desktop(s):
  'Main', 'BG'`) both times — no stray desktop accumulated across runs.
- `tasklist` after each run showed no lingering `winver.exe` process.
- Cleanup is implemented as `try/finally` around the create→...→remove sequence in
  `Program.cs`, so the created desktop is removed (via `Remove(originalDesktop)`, an explicit
  fallback for deterministic behavior) and the guinea-pig process is killed even if an
  earlier check throws.

## Implications for Task 4

1. Task 4's entry point (or at minimum, whatever process/thread first touches
   `WindowsDesktop.VirtualDesktop`) must be an explicit class with `[STAThread]` on a
   **synchronous** `Main`, blocking on an async body via `.GetAwaiter().GetResult()` — not
   top-level statements, not `async Main`.
2. Call `VirtualDesktop.Configure()` exactly once at startup, before any other
   `VirtualDesktop` member access.
3. No vendored fallback, no COM GUID override, no net8 retarget needed — build on
   `net10.0-windows10.0.19041.0` with `Slions.VirtualDesktop` 6.9.2 as planned.
4. `VirtualDesktop.CurrentChanged` is confirmed to fire reliably and carries a correct
   `NewDesktop`/`OldDesktop` pair — safe to wrap directly in an `IObservable<VirtualDesktop>`
   (per `Observable.FromEventPattern` or similar) for the RX-based design called for in
   `CLAUDE.md`.
5. If a WPF/WinForms tray app hosts this later (rather than a console), the message-pump
   requirement is naturally satisfied by `Dispatcher.Run()`/`Application.Run()`, so the
   `GetAwaiter().GetResult()` workaround is a console-spike-only concern — worth re-confirming
   once Task 4 picks its actual hosting model (tray app likely uses a message loop already).

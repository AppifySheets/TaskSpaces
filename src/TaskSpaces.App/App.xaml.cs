using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
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
    SwitcherPanel? switcherPanel;
    HotkeyService? hotkeys;
    FloatingBar? floatingBar; // Task 11: created lazily on first show, same as switcherPanel
    IVirtualDesktopService? desktops; // Task 11 fix round 4: promoted from a local so PinOwnWindow (below) can reach it from the tray/hover callbacks, not just OnStartup
    bool floatingBarPinned, switcherPanelPinned; // Task 11 fix round 4: pin each window's real hwnd to all desktops exactly once (see PinOwnWindow)

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Reviewer (fix round 1, Critical, last-ditch backstop): an unhandled exception on
        // the dispatcher thread — e.g. the ArgumentException a duplicate-name dictionary
        // build used to throw — otherwise takes the whole process down immediately, with
        // every window still wearing its renamed title. WPF's default behavior for an
        // unhandled dispatcher exception is to terminate the process once this handler
        // returns with e.Handled left false, so this is NOT a crash suppressor: it is the
        // last opportunity to run RestoreAllTitles() — "leave every window as we found
        // it" — before that termination happens, plus a MessageBox so the failure isn't
        // silent. e.Handled is deliberately left false: we still want the crash (and its
        // real stack trace/telemetry), just not a window stuck with the wrong title.
        DispatcherUnhandledException += (_, args) =>
        {
            manager?.RestoreAllTitles();
            MessageBox.Show($"TaskSpaces hit an unexpected error and must close:\n{args.Exception.Message}",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = false; // let it die — titles are already restored
        };

        var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskSpaces");
        var statePath = Path.Combine(stateDir, "state.json");
        desktops = new VirtualDesktopService();
        monitor = new WindowMonitor();
        manager = new WorkspaceManager(desktops, monitor, new Win32WindowTitles(), new JsonPersistenceStore(stateDir));

        // Spec §Error handling: if the COM API is unrecognized (post-Windows-Update),
        // degrade to listing workspaces with a banner — never crash, never move windows.
        compatibilityMode = desktops.Initialize().IsFailure;
        if (compatibilityMode)
        {
            // Finding 2 (reviewer, Important): compatibility mode still lists workspaces
            // per spec ("switcher still lists workspaces but shows a compatibility
            // banner") — it just can't reconcile desktops (there are none to reconcile
            // onto) or start the window monitor (no desktop moves/renames will ever
            // happen, so there's nothing for it to drive). LoadState() alone gives the
            // tray menu and Manage window a read-only view of what's on disk.
            manager.LoadState()
                .TapError(err => MessageBox.Show(
                    $"TaskSpaces could not load your saved workspaces:\n{err}\n\nStarting with an empty list.",
                    "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        else
        {
            // Finding 1 (reviewer, Critical): manager.Start()'s Result used to be discarded.
            // JsonPersistenceStore.Load() deliberately FAILS (rather than degrading to
            // AppState.Empty) when state.json is corrupt, precisely so this call site can
            // tell the difference and refuse to silently overwrite the user's data. If we
            // ignored the failure here, State would stay empty and the very next action
            // that persists (adding a workspace, a window appearing, ...) would happily
            // write that empty state straight over the corrupt file — destroying whatever
            // was recoverable in it. Instead: back the corrupt file up (rename, never
            // delete), tell the user, and only THEN retry — the retry succeeds because
            // Load() now sees a missing file, which is the normal "first run" case.
            var started = manager.Start();
            if (started.IsFailure)
            {
                var loadError = started.Error;
                BackupCorruptState(statePath, loadError);
                started = manager.Start();
                if (started.IsFailure)
                    MessageBox.Show(
                        $"TaskSpaces failed to start even after backing up state.json:\n{started.Error}",
                        "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // Finding 1(b): monitor.Start() was also unchecked. A failure here means
            // WinEvent hooks never registered — the app would run silently believing it
            // sees every window when it in fact sees none. Never fatal (v1 can limp along
            // with manual "Refresh" in Manage), but never silent either.
            monitor.Start().TapError(err => MessageBox.Show(
                $"TaskSpaces: window monitoring is unavailable:\n{err}\n\nRules, auto-renaming and the window list will not update automatically.",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning));
        }

        trayIcon = new TaskbarIcon
        {
            Icon = SystemIcons.Application, // placeholder until the product name settles
            ToolTipText = compatibilityMode ? "TaskSpaces (compatibility mode)" : "TaskSpaces",
            ContextMenu = TrayMenu.Build(manager, compatibilityMode, OpenManage, ExitApp, ToggleFloatingBar, floatingBar is { IsVisible: true }),
            // Task 9 (Petre's testing feedback): left-click now opens the SAME menu as
            // right-click — hover replaces click as the way to reach the switcher panel
            // (see TrayMouseMove below), so left-click is free to become "menu" like
            // every other tray icon on Windows.
            MenuActivation = PopupActivationMode.LeftOrRightClick,
        };
        // H.NotifyIcon 2.x creates the shell icon lazily: with no window/XAML tree, a
        // code-built TaskbarIcon never registers with the tray until ForceCreate() is
        // called (see the library's own Wpf.Windowless sample — found the hard way when
        // the app ran headless with no icon at all). Efficiency mode stays OFF: it puts
        // the process under EcoQoS throttling, and we need WinEvent callbacks handled
        // promptly to re-apply renames and route new windows without visible lag.
        trayIcon.ForceCreate(enablesEfficiencyMode: false);
        // Rebuild the menu whenever workspaces change so names/counts stay honest. This
        // also keeps the "Show floating bar" checkmark honest: SaveFloatingBar (called
        // by every ShowBar()/HideBar()/drag) pulses StateChanged, which lands here.
        manager.StateChanged.Subscribe(_ => Dispatcher.Invoke(() =>
            trayIcon.ContextMenu = TrayMenu.Build(manager, compatibilityMode, OpenManage, ExitApp, ToggleFloatingBar, floatingBar is { IsVisible: true })));

        // Task 11 (spec §Floating icon bar): restore the bar's own on/off state across
        // restarts. Gated on !compatibilityMode for the same reason hotkeys/rehydration
        // are below -- every icon click calls JumpTo, which needs a real desktop to
        // switch to, and compatibility mode has none.
        if (!compatibilityMode && manager.State.FloatingBar is { Visible: true })
        {
            floatingBar = new FloatingBar(manager);
            floatingBar.ShowBar();
            PinFloatingBar();
        }

        // Task 9: hover-to-peek. TrayMouseMove fires continuously while the cursor sits
        // over the icon, so a (re)started 400ms DispatcherTimer measures "cursor has been
        // over the icon for a bit" without a separate enter/leave pair to track — each
        // move restarts the timer, and it only ever fires once the cursor stops moving
        // off the icon for 400ms. The panel itself (SwitcherPanel.Peek) owns hide-on-leave
        // via its own proximity poll, so this timer's only job is the initial summon.
        //
        // Fix wave (reviewer, Important): TrayMouseMove fires only while the cursor is
        // actually over the icon, but nothing previously cancelled the timer on leave —
        // it just kept counting down. Drive-by scenario: cursor crosses the tray icon
        // (restarts the timer) and keeps moving on toward, say, the middle of the screen;
        // 400ms later the Tick fires regardless, GetCursorPos() reads THAT current
        // (mid-screen) position, and Peek() opens/positions the panel there — nowhere
        // near the tray. Worse, Peek() then latches that far-away point as the summon
        // point (summonScreenX/Y), so the proximity keep-alive treats standing at that
        // unrelated spot as "still hovering" and the panel sticks open mid-screen.
        // Fix: remember where the cursor was on each TrayMouseMove (lastTrayMoveX/Y —
        // guaranteed to be ON/near the icon, since that event only fires there), and at
        // Tick time re-read the cursor and bail out if it wandered more than ~32px from
        // that last over-icon reading — the cursor didn't linger over the icon, it was
        // just passing through.
        const double HoverDriftRadiusPx = 32;
        double lastTrayMoveX = 0, lastTrayMoveY = 0;
        var hoverTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            // Fix round 2 (belt and braces, reviewer): Peek() already no-ops when the
            // panel is already visible, but check here too — two independent
            // idempotency layers mean this timer can never re-summon a panel that's
            // already showing, even if something upstream ever changes.
            if (switcherPanel is { IsVisible: true }) return;
            TaskSpaces.Windows.Monitoring.NativeMethods.GetCursorPos(out var cursor);
            // Fix wave: skip the peek entirely if the cursor has drifted away from the
            // tray icon since the timer was (re)started — see comment above.
            var dx = cursor.X - lastTrayMoveX;
            var dy = cursor.Y - lastTrayMoveY;
            if (dx * dx + dy * dy > HoverDriftRadiusPx * HoverDriftRadiusPx) return;
            switcherPanel ??= new SwitcherPanel(manager);
            switcherPanel.Peek(cursor.X, cursor.Y);
            PinSwitcherPanel();
        };
        trayIcon.TrayMouseMove += (_, _) =>
        {
            // Record the cursor position on every over-icon move so the Tick above has an
            // "on the icon" reference point to compare its later reading against.
            TaskSpaces.Windows.Monitoring.NativeMethods.GetCursorPos(out var cursor);
            lastTrayMoveX = cursor.X;
            lastTrayMoveY = cursor.Y;
            hoverTimer.Start();
        };

        // Task 9: global hotkeys (Ctrl+Alt+arrows cycle, Ctrl+Alt+1..9 direct switch).
        // Gated on !compatibilityMode: CycleWorkspace/SwitchToIndex both call
        // manager.Switch, which needs a real desktop to switch to — compatibility mode
        // has none. Results are fire-and-forget: a hotkey has no UI to report a failure
        // through, and a message box on every keypress that misses would be worse than a
        // silent no-op (e.g. Ctrl+Alt+3 with only two workspaces defined).
        if (!compatibilityMode)
        {
            hotkeys = new HotkeyService(
                () => manager.CycleWorkspace(-1),
                () => manager.CycleWorkspace(+1),
                n => manager.SwitchToIndex(n));
            if (hotkeys.Failures.Count > 0)
                MessageBox.Show(
                    "TaskSpaces could not register these keyboard shortcuts (another app already owns them):\n"
                    + string.Join("\n", hotkeys.Failures)
                    + "\n\nTaskSpaces will keep running; those chords just won't switch workspaces.",
                    "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Rename safety-net sweep (spec §5): event-driven re-apply is the fast path;
        // every 5s this re-asserts drifted titles and adopts persisted renames.
        if (!compatibilityMode)
        {
            var sweep = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            sweep.Tick += (_, _) => manager.ReapplyRenames();
            sweep.Start();
        }

        // Task 9: post-reboot rehydration. state.json's inventory survives a reboot even
        // though the desktops/windows it describes don't — offer to relaunch each
        // workspace's remembered apps. Compatibility mode has no desktops to place
        // windows onto, so skip it there; HasAnythingToRestore also skips the prompt
        // entirely on a clean start with an empty inventory (nothing to offer).
        if (!compatibilityMode && RehydratePrompt.HasAnythingToRestore(manager))
            new RehydratePrompt(manager).Show();

        // OS shutdown/logoff: every window is about to close, and each close would fire
        // Disappeared and ERASE the inventory that rehydration needs. Unhook the monitor
        // FIRST so state.json keeps its last-known contents, then put titles back.
        //
        // Fix round 1 (reviewer, minor): deliberately does NOT call hotkeys?.Dispose()
        // here, unlike ExitApp() below. SessionEnding means Windows is tearing the whole
        // process down for logoff/shutdown regardless of what we do — RegisterHotKey's
        // registrations are per-process and vanish with it, so unregistering first would
        // be pure ceremony with nothing left to observe the result. ExitApp is the
        // orderly, still-running-normally exit path (tray menu -> Exit), where disposing
        // first is the tidy, deterministic thing to do. The asymmetry is intentional.
        SessionEnding += (_, _) =>
        {
            monitor.Dispose();
            manager.RestoreAllTitles();
        };
    }

    // Finding 1 (reviewer, Critical): renames (never deletes) a corrupt state.json so the
    // user's data stays recoverable, and tells them about it. Called once, right before
    // the retried manager.Start() — by the time this returns, nothing has had a chance to
    // persist an empty state over the original file.
    static void BackupCorruptState(string statePath, string loadError)
    {
        try
        {
            if (File.Exists(statePath))
            {
                var backupPath = statePath + ".bak";
                // Don't clobber a previous backup's forensic value — an already-backed-up
                // corruption episode gets its own timestamped name instead.
                if (File.Exists(backupPath))
                    backupPath = $"{statePath}.{DateTime.Now:yyyyMMddHHmmss}.bak";
                File.Move(statePath, backupPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: even if the rename itself fails (e.g. file locked by another
            // process), the MessageBox below still fires — the user is never left thinking
            // everything is fine when it isn't.
        }

        MessageBox.Show(
            $"TaskSpaces found a corrupted state file and could not load your saved workspaces:\n{loadError}\n\n" +
            "The corrupted file was backed up (state.json.bak) rather than deleted. Starting fresh.",
            "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void OpenManage() => new ManageWindow(manager!, compatibilityMode).Show();

    // Task 11: tray menu callback for "Show floating bar". Created lazily like
    // switcherPanel; ShowBar()/HideBar() each call manager.SaveFloatingBar, whose
    // StateChanged pulse rebuilds the tray menu (see the subscription above) so the
    // checkmark reflects the new state without this method touching the menu itself.
    void ToggleFloatingBar()
    {
        if (floatingBar is { IsVisible: true }) floatingBar.HideBar();
        else
        {
            floatingBar ??= new FloatingBar(manager!);
            floatingBar.ShowBar();
            PinFloatingBar(); // no-op after the first successful/attempted pin (see PinFloatingBar)
        }
    }

    // Task 11 fix round 4 (Petre: the bar stayed behind on workspace switch): dogfooding
    // our own Pin support. FloatingBar/SwitcherPanel are ordinary top-level windows --
    // without pinning, each belongs to whichever desktop it happened to be showing on
    // and vanishes the instant Petre switches away, defeating the entire point of an
    // "always visible" bar / "peek from anywhere" panel. Pinning makes them omnipresent.
    //
    // WindowInteropHelper.Handle is non-zero only once the window has a REAL hwnd (i.e.
    // after Show()/SourceInitialized) -- both call sites below call this immediately
    // after ShowBar()/Peek(), which already called Show(). Guarded by the pinned flag
    // so repeated toggles/peeks attempt the native pin call (and, on failure, show a
    // MessageBox) at most ONCE per window's lifetime: Windows' pin state lives on the
    // hwnd itself and survives Hide()/Show() cycles (only Close()+recreate would lose
    // it), and neither window is ever closed/recreated during the app's lifetime.
    //
    // Sanity check (grep-confirmed): WindowMonitor's hooks are registered with
    // WINEVENT_SKIPOWNPROCESS (WindowMonitor.cs, Hook()), so our own windows never fire
    // WinEvents into WorkspaceManager's knownWindows/memberships in the first place --
    // pinning them cannot pollute the roster or the switcher/bar's own overview
    // (WorkspaceManager.WindowsByWorkspace/IsPinned only ever look at knownWindows).
    void PinFloatingBar()
    {
        if (floatingBarPinned) return;
        var hwnd = new WindowInteropHelper(floatingBar!).Handle;
        if (hwnd == nint.Zero) return; // not shown yet -- shouldn't happen, called right after ShowBar()
        floatingBarPinned = true;
        desktops!.Pin(new WindowHandle(hwnd))
            .TapError(err => MessageBox.Show(
                $"TaskSpaces could not pin the floating bar to every workspace:\n{err}\n\nIt will only stay visible on the desktop it was shown on.",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    // Same rationale as PinFloatingBar above, for the hover-peeked switcher panel: a
    // Hide()n window keeps whatever desktop affinity it had, so after a workspace
    // switch a later Peek() could resurrect it on the PREVIOUS desktop -- invisible on
    // the one Petre is actually looking at, even though IsVisible would say true.
    void PinSwitcherPanel()
    {
        if (switcherPanelPinned) return;
        var hwnd = new WindowInteropHelper(switcherPanel!).Handle;
        if (hwnd == nint.Zero) return;
        switcherPanelPinned = true;
        desktops!.Pin(new WindowHandle(hwnd))
            .TapError(err => MessageBox.Show(
                $"TaskSpaces could not pin the switcher panel to every workspace:\n{err}\n\nHover-peek may show it on the wrong desktop after switching.",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    void ExitApp()
    {
        manager?.RestoreAllTitles();  // leave every window as we found it
        monitor?.Dispose();
        hotkeys?.Dispose(); // unregisters RegisterHotKey chords before the process exits
        trayIcon?.Dispose();
        Shutdown();
    }
}

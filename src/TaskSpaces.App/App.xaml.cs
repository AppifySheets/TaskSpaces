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
using TaskSpaces.Core.Rehydration;
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
    ManageWindow? manageWindow; // single instance: a left-click on the tray opens this
    HotkeyService? hotkeys;
    WorkspaceSwitchGesture? switcher; // Alt+Tab-style workspace picker (Win+Ctrl+Tab by default)
    Chord boundSwitcher;              // the chord the picker and the hotkey are currently registered on
    FloatingBar? floatingBar; // Task 11: created lazily on first show

    // Held for the whole process lifetime, in a FIELD so the GC cannot collect it and quietly
    // release the lock while we are still running. See OnStartup for why it exists.
    System.Threading.Mutex? singleInstance;
    IVirtualDesktopService? desktops; // Task 11 fix round 4: promoted from a local so PinOwnWindow (below) can reach it from the tray/hover callbacks, not just OnStartup
    bool floatingBarPinned; // Task 11 fix round 4: pin the bar's real hwnd to all desktops exactly once (see PinFloatingBar)

    // The app's icon, loaded once from the Resource the csproj also stamps into the exe.
    // Public so every window can bind its own Icon to it (Manage, switcher, prompts) without
    // each one re-decoding the file or hardcoding its own path.
    // Assembly-qualified pack URI, matching the window XAML: the short "/Assets/..." form
    // resolves against Application.ResourceAssembly, which only a WPF exe's generated Main
    // sets, so it breaks anywhere the app is loaded as a library (notably under test).
    public static readonly System.Windows.Media.ImageSource AppIcon =
        new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/TaskSpaces.App;component/Assets/taskspaces.ico"));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SINGLE INSTANCE, and it must be the very first thing that happens.
        //
        // Petre hit the visible symptom: "TaskSpaces could not register these keyboard
        // shortcuts (another app already owns them)" listing ALL of them. RegisterHotKey is
        // exclusive per chord, and the only thing that would own exactly OUR set is another
        // copy of TaskSpaces. The failed hotkeys were the least of it though. A second
        // instance also means two tray icons, two rename sweeps fighting over the same
        // windows, two startup placement sweeps, and two processes writing state.json with
        // last-writer-wins -- i.e. silent loss of workspaces or renames.
        //
        // This matters more now than it would have a week ago: the app ships as a portable
        // exe with no installer, so double-clicking it twice is the ordinary mistake rather
        // than an unusual one.
        //
        // "Local\" scopes the mutex to the login SESSION, not the machine: two users on one
        // PC should each get their own instance, since every piece of state this app touches
        // (state.json under %APPDATA%, the HKCU Run key, the user's own desktops) is per-user.
        singleInstance = new System.Threading.Mutex(initiallyOwned: true, @"Local\TaskSpaces.SingleInstance", out var isOnlyInstance);
        if (!isOnlyInstance)
        {
            // Told, not silently exited: a portable exe that appears to do nothing when
            // double-clicked reads as broken, and the icon is easy to miss in a full tray.
            MessageBox.Show(
                "TaskSpaces is already running.\n\nLook for the tiled icon in the notification area, and click it to open Manage.",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Information);
            singleInstance = null; // not ours to release
            Shutdown();
            return;
        }

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
            // The real app icon, replacing the generic SystemIcons.Application placeholder.
            // IconSource (an ImageSource) rather than Icon (a System.Drawing.Icon): the ico's
            // frames are PNG-compressed, which WPF's icon decoder handles cleanly while
            // GDI+ historically does not. Same file the exe is stamped with, so tray,
            // taskbar and window icons cannot drift apart.
            IconSource = AppIcon,
            ToolTipText = compatibilityMode ? "TaskSpaces (compatibility mode)" : "TaskSpaces",
            ContextMenu = TrayMenu.Build(compatibilityMode, OpenManage, ExitApp),
            // Petre: "left click gives us the main window, right click gives exit and
            // manage". RightClick only, so a left-click is free to open Manage (wired
            // below) instead of raising the same menu twice.
            MenuActivation = PopupActivationMode.RightClick,
        };
        // Left-click IS the main window now. Manage was previously reachable only through a
        // menu item, which made the app's one real window the least accessible thing in it.
        trayIcon.TrayLeftMouseUp += (_, _) => OpenManage();
        // H.NotifyIcon 2.x creates the shell icon lazily: with no window/XAML tree, a
        // code-built TaskbarIcon never registers with the tray until ForceCreate() is
        // called (see the library's own Wpf.Windowless sample — found the hard way when
        // the app ran headless with no icon at all). Efficiency mode stays OFF: it puts
        // the process under EcoQoS throttling, and we need WinEvent callbacks handled
        // promptly to re-apply renames and route new windows without visible lag.
        trayIcon.ForceCreate(enablesEfficiencyMode: false);
        // NOTE: no StateChanged subscription rebuilding this menu any more. It used to be
        // rebuilt on every pulse so the workspace list and the "Show floating bar" checkmark
        // stayed accurate; the menu now holds neither, so it is built once and never needs
        // to change. One fewer thing reacting to every window event.

        // Task 11 (spec §Floating icon bar): restore the bar's own on/off state across
        // restarts. Gated on !compatibilityMode for the same reason hotkeys/rehydration
        // are below -- every icon click calls JumpTo, which needs a real desktop to
        // switch to, and compatibility mode has none.
        // Always shown, no longer conditional on the persisted Visible flag. Petre: "show
        // floating bar doesn't make sense anymore, it's crucial for the app's design." The bar
        // is the only surface that lists windows and jumps to them now, so it starts with the
        // app. AppState.FloatingBar is still read for its POSITION; Visible is retained in the
        // record only so older state.json files keep deserialising, and is ignored.
        // Still gated on compatibility mode: every icon click calls JumpTo, which needs real
        // desktops to switch between.
        if (!compatibilityMode)
        {
            floatingBar = new FloatingBar(manager);
            // The bar's hwnd is created HERE, before it is ever shown, for two reasons that
            // both hang off the same handle:
            //
            //   1. monitor.Ignore -- WindowMonitor no longer hooks with
            //      WINEVENT_SKIPOWNPROCESS (Petre: "why isn't the taskspaces window in the
            //      floating window?"), so without this the bar would list ITSELF, and since
            //      we pin it below, it would list itself in the pinned row forever. Must
            //      happen before the first Show(), or that first EVENT_OBJECT_SHOW slips
            //      through and the bar acquires a permanent row for itself.
            //   2. PinFloatingBar -- which needed a real handle anyway, and previously had
            //      to guard against being called too early.
            //
            // EnsureHandle() is safe this early: AllowsTransparency/WindowStyle are set in
            // XAML and applied by InitializeComponent, which the constructor above ran.
            var barHwnd = new WindowInteropHelper(floatingBar).EnsureHandle();
            monitor.Ignore(barHwnd);
            floatingBar.ShowBar();
            PinFloatingBar(barHwnd);

            // Petre: "if i activate the taskbar, it hides the floating window". Topmost is a
            // shared band, not a rank, so the taskbar (and StartAllBack's menu) climbs over
            // the bar the moment it is activated. Reclaiming the top of the band on every
            // foreground change is the fix -- see FloatingBar.ReclaimTopmost. Subscribed here
            // rather than inside the bar because the monitor is the composition root's to hand
            // out, and the bar has no business knowing what a WinEvent hook is.
            monitor.ForegroundChanged.Subscribe(_ => floatingBar.ReclaimTopmost());
        }

        // The hover-to-peek switcher panel USED to be summoned from here, with a 400ms
        // DispatcherTimer, a drift radius to reject drive-by cursor passes, and a proximity
        // keep-alive poll inside the panel. All of it is gone, along with the panel itself.
        //
        // Petre: "no switcher panel required on hover either", "we already have a nice way to
        // move windows across workspaces". Every job the panel did is now done by a surface
        // that is permanently on screen or one left-click away:
        //   see every window across workspaces -> the floating bar
        //   jump to a window                   -> bar icons
        //   drag windows between workspaces    -> bar rows, and Manage's Windows tab
        //   switch workspace                   -> bar row labels, and the Win+Ctrl+Tab switcher
        //   rename / pin / restore             -> bar icon right-click, Manage's Windows tab
        //
        // The deletion was cheap for one specific reason: the panel and Manage's Windows tab
        // already shared ONE control (WindowGroupsView), built that way in Task 10 so they
        // could not drift apart. Removing the panel left that control untouched in Manage, so
        // grouped drag-and-drop window management survived intact.

        // The app's ONE global chord: the Alt+Tab-style workspace switcher, Win+Ctrl+Tab by
        // default. Petre: "i don't think we need ctrl+alt and those, ctrl+tab is good enough" --
        // Ctrl+Alt+arrows and Ctrl+Alt+1..9 are gone, and HotkeyService's header records why.
        //
        // Gated on !compatibilityMode: the switcher ends in manager.Switch, which needs a real
        // desktop to switch to, and compatibility mode has none.
        if (!compatibilityMode)
        {
            // Win+Ctrl+Tab (the configured chord) walks workspaces in most-recently-used order
            // while the modifiers stay held, and switches on release -- Alt+Tab's gesture,
            // applied to workspaces rather than windows.
            // Ignored by the monitor for the same reason the floating bar is: it is our own
            // chrome, and now that the hooks see our process it would otherwise appear in
            // the bar as a window every time it flashed up.
            // WorkspaceManager.SwitcherShortcut has already fallen back to the default for
            // anything unusable, so this parse cannot realistically fail -- but Parse returns
            // a Result, and inventing a value on failure here would hide a real bug behind a
            // silently different shortcut. Taking .Value is the honest reading.
            boundSwitcher = Chord.Parse(manager.SwitcherShortcut).Value;
            switcher = new WorkspaceSwitchGesture(manager, boundSwitcher);
            monitor.Ignore(switcher.EnsureHandle());

            hotkeys = new HotkeyService(direction => switcher.Step(direction), boundSwitcher);

            // Petre: "i want it configurable". Rebinding is driven off StateChanged rather
            // than off a callback from the Shortcuts tab, so ANY route that changes the
            // shortcut takes effect immediately -- the editor today, and anything else that
            // ends up writing it later. Comparing against what is currently bound makes this
            // a no-op on the many pulses that have nothing to do with shortcuts.
            manager.StateChanged.Subscribe(_ => RebindSwitcherIfChanged());
            // One chord now, so at most one failure -- and it names the chord, since the whole
            // point is that the reader can go and change it on Manage -> Shortcuts.
            if (hotkeys.Failures.Count > 0)
                MessageBox.Show(
                    string.Join("\n", hotkeys.Failures)
                    + "\n\nTaskSpaces will keep running. Pick a different chord on Manage → Shortcuts.",
                    "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Safety-net sweep (spec §5): event-driven handling is the fast path, this is the
        // truth. Every 5s it re-asserts drifted titles, adopts persisted renames, and —
        // added after Petre found two windows missing from his Personal row — reconciles the
        // window list itself against what the OS actually lists.
        //
        // That second job matters because WinEvents are lossy in two different ways: an
        // OUTOFCONTEXT event can be dropped when the message queue is busy, and a HIDE that
        // did not mean "gone" leaves a window flagged hidden until a SHOW that a window on
        // another virtual desktop never fires. Either way the bar silently loses a window
        // forever. See WindowMonitor.Resync for the full account. Costs one EnumWindows per
        // tick in the steady state.
        if (!compatibilityMode)
        {
            var sweep = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            sweep.Tick += (_, _) =>
            {
                monitor.Resync();
                manager.ReapplyRenames();
                // Backstop for the topmost-band fix above: an activation that somehow fires no
                // foreground event would otherwise leave the bar buried until the next one.
                floatingBar?.ReclaimTopmost();
            };
            sweep.Start();
        }

        // Task 9: post-reboot rehydration. state.json's inventory survives a reboot even
        // though the desktops/windows it describes don't — offer to relaunch each
        // workspace's remembered apps. Compatibility mode has no desktops to place
        // windows onto, so skip it there; HasAnythingToRestore also skips the prompt
        // entirely on a clean start with an empty inventory (nothing to offer).
        // Gated on "first run since this machine booted" as well as "there is something to
        // restore". Petre, on seeing it for the fifteenth time in an afternoon: "this seems like
        // an overkill" -- it was, because the only condition used to be that some app was not
        // running, which is true the moment you close anything. A reboot is the case this
        // feature exists for: desktops do not survive one, so state.json is the only record of
        // what was where. An app restart within the same session is not that case, since the
        // windows are still sitting on their desktops.
        //
        // Boot time from TickCount64 (uptime) rather than a WMI query: it needs no round trip
        // and cannot fail, and a few seconds of imprecision cannot change the answer to "was
        // the last run before or after the machine started".
        var bootedAt = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (!compatibilityMode
            && RestoreOffer.ShouldOffer(manager.PreviousRunAt, bootedAt)
            && RehydratePrompt.HasAnythingToRestore(manager))
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

    // SINGLE INSTANCE, which matters now that a left-click opens this: the old version built
    // a new ManageWindow on every call, which was tolerable behind a menu item and would
    // stack up a pile of identical windows behind an easily-mis-clicked tray icon. A second
    // click now surfaces the window that is already open instead.
    void OpenManage()
    {
        if (manageWindow is { IsVisible: true })
        {
            if (manageWindow.WindowState == WindowState.Minimized) manageWindow.WindowState = WindowState.Normal;
            manageWindow.Activate();
            return;
        }

        manageWindow = new ManageWindow(manager!, compatibilityMode);
        manageWindow.Closed += (_, _) => manageWindow = null;
        manageWindow.Show();
    }


    // Task 11 fix round 4 (Petre: the bar stayed behind on workspace switch): dogfooding
    // our own Pin support. The FloatingBar is an ordinary top-level window --
    // without pinning, each belongs to whichever desktop it happened to be showing on
    // and vanishes the instant Petre switches away, defeating the entire point of an
    // "always visible" bar / "peek from anywhere" panel. Pinning makes them omnipresent.
    //
    // The caller passes the handle it already created with EnsureHandle(), so there is no
    // "is it shown yet" question left to guard. Guarded by the pinned flag so the native
    // pin call (and, on failure, the MessageBox) happens at most ONCE per window lifetime:
    // Windows' pin state lives on the hwnd and survives Hide()/Show() cycles, and this
    // window is never closed or recreated while the app runs.
    //
    // Pinning our own window used to be invisible to the rest of the app because
    // WindowMonitor hooked with WINEVENT_SKIPOWNPROCESS. That flag is gone (Petre wanted to
    // see the Manage window in the bar), so the bar would now be a perfectly ordinary
    // pinned window as far as the overview is concerned -- which is exactly why the caller
    // registers this handle with monitor.Ignore first.
    void PinFloatingBar(nint hwnd)
    {
        if (floatingBarPinned) return;
        floatingBarPinned = true;
        desktops!.Pin(new WindowHandle(hwnd))
            .TapError(err => MessageBox.Show(
                $"TaskSpaces could not pin the floating bar to every workspace:\n{err}\n\nIt will only stay visible on the desktop it was shown on.",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    // Re-registers the Alt+Tab-style switcher when its configured chord changes, and moves
    // the picker's hold-detection onto the new modifiers at the same time. Both halves must
    // move together: a chord registered on Win+Tab whose release poll still watched Ctrl+Alt
    // would open the picker and never close it.
    void RebindSwitcherIfChanged()
    {
        var configured = Chord.Parse(manager!.SwitcherShortcut);
        if (configured.IsFailure || configured.Value == boundSwitcher) return;
        boundSwitcher = configured.Value;
        switcher!.Rebind(boundSwitcher);
        // A chord another app already owns is worth saying out loud: this is a change Petre
        // just made by hand, so silence would read as "applied" when nothing was.
        hotkeys!.BindSwitcher(boundSwitcher)
            .TapError(err => MessageBox.Show(err, "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    void ExitApp()
    {
        manager?.RestoreAllTitles();  // leave every window as we found it
        monitor?.Dispose();
        hotkeys?.Dispose(); // unregisters RegisterHotKey chords before the process exits
        switcher?.Dispose(); // stops the release poll and closes the picker window
        trayIcon?.Dispose();
        // Released explicitly on the orderly exit path so a relaunch a moment later is never
        // refused. Windows would release it at process death anyway; being deterministic here
        // costs one line and removes any doubt about ordering.
        if (singleInstance is { } held)
        {
            held.ReleaseMutex();
            held.Dispose();
            singleInstance = null;
        }
        Shutdown();
    }
}

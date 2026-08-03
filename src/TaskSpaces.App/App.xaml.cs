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
    ManageWindow? manageWindow; // single instance: a left-click on the tray opens this
    HotkeyService? hotkeys;
    FloatingBar? floatingBar; // Task 11: created lazily on first show, same as switcherPanel
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
        if (!compatibilityMode && manager.State.FloatingBar is { Visible: true })
        {
            floatingBar = new FloatingBar(manager);
            floatingBar.ShowBar();
            PinFloatingBar();
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
        //   switch workspace                   -> bar row labels, Ctrl+Alt+arrows, Ctrl+Alt+1..9
        //   rename / pin / restore             -> bar icon right-click, Manage's Windows tab
        //
        // The deletion was cheap for one specific reason: the panel and Manage's Windows tab
        // already shared ONE control (WindowGroupsView), built that way in Task 10 so they
        // could not drift apart. Removing the panel left that control untouched in Manage, so
        // grouped drag-and-drop window management survived intact.

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

        // Manage owns the "Show floating bar" checkbox now that the tray menu is down to
        // Manage + Exit, so it needs both the toggle and a way to read the live state. The
        // state is read through a delegate rather than passed by value because the bar can
        // also be hidden from its OWN right-click menu, which would leave a by-value copy
        // stale the moment the window reopened.
        manageWindow = new ManageWindow(manager!, compatibilityMode, ToggleFloatingBar, () => floatingBar is { IsVisible: true });
        manageWindow.Closed += (_, _) => manageWindow = null;
        manageWindow.Show();
    }

    // Toggles the bar. Called from Manage's checkbox (it used to be a tray menu item).
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
    // our own Pin support. The FloatingBar is an ordinary top-level window --
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

    void ExitApp()
    {
        manager?.RestoreAllTitles();  // leave every window as we found it
        monitor?.Dispose();
        hotkeys?.Dispose(); // unregisters RegisterHotKey chords before the process exits
        trayIcon?.Dispose();
        Shutdown();
    }
}

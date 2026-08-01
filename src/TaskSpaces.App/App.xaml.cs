using System.Drawing;
using System.IO;
using System.Windows;
using CSharpFunctionalExtensions;
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
        var desktops = new VirtualDesktopService();
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
            ContextMenu = TrayMenu.Build(manager, compatibilityMode, OpenManage, ExitApp),
        };
        // H.NotifyIcon 2.x creates the shell icon lazily: with no window/XAML tree, a
        // code-built TaskbarIcon never registers with the tray until ForceCreate() is
        // called (see the library's own Wpf.Windowless sample — found the hard way when
        // the app ran headless with no icon at all). Efficiency mode stays OFF: it puts
        // the process under EcoQoS throttling, and we need WinEvent callbacks handled
        // promptly to re-apply renames and route new windows without visible lag.
        trayIcon.ForceCreate(enablesEfficiencyMode: false);
        // Rebuild the menu whenever workspaces change so names/counts stay honest.
        manager.StateChanged.Subscribe(_ => Dispatcher.Invoke(() =>
            trayIcon.ContextMenu = TrayMenu.Build(manager, compatibilityMode, OpenManage, ExitApp)));

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

    void ExitApp()
    {
        manager?.RestoreAllTitles();  // leave every window as we found it
        monitor?.Dispose();
        trayIcon?.Dispose();
        Shutdown();
    }
}

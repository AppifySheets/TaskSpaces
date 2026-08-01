using System.Drawing;
using System.IO;
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

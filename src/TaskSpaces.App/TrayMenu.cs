using System.Windows.Controls;
using TaskSpaces.Core;

namespace TaskSpaces.App;

// The tray context menu IS the v1 switcher: one workspace per item, click to switch.
// (The dedicated switcher surface — pill/flyout/bar — is a separate, post-mockup plan.)
public static class TrayMenu
{
    // Task 11: toggleFloatingBar/floatingBarVisible added for the "Show floating bar"
    // checkable item. Checked state is passed in (rather than read from AppState here)
    // because the App composition root owns the FloatingBar instance's actual IsVisible —
    // the persisted AppState.FloatingBar can lag one toggle behind at the instant the
    // menu rebuilds, and the live instance is the source of truth for what the checkbox
    // should show right now.
    public static ContextMenu Build(WorkspaceManager manager, bool compatibilityMode, Action openManage, Action exit, Action toggleFloatingBar, bool floatingBarVisible)
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

        // Task 11 (spec §Floating icon bar): JumpTo (which the bar's icons call) needs
        // real desktops to switch to, same reason hotkeys/rehydration are gated on
        // !compatibilityMode elsewhere (App.xaml.cs) — disabled, not hidden, so it's
        // still discoverable and its state survives compatibility mode ending on a
        // later restart.
        var floatingBar = new MenuItem { Header = "Show floating bar", IsCheckable = true, IsChecked = floatingBarVisible, IsEnabled = !compatibilityMode };
        floatingBar.Click += (_, _) => toggleFloatingBar();
        menu.Items.Add(floatingBar);

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

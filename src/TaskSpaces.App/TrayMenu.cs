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

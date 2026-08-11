using System.Windows.Controls;

namespace TaskSpaces.App;

// The right-click menu, deliberately down to two commands.
//
// Petre: "no need for workspace switching on right click", and "right click gives exit and
// manage". Both removals are because the work moved elsewhere rather than because the
// features went away:
//   - switching workspaces is the floating bar's row labels, plus the Win+Ctrl+Tab switcher,
//     so a list of workspaces here was a third way to do the same thing;
//   - "Show floating bar" moved into Manage, next to "Start with Windows". It could not
//     simply be deleted: the bar can hide itself from its own right-click menu, and with no
//     way back a hidden bar would look like data loss. Manage is now one left-click away,
//     which makes it a better home for a persistent setting than a transient menu.
//
// The manager is no longer a parameter at all, which is the real signal that this menu
// stopped being a switcher.
public static class TrayMenu
{
    // `update` is the one item that is not always here: null until a check finds a newer release
    // (#71), at which point App rebuilds this menu with it. The menu only ever says there IS one.
    //
    // `checkNow` is #110, and it overturns a decision recorded here: a permanent "Check for
    // updates…" item was considered and left out, because it would be a button that usually reports
    // nothing on a menu deliberately cut to two commands. Petre asked for it anyway, which settles
    // that, and the objection survives as a requirement rather than as a refusal -- a manual check
    // MUST say what it found, including "nothing", or it is a button that looks broken every time
    // it works. See App.CheckForUpdateNow.
    //
    // Third rather than first: it is the least used of the three, and the two that were here are
    // where the hand already expects them.
    public static ContextMenu Build(bool compatibilityMode, Action openManage, Action exit, Action checkNow,
        (string Label, Action Open)? update = null)
    {
        var menu = new ContextMenu();

        // Informational, not a command, and disabled: it exists so the user is not left
        // wondering why nothing moves between desktops on a Windows build whose COM API we
        // cannot drive.
        if (compatibilityMode)
            menu.Items.Add(new MenuItem
            {
                Header = "⚠ Virtual desktops unavailable on this Windows build",
                IsEnabled = false,
            });

        // FIRST, and separated, because it is news rather than a command: it appears once and
        // stops being there again as soon as the user is on the new version.
        if (update is { } available)
        {
            var item = new MenuItem { Header = available.Label };
            item.Click += (_, _) => available.Open();
            menu.Items.Add(item);
            menu.Items.Add(new Separator());
        }

        var manage = new MenuItem { Header = "Manage…" };
        manage.Click += (_, _) => openManage();
        menu.Items.Add(manage);

        var check = new MenuItem { Header = "Check for updates…" };
        check.Click += (_, _) => checkNow();
        menu.Items.Add(check);

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(exitItem);

        return menu;
    }
}

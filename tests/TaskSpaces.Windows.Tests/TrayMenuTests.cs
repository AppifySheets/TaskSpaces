using System.Windows.Controls;
using TaskSpaces.App;

namespace TaskSpaces.Windows.Tests;

// The tray menu is two commands by explicit decision, and it has now gained a third (#110), so what
// is on it is worth pinning: this menu is the only surface some of these actions have, and an item
// dropped by accident would be a feature that quietly stopped existing.
//
// The item HEADERS are the assertion rather than a count, because a count passes while the wrong
// three items are present.
public class TrayMenuTests
{
    static IReadOnlyList<string> Headers(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToList();

    // Petre: "right click gives exit and manage", then #110: "check for new version as a right-click
    // menu option."
    [Fact]
    public void The_menu_offers_manage_a_manual_update_check_and_exit() => StaThread.Run(() =>
    {
        var menu = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { });

        Assert.Equal(["Manage…", "Check for updates…", "Exit"], Headers(menu));
    });

    // Clicking it has to actually reach the app, which is the half a header assertion cannot see.
    [Fact]
    public void The_update_check_item_invokes_the_check() => StaThread.Run(() =>
    {
        var checks = 0;
        var menu = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => checks++);

        menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Check for updates…")
            .RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(1, checks);
    });

    // The news item (#71) is not always there, and when it is it comes FIRST: it is news rather than
    // a command, and it stops existing again once the user is on the new version.
    [Fact]
    public void An_available_update_is_announced_above_everything_else() => StaThread.Run(() =>
    {
        var opened = 0;
        var menu = TrayMenu.Build(compatibilityMode: false, () => { }, () => { }, () => { },
            update: ("Update to 9.9.9…", () => opened++));

        Assert.Equal(["Update to 9.9.9…", "Manage…", "Check for updates…", "Exit"], Headers(menu));

        menu.Items.OfType<MenuItem>().First()
            .RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, opened);
    });

    // On a Windows build whose virtual-desktop COM API cannot be driven, the menu leads with a
    // disabled explanation rather than leaving the user wondering why nothing moves between desktops.
    [Fact]
    public void Compatibility_mode_says_so_and_the_notice_cannot_be_clicked() => StaThread.Run(() =>
    {
        var menu = TrayMenu.Build(compatibilityMode: true, () => { }, () => { }, () => { });

        var notice = menu.Items.OfType<MenuItem>().First();
        Assert.Contains("Virtual desktops unavailable", (string)notice.Header);
        Assert.False(notice.IsEnabled);
    });
}

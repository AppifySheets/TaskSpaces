using System.Windows;
using System.Windows.Controls;

namespace TaskSpaces.Windows.Tests;

// #149. Petre: "right-clicking on the default desktop (desktop1) doesn't do anything, no context menu
// for it."
//
// The bug was a decision that outlived its reasoning. The row menu was attached "only on real
// workspaces", which is right for 📌 Pinned and for the Unplaced catch-all -- neither is a workspace and
// neither ever will be -- and wrong for an unbound desktop, which is a workspace nobody has named yet.
// With the bar's own background menu deleted there was nothing to fall through to either, so the click
// landed on literally nothing.
//
// Asserted on the MENU rather than on what naming does, which NameDesktopTests covers: what broke here
// was reachability.
public class UnboundDesktopMenuTests
{
    // Rows are a Border wrapping a Grid, and the menu is on that Grid -- the container -- so the whole
    // row answers: label, lane and the empty space between.
    static IReadOnlyList<Grid> RowContainers(Panel rows) =>
        rows.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>().ToList();

    static Grid RowLabelled(Panel rows, string label) =>
        RowContainers(rows).Single(row => TextIn(row).Any(text => text.Contains(label)));

    static IReadOnlyList<string> TextIn(DependencyObject root)
    {
        var found = new List<string>();
        Collect(root);
        return found;

        void Collect(DependencyObject node) =>
            LogicalTreeHelper.GetChildren(node)
                .OfType<DependencyObject>()
                .ToList()
                .ForEach(child =>
                {
                    if (child is TextBlock { Text: { } text }) found.Add(text);
                    Collect(child);
                });
    }

    static IReadOnlyList<string> HeadersOf(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToList();

    [Fact]
    public void An_unbound_desktops_row_offers_to_name_it() => StaThread.Run(() =>
    {
        var harness = Harness.Build(withUnnamedDesktop: true);
        using var bar = harness.ShowBar();

        var menu = RowLabelled(bar.Rows, "Desktop 1").ContextMenu;

        Assert.NotNull(menu);
        Assert.Contains("Name this desktop…", HeadersOf(menu));
    });

    // One item, and the reason is worth pinning: every other entry on the workspace menu needs a
    // workspace to act on -- reorder positions a row in a list this desktop is not in, colour comes
    // from that position, delete would close somebody's desktop from a right-click. Offering them
    // greyed out would advertise five things that cannot work.
    [Fact]
    public void That_menu_offers_nothing_else() => StaThread.Run(() =>
    {
        var harness = Harness.Build(withUnnamedDesktop: true);
        using var bar = harness.ShowBar();

        Assert.Single(HeadersOf(RowLabelled(bar.Rows, "Desktop 1").ContextMenu!));
    });

    // The original reasoning still holds where it was right: 📌 Pinned is not a workspace and has
    // nothing to name.
    [Fact]
    public void The_pinned_row_still_has_no_menu() => StaThread.Run(() =>
    {
        var harness = Harness.Build(withUnnamedDesktop: true);
        using var bar = harness.ShowBar();

        Assert.Null(RowLabelled(bar.Rows, "📌").ContextMenu);
    });

    // A named workspace keeps the full menu, so the new branch cannot have stolen the old one.
    [Fact]
    public void A_workspace_row_still_gets_the_workspace_menu() => StaThread.Run(() =>
    {
        var harness = Harness.Build(withUnnamedDesktop: true);
        using var bar = harness.ShowBar();

        var headers = HeadersOf(RowLabelled(bar.Rows, "Sparrow").ContextMenu!);

        Assert.Contains("Rename…", headers);
        Assert.True(headers.Count > 1);
    });
}

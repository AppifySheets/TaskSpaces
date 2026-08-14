using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Geometry;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// #150, the memory half: one remembered position per monitor arrangement, so returning to the desk
// puts the bar back where it was at the desk rather than somewhere merely legal.
//
// The bar's own reaction to a display change needs a real window and real monitors and is verified by
// hand; what is testable without either is the bookkeeping, which is where the mistakes with lasting
// consequences live: a position saved under the wrong layout is wrong for as long as the file exists.
public class BarPlacementTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    static readonly string Desk = MonitorLayoutKey.Of([new MonitorBounds(0, 0, 2560, 1440)]);
    static readonly string Laptop = MonitorLayoutKey.Of([new MonitorBounds(0, 0, 1920, 1080)]);

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static FloatingBarState At(double left, double top) => new(left, top, true);

    [Fact]
    public void A_position_saved_on_a_layout_comes_back_for_that_layout()
    {
        var manager = Started();

        manager.SaveFloatingBar(At(2300, 1300), Desk);

        Assert.Equal(2300, manager.BarPlacementFor(Desk).Value.Left);
    }

    // The whole point of the feature: two layouts, two positions, neither overwriting the other.
    [Fact]
    public void Each_layout_keeps_its_own_position()
    {
        var manager = Started();

        manager.SaveFloatingBar(At(2300, 1300), Desk);
        manager.SaveFloatingBar(At(1600, 900), Laptop);

        Assert.Equal(2300, manager.BarPlacementFor(Desk).Value.Left);
        Assert.Equal(1600, manager.BarPlacementFor(Laptop).Value.Left);
    }

    // Saving again on the same layout REPLACES rather than appends, or the list grows without bound
    // and the lookup starts answering with the first position ever chosen.
    [Fact]
    public void Saving_twice_on_one_layout_keeps_one_entry()
    {
        var manager = Started();

        manager.SaveFloatingBar(At(2300, 1300), Desk);
        manager.SaveFloatingBar(At(2000, 1200), Desk);

        Assert.Single(manager.State.BarPlacements);
        Assert.Equal(2000, manager.BarPlacementFor(Desk).Value.Left);
    }

    // A layout the bar has never been parked on has nothing to say, and the caller falls back to
    // clamping the last position into a screen that exists -- the behaviour before this feature.
    [Fact]
    public void An_unseen_layout_has_no_remembered_position()
    {
        var manager = Started();

        manager.SaveFloatingBar(At(2300, 1300), Desk);

        Assert.True(manager.BarPlacementFor(Laptop).HasNoValue);
    }

    // Unknown is what a failed monitor query produces, and it must not become a bucket: two different
    // unknown layouts would otherwise share one entry and hand each other's position back.
    [Fact]
    public void An_unknown_layout_is_not_stored()
    {
        var manager = Started();

        manager.SaveFloatingBar(At(2300, 1300), MonitorLayoutKey.Unknown);

        Assert.Empty(manager.State.BarPlacements);
    }

    [Fact]
    public void An_unknown_layout_is_never_looked_up()
    {
        var manager = Started();

        manager.SaveFloatingBar(At(2300, 1300), Desk);

        Assert.True(manager.BarPlacementFor(MonitorLayoutKey.Unknown).HasNoValue);
    }

    // The single position stays exactly what it always was -- the last position on any layout -- so an
    // older build reads the position it expects and the unknown-layout fallback still works.
    [Fact]
    public void The_single_position_is_still_written_alongside()
    {
        var manager = Started();

        manager.SaveFloatingBar(At(2300, 1300), Desk);
        manager.SaveFloatingBar(At(1600, 900), Laptop);

        Assert.Equal(1600, manager.State.FloatingBar!.Left);
    }

    [Fact]
    public void A_save_with_no_layout_at_all_still_records_the_position()
    {
        var manager = Started();

        manager.SaveFloatingBar(At(2300, 1300));

        Assert.Equal(2300, manager.State.FloatingBar!.Left);
        Assert.Empty(manager.State.BarPlacements);
    }

    [Fact]
    public void Remembered_placements_are_persisted()
    {
        var manager = Started();

        manager.SaveFloatingBar(At(2300, 1300) with { Right = 2500, Width = 400 }, Desk);

        var saved = Assert.Single(store.Stored.BarPlacements);
        Assert.Equal(Desk, saved.Layout);
        Assert.Equal(2500, saved.Bar.Right);
        Assert.Equal(400, saved.Bar.Width);
    }

    // An RDP session with dynamic resolution mints a new layout on every resize of the client window,
    // so this list has a churning source and needs a ceiling. Without one, state.json accumulates
    // positions for desks that existed for four seconds.
    [Fact]
    public void Only_the_last_dozen_layouts_are_kept()
    {
        var manager = Started();

        Enumerable.Range(1, 20).ToList().ForEach(i =>
            manager.SaveFloatingBar(At(i, i), MonitorLayoutKey.Of([new MonitorBounds(0, 0, 1000 + i, 800)])));

        Assert.Equal(12, manager.State.BarPlacements.Count);
    }

    // What falls off is the layout longest unseen, and what stays is the one just used -- the other way
    // round would forget the desk you are sitting at.
    [Fact]
    public void The_oldest_layout_is_the_one_dropped()
    {
        var manager = Started();

        manager.SaveFloatingBar(At(1, 1), Laptop);
        Enumerable.Range(1, 12).ToList().ForEach(i =>
            manager.SaveFloatingBar(At(i, i), MonitorLayoutKey.Of([new MonitorBounds(0, 0, 1000 + i, 800)])));

        Assert.True(manager.BarPlacementFor(Laptop).HasNoValue);
    }

    // A state.json written before this key existed has no placements and must load without one.
    [Fact]
    public void A_state_file_without_placements_loads_empty()
    {
        store.Stored = AppState.Empty with { FloatingBar = At(2300, 1300) };

        Assert.Empty(Started().State.BarPlacements);
    }
}

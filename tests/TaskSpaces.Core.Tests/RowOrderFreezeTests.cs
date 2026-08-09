using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;

namespace TaskSpaces.Core.Tests;

// Petre: "when an app becomes the top app, if i press on it in the workspace, it moves to the
// first position, which is good, but i want that position changing to happen after i've left the
// floating window with a mouse... so that i can minimize it back if i didn't want to use it and
// am testing what it is."
//
// The whole ordering rule lives in RowOrderFreeze so it can be pinned here, without a bar, a
// message pump or a mouse. What CANNOT be tested here is the hover wiring itself -- see the
// design doc; that half is verified by hand.
public class RowOrderFreezeTests
{
    // MonitorRank is the only field the freeze reads besides the handle, so the rows are built
    // directly rather than through OverviewBuilder: a fake ScreenFacts would add a layer that
    // proves nothing about this rule.
    static WindowRow Row(nint handle, int monitorRank = 0) =>
        new(new WindowInfo(new WindowHandle(handle), (int)handle, $"proc{handle}", null, $"window {handle}", null),
            Maybe<string>.None,
            MonitorRank: monitorRank);

    static IReadOnlyList<nint> Handles(IEnumerable<WindowRow> rows) =>
        rows.Select(r => r.Window.Handle.Value).ToList();

    // The reported case. Live z-order has just promoted C to the front of the row (the icon the
    // user clicked); the snapshot taken when the pointer entered still says A, B, C, and that is
    // what stays on screen until the pointer leaves.
    [Fact]
    public void The_frozen_order_wins_over_live_z_order()
    {
        var frozen = RowOrderFreeze.Capture([Row(0xA), Row(0xB), Row(0xC)]);

        var held = RowOrderFreeze.Apply([Row(0xC), Row(0xA), Row(0xB)], frozen);

        Assert.Equal([0xA, 0xB, 0xC], Handles(held));
    }

    // A window closed while the row was frozen. Its snapshot entry is inert -- the survivors keep
    // their positions rather than the whole row falling back to live order.
    [Fact]
    public void A_window_that_closed_drops_out_without_disturbing_the_others()
    {
        var frozen = RowOrderFreeze.Capture([Row(0xA), Row(0xB), Row(0xC)]);

        var held = RowOrderFreeze.Apply([Row(0xC), Row(0xA)], frozen);

        Assert.Equal([0xA, 0xC], Handles(held));
    }

    // A window that opened while the row was frozen is in no snapshot, so it has no held
    // position and lands after the ones that do. Front of the live order or not.
    [Fact]
    public void A_window_that_appeared_while_frozen_lands_after_the_held_ones()
    {
        var frozen = RowOrderFreeze.Capture([Row(0xA), Row(0xB)]);

        var held = RowOrderFreeze.Apply([Row(0xD), Row(0xA), Row(0xB)], frozen);

        Assert.Equal([0xA, 0xB, 0xD], Handles(held));
    }

    // ...but "after" means after the held icons OF ITS OWN MONITOR GROUP, not at the end of the
    // row. GroupRow draws a hairline wherever the monitor changes as it walks the row, so an icon
    // parked past its group would draw a boundary that is not there -- a rendering fault caused by
    // a hover, which is the one thing a hover must never do.
    [Fact]
    public void A_new_window_stays_inside_its_own_monitor_group()
    {
        var frozen = RowOrderFreeze.Capture([Row(0xA), Row(0xB, monitorRank: 1)]);

        var held = RowOrderFreeze.Apply([Row(0xA), Row(0xD), Row(0xB, monitorRank: 1)], frozen);

        Assert.Equal([0xA, 0xD, 0xB], Handles(held));
    }

    // Two newcomers in one group keep the order live z-order gave them: OrderBy is stable, so
    // "no held position" is not a licence to shuffle.
    [Fact]
    public void Windows_with_no_held_position_keep_their_live_order()
    {
        var frozen = RowOrderFreeze.Capture([Row(0xA)]);

        var held = RowOrderFreeze.Apply([Row(0xE), Row(0xD), Row(0xA)], frozen);

        Assert.Equal([0xA, 0xE, 0xD], Handles(held));
    }

    // The freeze may reorder icons WITHIN a monitor group and never across one: the row's
    // structure is not something a hover is allowed to change. Here the snapshot is a lie about
    // grouping (it puts the monitor-2 window first) and is overruled.
    [Fact]
    public void Monitor_grouping_survives_a_snapshot_that_contradicts_it()
    {
        var frozen = RowOrderFreeze.Capture([Row(0xB, monitorRank: 1), Row(0xA)]);

        var held = RowOrderFreeze.Apply([Row(0xA), Row(0xB, monitorRank: 1)], frozen);

        Assert.Equal([0xA, 0xB], Handles(held));
    }

    // An unfrozen row -- every row on the bar, most of the time -- must come out bit-for-bit as
    // it went in, or this feature has changed the resting behaviour of the whole surface.
    [Fact]
    public void An_empty_snapshot_is_the_identity()
    {
        var live = new[] { Row(0xC), Row(0xA), Row(0xB, monitorRank: 1) };

        Assert.Equal([0xC, 0xA, 0xB], Handles(RowOrderFreeze.Apply(live, [])));
    }
}

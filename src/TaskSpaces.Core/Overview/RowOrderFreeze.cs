using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Overview;

// Petre: "when an app becomes the top app, if i press on it in the workspace, it moves to the
// first position, which is good, but i want that position changing to happen after i've left the
// floating window with a mouse... so that i can minimize it back if i didn't want to use it and
// am testing what it is."
//
// Icons sort front-most first (OverviewBuilder.OnDesktop), so clicking one activates its window,
// which puts it in front, which moves its icon to the head of its monitor group -- out from under
// the pointer that just clicked it. Probing a row (click, look, minimise back) therefore
// rearranges it twice while the hand is still on it.
//
// The promotion is wanted; its timing is not. So the bar snapshots a row's displayed order when
// the pointer enters it and re-imposes that order until the pointer leaves THAT ROW -- not the
// bar: leaving onto a neighbouring row releases it too, which is what was asked for.
//
// Only the ORDER is held. Everything else about the row stays live -- the clicked icon takes the
// active highlight and the "on top" mark in place, dimming updates, windows still appear and
// vanish -- because a row that froze completely would answer a click with no visible change at
// all, which reads as a click that did not land.
//
// Pure and here rather than in the bar so the whole rule is testable without a window, a message
// pump or a mouse (RowOrderFreezeTests). The hover half necessarily lives in FloatingBar.
public static class RowOrderFreeze
{
    // What is on screen right now, in the order it is drawn. Handles rather than rows because
    // the rows themselves are rebuilt constantly and only their identity and position matter.
    public static IReadOnlyList<WindowHandle> Capture(IEnumerable<WindowRow> rows) =>
        rows.Select(r => r.Window.Handle).ToList();

    // Re-impose a captured order on a freshly built row.
    //
    // MonitorRank stays the PRIMARY key, so a freeze can only ever reorder icons within a monitor
    // group and never across one. That is not tidiness: GroupRow draws a hairline wherever the
    // monitor changes as it walks the row, so an icon parked outside its group would draw a
    // boundary that is not there -- a rendering fault caused by hovering, which is the one thing
    // hovering must never cause.
    //
    // A window that opened while the row was frozen is in no snapshot, so it sorts after the held
    // ones (int.MaxValue) -- inside its own group, by the rule above. A window that closed is
    // simply absent from `rows` and its snapshot entry is inert.
    //
    // OrderBy is STABLE, and that is load-bearing twice over: two newcomers keep the relative
    // order live z-order gave them, and an empty snapshot leaves the row bit-for-bit as it
    // arrived -- which is every row on the bar, most of the time.
    public static IReadOnlyList<WindowRow> Apply(IReadOnlyList<WindowRow> rows, IReadOnlyList<WindowHandle> frozen)
    {
        if (frozen.Count == 0) return rows;

        var held = frozen
            .Select((handle, index) => (handle, index))
            // A snapshot cannot contain a handle twice (one window has one row), but a
            // ToDictionary that throws on the day it does would take the whole bar down, and the
            // first entry is the right answer anyway.
            .GroupBy(x => x.handle)
            .ToDictionary(g => g.Key, g => g.First().index);

        return rows
            .OrderBy(r => r.MonitorRank.GetValueOrDefault(int.MaxValue))
            .ThenBy(r => held.TryGetValue(r.Window.Handle, out var index) ? index : int.MaxValue)
            .ToList();
    }
}

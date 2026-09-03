using TaskSpaces.Windows.Diagnostics;

namespace TaskSpaces.Windows.Tests;

// Petre: "app crashed. start and see why."
//
// Windows' own account of it, two seconds after the last line in our trace log:
//
//   Application: TaskSpaces-1.13.2-win-x64.exe
//   Description: The process was terminated due to an unhandled exception.
//   System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
//      at System.Collections.Generic.Dictionary`2.ValueCollection.Enumerator.MoveNext()
//      at System.Windows.Input.StylusWisp.WispLogic.RefreshTablets()
//      at System.Windows.Input.StylusWisp.WispLogic.OnTabletRemovedImpl(UInt32 wisptisIndex, ...)
//      at System.Windows.SystemResources.InvalidateTabletDevices(WindowMessage msg, ...)
//
// Not our code anywhere on that stack. It is WPF's own tablet bookkeeping walking a dictionary while
// something else mutates it, provoked by a pen or touch device being REMOVED -- which matches the
// `geometry: monitors=1` line in the log just before, where it had been 2 all evening: his spacedesk
// screen disconnected and took its touch device with it.
//
// So the app died for a bug it does not contain and cannot prevent. This predicate recognises that
// one fault so the dispatcher handler can swallow it and carry on, while every other unhandled
// exception keeps the existing behaviour -- restore every window's title, say so, and let the process
// end.
//
// The stack text below is the real one, copied from the event log entry for that crash.
public class KnownWpfFaultsTests
{
    const string TabletRemovalStack = """
           at System.Collections.Generic.Dictionary`2.ValueCollection.Enumerator.MoveNext()
           at System.Windows.Input.StylusWisp.WispLogic.RefreshTablets()
           at System.Windows.Input.StylusWisp.WispLogic.OnTabletRemovedImpl(UInt32 wisptisIndex, Boolean isInternalCall)
           at System.Windows.SystemResources.InvalidateTabletDevices(WindowMessage msg, IntPtr wParam, IntPtr lParam)
           at MS.Win32.HwndSubclass.DispatcherCallbackOperation(Object o)
        """;

    [Fact]
    public void Recognises_the_tablet_removal_race() =>
        Assert.True(KnownWpfFaults.IsTabletBookkeepingRace(new InvalidOperationException("Collection was modified; enumeration operation may not execute."), TabletRemovalStack));

    // Narrow on the TYPE as well as the stack, because the point is to swallow one known fault rather
    // than everything that happens to pass through WPF's input code.
    [Fact]
    public void A_different_exception_from_the_same_place_is_not_swallowed() =>
        Assert.False(KnownWpfFaults.IsTabletBookkeepingRace(new NullReferenceException(), TabletRemovalStack));

    // ...and narrow on the stack as well as the type: a collection modified while OUR code enumerates
    // it is a bug of ours, and hiding it would be the worst thing this could do.
    [Fact]
    public void Our_own_collection_bug_is_not_swallowed()
    {
        const string ours = """
               at System.Collections.Generic.Dictionary`2.Enumerator.MoveNext()
               at TaskSpaces.Core.WorkspaceManager.RepairWindowList()
               at TaskSpaces.App.App.<>c.<OnStartup>b__0()
            """;

        Assert.False(KnownWpfFaults.IsTabletBookkeepingRace(new InvalidOperationException("Collection was modified; enumeration operation may not execute."), ours));
    }

    // An exception that never got a stack (thrown and caught without being raised through a frame) is
    // not evidence of anything, so it is not this.
    [Fact]
    public void No_stack_is_not_a_match() =>
        Assert.False(KnownWpfFaults.IsTabletBookkeepingRace(new InvalidOperationException(), null));

    // The whole chain is inspected, because WPF's dispatcher wraps what it catches: the real fault can
    // arrive as the InnerException of a TargetInvocationException.
    [Fact]
    public void A_wrapped_fault_is_still_recognised() =>
        Assert.True(KnownWpfFaults.IsTabletBookkeepingRace(
            new System.Reflection.TargetInvocationException(
                new InvalidOperationException("Collection was modified; enumeration operation may not execute.")),
            TabletRemovalStack));
}

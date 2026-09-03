namespace TaskSpaces.Windows.Diagnostics;

// Faults that are WPF's own, not ours, and that killing the app over would be a choice rather than a
// necessity. One so far.
public static class KnownWpfFaults
{
    // WPF's tablet bookkeeping, racing itself when a pen or touch device is REMOVED.
    //
    // Petre: "app crashed. start and see why." Windows' account of it, two seconds after the last line
    // in our own trace log:
    //
    //   Description: The process was terminated due to an unhandled exception.
    //   System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
    //      at System.Collections.Generic.Dictionary`2.ValueCollection.Enumerator.MoveNext()
    //      at System.Windows.Input.StylusWisp.WispLogic.RefreshTablets()
    //      at System.Windows.Input.StylusWisp.WispLogic.OnTabletRemovedImpl(UInt32 wisptisIndex, ...)
    //      at System.Windows.SystemResources.InvalidateTabletDevices(WindowMessage msg, ...)
    //
    // Nothing of ours is anywhere on that stack. WPF walks its tablet dictionary while something else
    // mutates it, and the trigger is a device disappearing -- which the log corroborates: the line
    // before the silence reads `geometry: monitors=1`, where it had been 2 all evening. His spacedesk
    // screen disconnected and took its touch device with it.
    //
    // We cannot prevent it and cannot fix it from out here, so the only decision available is whether
    // to die of it. The app's state is untouched by this: no window has been moved, no title is
    // half-applied, and the workspace model has not been entered. Dying would cost him every renamed
    // title and the bar itself over a fault in WPF's input plumbing, so the dispatcher handler
    // swallows this one and keeps going. Everything else still restores titles and lets the process
    // end, which is what that handler was written for.
    //
    // MATCHED ON BOTH the exception type and the stack, and both halves earn their place. The stack
    // alone would swallow anything that happened to pass through WPF's input code; the type alone
    // would swallow OUR OWN "collection was modified" bugs, which is the worst thing this file could
    // do -- that is a real defect and it must keep crashing loudly.
    //
    // The whole chain is inspected because the dispatcher wraps what it catches, so the fault can
    // arrive as an InnerException.
    public static bool IsTabletBookkeepingRace(Exception fault) =>
        Chain(fault).Any(e => IsTabletBookkeepingRace(fault, e.StackTrace));

    // The stack passed in rather than read off the exception, which is what lets the tests assert
    // against the REAL stack text from that crash instead of trying to provoke WPF into producing one.
    public static bool IsTabletBookkeepingRace(Exception fault, string? stack) =>
        stack is not null
        && stack.Contains("System.Windows.Input.StylusWisp", StringComparison.Ordinal)
        && Chain(fault).Any(e => e is InvalidOperationException);

    static IEnumerable<Exception> Chain(Exception? fault)
    {
        for (var at = fault; at is not null; at = at.InnerException)
            yield return at;
    }
}

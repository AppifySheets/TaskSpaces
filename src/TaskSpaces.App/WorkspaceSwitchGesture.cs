using System.Windows.Threading;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// Petre: "maybe an alt-tab like shortcut for me to switch through workspaces".
//
// Alt+Tab's whole trick is that the gesture spans a HOLD: the modifier stays down while you
// walk a most-recently-used list, and releasing it commits. That is what makes one tap mean
// "back to where I just was" no matter how the list is ordered -- and it is exactly what
// this app could not do before, because RegisterHotKey reports a chord being pressed and has
// no concept of release whatsoever.
//
// So the gesture is assembled from two halves that each do what they are good at:
//   press   RegisterHotKey (HotkeyService) -> Step(+1/-1), which only moves a highlight
//   release GetAsyncKeyState, polled on a 30ms timer -> Commit(), which does the switch
//
// The timer runs ONLY while the gesture is in progress, so the steady-state cost is zero. The
// alternative, a WH_KEYBOARD_LL hook, would see the release directly but would put this
// process in the input path of every keystroke on the machine -- a much larger liability
// than a poll measured in milliseconds and bounded by the length of one gesture.
//
// Nothing switches until commit, deliberately: switching on each tap would mean walking past
// three workspaces to reach the fourth, and virtual-desktop switches animate.
public sealed class WorkspaceSwitchGesture : IDisposable
{
    // 30ms: fast enough that release feels instant (a frame at 60Hz is ~17ms), slow enough to
    // be free. The gesture is normally over in well under a second.
    static readonly TimeSpan ReleasePoll = TimeSpan.FromMilliseconds(30);

    readonly WorkspaceManager manager;
    // TWO surfaces, splitting one job. Petre tried the bar carrying the whole gesture -- amber
    // rings plus a number on every row -- and rejected it ("this is bad"), then: "show the
    // previous list but ONLY next to the floating window", "also maintain the yellow rings,
    // remove the numbers".
    //
    // So the list answers "what order does this walk in", which a bar sorted in his own fixed
    // order cannot express; and the rings answer "which row, right now", on the rows themselves
    // where the answer is finally acted on. The bar is also the anchor -- the picker is placed
    // against it, so the two are always read together.
    readonly WorkspaceSwitcher picker = new();
    readonly FloatingBar bar;
    readonly DispatcherTimer release = new() { Interval = ReleasePoll };

    // The whole record rather than just its Ordered list: Step below resolves the next index
    // through RecentWorkspaces.IndexAfter, so that the chord and the floating bar's back
    // button cannot disagree about where "one tap away" is.
    RecentWorkspaces recent = new([], -1);
    int selected = -1;
    bool active;
    // The chord currently bound. Held here as well as in HotkeyService because BOTH halves
    // of the gesture depend on it: the service needs the key to register, and this class
    // needs the MODIFIERS to know what "still held" means. Petre: "i want it configurable" --
    // so this is a field that changes, not a constant.
    Chord chord;

    public WorkspaceSwitchGesture(WorkspaceManager manager, Chord chord, FloatingBar bar)
    {
        this.manager = manager;
        this.chord = chord;
        this.bar = bar;
        // Escape is checked BEFORE the release test, so a gesture abandoned with Escape cannot
        // be committed by the same tick that noticed it -- and the poll is already running, so
        // cancelling needs no second timer and no keyboard hook.
        release.Tick += (_, _) =>
        {
            if (Down(NativeMethods.VK_ESCAPE)) Cancel();
            else if (!ModifiersHeld()) Commit();
        };
    }

    // Mid-gesture rebinding would leave this watching for a modifier nobody is holding, so
    // commit whatever is selected first and start clean on the new chord.
    public void Rebind(Chord replacement)
    {
        if (active) Commit();
        chord = replacement;
    }

    // One tap of the bound chord (direction +1) or that chord plus Shift (-1).
    public void Step(int direction)
    {
        if (!active) Begin(direction);
        if (recent.Ordered.Count == 0) return;
        selected = recent.IndexAfter(selected, direction);
        picker.Select(selected);
        bar.ShowCandidate(recent.Ordered[selected].Id);
    }

    int DefinedIndexOf(Workspace workspace) =>
        Math.Max(0, manager.State.Workspaces.ToList().FindIndex(w => w.Id == workspace.Id));

    // The workspace this one borrows its windows from, or null (#42).
    //
    // Reads AppState.LendsWindowsTo, so the answer is null in three situations that all mean the
    // same thing to the picker: the workspace is in no group, it is in an ANCHORLESS group (#84,
    // where there is no parent workspace at all), or it is the anchor itself. In each case the
    // picker shows a plain name rather than a prefix.
    //
    // An anchorless group's NAME is deliberately not used as a prefix here. It names a set, not a
    // place, and "Clients / EuroCredit" would read as a path to a workspace that the chord cannot
    // land on.
    Workspace? ParentOf(Workspace workspace) =>
        manager.State.LendsWindowsTo(workspace.Id) is { } lender
            ? manager.State.Workspaces.FirstOrDefault(w => w.Id == lender)
            : null;

    // The picker's hwnd, so the composition root can hand it to WindowMonitor.Ignore. The monitor
    // no longer skips our own process (Petre wanted the Manage window in the bar), so without
    // this the picker would flicker into the bar as a window every time it appeared. Created
    // before the first Show() so its very first SHOW event is filtered.
    public nint EnsureHandle() => new System.Windows.Interop.WindowInteropHelper(picker).EnsureHandle();

    void Begin(int direction)
    {
        recent = manager.ByRecentUse();
        // Nothing (or nothing but where we already are) to switch between: no highlight, no
        // timer, and Step's guard above turns the tap into a no-op.
        if (recent.Ordered.Count < 2) return;

        // CurrentIndex is -1 when the current desktop is not a workspace at all (one of the
        // unbound ones, e.g. Petre's "Main"). Walking forward from -1 lands on index 0, the
        // most recently used workspace, which is the right answer. Walking BACKWARD from -1
        // would land two short of the end, so that direction starts from 0 instead and wraps
        // to the last entry.
        selected = recent.CurrentIndex >= 0 ? recent.CurrentIndex : direction > 0 ? -1 : 0;
        active = true;

        // The bar shows no rings yet: Step calls ShowCandidate the instant this returns, having
        // advanced `selected` by one.
        bar.BeginSwitch();
        picker.Present(
            recent.Ordered.Select(workspace =>
                // Colour by DEFINED position, not by position in the recency list -- the same
                // rule the floating bar's lane tints follow, so the two surfaces agree.
                new SwitcherChoice(
                    workspace.Name,
                    // Colour follows the PARENT for a nested workspace, exactly as the bar's lane
                    // tint does (#42) -- the picker's swatch and the bar's lane are the same fact
                    // told twice, and a family that shares a colour in one place has to share it
                    // in the other.
                    WorkspacePalette.For(ParentOf(workspace) ?? workspace, DefinedIndexOf(ParentOf(workspace) ?? workspace)),
                    ParentOf(workspace)?.Name)).ToList(),
            selected,
            // The hint names the chord actually in force, not a hardcoded one -- the whole point
            // of making it configurable is undone if the picker still tells you to hold Ctrl+Alt
            // after you have rebound it.
            chord,
            // Anchored to the bar: "next to, meaning: on the same screen as the floating window
            // is", "either on the left or on the right".
            bar);
        release.Start();
    }

    void Commit()
    {
        var destination = selected;
        Finish();
        // Fire-and-forget, like every other hotkey path in this app: a keypress has no UI to
        // report a failure through, and a message box raised by a chord would be worse than
        // a switch that quietly did not happen.
        if (destination >= 0 && destination < recent.Ordered.Count) manager.Switch(recent.Ordered[destination].Id);
    }

    // Petre: "pressing an escape mid-switching shortcuts session should cancel it."
    //
    // Alt+Tab's own escape hatch, and the reason it matters here is that this gesture COMMITS ON
    // RELEASE: once the walk has gone past what you wanted there is otherwise no way out except
    // walking all the way round to where you started, and letting go anywhere else takes you
    // somewhere you did not ask for.
    //
    // Identical teardown to Commit, minus the switch -- which is the whole definition of
    // cancelling, and why the two share Finish rather than each doing their own tidying.
    //
    // The modifiers are usually still held at this point. That is deliberately not waited for:
    // the timer is stopped, so nothing can commit, and tapping the chord again simply begins a
    // fresh gesture.
    void Cancel() => Finish();

    void Finish()
    {
        release.Stop();
        active = false;
        picker.Hide();
        bar.EndSwitch();
        selected = -1;
    }

    // Which physical keys each modifier bit corresponds to. Win has two, and either one
    // counts as holding it.
    static readonly IReadOnlyList<(uint Bit, int[] Keys)> ModifierKeys =
    [
        (Chord.Control, [NativeMethods.VK_CONTROL]),
        (Chord.Alt, [NativeMethods.VK_MENU]),
        (Chord.Shift, [NativeMethods.VK_SHIFT]),
        (Chord.Win, [NativeMethods.VK_LWIN, NativeMethods.VK_RWIN]),
    ];

    // True while EVERY modifier of the bound chord is still down; the gesture commits the
    // moment any one of them goes up, rather than waiting for all of them, because letting
    // go of Ctrl+Alt is one motion and insisting on a particular order would feel stuck.
    //
    // Note this asks about the chord's OWN modifiers, so the Shift used for the reverse
    // direction is not among them (unless the user bound Shift deliberately) -- which is
    // what lets you release Shift mid-walk to go forwards again, exactly as Alt+Tab does.
    bool ModifiersHeld() =>
        ModifierKeys.Where(modifier => (chord.Modifiers & modifier.Bit) != 0)
            .All(modifier => modifier.Keys.Any(Down));

    static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & NativeMethods.KeyDownBit) != 0;

    // The picker is ours to close; the bar is not -- it outlives this gesture and belongs to the
    // composition root.
    public void Dispose()
    {
        release.Stop();
        picker.Close();
    }
}

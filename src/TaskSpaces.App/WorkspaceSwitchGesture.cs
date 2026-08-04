using System.Windows.Interop;
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
// The timer runs ONLY while the picker is on screen, so the steady-state cost is zero. The
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
    readonly WorkspaceSwitcher picker = new();
    readonly DispatcherTimer release = new() { Interval = ReleasePoll };

    IReadOnlyList<Workspace> candidates = [];
    int selected = -1;
    bool active;
    // The chord currently bound. Held here as well as in HotkeyService because BOTH halves
    // of the gesture depend on it: the service needs the key to register, and this class
    // needs the MODIFIERS to know what "still held" means. Petre: "i want it configurable" --
    // so this is a field that changes, not a constant.
    Chord chord;

    public WorkspaceSwitchGesture(WorkspaceManager manager, Chord chord)
    {
        this.manager = manager;
        this.chord = chord;
        release.Tick += (_, _) => { if (!ModifiersHeld()) Commit(); };
    }

    // Mid-gesture rebinding would leave the picker watching for a modifier nobody is
    // holding, so commit whatever is selected first and start clean on the new chord.
    public void Rebind(Chord replacement)
    {
        if (active) Commit();
        chord = replacement;
    }

    // The picker's hwnd, so the composition root can hand it to WindowMonitor.Ignore. The
    // monitor no longer skips our own process (Petre wanted the Manage window in the bar),
    // so without this the picker would flicker into the bar as a window every time it
    // appeared. Created before the first Show() so its very first SHOW event is filtered.
    public nint EnsureHandle() => new WindowInteropHelper(picker).EnsureHandle();

    // One tap of Ctrl+Alt+` (direction +1) or Ctrl+Alt+Shift+` (-1).
    public void Step(int direction)
    {
        if (!active) Begin(direction);
        if (candidates.Count == 0) return;
        // Wrapping in both directions, and written the long way because C#'s % keeps the
        // sign of the left operand: -1 % 3 is -1, not 2.
        selected = ((selected + direction) % candidates.Count + candidates.Count) % candidates.Count;
        picker.Select(selected);
    }

    void Begin(int direction)
    {
        var recent = manager.ByRecentUse();
        candidates = recent.Ordered;
        // Nothing (or nothing but where we already are) to switch between: no picker, no
        // timer, and Step's guard above turns the tap into a no-op.
        if (candidates.Count < 2) return;

        // CurrentIndex is -1 when the current desktop is not a workspace at all (one of the
        // unbound ones, e.g. Petre's "Main"). Walking forward from -1 lands on index 0, the
        // most recently used workspace, which is the right answer. Walking BACKWARD from -1
        // would land two short of the end, so that direction starts from 0 instead and wraps
        // to the last entry.
        selected = recent.CurrentIndex >= 0 ? recent.CurrentIndex : direction > 0 ? -1 : 0;
        active = true;

        picker.Present(
            candidates.Select((workspace, index) =>
                // Colour by DEFINED position, not by position in the recency list -- the same
                // rule the floating bar's lane tints follow, so the two surfaces agree.
                new SwitcherChoice(workspace.Name, WorkspacePalette.For(workspace, DefinedIndexOf(workspace)))).ToList(),
            selected,
            // The on-screen hint names the chord actually in force, not a hardcoded one --
            // the whole point of making it configurable is undone if the picker still tells
            // you to hold Ctrl+Alt after you have rebound it to something else.
            chord);
        release.Start();
    }

    int DefinedIndexOf(Workspace workspace) =>
        Math.Max(0, manager.State.Workspaces.ToList().FindIndex(w => w.Id == workspace.Id));

    void Commit()
    {
        release.Stop();
        active = false;
        picker.Hide();
        // Fire-and-forget, like every other hotkey path in this app: a keypress has no UI to
        // report a failure through, and a message box raised by a chord would be worse than
        // a switch that quietly did not happen.
        if (selected >= 0 && selected < candidates.Count) manager.Switch(candidates[selected].Id);
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

    public void Dispose()
    {
        release.Stop();
        picker.Close();
    }
}

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

    public WorkspaceSwitchGesture(WorkspaceManager manager)
    {
        this.manager = manager;
        release.Tick += (_, _) => { if (!ModifiersHeld()) Commit(); };
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
            selected);
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

    // Commits as soon as EITHER modifier goes up, rather than waiting for both: releasing
    // Ctrl+Alt is one motion, and insisting on the exact order they happen to leave the keys
    // would make the gesture feel like it stuck.
    static bool ModifiersHeld() => Down(NativeMethods.VK_CONTROL) && Down(NativeMethods.VK_MENU);

    static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & NativeMethods.KeyDownBit) != 0;

    public void Dispose()
    {
        release.Stop();
        picker.Close();
    }
}

using System.Windows.Interop;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// The app's global hotkeys, which are now exactly ONE chord and its Shift variant: the
// Alt+Tab-style workspace switcher.
//
// Petre: "i don't think we need ctrl+alt and those, ctrl+tab is good enough". It was: this
// class used to register eleven chords -- Ctrl+Alt+Left/Right to cycle and Ctrl+Alt+1..9 to
// jump by position -- and holding eleven chords EXCLUSIVELY, machine-wide, is a real price for
// features the bar and the switcher already cover. Ctrl+Alt+arrows cycled in LIST order, which
// is strictly worse than most-recently-used for the only job cycling does well (going back).
// Ctrl+Alt+1..9 bound by list POSITION, so reordering workspaces silently changed what each
// chord did. Direct jumps did not disappear with them: a bar row label is one click, and the
// scaffolding for per-workspace NAMED chords (Workspace.Shortcut, Chord) is already in place
// for when a keyboard version is wanted -- keyed to a workspace's identity rather than its
// index, and chosen rather than squatting on nine chords.
//
// RegisterHotKey needs SOME window handle to deliver WM_HOTKEY to, and this app has no
// always-visible main window. A message-only window (HWND_MESSAGE parent, per Win32 docs) is
// the standard fix: it can receive messages but never paints, is never visible, and never
// shows up in Alt+Tab or the taskbar.
public sealed class HotkeyService : IDisposable
{
    // Ids 3/4 are the switcher's forward and backward chords. The numbering starts at 3 rather
    // than 1 because ids 1, 2 and 11..19 belonged to the cycle and digit chords removed above;
    // leaving the gap keeps the ids stable for anyone reading an old log.
    const int IdRecentNext = 3, IdRecentPrev = 4;

    readonly HwndSource source;
    readonly List<int> registeredIds = [];
    readonly List<string> failures = [];

    // Failed registrations (another app already owns that chord) are recorded here rather
    // than thrown -- spec: "Registration failures ... surface once as a warning -- never a
    // crash, never silent." The composition root reads this once, after construction, to show
    // a single MessageBox if non-empty. At most one entry now that there is one chord.
    public IReadOnlyList<string> Failures => failures;

    readonly Action<int> stepRecent;

    public HotkeyService(Action<int> stepRecent, Chord switcher)
    {
        this.stepRecent = stepRecent;

        source = new HwndSource(new HwndSourceParameters("TaskSpacesHotkeys")
        {
            WindowStyle = 0,          // no visible-window styles: this hwnd never paints
            Width = 0,
            Height = 0,
            ParentWindow = new nint(-3), // HWND_MESSAGE: message-only window, never shown, never focusable
        });
        source.AddHook(WndProc);

        // Configurable (Petre: "i want it configurable"), hence a parameter rather than a
        // constant, and rebindable below without a restart.
        BindSwitcher(switcher).TapError(failures.Add);
    }

    // Rebinds the Alt+Tab-style switcher to a new chord, releasing the old one first.
    // Returns a Result rather than adding to Failures, because unlike a startup registration
    // this one has a human waiting on it in the Shortcuts tab.
    public Result BindSwitcher(Chord chord)
    {
        Unregister(IdRecentNext);
        Unregister(IdRecentPrev);

        // Shift is the reverse direction, exactly as Shift+Alt+Tab is, and it has to be a
        // SEPARATE registration because RegisterHotKey matches modifiers EXACTLY -- the
        // forward chord does not fire at all while Shift is held, so without this the
        // backward walk would simply stop responding.
        //
        // Unless the chord already uses Shift, in which case there is no free modifier left
        // to mean "backwards" and the walk is forward-only. Not an error: wrapping means
        // every workspace is still reachable, just not in one tap.
        if ((chord.Modifiers & Chord.Shift) == 0)
            // A failure here is deliberately ignored: losing the reverse direction is a
            // degradation, not a reason to reject a chord whose forward half works.
            Register(IdRecentPrev, chord.Modifiers | Chord.Shift, chord.VirtualKey);

        return Result.SuccessIf(
            Register(IdRecentNext, chord.Modifiers, chord.VirtualKey),
            $"{chord} is already taken by another app, so it will not switch workspaces.");
    }

    bool Register(int id, uint modifiers, uint vk)
    {
        // Chord's own modifier constants carry the same values as Win32's MOD_*, which is why
        // a parsed Chord's Modifiers can be handed to RegisterHotKey unconverted.
        if (!NativeMethods.RegisterHotKey(source.Handle, id, modifiers, vk)) return false;
        registeredIds.Add(id);
        return true;
    }

    void Unregister(int id)
    {
        if (registeredIds.Remove(id)) NativeMethods.UnregisterHotKey(source.Handle, id);
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != (int)NativeMethods.WM_HOTKEY) return nint.Zero;

        var id = (int)wParam;
        // Note these do NOT switch: they advance a highlight in the picker, which commits when
        // the chord's modifiers are released. See WorkspaceSwitchGesture.
        if (id == IdRecentNext) stepRecent(+1);
        else if (id == IdRecentPrev) stepRecent(-1);
        else return nint.Zero; // not one of ours: leave `handled` false

        handled = true;
        return nint.Zero;
    }

    public void Dispose()
    {
        registeredIds.ForEach(id => NativeMethods.UnregisterHotKey(source.Handle, id));
        source.RemoveHook(WndProc);
        source.Dispose();
    }
}

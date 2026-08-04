using System.Windows.Interop;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// Task 9: global workspace hotkeys (Ctrl+Alt+Left/Right cycle, Ctrl+Alt+1..9 direct
// switch), spec §Tray interaction & hotkeys. RegisterHotKey needs SOME window handle to
// deliver WM_HOTKEY to, but this app has no always-visible main window (SwitcherPanel
// only exists once summoned, and hiding/showing it would be a fragile place to hang
// global input on). A message-only window (HWND_MESSAGE parent, per Win32 docs) is the
// standard fix: it can own a message loop and receive messages but never paints, is
// never visible, and never shows up in Alt+Tab or the taskbar.
public sealed class HotkeyService : IDisposable
{
    // Ids 1/2 are the arrow chords; 3/4 the Alt+Tab-style recent-order chords; 11..19 are
    // Ctrl+Alt+1..9 (10 + digit) so every registered id is trivially reversible back to
    // "which chord was this".
    const int IdCyclePrev = 1, IdCycleNext = 2, IdRecentNext = 3, IdRecentPrev = 4, IdDigitBase = 10;

    readonly HwndSource source;
    readonly List<int> registeredIds = [];
    readonly List<string> failures = [];

    // Failed registrations (another app already owns that chord) are recorded here
    // rather than thrown — spec: "Registration failures ... surface once as a warning —
    // never a crash, never silent." The composition root reads this once, after
    // construction, to show a single MessageBox if non-empty.
    public IReadOnlyList<string> Failures => failures;

    readonly Action cyclePrev;
    readonly Action cycleNext;
    readonly Action<int> switchTo;
    readonly Action<int> stepRecent;

    public HotkeyService(Action cyclePrev, Action cycleNext, Action<int> switchTo, Action<int> stepRecent, Chord switcher)
    {
        this.cyclePrev = cyclePrev;
        this.cycleNext = cycleNext;
        this.switchTo = switchTo;
        this.stepRecent = stepRecent;

        source = new HwndSource(new HwndSourceParameters("TaskSpacesHotkeys")
        {
            WindowStyle = 0,          // no visible-window styles: this hwnd never paints
            Width = 0,
            Height = 0,
            ParentWindow = new nint(-3), // HWND_MESSAGE: message-only window, never shown, never focusable
        });
        source.AddHook(WndProc);

        RegisterOrNote(IdCyclePrev, CtrlAlt, NativeMethods.VK_LEFT, "Ctrl+Alt+Left");
        RegisterOrNote(IdCycleNext, CtrlAlt, NativeMethods.VK_RIGHT, "Ctrl+Alt+Right");
        // The one configurable chord (Petre: "i want it configurable"), so unlike everything
        // else here it arrives as a parameter and can be changed later without a restart.
        BindSwitcher(switcher).TapError(failures.Add);

        // '1'..'9' virtual-key codes equal their ASCII char codes (0x31..0x39).
        Enumerable.Range(1, 9).ToList()
            .ForEach(digit => RegisterOrNote(IdDigitBase + digit, CtrlAlt, (uint)('0' + digit), $"Ctrl+Alt+{digit}"));
    }

    // The fixed chords all share these. Chord's own constants carry the same values as
    // Win32's MOD_*, which is why a parsed Chord's Modifiers can be handed to RegisterHotKey
    // unconverted.
    const uint CtrlAlt = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;

    // Rebinds the Alt+Tab-style switcher to a new chord, releasing the old one first.
    // Returns a Result rather than adding to Failures, because unlike the startup
    // registrations this one has a human waiting on it in the Shortcuts tab.
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

    void RegisterOrNote(int id, uint modifiers, uint vk, string chordName)
    {
        // Best-effort: a chord already owned by another app (Intel graphics rotate,
        // another utility, ...) is expected on some machines — never a crash.
        if (!Register(id, modifiers, vk)) failures.Add(chordName);
    }

    bool Register(int id, uint modifiers, uint vk)
    {
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
        // Fire-and-forget (spec comment, App wiring): a failed switch triggered from a
        // hotkey has no UI to speak through — silent no-op beats a MessageBox storm on
        // every keypress that happens to hit a stale/removed workspace.
        if (id == IdCyclePrev) cyclePrev();
        else if (id == IdCycleNext) cycleNext();
        // Note these do NOT switch: they advance a highlight in the picker, which commits
        // when Ctrl+Alt is released. See WorkspaceSwitchGesture.
        else if (id == IdRecentNext) stepRecent(+1);
        else if (id == IdRecentPrev) stepRecent(-1);
        else if (id is >= IdDigitBase + 1 and <= IdDigitBase + 9) switchTo(id - IdDigitBase - 1); // 0-based index

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

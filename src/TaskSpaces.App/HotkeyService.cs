using System.Windows.Interop;
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
    // Ids 1/2 are the arrow chords; 11..19 are Ctrl+Alt+1..9 (10 + digit) so every
    // registered id is trivially reversible back to "which chord was this".
    const int IdCyclePrev = 1, IdCycleNext = 2, IdDigitBase = 10;

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

    public HotkeyService(Action cyclePrev, Action cycleNext, Action<int> switchTo)
    {
        this.cyclePrev = cyclePrev;
        this.cycleNext = cycleNext;
        this.switchTo = switchTo;

        source = new HwndSource(new HwndSourceParameters("TaskSpacesHotkeys")
        {
            WindowStyle = 0,          // no visible-window styles: this hwnd never paints
            Width = 0,
            Height = 0,
            ParentWindow = new nint(-3), // HWND_MESSAGE: message-only window, never shown, never focusable
        });
        source.AddHook(WndProc);

        Register(IdCyclePrev, NativeMethods.VK_LEFT, "Ctrl+Alt+Left");
        Register(IdCycleNext, NativeMethods.VK_RIGHT, "Ctrl+Alt+Right");
        // '1'..'9' virtual-key codes equal their ASCII char codes (0x31..0x39).
        Enumerable.Range(1, 9).ToList()
            .ForEach(digit => Register(IdDigitBase + digit, (uint)('0' + digit), $"Ctrl+Alt+{digit}"));
    }

    void Register(int id, uint vk, string chordName)
    {
        // Best-effort: a chord already owned by another app (Intel graphics rotate,
        // another utility, ...) is expected on some machines — never a crash.
        if (NativeMethods.RegisterHotKey(source.Handle, id, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, vk))
            registeredIds.Add(id);
        else
            failures.Add(chordName);
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

using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// Petre: "show the ctrlwintab window on all screens."
//
// One WorkspaceSwitcher per monitor, driven as a single picker: Present/Select/Hide fan out to
// every window, so whichever screen Petre happens to be looking at already has the list on it.
//
// It replaces a guess rather than adding a feature on top of one. The single picker centred
// itself on the monitor holding the CURSOR, which is only ever a proxy for where he is looking --
// and a wrong proxy whenever the pointer is parked on one screen while he works on another. The
// gesture is driven entirely from the keyboard, so the mouse had no reason to be anywhere in
// particular.
//
// Cheap enough not to need managing: these windows are created once, shown and hidden rather
// than rebuilt, and each is a borderless panel of a few rows that never takes focus.
sealed class SwitcherPickers
{
    // Keyed by HMONITOR. Stable for as long as the display exists, which is exactly the lifetime
    // the window beside it should have.
    readonly Dictionary<nint, WorkspaceSwitcher> byMonitor = [];
    readonly Action<nint> ignoreWindow;

    // ignoreWindow hands each new hwnd to WindowMonitor.Ignore. The monitor no longer skips our
    // own process (Petre wanted the Manage window in the bar), so without it every picker would
    // flash into the floating bar as a window each time the chord was held. Called BEFORE the
    // window is ever shown, so even its first SHOW event is filtered.
    internal SwitcherPickers(Action<nint> ignoreWindow)
    {
        this.ignoreWindow = ignoreWindow;
        Sync(); // built up front, so the first press of the chord is not the first Show
    }

    internal void Present(IReadOnlyList<SwitcherChoice> choices, int selected, Chord chord)
    {
        Sync();
        byMonitor.ToList().ForEach(p => p.Value.Present(choices, selected, chord, p.Key));
    }

    // Repaint only, on every window. Called on each tap of the key, so -- like
    // WorkspaceSwitcher.Select itself -- it must not relayout or reposition anything.
    internal void Select(int selected) => byMonitor.Values.ToList().ForEach(p => p.Select(selected));

    internal void Hide() => byMonitor.Values.ToList().ForEach(p => p.Hide());

    // Shutdown. Every picker is a real top-level window, so leaving them open would hold the
    // process alive past Exit -- the same reason the gesture closed its single picker before.
    internal void Close()
    {
        byMonitor.Values.ToList().ForEach(p => p.Close());
        byMonitor.Clear();
    }

    // Reconciled on every Present rather than once at startup, because monitors come and go:
    // Petre docks and undocks, and a picker left behind on a detached screen would be a window
    // positioned nowhere, still listed in the app and never seen again.
    void Sync()
    {
        var live = Monitors();

        live.Where(m => !byMonitor.ContainsKey(m)).ToList().ForEach(m =>
        {
            var picker = new WorkspaceSwitcher();
            // Handle forced into existence, and ignored, before the window can ever be shown.
            ignoreWindow(new System.Windows.Interop.WindowInteropHelper(picker).EnsureHandle());
            byMonitor[m] = picker;
        });

        byMonitor.Keys.Where(m => !live.Contains(m)).ToList().ForEach(m =>
        {
            byMonitor[m].Close();
            byMonitor.Remove(m);
        });
    }

    static IReadOnlyList<nint> Monitors()
    {
        var found = new List<nint>();
        // Synchronous, so the delegate cannot outlive this call and there is nothing for the GC
        // to collect out from under us -- the same reasoning ScreenLayout.MonitorNumbers records
        // for its own use of this API, and unlike WindowMonitor's long-lived WinEvent hook.
        NativeMethods.EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref NativeMethods.RECT _, nint _) =>
        {
            found.Add(monitor);
            return true;
        }, 0);
        return found;
    }
}

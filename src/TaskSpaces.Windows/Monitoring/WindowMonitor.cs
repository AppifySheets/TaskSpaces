using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Windows.Monitoring;

using static NativeMethods;

public sealed class WindowMonitor : IWindowMonitor, IDisposable
{
    readonly Subject<WindowEvent> events = new();
    // Known windows: needed to (a) suppress duplicate SHOW events and (b) emit a full
    // WindowInfo on DESTROY, when the hwnd can no longer be queried.
    readonly Dictionary<nint, WindowInfo> known = new();
    // Finding 3 (reviewer, Important): hwnds currently HIDden but not yet destroyed
    // (e.g. Discord/Outlook minimized to tray — the window still exists, it just left
    // the taskbar). Tracked separately from `known` so a later DESTROY of a hidden
    // window still emits Disappeared (using the last-known WindowInfo still sitting in
    // `known`), and so a later re-SHOW of a hidden window is recognised as "came back"
    // rather than silently deduplicated away.
    readonly HashSet<nint> hidden = [];
    // OUR OWN chrome, opted out by hwnd (see Ignore below).
    readonly HashSet<nint> ignored = [];
    // CRITICAL: the delegate must be kept alive in a field. If the GC collects it,
    // the hook silently dies — the classic SetWinEventHook bug.
    readonly WinEventProc callback;
    readonly List<nint> hooks = [];

    public WindowMonitor() => callback = OnWinEvent;

    public IObservable<WindowEvent> Events => events.AsObservable();

    // EVERY foreground change, tracked window or not -- which is the difference between this
    // and the Activated WindowEvent below.
    //
    // Petre: "if i activate the taskbar, it hides the floating window". The taskbar and
    // StartAllBack's menu are not taskbar candidates, so they never enter `known` and never
    // produce an Activated event; but they ARE topmost, so activating one puts it above the
    // floating bar. The bar has to hear about those activations specifically in order to
    // re-assert its place in the topmost band, so this reports the raw fact and lets the
    // caller decide what it means.
    public IObservable<nint> ForegroundChanged => foreground.AsObservable();
    readonly Subject<nint> foreground = new();

    // Petre: "why isn't the taskspaces window in the floating window? it's clearly open and
    // i can't see it in the floating bar." It wasn't there because Hook() below used to pass
    // WINEVENT_SKIPOWNPROCESS, which made this monitor structurally blind to every window
    // TaskSpaces owns. That flag is gone, so our Manage window now behaves like any other
    // app's window: it appears, it can be jumped to, it can be dragged between workspaces.
    //
    // But "see our windows" must NOT mean "see ALL our windows". The floating bar is itself
    // a top-level window with a title, so without an opt-out the bar would list ITSELF --
    // and, because App pins the bar to every desktop, it would list itself in the 📌 Pinned
    // row, permanently. Hence an explicit hwnd allow-list-in-reverse rather than a guess
    // about window styles: the composition root names the windows that are app CHROME, and
    // everything else it owns is treated as an ordinary window.
    //
    // Call this BEFORE the window is first shown (WindowInteropHelper.EnsureHandle creates
    // the hwnd without showing it) so its very first EVENT_OBJECT_SHOW is already filtered.
    public void Ignore(nint hwnd) => ignored.Add(hwnd);

    // The single "do we care about this hwnd" question, so the snapshot and the event path
    // can never disagree about it.
    bool IsTracked(nint hwnd) => !ignored.Contains(hwnd) && TopLevelWindows.IsTaskbarCandidate(hwnd);

    // Must run on a message-pumping thread (WPF dispatcher): WINEVENT_OUTOFCONTEXT
    // delivers callbacks via the registering thread's message queue.
    public Result Start() =>
        Result.Try(() =>
        {
            // One hook per range: SHOW..HIDE+DESTROY lifecycle, NAMECHANGE for renames.
            hooks.Add(Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE));
            hooks.Add(Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE));
            // Its own hook, not a widened range: EVENT_SYSTEM_FOREGROUND sits at 0x0003
            // while the object events are at 0x800x, so one hook covering both would
            // subscribe us to every accessibility event in between.
            //
            // Widened by exactly eight events to take in EVENT_SYSTEM_MOVESIZEEND at 0x000B,
            // which is how the bar learns a window was dragged to another monitor. The range
            // between them is the menu/alert/scroll family -- quiet, and cheaper to filter out
            // in the switch below than to justify a third hook for.
            hooks.Add(Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MOVESIZEEND));
            if (hooks.Any(h => h == 0)) throw new InvalidOperationException("SetWinEventHook failed");
            Snapshot().ToList().ForEach(w => known[w.Handle.Value] = w); // seed before events flow
        }, e => $"Window monitoring unavailable: {e.Message}");

    public IReadOnlyList<WindowInfo> Snapshot()
    {
        var commandLines = WindowInfoFactory.AllCommandLines(); // one WMI query, not one per window
        return TopLevelWindows.Enumerate()
            .Where(h => !ignored.Contains(h)) // EnumWindows never skipped our own process; only the WinEvent hooks did
            .Select(h => WindowInfoFactory.FromHwnd(h, commandLines))
            .Where(m => m.HasValue).Select(m => m.Value)
            .ToList();
    }

    // Only reports a window we actually track, so the highlight can never point at something
    // that has no row (the shell, a tooltip, a non-taskbar helper window).
    public Maybe<WindowHandle> Foreground()
    {
        var hwnd = GetForegroundWindow();
        return hwnd != 0 && known.ContainsKey(hwnd) ? new WindowHandle(hwnd) : Maybe<WindowHandle>.None;
    }

    // WINEVENT_SKIPOWNPROCESS is deliberately NOT passed any more -- see Ignore() above for
    // why (Petre could not see the TaskSpaces window in his own bar). Our chrome opts out by
    // hwnd instead, which is precise; the flag was a blunt instrument that also hid the one
    // window he wanted to see.
    // The window list DRIFTS, and until now nothing ever put it back.
    //
    // Evidence, from Petre's report that YouTube Music had no icon in his Personal row: the
    // row was EMPTY. A fresh Snapshot() taken at that moment found both Obsidian and YouTube
    // Music sitting on Personal's desktop, and every other row in the bar matched that
    // snapshot exactly -- Messaging 2, Work 3, TaskSpace 2, Sparrow 3. So the two windows
    // were not mis-grouped and their icons were not failing to load. They had fallen out of
    // our bookkeeping, and nothing was ever going to bring them back.
    //
    // How that happens. `known` only ever GAINS a window from a WinEvent and only ever loses
    // one to HIDE or DESTROY, and both halves of that are leaky:
    //   * HIDE does not mean gone. A tray-minimise fires it, and so does the shell for its
    //     own reasons. The window keeps existing, and once flagged hidden it stays flagged
    //     until a SHOW arrives -- which a window sitting quietly on another virtual desktop
    //     never fires, because desktop switches CLOAK windows rather than showing them.
    //   * WINEVENT_OUTOFCONTEXT events are delivered through this thread's message queue, so
    //     they can simply be dropped when the queue is busy. A dropped SHOW means a window
    //     that never existed as far as the app is concerned.
    //
    // Hence a periodic reconcile against what the OS actually lists, on the same 5s
    // safety-net timer that already re-asserts drifted titles (App.OnStartup) -- the pattern
    // this codebase already uses for exactly this class of problem: events are the fast path,
    // the sweep is the truth.
    //
    // Cheap in the steady state: one EnumWindows and nothing else. No WMI query happens
    // unless something genuinely new turned up, because TryAppear is what does the per-window
    // lookup and it returns immediately for windows already known.
    public void Resync()
    {
        var live = TopLevelWindows.Enumerate().Where(hwnd => !ignored.Contains(hwnd)).ToList();

        // Recovered: listed by the OS but missing from `known`, or flagged hidden despite
        // being visible again. TryAppear handles both, and no-ops for the ones already fine.
        live.ForEach(TryAppear);

        // Gone for real. Deliberately NOT "absent from `live`": a window minimised to the
        // tray is absent from the taskbar-candidate list while still existing, and reporting
        // that as Disappeared would make WorkspaceManager forget its rename ledger entry --
        // the exact Finding 3 defect the HIDE handling above was written to avoid. IsWindow
        // is the unambiguous question, so only DESTROY-equivalent losses are reported and a
        // missed HIDE can never be mistaken for a close.
        known.Keys.Where(hwnd => !IsWindow(hwnd)).ToList().ForEach(hwnd =>
        {
            if (!known.Remove(hwnd, out var gone)) return;
            hidden.Remove(hwnd);
            events.OnNext(new WindowEvent(WindowEventKind.Disappeared, gone));
        });
    }

    nint Hook(uint min, uint max) =>
        SetWinEventHook(min, max, 0, callback, 0, 0, WINEVENT_OUTOFCONTEXT);

    void OnWinEvent(nint hook, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == 0) return;
        // Cheapest possible rejection of our own chrome, before any P/Invoke or dictionary
        // work. Necessary now that the hooks no longer skip our process: the floating bar
        // repaints and re-titles constantly, and every one of those events would otherwise
        // walk this switch.
        if (ignored.Contains(hwnd)) return;

        // Raised for every foreground change BEFORE the switch below filters to windows we
        // track: the taskbar and the Start menu are exactly the activations the floating bar
        // needs to hear about, and neither is a window we would ever track. Our own chrome is
        // already excluded by the line above, so the bar is never told to raise itself over
        // a click on itself.
        if (@event == EVENT_SYSTEM_FOREGROUND) foreground.OnNext(hwnd);

        // INVARIANT: no managed exception may ever escape a native callback. OnWinEvent is
        // invoked directly by user32 via the SetWinEventHook out-of-context callback — an
        // unhandled exception here unwinds through native stack frames and takes down the
        // whole process, not just this monitor. Swallow anything unexpected and log it;
        // losing one event is far better than crashing the app that's hosting the monitor.
        try
        {
            switch (@event)
            {
                case EVENT_OBJECT_SHOW:
                    TryAppear(hwnd);
                    break;

                // Finding 3 (reviewer, Important): HIDE on a window we still track — and
                // haven't already flagged hidden — does NOT mean the window is gone. Apps
                // that minimize to the tray (Discord, Outlook, ...) fire HIDE while the
                // window (and its hwnd) keep existing; only DESTROY means "gone for real",
                // handled separately below. Emitting Disappeared here (as this code used to)
                // made WorkspaceManager forget the rename ledger's original-title entry, so a
                // later re-show would permanently mistake our own short name for the
                // original. `known` deliberately keeps the entry — DESTROY still needs it.
                // Note: moving a window to another virtual desktop CLOAKS it (DWM), it does
                // not fire HIDE — so our own desktop moves never produce a false Hidden either.
                case EVENT_OBJECT_HIDE when known.TryGetValue(hwnd, out var w) && hidden.Add(hwnd):
                    events.OnNext(new WindowEvent(WindowEventKind.Hidden, w));
                    break;

                // DESTROY always means gone for real, whether or not HIDE preceded it (some
                // apps close directly without hiding first). Remove from both trackers and
                // emit Disappeared with the last-known WindowInfo — the hwnd can no longer
                // be queried at this point.
                case EVENT_OBJECT_DESTROY when known.Remove(hwnd, out var gone):
                    hidden.Remove(hwnd);
                    events.OnNext(new WindowEvent(WindowEventKind.Disappeared, gone));
                    break;

                // Title changed on a window we track -> updated snapshot. WindowRenamer's own
                // WM_SETTEXT also lands here; WorkspaceManager breaks the loop by comparing titles.
                case EVENT_OBJECT_NAMECHANGE when known.TryGetValue(hwnd, out var tracked):
                    var updated = tracked with { Title = WindowInfoFactory.TitleOf(hwnd) };
                    if (updated.Title == tracked.Title) break; // spurious NAMECHANGE, ignore
                    known[hwnd] = updated;
                    events.OnNext(new WindowEvent(WindowEventKind.TitleChanged, updated));
                    break;

                // A window can become taskbar-worthy late (title set only after SHOW).
                case EVENT_OBJECT_NAMECHANGE:
                    TryAppear(hwnd);
                    break;

                // Focus moved to a window we track. Only tracked windows qualify: foreground
                // also lands on things that are not taskbar candidates at all, and our own
                // chrome is filtered at the top of this method, so clicking the bar itself
                // never clears the highlight. Focus moving to an UNtracked window emits
                // nothing, deliberately — the highlight then stays on the last real window,
                // which is the useful answer to "which window am I in".
                case EVENT_SYSTEM_FOREGROUND when known.TryGetValue(hwnd, out var activated):
                    events.OnNext(new WindowEvent(WindowEventKind.Activated, activated));
                    break;

                // Focus landed on a window we do not know YET. Adopt it first, then activate --
                // exactly the late-arrival fallback EVENT_OBJECT_NAMECHANGE has above, which
                // this arm was missing.
                //
                // Why it matters: `known` is populated by SHOW, and a SHOW dropped by the busy
                // message queue means the guard on the arm above fails silently. The window then
                // has no row AND cannot take the highlight, and the next 5s Resync re-adopts it
                // as Appeared -- which fixes the missing row but NOT the highlight, because
                // Appeared says nothing about focus and the activation that would have said so
                // is long gone. So the bar kept pointing at whatever was focused before.
                //
                // TryAppear is the right gate rather than a bare OnNext: it applies IsTracked,
                // so the taskbar and the Start menu still adopt nothing and still emit nothing,
                // and the highlight still stays put when focus goes somewhere untracked.
                case EVENT_SYSTEM_FOREGROUND:
                    TryAppear(hwnd);
                    if (known.TryGetValue(hwnd, out var adopted))
                        events.OnNext(new WindowEvent(WindowEventKind.Activated, adopted));
                    break;

                // The user finished dragging or resizing a window. Petre: "when i drag a window
                // to another monitor, it doesn't show the hairline separator until i switch to
                // another workspace."
                //
                // Nothing is re-queried here and nothing is decided -- the window's monitor is
                // read fresh by ScreenLayout on every overview build, so all that was ever
                // missing was a reason to build one. This is that reason and nothing more.
                case EVENT_SYSTEM_MOVESIZEEND when known.TryGetValue(hwnd, out var moved):
                    events.OnNext(new WindowEvent(WindowEventKind.Moved, moved));
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"WindowMonitor.OnWinEvent swallowed an exception for hwnd={hwnd}, event=0x{@event:X}: {e}");
        }
    }

    // Appeared, deduplicated: SHOW fires repeatedly for the same hwnd. A hwnd we'd
    // previously flagged Hidden is the one exception to "known == ignore this SHOW": it
    // came back, so clear the hidden flag and re-announce it as Appeared (Finding 3).
    void TryAppear(nint hwnd)
    {
        if (hidden.Remove(hwnd))
        {
            // Re-query first (title/process may have changed while hidden); fall back to
            // the last-known snapshot if the hwnd is suddenly unqueryable, and give up
            // silently only if we have neither — it's gone again already.
            WindowInfoFactory.FromHwnd(hwnd)
                .Or(() => known.TryGetValue(hwnd, out var last) ? Maybe<WindowInfo>.From(last) : Maybe<WindowInfo>.None)
                .Tap(info =>
                {
                    known[hwnd] = info;
                    events.OnNext(new WindowEvent(WindowEventKind.Appeared, info));
                });
            return;
        }
        if (known.ContainsKey(hwnd) || !IsTracked(hwnd)) return;
        WindowInfoFactory.FromHwnd(hwnd).Tap(info =>
        {
            known[hwnd] = info;
            events.OnNext(new WindowEvent(WindowEventKind.Appeared, info));
        });
    }

    public void Dispose()
    {
        hooks.ForEach(h => UnhookWinEvent(h));
        events.OnCompleted();
        foreground.OnCompleted();
    }
}

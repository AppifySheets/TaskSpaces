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
    // CRITICAL: the delegate must be kept alive in a field. If the GC collects it,
    // the hook silently dies — the classic SetWinEventHook bug.
    readonly WinEventProc callback;
    readonly List<nint> hooks = [];

    public WindowMonitor() => callback = OnWinEvent;

    public IObservable<WindowEvent> Events => events.AsObservable();

    // Must run on a message-pumping thread (WPF dispatcher): WINEVENT_OUTOFCONTEXT
    // delivers callbacks via the registering thread's message queue.
    public Result Start() =>
        Result.Try(() =>
        {
            // One hook per range: SHOW..HIDE+DESTROY lifecycle, NAMECHANGE for renames.
            hooks.Add(Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE));
            hooks.Add(Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE));
            if (hooks.Any(h => h == 0)) throw new InvalidOperationException("SetWinEventHook failed");
            Snapshot().ToList().ForEach(w => known[w.Handle.Value] = w); // seed before events flow
        }, e => $"Window monitoring unavailable: {e.Message}");

    public IReadOnlyList<WindowInfo> Snapshot() =>
        TopLevelWindows.Enumerate()
            .Select(WindowInfoFactory.FromHwnd)
            .Where(m => m.HasValue).Select(m => m.Value)
            .ToList();

    nint Hook(uint min, uint max) =>
        SetWinEventHook(min, max, 0, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

    void OnWinEvent(nint hook, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == 0) return;

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
        if (known.ContainsKey(hwnd) || !TopLevelWindows.IsTaskbarCandidate(hwnd)) return;
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
    }
}

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

                // Note: moving a window to another virtual desktop CLOAKS it (DWM), it does
                // not fire HIDE — so our own desktop moves never produce false Disappeared.
                case EVENT_OBJECT_DESTROY or EVENT_OBJECT_HIDE when known.Remove(hwnd, out var gone):
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

    // Appeared, deduplicated: SHOW fires repeatedly for the same hwnd.
    void TryAppear(nint hwnd)
    {
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

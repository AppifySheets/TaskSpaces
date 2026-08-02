using System.IO;

namespace TaskSpaces.App;

// TEMPORARY diagnostic trace for the "drag DOWN onto a lower group works, drag UP onto
// a higher group doesn't" bug report (Petre, live testing, 2026-08-02). The failure is
// a real-mouse/real-window interaction that cannot be reproduced headless in this
// environment, so instead of guessing further this writes a plain-text breadcrumb trail
// to %TEMP%\taskspaces-dnd.log that Petre can tail (`Get-Content -Wait`) while
// reproducing it — the next repro's log tells us which hypothesis (stale mouse capture,
// a stale dragStart, or the ScrollViewer having no auto-scroll) actually fired.
//
// Const flag, NOT `#if DEBUG`: Petre reproduces this in the day-to-day Release build,
// not a Debug session, so `#if DEBUG` would compile the trace out of the exact build
// that needs it. Flip Enabled to false (or delete this file and its call sites in
// WindowGroupsView.xaml.cs) once the bug is diagnosed — this is scaffolding, not a
// permanent feature.
static class DnDTrace
{
    // `static readonly`, not `const`: a `const bool` here folds `if (!Enabled) return;`
    // into compile-time-dead code and trips CS0162 ("unreachable code") — the build's
    // 0-warnings bar would force choosing between the warning and the early-return
    // guard. `static readonly` is exactly as trivial to flip (still one line) without
    // the compiler treating the flag as a compile-time constant.
    static readonly bool Enabled = true;

    static readonly string path = Path.Combine(Path.GetTempPath(), "taskspaces-dnd.log");
    static readonly object gate = new();

    // DragOver fires on every mouse-move while a drag is over a drop target — dozens of
    // events per second. Logging each one would flood the file within a second of
    // dragging and bury the one thing worth seeing (which group the drag is over).
    // This holds the last-logged target so LogTargetChange below only writes a line
    // when the cursor actually crosses into a different group.
    static string? lastTarget;

    public static void Log(string message)
    {
        if (!Enabled) return;
        lock (gate)
        {
            // Reviewer (Task 11 fix round 1, Important): catch (Exception), not just
            // IOException -- UnauthorizedAccessException (e.g. a locked/permission-
            // denied %TEMP% file) does NOT derive from IOException and would otherwise
            // propagate straight out of Log(), which runs on the dispatcher thread
            // inside live drag handlers. This trace is deliberately ON in the Release
            // build Petre uses day-to-day, and App's DispatcherUnhandledException
            // handler intentionally lets crashes die -- so a narrower catch here would
            // crash the app mid-drag over a diagnostic write failure. Never-crash
            // discipline: logging must never be able to break the feature it's tracing.
            try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch (Exception) { /* best-effort diagnostic; never let logging break a drag */ }
        }
    }

    public static void LogTargetChange(string target, string effects)
    {
        if (!Enabled || target == lastTarget) return;
        lastTarget = target;
        Log($"DragOver -> '{target}' (effects={effects})");
    }

    // Called on drag-start and on Drop so the next drag's first DragOver always logs,
    // even if it happens to land back on the group the previous drag ended on.
    public static void ResetTarget() => lastTarget = null;
}

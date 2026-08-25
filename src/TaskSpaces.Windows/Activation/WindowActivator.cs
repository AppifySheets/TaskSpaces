using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.Windows.Activation;

using static NativeMethods;

// "Bring it to me": restore if minimized, then foreground.
//
// Petre: "ctrl+win+tab now lands on the first window on the left screen", then -- the detail
// that identified this outright -- "notice that the teams window is blinking now", "it's not
// active though, that workspace lost the active window".
//
// A blinking taskbar button IS the failure, not a side effect of one. Windows only grants
// SetForegroundWindow to a process with a claim on the foreground; denied, it does not return an
// error, it flashes the button instead. This used to be documented here as acceptable
// best-effort -- "called from a click inside OUR focused panel, which is exactly the situation
// where Windows grants permission" -- and that was true of the only caller it had.
//
// The workspace switcher then became a caller and broke the assumption without tripping
// anything. Its picker is deliberately ShowActivated="False" (it appears WHILE the chord is
// physically held, and taking focus there would hand the app underneath a stray modifier state),
// so at the moment the restore runs our process has no foreground claim at all. Every
// keyboard-driven restore was being refused.
//
// Traced rather than guessed. From the probe log, leaving f69c9222 for f60a8bea:
//
//   restore arriving=f69c9222 fallback=F0626      <- we asked for Edge on the arriving desktop
//   record  active=1045C desktopOf=f60a8bea       <- ...and the active window never became it
//
// The cascade off that one refusal is the whole of what Petre reported: focus never moves, so
// activeWindow stays naming a window on the desktop we left, so the next stamp is a foreign
// window that RecordLastActive's DesktopOf check correctly refuses, so nothing is remembered, so
// the next arrival falls back -- and the fallback is refused too. The workspace "loses" its
// active window and Windows' own default focus wins, which is the first window on the left
// screen.
public sealed class WindowActivator : IWindowActivator
{
    // Tries the plain call first: when we DO hold the foreground -- a click on the bar, the
    // common case -- it succeeds outright and nothing below runs.
    //
    // Otherwise the input queue of THIS thread is briefly attached to that of whichever thread
    // owns the current foreground window. Windows grants SetForegroundWindow across an attached
    // input queue, so the attachment borrows the incumbent's claim just long enough to hand
    // focus on deliberately. This is the documented mechanism for a transfer the user actually
    // asked for, which a workspace switch is -- Petre pressed the chord.
    //
    // Verified by READING the foreground back rather than by trusting the return value:
    // SetForegroundWindow can return true having only flashed the button, so its bool cannot
    // tell success from the exact failure this exists to fix.
    public Result Activate(WindowHandle window) =>
        Result.Try(() =>
        {
            if (IsIconic(window.Value)) ShowWindowAsync(window.Value, SW_RESTORE);
            SetForegroundWindow(window.Value);
            if (GetForegroundWindow() != window.Value) ForceForeground(window.Value);
        }, e => $"Could not activate window {window.Value}: {e.Message}");

    // Ask a whole row's worth of windows to close, then wait once for them all rather than once
    // each: they close in parallel, and a per-window wait would turn a workspace of ten windows
    // into ten waits end to end.
    //
    // WM_CLOSE, posted. That is the same request the X in the title bar makes, so an app with
    // unsaved work does what it always does -- puts a dialog up and stays -- and this reports it as
    // a survivor instead of taking the window from under it. Nothing here kills a process.
    //
    // A dead handle needs no special case: PostMessage to it fails, IsWindow already says false,
    // and the window is not a survivor. Which is why the return value of the post is ignored --
    // "the window is already gone" and "the post worked" are the same outcome here.
    //
    // It BLOCKS the calling thread, which is the bar's UI thread, and that is deliberate rather than
    // overlooked: the caller has just been told what will close and has agreed to it, so the honest
    // thing is for the bar to be busy until it has happened. Handing the wait to a background thread
    // would buy a responsive bar and then have to come back to the UI thread anyway, because
    // removing the desktop afterwards is a COM call that cannot be made anywhere else.
    //
    // The budget is generous compared with the 300ms move-settling wait elsewhere in the app,
    // because this waits on more than one process's UI thread and on apps that flush state on the
    // way out. A browser with many tabs is the case that pushed it up. Longer would start to feel
    // like a hang for the one thing this cannot help with anyway: an app that has decided to ask a
    // question and will sit there until it is answered.
    public IReadOnlyList<WindowHandle> CloseAll(IReadOnlyList<WindowHandle> windows)
    {
        windows.ToList().ForEach(w => PostMessage(w.Value, WM_CLOSE, 0, 0));

        for (var waited = 0; waited < CloseBudgetMs && windows.Any(w => IsWindow(w.Value)); waited += CloseStepMs)
            Thread.Sleep(CloseStepMs);

        return windows.Where(w => IsWindow(w.Value)).ToList();
    }

    const int CloseBudgetMs = 2000, CloseStepMs = 25;

    static void ForceForeground(nint window)
    {
        var incumbent = GetForegroundWindow();
        if (incumbent == 0) return;

        var theirs = GetWindowThreadProcessId(incumbent, out _);
        var ours = GetCurrentThreadId();
        // Attaching a thread to ITSELF is an error, and the guard is not theoretical: when the
        // foreground window is one of our own (the Manage window, say) both ids are this thread.
        if (theirs == ours) return;

        // try/finally rather than two plain calls: an exception between them would leave this
        // thread's input queue permanently attached to another process's, which outlives the
        // mistake and makes our UI hostage to theirs.
        AttachThreadInput(ours, theirs, true);
        try
        {
            SetForegroundWindow(window);
        }
        finally
        {
            AttachThreadInput(ours, theirs, false);
        }
    }

    // SW_MINIMIZE, emphatically NOT SW_HIDE -- a hidden window can be orphaned by a crash with
    // no way back, which is the reason this codebase is built on virtual desktops in the first
    // place (see the spec). A minimized window is still on the taskbar and still on its desktop;
    // it has simply been put down.
    //
    // Async, like the restore above: ShowWindowAsync posts rather than waiting for the target's
    // message loop to answer, so a hung window cannot block the bar's click handler.
    //
    // No IsIconic guard. Minimizing an already-minimized window is a no-op in Windows, and the
    // one caller only reaches this for the FOCUSED window, which by definition is not minimized.
    public Result Minimize(WindowHandle window) =>
        Result.Try(() => ShowWindowAsync(window.Value, SW_MINIMIZE),
            e => $"Could not minimize window {window.Value}: {e.Message}");

    // The live answer, read at the moment somebody needs it rather than remembered from a sweep.
    // Same call Activate uses above to decide whether to restore, which is what keeps the two
    // halves of the bar's toggle talking about the same fact.
    public bool IsMinimized(WindowHandle window) => IsIconic(window.Value);
}

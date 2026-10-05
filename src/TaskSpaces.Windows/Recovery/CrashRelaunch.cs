using System.Diagnostics;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Windows.Recovery;

// The app starting its own successor when it crashes, because Windows turned out not to.
//
// Petre: "taskspaces terminated and didn't restart". CrashRestart asks Windows Error Reporting to
// bring the app back after a crash, and on his machine WER never does: it is switched off
// machine-wide (HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\Disabled = 1), the kind of
// thing a privacy or debloat tool sets and that many other machines will share. LocalDumps still
// writes dumps with WER off, which is why the evidence kept arriving while the restarts never did.
// Measured, not inferred: a probe registered for restart, lived 70 seconds and died three ways (an
// unhandled exception in a timer tick, one inside a wndproc that SetWindowPos called, and FailFast
// from the dispatcher handler), and none of the three came back.
//
// So the crash handler starts the next copy itself. It still tells a crash from the tray's Exit,
// which was CrashRestart's whole argument against a watchdog: only the crash handler ever calls this,
// so a deliberate exit stays final with no extra state about the app's life kept anywhere.
//
// WHAT THIS DOES NOT DO is release the single-instance mutex for the successor. The dying copy keeps
// the hotkey, the control pipe and the mutex until the process is really gone, and LocalDumps took
// about four seconds per dump on Petre's machine. A successor let in before then would fail to
// register the hotkey and say so in a dialog. It is started with Switch instead, and waits for the
// mutex to become free (see App.OnStartup), so the order is decided by the OS reclaiming the dead
// process rather than by timing.
public static class CrashRelaunch
{
    // Tells the successor it was started by a crash: wait for the dying copy to let go, rather than
    // reporting "already running" on its first look at the mutex.
    public const string Switch = "--after-crash";

    // WER's own rule, copied rather than invented: a copy that died inside its first minute may be
    // dying of something in startup, and relaunching that would turn one crash into a loop. Past the
    // minute, the deaths seen so far are the memory kind, and a fresh process recovers from those.
    public static readonly TimeSpan MinimumLife = TimeSpan.FromSeconds(60);

    public static bool IsWorthwhile(TimeSpan lived) => lived >= MinimumLife;

    // Result rather than an exception, because the caller is a crash handler running in a process
    // that is already failing, often out of memory: a launch that cannot happen must come back as
    // something to fall back on (the old message box), never as a second throw from inside the first.
    //
    // UseShellExecute off: CreateProcess straight away, with no shell or COM involved, which is the
    // least a process low on memory can be asked to do. The mutex is not created inheritable, so the
    // child does not inherit the handle that would keep the mutex alive.
    public static Result Start(string? exePath, TimeSpan lived) =>
        !IsWorthwhile(lived)
            ? Result.Failure($"not relaunching: this copy lived {lived.TotalSeconds:N0}s, under the {MinimumLife.TotalSeconds:N0}s that rules out a startup crash loop")
            : exePath is null
                ? Result.Failure("not relaunching: the process has no exe path to start")
                : Result.Try(
                    () => Process.Start(new ProcessStartInfo(exePath, Switch) { UseShellExecute = false })?.Dispose(),
                    e => $"could not start the next copy: {e.Message}");
}

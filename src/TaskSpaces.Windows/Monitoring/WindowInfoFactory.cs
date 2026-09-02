using System.Diagnostics;
using System.Management;
using System.Text;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Windows.Monitoring;

using static NativeMethods;

// hwnd -> immutable WindowInfo snapshot. Anything can vanish between calls
// (window closed, process exited), so the whole thing is Maybe, not exceptions.
public static class WindowInfoFactory
{
    public static Maybe<WindowInfo> FromHwnd(nint hwnd, IReadOnlyDictionary<uint, string>? commandLines = null)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return Maybe<WindowInfo>.None;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = TryPath(process);
            // Command line for EVERY window now (roster identity is path+args, not just browser
            // profiles); the startup snapshot passes a prefetched batch instead of querying here.
            //
            // The line above this one used to read "Per-event single WMI lookup ~10ms -- fine at
            // human window-opening rates". MEASURED on Petre's machine while chasing #51: 680ms,
            // and not merely on first use -- four consecutive per-pid queries cost 680/656/628/675.
            // A whole-table query for all 720 processes costs 720ms, which is to say the per-pid
            // WHERE clause buys nothing at all.
            //
            // That number is spent on the DISPATCHER THREAD, inside the WinEvent callback, every
            // time any window appears anywhere on the machine. See TryCommandLine.
            // With a batch in hand (the startup snapshot), a miss is worth ONE more try -- the fast
            // one. WMI has already been asked about every process at that point, so falling all the
            // way through to TryCommandLine would re-ask it per window and pay 656ms for an answer
            // it has already declined to give.
            var commandLine = commandLines is not null
                ? commandLines.GetValueOrDefault(pid) ?? ProcessCommandLine.TryRead(pid)
                : TryCommandLine(pid);
            return new WindowInfo(new WindowHandle(hwnd), (int)pid, process.ProcessName, path, TitleOf(hwnd), commandLine);
        }
        catch (ArgumentException) { return Maybe<WindowInfo>.None; } // process already gone
    }

    // One WMI round-trip for ALL processes -- the startup snapshot enumerates dozens of
    // windows; per-window queries there would cost seconds on the dispatcher thread.
    //
    // BUDGETED, and that is the fix for Petre's "the bar is stuck, i can see but it's stuck" after a
    // resume from hibernation. His Winmgmt had not come back: a probe measured this very query still
    // running after 90 seconds. This call is made on the dispatcher thread before the bar has drawn
    // anything, so a wedged service meant an app that never finished starting -- and restarting it,
    // the obvious cure, hung in exactly the same place.
    //
    // A miss costs nothing that matters: every caller falls back to ProcessCommandLine per window,
    // which reads the command line out of the process itself in about 0.1ms and answered for 18 of 19
    // windows when the two were measured against each other (#59). The batch is a shortcut, not the
    // source of truth.
    public static IReadOnlyDictionary<uint, string> AllCommandLines() =>
        Budgeted(StartupBudget, () =>
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process")
                    { Options = Bounded(StartupBudget) };
                return (IReadOnlyDictionary<uint, string>)searcher.Get().Cast<ManagementBaseObject>()
                    .Where(o => o["CommandLine"] is string { Length: > 0 })
                    .ToDictionary(o => (uint)o["ProcessId"], o => (string)o["CommandLine"]);
            })
        ?? new Dictionary<uint, string>();

    // Two seconds at startup and a fifth of a second per window, and the asymmetry is deliberate: the
    // startup query answers for every process at once and happens before anything is on screen, while
    // the per-window one runs inside a WinEvent callback and is paid again for every window that
    // appears. Both are far above what a healthy service takes (a whole-table query measured 720ms on
    // Petre's machine, a per-pid one 656ms) and far below what a sick one costs.
    static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(2);
    static readonly TimeSpan PerWindowBudget = TimeSpan.FromMilliseconds(200);

    // Shared, because the point of the breaker is that ONE blown budget stops the next call: WMI is a
    // single service, and a window appearing is not a reason to re-test a service that just failed to
    // answer the last question.
    static readonly WmiBreaker Breaker = new();

    // Ask WMI for something, or give up. Null means "no answer": the service is in its cooldown, the
    // call blew its budget, or it threw.
    //
    // The budget is enforced from OUTSIDE the call as well as inside it (see Bounded), because WMI's
    // own timeout applies to enumerating results and a wedged service can hang before there are any --
    // which is precisely what happened here. A call that overruns is abandoned on its thread-pool
    // thread rather than cancelled, since there is nothing in the API to cancel; it ends when the
    // service finally answers or throws, and its result is discarded.
    // Func<T?> rather than Func<T>, and T? throughout: the per-window caller's query legitimately
    // answers "no command line" for a process WMI knows nothing about, so the lambda's own return is
    // nullable. Written as Func<T> it inferred T = string?, which breaks the `class` constraint and
    // fails the release build, where warnings are errors (-warnaserror) even though a Debug build says
    // nothing. That is how it reached a published tag with no executable attached.
    static T? Budgeted<T>(TimeSpan budget, Func<T?> ask) where T : class
    {
        if (!Breaker.ShouldAsk(DateTimeOffset.UtcNow)) return null;

        var call = Task.Run(ask);
        if (!call.Wait(budget))
        {
            Breaker.TimedOut(DateTimeOffset.UtcNow);
            // Observed so an abandoned call that later faults cannot come back as an unobserved task
            // exception on the finalizer thread.
            call.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }

        if (call.IsFaulted) return null; // best-effort metadata, as it always was
        Breaker.Answered();
        return call.Result;
    }

    // WMI's own bound on the work, so a call that beats the wrapper's budget still cannot sit in the
    // service for minutes. ReturnImmediately makes Get() semi-synchronous, which is what allows the
    // timeout to apply at all.
    // Fully qualified: System.IO has a type of the same name and this file reads directories nowhere.
    static System.Management.EnumerationOptions Bounded(TimeSpan budget) =>
        new() { Timeout = budget, ReturnImmediately = true, Rewindable = false };

    public static string TitleOf(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    // Elevated processes deny module access to non-elevated callers -- expected, not an error.
    static string? TryPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    static string? TryCommandLine(uint pid)
    {
        // Petre: "the new workspace / rename dialog takes a few seconds before the textbox
        // appears." Traced to here, and the chain is short once the numbers are real:
        //
        //   * PromptDialog is a top-level, visible, titled window, so IsTaskbarCandidate says yes
        //     -- ownership is not parentage, and ShowInTaskbar="False" does not set
        //     WS_EX_TOOLWINDOW (see FloatingBar.OnSourceInitialized, which had to learn the same
        //     thing about Alt+Tab).
        //   * Showing it therefore fires our own EVENT_OBJECT_SHOW, and the hook runs TryAppear on
        //     the DISPATCHER THREAD -- the same thread that has to render the dialog.
        //   * Which lands here, and costs 680ms. Twice, as the title arrives after the show.
        //
        // From the trace: prompt "Rename workspace" built=6ms rendered=1550ms. The dialog was
        // never slow; it was waiting for its own process to be asked about itself over WMI.
        //
        // Our own command line needs no service at all. This is the whole fix for that report, and
        // it is exact rather than approximate: Environment.CommandLine IS this process's command
        // line, which is more than WMI can promise for anyone else's.
        if (pid == (uint)Environment.ProcessId) return Environment.CommandLine;

        // Read from the process itself before asking WMI, because the difference is not small
        // (#59). Measured on Petre's machine over the 19 processes that owned a visible window:
        //
        //     WMI   656ms each   12,468ms for all 19
        //     PEB     0.1ms each        2ms for all 19
        //
        // ...with zero disagreements -- both answered for 18 of the 19, byte for byte, and the one
        // neither could read was the same protected process on both sides.
        //
        // WMI stays as the fallback rather than being deleted. It answers as SYSTEM, so it can
        // still speak for an elevated process that ProcessCommandLine cannot open, and paying
        // 656ms for one of those is a fair price now that ordinary windows never reach it.
        if (ProcessCommandLine.TryRead(pid) is { Length: > 0 } fromProcess) return fromProcess;

        // Budgeted and breakered, like the startup batch above and for the same morning's reason: this
        // runs on the dispatcher thread inside a WinEvent callback, so a WMI that does not answer is a
        // bar that does not move. Only elevated and protected processes reach this line at all, the
        // PEB read above having answered for everything else.
        return Budgeted(PerWindowBudget, () =>
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}")
                { Options = Bounded(PerWindowBudget) };
            return searcher.Get().Cast<ManagementBaseObject>().FirstOrDefault()?["CommandLine"] as string;
        });
    }
}

namespace TaskSpaces.Windows.Monitoring;

// "Has WMI just failed to answer, and if so, leave it alone for a while."
//
// Petre, after resuming from hibernation: "the bar is stuck, i can see but it's stuck." It was, and
// the cause was outside the app. Measured at the time with a throwaway probe:
//
//   WMI whole-table query: STILL RUNNING after 90016ms (wedged)
//
// Winmgmt had not recovered from the resume. TaskSpaces asks it for a process's command line on the
// DISPATCHER THREAD -- once at startup for every process, and per window whenever the fast path
// (ProcessCommandLine, read straight out of the process's memory) is denied. A service that never
// answers therefore freezes the bar: his log shows a 12.4-second rebuild at the moment of resume and
// then the 5-second sweep, the update check and time accrual all silent for the rest of the morning,
// because they share the thread that was waiting. Restarting was no cure: the fresh instance hung in
// the same query before it had drawn anything.
//
// So every WMI call now carries a budget (see WindowInfoFactory), and this decides whether to make
// the call at all. Once one blows its budget the service is presumed sick and left alone, because the
// alternative is paying that budget again for every window that appears.
//
// A COOLDOWN rather than a latch, because the sick state is temporary by nature -- a resume, a
// service restart, a machine under load -- and nothing else in the app would ever notice it
// recovering. And an answer clears it immediately: the cooldown is a guess that the service is
// unwell, and an answer is evidence that it is not.
//
// The command line is best-effort metadata (it feeds roster identity), and the app has swallowed
// WMI's failures since the beginning. Skipping it costs identity accuracy for the few windows whose
// process cannot be read directly; blocking on it costs everything.
public sealed class WmiBreaker
{
    // Long enough that a wedged service is not asked once a second by a busy machine's window events,
    // short enough that a recovered one is picked up without a restart. Public so the tests can say
    // "just before" and "just after" without copying the number.
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    // The moment the current cooldown ends, or null when there is none. A plain field guarded by no
    // lock: it is written from the dispatcher thread and from whichever thread pool thread a budgeted
    // call ran on, and the only harm a race can do is ask WMI once more than intended.
    DateTimeOffset? until;

    public bool ShouldAsk(DateTimeOffset now) => until is not { } deadline || now >= deadline;

    // A call that blew its budget. Restarts the wait rather than extending the old one, so a service
    // that is still wedged is not asked back on the original cadence.
    public void TimedOut(DateTimeOffset now) => until = now + Cooldown;

    public void Answered() => until = null;
}

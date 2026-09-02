using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.Windows.Tests;

// Petre, after bringing the machine out of hibernation: "the bar is stuck, i can see but it's stuck."
//
// It was, and it was WMI. Measured on his machine at the time, with a throwaway probe:
//
//   WMI whole-table query: STILL RUNNING after 90016ms (wedged)
//
// Winmgmt had not recovered from the resume. TaskSpaces asks it for a process's command line, on the
// DISPATCHER THREAD, once at startup for every process and again per window whenever the fast path
// (ProcessCommandLine, straight out of the process's own memory) is denied. So a wedged service
// froze the bar: a 12.4-second rebuild at the moment of resume, then the 5-second sweep, the update
// check and time accrual all silent for the rest of the morning, because the thread they share was
// waiting on a service that never answered. A restart was no cure either -- the fresh instance hung
// in the same query at startup.
//
// Two things follow, and this class is the second. Every WMI call gets a budget, and once one blows
// it, the service is presumed sick and left alone for a while rather than asked again by the next
// window that appears. The command line is best-effort metadata (identity for the roster), so
// skipping it costs a little accuracy for windows we cannot read directly; blocking on it costs the
// whole app.
public class WmiBreakerTests
{
    static readonly DateTimeOffset Noon = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Asks_by_default() => Assert.True(new WmiBreaker().ShouldAsk(Noon));

    [Fact]
    public void Stops_asking_once_a_call_blows_its_budget()
    {
        var breaker = new WmiBreaker();

        breaker.TimedOut(Noon);

        Assert.False(breaker.ShouldAsk(Noon));
    }

    // The cooldown is what makes this a breaker rather than a switch: a service that was wedged after
    // a resume comes back, and nothing else in the app would ever notice.
    [Fact]
    public void Asks_again_once_the_cooldown_has_passed()
    {
        var breaker = new WmiBreaker();
        breaker.TimedOut(Noon);

        Assert.False(breaker.ShouldAsk(Noon + WmiBreaker.Cooldown - TimeSpan.FromSeconds(1)));
        Assert.True(breaker.ShouldAsk(Noon + WmiBreaker.Cooldown));
    }

    // An answer during the cooldown clears it, which is the honest reading of one: the cooldown is a
    // guess about the service being sick, and an answer is evidence that it is not.
    [Fact]
    public void An_answer_clears_the_cooldown()
    {
        var breaker = new WmiBreaker();
        breaker.TimedOut(Noon);

        breaker.Answered();

        Assert.True(breaker.ShouldAsk(Noon));
    }

    // A second timeout restarts the wait rather than letting the first one expire on schedule, or a
    // service that is still wedged would be asked again on the same cadence for ever.
    [Fact]
    public void A_second_timeout_restarts_the_cooldown()
    {
        var breaker = new WmiBreaker();
        breaker.TimedOut(Noon);
        var later = Noon + WmiBreaker.Cooldown - TimeSpan.FromSeconds(1);

        breaker.TimedOut(later);

        Assert.False(breaker.ShouldAsk(later + WmiBreaker.Cooldown - TimeSpan.FromSeconds(1)));
        Assert.True(breaker.ShouldAsk(later + WmiBreaker.Cooldown));
    }
}

using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Time;

// The rule that decides whether a tick counts, kept apart from the timer that produces ticks and
// from the OS call that reports input (#53).
//
// Pure on purpose: "was that fifteen seconds work?" is the entire question this feature turns on,
// and it is answerable with three values and no machine. Every awkward case -- idle, switching,
// midnight, a clock that jumps -- is decided here and tested without a clock, a desktop or a
// window.
public static class ActivityAccrual
{
    // Petre's ruling, and the number that defines what this feature measures. Type, pause 90
    // seconds to read, type again: the reading pause was work and stays counted. Walk away and
    // accrual stops two minutes in.
    //
    // The honest cost, recorded rather than hidden: watching a video without touching anything
    // reads as idle. That is what "active = input" MEANS, and softening it here (say, by counting
    // wall-clock while a media window is focused) would be inventing a second definition to sit
    // beside a stated one.
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(2);

    // How often to ask. Also the granularity of the whole feature: a switch mid-tick attributes
    // that slice to whichever workspace is current when the tick lands, so this IS the error bar,
    // and fifteen seconds of it is not worth more precision.
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    // How much of this tick to credit, and to which day.
    //
    // Returns None -- rather than zero -- for "nothing to record", so a caller cannot accidentally
    // write an empty day into the ledger and make a workspace look visited.
    public static Maybe<(DateOnly Day, TimeSpan Amount)> Slice(TimeSpan sinceLastInput, TimeSpan tickInterval, DateTime now)
    {
        if (sinceLastInput >= IdleAfter) return Maybe<(DateOnly, TimeSpan)>.None;

        // A tick longer than the idle threshold means the timer did not fire when it should have
        // -- the machine slept, or the dispatcher was blocked for minutes. Crediting the whole gap
        // would silently invent hours across a suspend, so the slice is capped at what the
        // threshold can vouch for: input happened within IdleAfter of NOW, and nothing is known
        // about the gap before that.
        var amount = tickInterval > IdleAfter ? IdleAfter : tickInterval;
        if (amount <= TimeSpan.Zero) return Maybe<(DateOnly, TimeSpan)>.None;

        // Attributed to the day the tick LANDS on, whole. A slice straddling midnight is fifteen
        // seconds in the wrong column once a day, which is not worth splitting a tick over.
        return (DateOnly.FromDateTime(now), amount);
    }
}

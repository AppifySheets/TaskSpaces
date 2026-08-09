using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Time;

namespace TaskSpaces.Core.Tests;

// Petre: "track how much active time is spent in each workspace", with the ruling that decides
// everything: "active means keyboard or mouse activity -- pure wall-clock presence does not count."
//
// The whole feature turns on one question -- "was that fifteen seconds work?" -- and it is
// answerable from three values, so all of it is tested without a clock, a desktop or a window.
public class WorkspaceTimeTests
{
    static readonly Guid Work = Guid.NewGuid();
    static readonly Guid Personal = Guid.NewGuid();
    static readonly DateOnly Today = new(2026, 8, 9);
    static readonly DateTime Noon = new(2026, 8, 9, 12, 0, 0);

    // --- the rule ---------------------------------------------------------------------------

    [Fact]
    public void Recent_input_credits_the_whole_tick()
    {
        var slice = ActivityAccrual.Slice(sinceLastInput: TimeSpan.FromSeconds(3), tickInterval: TimeSpan.FromSeconds(15), now: Noon);

        Assert.Equal((Today, TimeSpan.FromSeconds(15)), slice.Value);
    }

    // The reading pause Petre described: type, stop for 90 seconds to read, type again. That pause
    // was work, and a threshold shorter than it would quietly stop counting the thinking parts.
    [Fact]
    public void A_pause_shorter_than_the_threshold_still_counts() =>
        Assert.True(ActivityAccrual.Slice(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(15), Noon).HasValue);

    [Fact]
    public void Idle_credits_nothing() =>
        Assert.True(ActivityAccrual.Slice(ActivityAccrual.IdleAfter, TimeSpan.FromSeconds(15), Noon).HasNoValue);

    // The machine slept, or the dispatcher was blocked for minutes. Crediting the whole gap would
    // invent hours across a suspend; the threshold is the most that recent input can vouch for.
    [Fact]
    public void A_tick_that_arrives_late_credits_no_more_than_the_threshold()
    {
        var slice = ActivityAccrual.Slice(TimeSpan.FromSeconds(3), tickInterval: TimeSpan.FromHours(8), now: Noon);

        Assert.Equal(ActivityAccrual.IdleAfter, slice.Value.Amount);
    }

    // --- the ledger -------------------------------------------------------------------------

    [Fact]
    public void Credits_accumulate_per_workspace_and_day()
    {
        var time = WorkspaceTime.Empty
            .Credit(Work, Today, TimeSpan.FromMinutes(10))
            .Credit(Work, Today, TimeSpan.FromMinutes(5))
            .Credit(Personal, Today, TimeSpan.FromMinutes(2))
            .Credit(Work, Today.AddDays(-1), TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.FromMinutes(15), time.On(Work, Today));
        Assert.Equal(TimeSpan.FromMinutes(2), time.On(Personal, Today));
        Assert.Equal(TimeSpan.FromHours(3), time.On(Work, Today.AddDays(-1)));
        Assert.Equal(TimeSpan.Zero, time.On(Personal, Today.AddDays(-1)));
    }

    // Inclusive at both ends, because every caller asks in human terms ("Monday to today") and an
    // exclusive end is where off-by-one days come from.
    [Fact]
    public void A_range_includes_both_ends()
    {
        var time = WorkspaceTime.Empty
            .Credit(Work, Today.AddDays(-2), TimeSpan.FromHours(1))
            .Credit(Work, Today.AddDays(-1), TimeSpan.FromHours(1))
            .Credit(Work, Today, TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromHours(3), time.Between(Work, Today.AddDays(-2), Today));
        Assert.Equal(TimeSpan.FromHours(2), time.Between(Work, Today.AddDays(-1), Today));
    }

    // A clock that moved backwards is the only way this happens, and silently subtracting somebody's
    // tracked time is a worse answer than ignoring the tick that produced it.
    [Fact]
    public void A_negative_or_empty_credit_changes_nothing()
    {
        var time = WorkspaceTime.Empty.Credit(Work, Today, TimeSpan.FromMinutes(5));

        Assert.Equal(time, time.Credit(Work, Today, TimeSpan.Zero).Credit(Work, Today, TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public void Forgetting_drops_old_days_and_keeps_the_rest()
    {
        var time = WorkspaceTime.Empty
            .Credit(Work, Today.AddDays(-400), TimeSpan.FromHours(1))
            .Credit(Work, Today, TimeSpan.FromHours(2))
            .Credit(Personal, Today.AddDays(-400), TimeSpan.FromHours(1));

        var pruned = time.Forgetting(Today.AddDays(-30));

        Assert.Equal(TimeSpan.FromHours(2), pruned.On(Work, Today));
        Assert.Equal(TimeSpan.Zero, pruned.On(Work, Today.AddDays(-400)));
        // Personal had nothing but old days, so it leaves entirely rather than lingering as an
        // empty row that every reader then has to skip.
        Assert.DoesNotContain(Personal, pruned.ByWorkspace.Keys);
    }

    // --- the tracker ------------------------------------------------------------------------

    sealed class FakeInput : IInputActivity
    {
        public TimeSpan Idle { get; set; }
        public TimeSpan SinceLastInput() => Idle;
    }

    sealed class FakeTimeStore : ITimeStore
    {
        public WorkspaceTime Stored { get; set; } = WorkspaceTime.Empty;
        public int Saves { get; private set; }
        public Result<WorkspaceTime> Load() => Stored;
        public Result Save(WorkspaceTime time) { Stored = time; Saves++; return Result.Success(); }
    }

    [Fact]
    public void Ticking_in_a_workspace_credits_it()
    {
        var input = new FakeInput { Idle = TimeSpan.Zero };
        var tracker = new TimeTracker(new FakeTimeStore(), input, () => Noon);
        tracker.Start();

        tracker.Tick(Work, TimeSpan.FromSeconds(15));
        tracker.Tick(Work, TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(30), tracker.Time.On(Work, Today));
    }

    // Standing on a desktop that is not a workspace, which is the ordinary state on Petre's
    // machine -- most of his windows live on one he never named. Time there is attributed to
    // nothing rather than guessed onto a neighbour.
    [Fact]
    public void Ticking_outside_any_workspace_credits_nothing()
    {
        var tracker = new TimeTracker(new FakeTimeStore(), new FakeInput(), () => Noon);
        tracker.Start();

        tracker.Tick(Maybe<Guid>.None, TimeSpan.FromSeconds(15));

        Assert.Empty(tracker.Time.ByWorkspace);
    }

    [Fact]
    public void An_idle_tick_credits_nothing()
    {
        var tracker = new TimeTracker(new FakeTimeStore(), new FakeInput { Idle = TimeSpan.FromMinutes(5) }, () => Noon);
        tracker.Start();

        tracker.Tick(Work, TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.Zero, tracker.Time.On(Work, Today));
    }

    // A tick is every 15 seconds forever. Rewriting a growing file 240 times an hour to add 15
    // seconds to one row is how you wear out an SSD for no reason.
    [Fact]
    public void Ticks_do_not_each_write_the_file()
    {
        var store = new FakeTimeStore();
        var tracker = new TimeTracker(store, new FakeInput(), () => Noon);
        tracker.Start();

        Enumerable.Range(0, 10).ToList().ForEach(_ => tracker.Tick(Work, TimeSpan.FromSeconds(15)));

        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void Flushing_writes_once_and_only_when_something_changed()
    {
        var store = new FakeTimeStore();
        var tracker = new TimeTracker(store, new FakeInput(), () => Noon);
        tracker.Start();
        tracker.Tick(Work, TimeSpan.FromSeconds(15));

        tracker.Flush();
        tracker.Flush();

        Assert.Equal(1, store.Saves);
        Assert.Equal(TimeSpan.FromSeconds(15), store.Stored.On(Work, Today));
    }

    [Fact]
    public void Starting_loads_what_was_tracked_before()
    {
        var store = new FakeTimeStore { Stored = WorkspaceTime.Empty.Credit(Work, Today, TimeSpan.FromHours(4)) };
        var tracker = new TimeTracker(store, new FakeInput(), () => Noon);

        tracker.Start();

        Assert.Equal(TimeSpan.FromHours(4), tracker.Time.On(Work, Today));
    }
}

using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

// Petre, shown the "Restore workspaces?" prompt for about the fifteenth time in an afternoon:
// "this seems like an overkill".
//
// It was: the only condition was "some workspace has an app that is not running", which is true
// the moment you close anything, so every app restart raised it. The feature is post-REBOOT
// rehydration, and that is the condition that was missing.
public class RestoreOfferTests
{
    static readonly DateTimeOffset Booted = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    // The defect, as a test: we already ran after this boot, so the windows are still on their
    // desktops and there is nothing to rehydrate.
    [Fact]
    public void An_app_restart_within_the_same_session_does_not_offer() =>
        Assert.False(RestoreOffer.ShouldOffer(Booted.AddMinutes(5), Booted));

    // The case the feature exists for: desktops do not survive a reboot, so state.json is the
    // only record of what was where.
    [Fact]
    public void The_first_run_after_a_reboot_offers() =>
        Assert.True(RestoreOffer.ShouldOffer(Booted.AddDays(-1), Booted));

    // Both unknowns resolve to "offer": asking once needlessly costs a dialog, staying silent
    // when it was needed costs a workspace rebuilt by hand.
    [Fact]
    public void A_state_file_that_never_recorded_a_run_offers() =>
        Assert.True(RestoreOffer.ShouldOffer(null, Booted));

    // Exactly at boot time is not "after" it, and cannot prove we already ran.
    [Fact]
    public void A_run_recorded_exactly_at_boot_time_offers() =>
        Assert.True(RestoreOffer.ShouldOffer(Booted, Booted));

    // --- and the recording half, through the manager -------------------------------------

    [Fact]
    public void Start_records_this_run_and_reports_the_previous_one()
    {
        var yesterday = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        var today = new DateTimeOffset(2026, 8, 5, 9, 5, 0, TimeSpan.Zero);
        var store = new FakeStore { Stored = AppState.Empty with { LastRunAt = yesterday } };
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store, () => today);

        Assert.True(manager.Start().IsSuccess);

        Assert.Equal(yesterday, manager.PreviousRunAt); // captured before being overwritten
        Assert.Equal(today, store.Stored.LastRunAt);    // and this run is now on record
    }

    // A first-ever launch has nothing to report, which RestoreOffer reads as "offer".
    [Fact]
    public void A_first_ever_launch_reports_no_previous_run()
    {
        var store = new FakeStore();
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), store);

        Assert.True(manager.Start().IsSuccess);

        Assert.Null(manager.PreviousRunAt);
        Assert.NotNull(store.Stored.LastRunAt);
    }
}

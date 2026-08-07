using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.Windows.Tests;

// The ordering rules are covered purely in MonitorOrderingTests; what CANNOT be covered there is
// whether the P/Invoke underneath actually returns anything. A MONITORINFOEX with a wrong cbSize
// or a missing CharSet.Unicode fails SILENTLY -- GetMonitorInfoEx just returns false, or szDevice
// comes back as garbage -- and the only visible symptom would be badges never appearing, which
// looks identical to the feature being switched off.
//
// This codebase has been bitten by exactly that before (see the probe notes in CLAUDE.md: a
// FindWindowW without CharSet.Unicode silently returned 0 and "proved" whatever was hoped). So
// these run against the real machine and assert the shape of what comes back.
public class ScreenLayoutTests
{
    [Fact]
    public void The_layout_resolves_at_least_one_monitor_for_the_windows_it_finds()
    {
        var facts = new ScreenLayout().Snapshot();

        // A machine running this test has a desktop session, so it has windows and at least one
        // display. If the struct marshalling were wrong, MonitorOf would be empty here.
        Assert.NotEmpty(facts.ZOrder);
        Assert.NotEmpty(facts.MonitorOf);
    }

    // Display numbers come out of "\\.\DISPLAYn", so they start at 1. A 0 would mean the parse
    // found no digits and fell through to something meaningless.
    [Fact]
    public void Every_resolved_monitor_number_is_a_real_display_number()
    {
        var facts = new ScreenLayout().Snapshot();

        Assert.All(facts.MonitorOf.Values, number => Assert.True(number >= 1, $"display number {number} should be >= 1"));
    }

    // The main-monitor fallback (WorkspaceManager.FrontmostOnMainMonitor) is dead code without
    // this, and silently so: a dwFlags read that never matched would simply mean the fallback
    // never fires, which looks exactly like "there was nothing to restore".
    //
    // Deliberately NOT asserted to be display 1. On the setup this was built against the primary
    // is DISPLAY2, which is the whole reason "main monitor" is asked of Windows rather than
    // assumed.
    [Fact]
    public void Windows_reports_which_display_is_primary()
    {
        var facts = new ScreenLayout().Snapshot();

        Assert.True(facts.PrimaryMonitor.HasValue, "no display reported itself as primary");
        Assert.Contains(facts.PrimaryMonitor.Value, facts.MonitorOf.Values);
    }

    // Z-order is an index into one enumeration, so it must be dense and start at 0 -- if it were
    // not, "smallest index wins" (how the front-most window per monitor is chosen) would be
    // comparing positions from different enumerations.
    [Fact]
    public void Z_order_is_a_dense_front_to_back_ranking()
    {
        var facts = new ScreenLayout().Snapshot();

        Assert.Equal(Enumerable.Range(0, facts.ZOrder.Count), facts.ZOrder.Values.OrderBy(i => i));
    }
}

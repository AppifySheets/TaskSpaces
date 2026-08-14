using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// #151, the storage half. Petre: "make things configurable, like dimming opacity, timeout, etc."
//
// The two behaviours worth pinning are the ones with consequences beyond the tab: a change must pulse,
// because that pulse is what applies it to the live bar, and a NON-change must not, because the tab
// sets every control from state when Manage opens and would otherwise rewrite state.json and rebuild
// the bar every time the window is opened.
public class BarAppearanceTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    [Fact]
    public void The_four_values_are_stored()
    {
        var manager = Started();

        manager.SetBarAppearance(0.5, 20, 2000, 300);

        Assert.Equal(0.5, manager.State.BarIdleOpacity);
        Assert.Equal(20, manager.State.BarFadeGraceSeconds);
        Assert.Equal(2000, manager.State.BarFadeDurationMs);
        Assert.Equal(300, manager.State.HoverDwellMs);
    }

    [Fact]
    public void They_are_persisted()
    {
        var manager = Started();

        manager.SetBarAppearance(0.5, 20, 2000, 300);

        Assert.Equal(0.5, store.Stored.BarIdleOpacity);
        Assert.Equal(300, store.Stored.HoverDwellMs);
    }

    // The pulse IS the live apply: the bar reads these values on every use and redraws on this signal.
    [Fact]
    public void A_change_pulses_so_the_bar_can_apply_it()
    {
        var manager = Started();
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        manager.SetBarAppearance(0.5, 20, 2000, 300);

        Assert.Equal(1, pulses);
    }

    // Opening Manage sets four controls from state, which raises four change events with the values
    // already stored. Without this guard that would be a write and a bar rebuild per window opening.
    [Fact]
    public void Setting_the_same_values_again_writes_nothing()
    {
        var manager = Started();
        manager.SetBarAppearance(0.5, 20, 2000, 300);
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        manager.SetBarAppearance(0.5, 20, 2000, 300);

        Assert.Equal(0, pulses);
    }

    // Null is the way back to the default for each value independently, and for the dwell it means
    // something stronger: inherit Windows' own hover time.
    [Fact]
    public void Nulls_clear_the_stored_values()
    {
        var manager = Started();
        manager.SetBarAppearance(0.5, 20, 2000, 300);

        manager.SetBarAppearance(null, null, null, null);

        Assert.Null(manager.State.BarIdleOpacity);
        Assert.Null(manager.State.BarFadeGraceSeconds);
        Assert.Null(manager.State.BarFadeDurationMs);
        Assert.Null(manager.State.HoverDwellMs);
    }

    [Fact]
    public void Clearing_an_already_clear_setting_writes_nothing()
    {
        var manager = Started();
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        manager.SetBarAppearance(null, null, null, null);

        Assert.Equal(0, pulses);
    }

    // A slider cannot produce an out-of-range value, and a slider is a UI rather than a guarantee.
    // This is also the door a future surface comes through.
    [Fact]
    public void Values_are_clamped_on_the_way_in()
    {
        var manager = Started();

        manager.SetBarAppearance(5, -10, 1, 99999);

        Assert.Equal(BarFading.Maximum, manager.State.BarIdleOpacity);
        Assert.Equal(BarFading.GraceMinimumSeconds, manager.State.BarFadeGraceSeconds);
        Assert.Equal(BarFading.DurationMinimumMs, manager.State.BarFadeDurationMs);
        Assert.Equal(HoverDwelling.MaximumMs, manager.State.HoverDwellMs);
    }

    // A number that is not a number clears the setting rather than being stored, so a hand-edited file
    // or a future caller cannot poison a value the bar reads on every fade.
    [Fact]
    public void A_dwell_that_is_not_a_real_number_clears_the_setting()
    {
        var manager = Started();
        manager.SetBarAppearance(null, null, null, 300);

        manager.SetBarAppearance(null, null, null, double.NaN);

        Assert.Null(manager.State.HoverDwellMs);
    }

    // One control moving must not reset the other three, which is what a per-setting writer would risk
    // and what taking the whole set makes impossible.
    [Fact]
    public void Changing_one_value_leaves_the_others_alone()
    {
        var manager = Started();
        manager.SetBarAppearance(0.5, 20, 2000, 300);

        manager.SetBarAppearance(0.8, 20, 2000, 300);

        Assert.Equal(0.8, manager.State.BarIdleOpacity);
        Assert.Equal(20, manager.State.BarFadeGraceSeconds);
        Assert.Equal(2000, manager.State.BarFadeDurationMs);
        Assert.Equal(300, manager.State.HoverDwellMs);
    }

    // A state.json written before any of these keys existed loads with all four unset, which is what
    // makes "null means the default" a no-migration story.
    [Fact]
    public void A_state_file_without_these_keys_loads_unset()
    {
        var manager = Started();

        Assert.Null(manager.State.BarFadeGraceSeconds);
        Assert.Null(manager.State.BarFadeDurationMs);
        Assert.Null(manager.State.HoverDwellMs);
    }
}

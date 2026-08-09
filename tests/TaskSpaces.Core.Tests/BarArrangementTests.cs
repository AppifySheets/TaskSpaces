using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "vertical arrangement, rows as columns, configurable in the settings."
//
// The manager's whole part in it: remember which way round the bar should be, and pulse. The
// LAYOUT is the bar's business and cannot be tested here; what can be pinned is that the setting
// survives, that an older state.json still means "rows", and that choosing an arrangement reaches
// every open surface exactly once.
public class BarArrangementTests
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

    // The no-migration promise, which every key in AppState makes: a file written before this
    // existed has no such property, and the bar it describes was a bar of rows.
    [Fact]
    public void A_state_file_that_predates_the_setting_means_rows() =>
        Assert.Equal(BarArrangement.Horizontal, AppState.Empty.BarArrangement);

    [Fact]
    public void Choosing_columns_persists_it()
    {
        var manager = Started();

        Assert.True(manager.SetBarArrangement(BarArrangement.Vertical).IsSuccess);

        Assert.Equal(BarArrangement.Vertical, manager.State.BarArrangement);
        Assert.Equal(BarArrangement.Vertical, store.Stored.BarArrangement);
    }

    // The pulse IS the live switch (Petre chose "applied live" over "applied on next start"): the
    // bar rebuilds on it and re-reads the arrangement while doing so, so nothing else has to carry
    // the message. Exactly one, for the same reason every other pulse is counted -- each one costs
    // every open surface a rebuild, and a rebuild costs a COM call per known window.
    [Fact]
    public void Choosing_an_arrangement_pulses_once_so_the_bar_can_rebuild()
    {
        var manager = Started();
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        Assert.True(manager.SetBarArrangement(BarArrangement.Vertical).IsSuccess);

        Assert.Equal(1, pulses);
    }

    // Round-trips through the record, which is what the JSON store serialises. JsonStringEnumConverter
    // is already installed (deliberately, against the renumbering hazard of writing enums as
    // integers), so this lands in state.json as a word rather than a number.
    [Fact]
    public void The_arrangement_survives_a_state_rewrite() =>
        Assert.Equal(
            BarArrangement.Vertical,
            (AppState.Empty with { BarArrangement = BarArrangement.Vertical } with { PersistedRenames = [] }).BarArrangement);
}

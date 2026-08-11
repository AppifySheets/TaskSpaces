using System.Windows.Controls;
using CSharpFunctionalExtensions;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Time;

namespace TaskSpaces.Windows.Tests;

// The Time tab (#53), and the two things Petre asked of it after living with it: "workspace should
// have group name; time should be sortable correctly, not by text."
//
// Against the real window rather than a view model, because both asks are about the GRID: one is a
// column that has to exist, and the other is which property a column sorts by. A view-model test
// would pass with the columns still bound the old way.
public class ManageTimeTabTests
{
    sealed class FakeInput : IInputActivity
    {
        public TimeSpan SinceLastInput() => TimeSpan.Zero; // never idle, so every tick counts
    }

    sealed class FakeTimeStore : ITimeStore
    {
        public WorkspaceTime Stored { get; set; } = WorkspaceTime.Empty;
        public Result<WorkspaceTime> Load() => Stored;
        public Result Save(WorkspaceTime time) { Stored = time; return Result.Success(); }
    }

    static readonly Guid GroupId = Guid.NewGuid();
    static readonly Workspace Grouped = new(Guid.NewGuid(), "slip39", Guid.NewGuid()) { GroupId = GroupId };
    static readonly Workspace Alone = new(Guid.NewGuid(), "Personal", Guid.NewGuid());

    // A tracker holding a deliberately awkward pair of durations: 47 minutes and 1 hour 45, which
    // format as "47m" and "1h 45m" and therefore sort the WRONG way round as text.
    //
    // SEEDED through the store rather than accrued with ticks, and that is not shortcut: Tick caps
    // one call at ActivityAccrual.IdleAfter, two minutes, because a longer interval means the machine
    // slept and crediting the gap would be a lie. Reaching 1h 45m through the front door would take
    // 53 ticks and would be testing the accrual rules, which WorkspaceTimeTests already does.
    //
    // Today comes from DateTime.Now because the WINDOW reads the real clock, not an injected one.
    static (WorkspaceManager Manager, TimeTracker Tracker) Built()
    {
        var desktops = new PulsingDesktops { CurrentId = Grouped.DesktopId!.Value };
        new[] { Grouped, Alone }.ToList()
            .ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));

        var store = new StubStore
        {
            Stored = AppState.Empty with
            {
                Workspaces = [Grouped, Alone],
                Groups = [new Group(GroupId, "Sparrow")],
            },
        };

        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var times = new FakeTimeStore
        {
            Stored = WorkspaceTime.Empty
                .Credit(Grouped.Id, today, TimeSpan.FromMinutes(105)) // 1h 45m
                .Credit(Alone.Id, today, TimeSpan.FromMinutes(47)),   // 47m
        };

        var tracker = new TimeTracker(times, new FakeInput(), () => DateTime.Now);
        tracker.Start();
        return (manager, tracker);
    }

    static DataGrid Grid(ManageWindow window) => (DataGrid)window.FindName("TimeGrid")!;

    static IReadOnlyList<ManageWindow.TimeRow> Rows(ManageWindow window) =>
        Grid(window).ItemsSource.Cast<ManageWindow.TimeRow>().ToList();

    // Petre: "workspace should have group name."
    [Fact]
    public void A_grouped_workspace_names_its_group_and_a_lone_one_does_not() => StaThread.Run(() =>
    {
        var (manager, tracker) = Built();
        var window = new ManageWindow(manager, compatibilityMode: false, tracker);

        var rows = Rows(window);
        Assert.Equal("Sparrow", rows.Single(r => r.Workspace == "slip39").Group);
        Assert.Equal("", rows.Single(r => r.Workspace == "Personal").Group);

        // And it is on screen, not merely in the row: a property no column shows is not the ask.
        Assert.Contains("Group", Grid(window).Columns.Select(c => (string)c.Header));

        window.Close();
    });

    // Petre: "time should be sortable correctly, not by text." Every duration column must sort by a
    // TimeSpan, and this asserts the paths rather than the rendering, because the paths are what a
    // future edit would quietly drop.
    [Fact]
    public void Every_duration_column_sorts_by_its_duration() => StaThread.Run(() =>
    {
        var (manager, tracker) = Built();
        var window = new ManageWindow(manager, compatibilityMode: false, tracker);

        var sorts = Grid(window).Columns
            .Where(c => ((string)c.Header) is "Today" or "This week" or "Last 30 days")
            .Select(c => c.SortMemberPath)
            .ToList();

        Assert.Equal(["TodayTime", "ThisWeekTime", "LastMonthTime"], sorts);

        window.Close();
    });

    // The bug itself, pinned: as TEXT these two are in the wrong order, and as durations they are in
    // the right one. If the columns are ever re-bound to the formatted strings, the assertion above
    // fails and this one explains why it mattered.
    [Fact]
    public void Sorting_as_text_is_wrong_where_sorting_as_a_duration_is_right() => StaThread.Run(() =>
    {
        var (manager, tracker) = Built();
        var window = new ManageWindow(manager, compatibilityMode: false, tracker);

        var rows = Rows(window);
        var busiest = rows.Single(r => r.Workspace == "slip39");   // 1h 45m
        var quieter = rows.Single(r => r.Workspace == "Personal"); // 47m

        Assert.Equal("1h 45m", busiest.Today);
        Assert.Equal("47m", quieter.Today);

        // Text: "1h 45m" < "47m", because '1' sorts before '4'. Which is what the grid was doing.
        Assert.True(string.CompareOrdinal(busiest.Today, quieter.Today) < 0);
        // Duration: the busy workspace really is the larger one.
        Assert.True(busiest.TodayTime > quieter.TodayTime);

        window.Close();
    });
}

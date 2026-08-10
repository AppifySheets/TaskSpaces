using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

// #94, the walk up the process chain. Petre: "if an app starts another app -- VS Code opening the
// browser via a clicked link -- the started app's window should be moved to the same workspace as the
// app that started it."
//
// The chains here are the ones MEASURED on Petre's machine while this was designed, not invented, and
// they are the reason the walk exists at all: reading the parent pid alone answers nothing for the
// headline case and answers "explorer" for nearly everything else.
public class LaunchedByTests
{
    readonly Dictionary<int, ProcessFacts> tree = [];
    readonly HashSet<int> windowed = [];

    // Builds a chain child-first: Chain(("chrome", 1), ("node", 2), ("Code", 3)) makes 1's parent 2 and
    // 2's parent 3.
    void Chain(params (string Name, int Pid)[] chain) =>
        chain.Select((p, i) => (p, parent: i + 1 < chain.Length ? chain[i + 1].Pid : 0)).ToList()
            .ForEach(x => tree[x.p.Pid] = new ProcessFacts(x.p.Pid, x.p.Name, x.parent));

    Maybe<ProcessFacts> Lookup(int pid) => tree.TryGetValue(pid, out var facts) ? facts : Maybe<ProcessFacts>.None;

    Maybe<int> Launcher(int pid) => LaunchedBy.Launcher(pid, Lookup, windowed.Contains);

    // The headline case, measured: a browser opened from inside VS Code sits seven hops below the VS
    // Code window, every one of them a windowless helper.
    [Fact]
    public void The_launcher_is_the_nearest_ancestor_that_owns_a_window()
    {
        Chain(("chrome", 1), ("node", 2), ("cmd", 3), ("node", 4), ("cmd", 5), ("claude", 6), ("Code", 7), ("Code", 8), ("explorer", 9));
        windowed.Add(8);

        Assert.Equal(8, Launcher(1));
    }

    // The whole reason the shell is excluded. Almost everything a person launches by hand is a child of
    // explorer, and explorer owns File Explorer windows, so without this every app started from the
    // taskbar would be placed wherever a folder window happened to be sitting.
    [Fact]
    public void The_shell_is_not_a_launcher()
    {
        Chain(("Beeper", 1), ("explorer", 2));
        windowed.Add(2);

        Assert.False(Launcher(1).HasValue);
    }

    // Same rule one hop further out: reaching the shell ENDS the walk rather than being skipped, so a
    // File Explorer window cannot be found by going around it.
    [Fact]
    public void The_walk_stops_at_the_shell_rather_than_passing_through_it()
    {
        Chain(("chrome", 1), ("node", 2), ("explorer", 3), ("Code", 4));
        windowed.Add(4);

        Assert.False(Launcher(1).HasValue);
    }

    [Theory]
    [InlineData("sihost")]
    [InlineData("svchost")]
    [InlineData("services")]
    [InlineData("StartMenuExperienceHost")]
    public void System_hosts_are_not_launchers_either(string host)
    {
        Chain(("WhatsApp", 1), (host, 2), ("Code", 3));
        windowed.Add(2);
        windowed.Add(3);

        Assert.False(Launcher(1).HasValue);
    }

    // The name test is case-insensitive, because the name comes from a file path and Windows does not
    // promise its casing.
    [Fact]
    public void The_shell_is_recognised_whatever_its_casing()
    {
        Chain(("app", 1), ("EXPLORER", 2));
        windowed.Add(2);

        Assert.False(Launcher(1).HasValue);
    }

    // A real app-started-app pair from the same measurement: a WebView window whose parent is the app
    // that owns the window.
    [Fact]
    public void A_direct_parent_that_owns_a_window_is_the_launcher()
    {
        Chain(("msedgewebview2", 1), ("WhatsApp.Root", 2), ("sihost", 3));
        windowed.Add(2);

        Assert.Equal(2, Launcher(1));
    }

    // A parent that has exited, which the measurement also found: several windowed apps on the machine
    // had parents already gone. IProcessTree reports no parent for those, and the walk stops.
    [Fact]
    public void A_chain_that_runs_out_has_no_launcher()
    {
        Chain(("ms-teams", 1));

        Assert.False(Launcher(1).HasValue);
    }

    [Fact]
    public void A_process_that_cannot_be_read_has_no_launcher() =>
        Assert.False(Launcher(999).HasValue);

    // Bounded, because this runs on the dispatcher thread inside a WinEvent callback. A chain longer
    // than the cap gives up rather than walking to the root of the machine.
    [Fact]
    public void The_walk_gives_up_rather_than_climbing_for_ever()
    {
        var chain = Enumerable.Range(1, LaunchedBy.MaxHops + 5).Select(i => ($"helper{i}", i)).ToArray();
        Chain(chain);
        windowed.Add(chain[^1].Item2);

        Assert.False(Launcher(1).HasValue);
    }

    // Cannot arise from real parentage, but the pids arrive from arithmetic this code does not own, and
    // an endless walk would freeze the bar rather than fail.
    [Fact]
    public void A_loop_in_the_chain_terminates()
    {
        tree[1] = new ProcessFacts(1, "a", 2);
        tree[2] = new ProcessFacts(2, "b", 1);

        Assert.False(Launcher(1).HasValue);
    }

    // The launcher must own a window, not merely exist: a windowless helper is what the app you
    // launched from used to do the launching, and the question is which app that was.
    [Fact]
    public void An_ancestor_with_no_window_is_not_the_launcher()
    {
        Chain(("chrome", 1), ("node", 2), ("cmd", 3));

        Assert.False(Launcher(1).HasValue);
    }
}

using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class PersistedRenameTests
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

    static WindowInfo Chrome(nint hwnd = 0x10, string title = "Some Page - Chrome") =>
        new(new WindowHandle(hwnd), 100, "chrome", @"C:\chrome.exe", title, null);

    [Fact]
    public void Manual_rename_is_persisted_with_the_original_title()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));

        Assert.True(manager.RenameWindow(new WindowHandle(0x10), "Amy related").IsSuccess);

        var persisted = store.Stored.PersistedRenames.Single();
        Assert.Equal(("chrome", "Some Page - Chrome", "Amy related"), (persisted.ProcessName, persisted.OriginalTitle, persisted.ShortName));
    }

    [Fact]
    public void Restore_removes_the_persisted_rename()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        manager.RenameWindow(new WindowHandle(0x10), "Amy related");

        Assert.True(manager.RestoreTitle(new WindowHandle(0x10)).IsSuccess);

        Assert.Empty(store.Stored.PersistedRenames);
    }

    [Fact]
    public void Rule_based_renames_are_not_persisted_as_manual_entries()
    {
        var manager = Started();
        manager.SetRules([], [new RenameRule(RuleMatchKind.ProcessName, "chrome", "Amy related")]);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        Assert.Empty(store.Stored.PersistedRenames); // the rule itself is already persistent
    }

    [Fact]
    public void After_restart_a_matching_window_gets_its_persisted_rename_back()
    {
        // Session 1: rename, then the app "exits" (manager discarded, store survives).
        var first = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        first.RenameWindow(new WindowHandle(0x10), "Amy related");
        titles.Titles.Clear();

        // Session 2: same store; the window is already open with its NATURAL title
        // (RestoreAllTitles put it back on exit), so it arrives via the snapshot.
        var monitor2 = new FakeMonitor();
        monitor2.InitialWindows.Add(Chrome(title: "Some Page - Chrome"));
        var second = new WorkspaceManager(desktops, monitor2, titles, store);
        Assert.True(second.Start().IsSuccess);

        Assert.Equal("Amy related", titles.Titles[new WindowHandle(0x10)]);
    }

    [Fact]
    public void After_restart_a_window_whose_title_drifted_stays_untouched()
    {
        var first = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        first.RenameWindow(new WindowHandle(0x10), "Amy related");
        titles.Titles.Clear();

        var monitor2 = new FakeMonitor();
        monitor2.InitialWindows.Add(Chrome(title: "Completely Different Page - Chrome"));
        var second = new WorkspaceManager(desktops, monitor2, titles, store);
        Assert.True(second.Start().IsSuccess);

        Assert.Empty(titles.Titles); // identity heuristic failed -> hands off (spec's known limit)
    }

    [Fact]
    public void Sweep_reapplies_a_drifted_title_even_without_an_event()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        manager.RenameWindow(new WindowHandle(0x10), "Amy related");

        // The app rewrote its title but the NAMECHANGE event was missed entirely:
        // only the OS-side title (what titles.Get returns) shows the drift.
        titles.Titles[new WindowHandle(0x10)] = "Drifted Page - Chrome";

        manager.ReapplyRenames();

        Assert.Equal("Amy related", titles.Titles[new WindowHandle(0x10)]);
    }

    [Fact]
    public void RestoreAllTitles_preserves_persisted_renames_for_restart()
    {
        // Session 1: rename window, then exit (app calls RestoreAllTitles)
        var first = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Chrome()));
        first.RenameWindow(new WindowHandle(0x10), "Amy related");

        // Verify persisted rename was recorded
        var persistedBefore = store.Stored.PersistedRenames.Single();
        Assert.Equal("Amy related", persistedBefore.ShortName);

        // App exit: RestoreAllTitles restores the original title but must NOT delete the
        // persisted entry — the durable record survives so renames re-apply at next start.
        first.RestoreAllTitles();

        // Persisted rename still in store (not wiped by exit-time restoration)
        var persistedAfter = store.Stored.PersistedRenames;
        Assert.Single(persistedAfter);
        Assert.Equal("Amy related", persistedAfter.Single().ShortName);

        // Session 2: restart with same store and same window (returned to natural title)
        var monitor2 = new FakeMonitor();
        monitor2.InitialWindows.Add(Chrome(title: "Some Page - Chrome")); // back to original
        var second = new WorkspaceManager(desktops, monitor2, titles, store);
        Assert.True(second.Start().IsSuccess);

        // Persisted rename was re-applied at startup
        Assert.Equal("Amy related", titles.Titles[new WindowHandle(0x10)]);
    }
}

using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

// Petre: "i've renamed remote desktop manager to RDP yesterday, today it's still the original
// name, why?"
//
// Because an exact-title rename cannot survive that app. His state.json recorded
// OriginalTitle "Remote Desktop Manager [_Richard - fhd]" while the window today reads
// "Remote Desktop Manager [i7-petre]" -- RDM puts the current session in its title, so
// ReapplyRenames' exact-match adoption could never fire again. The first test below IS that
// defect, reproduced; the rest cover renaming by APP, which is keyed on the process name and
// therefore immune to any title rewrite.
public class RenameAppTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    static WindowInfo Rdm(string title) =>
        new(new WindowHandle(0x601), 800, "RemoteDesktopManager", @"C:\apps\RDM.exe", title, null);

    static WindowInfo SecondRdm(string title) =>
        new(new WindowHandle(0x602), 800, "RemoteDesktopManager", @"C:\apps\RDM.exe", title, null);

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // The reported defect, as it actually happens: rename, restart, and the app has moved on
    // to a different session in its title.
    [Fact]
    public void An_exact_title_rename_lapses_when_the_app_rewrites_its_title()
    {
        store.Stored = AppState.Empty with
        {
            PersistedRenames = [new PersistedRename("RemoteDesktopManager", "Remote Desktop Manager [_Richard - fhd]", "RDP")],
        };
        monitor.InitialWindows.Add(Rdm("Remote Desktop Manager [i7-petre]")); // a different session today
        var manager = Started();

        manager.ReapplyRenames();

        Assert.Empty(titles.Titles); // nothing adopted it, and nothing ever will
    }

    // The same situation, with the rename recorded against the APP instead.
    [Fact]
    public void An_app_rename_survives_the_app_rewriting_its_title()
    {
        monitor.InitialWindows.Add(Rdm("Remote Desktop Manager [_Richard - fhd]"));
        var manager = Started();

        Assert.True(manager.RenameApp(Rdm("").Handle, "RDP").IsSuccess);
        Assert.Equal("RDP", titles.Titles[Rdm("").Handle]); // applied at once, not on the next event

        // A fresh session tomorrow: new handle, new title, same process.
        var tomorrow = new WorkspaceManager(desktops, new FakeMonitor(), titles, store, ownProcessId: 4242);
        monitor.InitialWindows.Clear();
        Assert.True(tomorrow.LoadState().IsSuccess);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Rdm("Remote Desktop Manager [i7-petre]")));

        // The rule is what carries across, so re-run it the way OnAppeared would.
        Assert.Equal("RDP", RulesEngine.MatchRename(Rdm("Remote Desktop Manager [i7-petre]"), tomorrow.State.RenameRules).Value);
    }

    [Fact]
    public void An_app_rename_covers_every_window_of_that_app()
    {
        monitor.InitialWindows.Add(Rdm("Remote Desktop Manager [_Richard - fhd]"));
        monitor.InitialWindows.Add(SecondRdm("Remote Desktop Manager [i7-petre]"));
        var manager = Started();

        Assert.True(manager.RenameApp(Rdm("").Handle, "RDP").IsSuccess);

        Assert.Equal("RDP", titles.Titles[Rdm("").Handle]);
        Assert.Equal("RDP", titles.Titles[SecondRdm("").Handle]);
    }

    [Fact]
    public void It_is_stored_as_a_process_name_rule()
    {
        monitor.InitialWindows.Add(Rdm("Remote Desktop Manager [_Richard - fhd]"));
        var manager = Started();

        Assert.True(manager.RenameApp(Rdm("").Handle, "RDP").IsSuccess);

        var rule = store.Stored.RenameRules.Single();
        Assert.Equal(RuleMatchKind.ProcessName, rule.Kind);
        Assert.Equal("RemoteDesktopManager", rule.Pattern);
        Assert.Equal("RDP", rule.ShortName);
    }

    // Otherwise renaming the same app twice would leave a rule that can never match sitting in
    // the list -- exactly how PersistedRenames became a graveyard of 18 dead entries.
    [Fact]
    public void Renaming_the_same_app_again_replaces_the_rule_rather_than_stacking_one()
    {
        monitor.InitialWindows.Add(Rdm("Remote Desktop Manager [_Richard - fhd]"));
        var manager = Started();

        Assert.True(manager.RenameApp(Rdm("").Handle, "RDP").IsSuccess);
        Assert.True(manager.RenameApp(Rdm("").Handle, "Remote").IsSuccess);

        Assert.Equal("Remote", store.Stored.RenameRules.Single().ShortName);
    }

    // Two records disagreeing about one app is a bug waiting to surface, and the app-wide rule
    // is unambiguously the newer intent.
    [Fact]
    public void An_app_rename_clears_the_superseded_exact_title_renames_for_that_app()
    {
        store.Stored = AppState.Empty with
        {
            PersistedRenames =
            [
                new PersistedRename("RemoteDesktopManager", "Remote Desktop Manager [_Richard - fhd]", "RDP"),
                new PersistedRename("RemoteDesktopManager", "Remote Desktop Manager [i7-petre]", "RDP"),
                new PersistedRename("Beeper", "Beeper | HRIS", "Beeper"), // another app: untouched
            ],
        };
        monitor.InitialWindows.Add(Rdm("Remote Desktop Manager [_Richard - fhd]"));
        var manager = Started();

        Assert.True(manager.RenameApp(Rdm("").Handle, "RDP").IsSuccess);

        Assert.Equal("Beeper", store.Stored.PersistedRenames.Single().ProcessName);
    }

    [Fact]
    public void A_blank_name_is_refused()
    {
        monitor.InitialWindows.Add(Rdm("Remote Desktop Manager [_Richard - fhd]"));
        var manager = Started();

        Assert.True(manager.RenameApp(Rdm("").Handle, "   ").IsFailure);
        Assert.Empty(store.Stored.RenameRules);
    }

    // The same carve-out every other rename path has: TaskSpaces never retitles itself, and a
    // rule keyed on OUR process would otherwise fight WPF for the Manage window's Title.
    [Fact]
    public void Renaming_our_own_app_writes_no_title()
    {
        var ours = new WindowInfo(new WindowHandle(0x1), 4242, "TaskSpaces.App", @"C:\apps\TaskSpaces.App.exe", "TaskSpaces: Manage", null);
        monitor.InitialWindows.Add(ours);
        var manager = Started();

        manager.RenameApp(ours.Handle, "nope");

        Assert.Empty(titles.Titles);
    }
}

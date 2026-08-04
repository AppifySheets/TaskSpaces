using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

// Petre: "i'm starting the edge browser and it immediately goes to personal, i'm starting it
// in messaging, why?"
//
// Because membership identity for a Chromium browser is the PROFILE, so every Default-profile
// Edge window shares one identity. One of his had been dragged to Personal; AddEntry strips an
// identity from every other workspace so exactly one can claim it; and from then on Personal
// owned every Edge window he would ever open. His state.json showed exactly that -- Personal
// holding one msedge entry with "--profile-directory=Default --restart --restore-last-session".
//
// The rule these tests pin: placement memory restores where an app lives when it comes BACK,
// so it must not herd additional windows of an app that is already open.
public class SharedIdentityPlacementTests
{
    const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    const string BeeperPath = @"C:\apps\Beeper.exe";

    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeStore store = new();

    Workspace personal = null!;
    Workspace messaging = null!;

    static WindowInfo Edge(nint hwnd, string args = "--profile-directory=Default") =>
        new(new WindowHandle(hwnd), 900, "msedge", EdgePath, "Edge", $"\"{EdgePath}\" {args}");

    static WindowInfo Beeper(nint hwnd) =>
        new(new WindowHandle(hwnd), 950, "Beeper", BeeperPath, "Beeper", $"\"{BeeperPath}\" ");

    WorkspaceManager Started(params WindowInfo[] alreadyOpen)
    {
        personal = new Workspace(Guid.NewGuid(), "Personal", Guid.NewGuid());
        messaging = new Workspace(Guid.NewGuid(), "Messaging", Guid.NewGuid());
        desktops.Desktops.Add(new Abstractions.DesktopInfo(personal.DesktopId!.Value, "Personal"));
        desktops.Desktops.Add(new Abstractions.DesktopInfo(messaging.DesktopId!.Value, "Messaging"));
        desktops.CurrentDesktopId = messaging.DesktopId!.Value; // Petre is IN Messaging
        store.Stored = AppState.Empty with { Workspaces = [personal, messaging] };
        monitor.InitialWindows.AddRange(alreadyOpen);

        var manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // Personal claims the shared Edge identity, exactly as his file did.
    void GivenPersonalOwnsEdge(WorkspaceManager manager, WindowInfo edge)
    {
        Assert.True(manager.AssignWindow(edge.Handle, personal.Id).IsSuccess);
        Assert.Equal(personal.DesktopId, desktops.WindowPlacements[edge.Handle]);
    }

    // The reported defect. A second Edge window opened while in Messaging must stay there.
    [Fact]
    public void A_new_browser_window_is_not_dragged_to_where_another_one_lives()
    {
        var manager = Started(Edge(0x701));
        GivenPersonalOwnsEdge(manager, Edge(0x701));

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Edge(0x702)));

        Assert.False(desktops.WindowPlacements.ContainsKey(Edge(0x702).Handle));
    }

    // The other half of the same rule: with no other window of that identity live, memory is
    // unambiguous and MUST still place it. This is the Beeper case -- it closes to tray,
    // destroying its window, so the replacement is the only one of its identity.
    [Fact]
    public void An_app_coming_back_alone_is_still_placed_from_memory()
    {
        var manager = Started(Beeper(0x801));
        Assert.True(manager.AssignWindow(Beeper(0x801).Handle, personal.Id).IsSuccess);

        // Closed to tray: gone from the live list, roster entry survives (that is the point).
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Beeper(0x801)));
        desktops.WindowPlacements.Clear();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper(0x802)));

        Assert.Equal(personal.DesktopId, desktops.WindowPlacements[Beeper(0x802).Handle]);
    }

    // Startup redistribution has the same problem in bulk: after a reboot Edge restores
    // several windows at once and memory knows ONE workspace for all of them, so applying it
    // would herd the lot.
    [Fact]
    public void Startup_does_not_herd_every_window_of_one_identity_into_one_workspace()
    {
        var manager = Started(Edge(0x701));
        GivenPersonalOwnsEdge(manager, Edge(0x701));

        // Restart with three Edge windows live, none of them on a workspace desktop.
        desktops.WindowPlacements.Clear();
        var reopened = new FakeMonitor();
        reopened.InitialWindows.AddRange([Edge(0x711), Edge(0x712), Edge(0x713)]);
        var restarted = new WorkspaceManager(desktops, reopened, new FakeTitles(), store, ownProcessId: 4242);
        Assert.True(restarted.Start().IsSuccess);

        Assert.Empty(desktops.WindowPlacements);
    }

    // --- identity: a PWA is not the browser -----------------------------------------------

    // Petre's YouTube Music runs as msedge on the Default profile, so on profile alone it was
    // the SAME identity as his ordinary Edge windows -- meaning it could never be remembered
    // anywhere of its own. The app id separates them.
    [Fact]
    public void An_installed_web_app_has_its_own_identity()
    {
        var browser = RosterIdentity.Of(EdgePath, $"\"{EdgePath}\" --profile-directory=Default");
        var youTubeMusic = RosterIdentity.Of(EdgePath, $"\"{EdgePath}\" --profile-directory=Default --app-id=cinhimbnkkaeohfgghhklpknlkffjgod");

        Assert.NotEqual(browser, youTubeMusic);
    }

    [Fact]
    public void Two_windows_of_the_same_web_app_are_still_one_identity()
    {
        var first = RosterIdentity.Of(EdgePath, $"\"{EdgePath}\" --profile-directory=Default --app-id=abc");
        var second = RosterIdentity.Of(EdgePath, $"\"{EdgePath}\" --app-id=abc --profile-directory=Default --restore-last-session");

        Assert.Equal(first, second);
    }

    // A URL shortcut rather than an installed PWA: same idea, --app= instead of --app-id=.
    [Fact]
    public void A_url_shortcut_window_also_gets_its_own_identity()
    {
        var browser = RosterIdentity.Of(EdgePath, $"\"{EdgePath}\" --profile-directory=Default");
        var shortcut = RosterIdentity.Of(EdgePath, $"\"{EdgePath}\" --profile-directory=Default --app=https://music.youtube.com/");

        Assert.NotEqual(browser, shortcut);
    }

    // Different profiles must remain different, as they always were.
    [Fact]
    public void Different_profiles_are_still_different_identities() =>
        Assert.NotEqual(
            RosterIdentity.Of(EdgePath, $"\"{EdgePath}\" --profile-directory=Default"),
            RosterIdentity.Of(EdgePath, $"\"{EdgePath}\" --profile-directory=\"Profile 2\""));
}

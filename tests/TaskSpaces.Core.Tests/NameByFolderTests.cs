using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

// #136. Petre: "that rename to SPS is bad. let's do smart-rename for windows that are in folders."
//
// What was bad about it, precisely, because that is what shapes the feature. A rename rule gives ONE
// name to every window of an app, which is the wrong shape for an editor: seven VS Code windows all
// reading "VSC" say nothing about which is which. His file had gone one worse -- a `Code -> VSC` rule
// and then an exact-title rename of "VSC" to "SPS", a rename keyed on a title the app had already
// written itself, which is the trap RenamePattern's own header predicted.
//
// So a window is named after what it HAS OPEN, and the name changes when that does.
public class NameByFolderTests
{
    const string CodePath = @"C:\Users\p\AppData\Local\Programs\Microsoft VS Code\Code.exe";
    const string RiderPath = @"C:\Program Files\JetBrains\Rider\bin\rider64.exe";

    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    static WindowInfo Code(nint hwnd, string title) =>
        new(new WindowHandle(hwnd), 21176, "Code", CodePath, title, $"\"{CodePath}\" ");

    // JetBrains put the project FIRST and separate with an en dash, which is why one "take the last
    // segment" rule cannot serve both families. Petre asked for Rider by name.
    static WindowInfo Rider(nint hwnd, string title) =>
        new(new WindowHandle(hwnd), 9100, "rider64", RiderPath, title, $"\"{RiderPath}\" ");

    WorkspaceManager Started(params WindowInfo[] alreadyOpen)
    {
        var only = new Workspace(Guid.NewGuid(), "framework", Guid.NewGuid());
        desktops.Desktops.Add(new Abstractions.DesktopInfo(only.DesktopId!.Value, only.Name));
        desktops.CurrentDesktopId = only.DesktopId!.Value;
        store.Stored = AppState.Empty with { Workspaces = [only] };
        monitor.InitialWindows.AddRange(alreadyOpen);

        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static void Appears(FakeMonitor monitor, WindowInfo window) =>
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));

    static void Retitled(FakeMonitor monitor, WindowInfo window) =>
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, window));

    string TitleOf(nint hwnd) => titles.Titles.GetValueOrDefault(new WindowHandle(hwnd), "");

    [Fact]
    public void A_window_is_named_after_the_folder_it_has_open()
    {
        var manager = Started();
        Appears(monitor, Code(0xA01, "WorkspaceManager.cs - TaskSpaces - Visual Studio Code"));

        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA01), on: true).IsSuccess);

        Assert.Equal("TaskSpaces", TitleOf(0xA01));
    }

    // Two windows of ONE app, two different names. This is the whole point, and it is the thing no
    // rename rule can express.
    [Fact]
    public void Two_windows_of_one_app_get_two_different_names()
    {
        var manager = Started();
        Appears(monitor, Code(0xA02, "index.ts - TaskSpaces - Visual Studio Code"));
        Appears(monitor, Code(0xA03, "seed.ts - dice-to-seed - Visual Studio Code"));

        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA02), on: true).IsSuccess);

        Assert.Equal("TaskSpaces", TitleOf(0xA02));
        Assert.Equal("dice-to-seed", TitleOf(0xA03));
    }

    // Petre: "folder name, but only the last part." A no-op for the ordinary case, which is what makes
    // it safe, and it earns its keep when the title carries a path instead of a bare folder name.
    [Fact]
    public void A_path_is_named_by_its_last_part_only()
    {
        var manager = Started();
        Appears(monitor, Code(0xA04, @"seed.ts - C:\Users\p\repos\bitcoin\dice-to-seed - Visual Studio Code"));

        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA04), on: true).IsSuccess);

        Assert.Equal("dice-to-seed", TitleOf(0xA04));
    }

    [Fact]
    public void A_remote_path_with_forward_slashes_is_named_the_same_way() => Assert.Equal(
        "hris", TitleToken.LastPart("/home/petre/src/hris"));

    // Petre asked for Rider specifically, and its title shape is the opposite of VS Code's: the project
    // comes first, separated by an EN dash.
    [Fact]
    public void Rider_is_named_after_its_project()
    {
        var manager = Started();
        Appears(monitor, Rider(0xA05, "TaskSpaces – WorkspaceManager.cs"));

        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA05), on: true).IsSuccess);

        Assert.Equal("TaskSpaces", TitleOf(0xA05));
    }

    // The name is not fixed, which is the one way this differs from every other rename in the app. A
    // rule's name is decided once; this one follows the window.
    [Fact]
    public void Opening_another_folder_renames_the_window_again()
    {
        var manager = Started();
        Appears(monitor, Code(0xA06, "index.ts - TaskSpaces - Visual Studio Code"));
        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA06), on: true).IsSuccess);
        Assert.Equal("TaskSpaces", TitleOf(0xA06));

        Retitled(monitor, Code(0xA06, "seed.ts - dice-to-seed - Visual Studio Code"));

        Assert.Equal("dice-to-seed", TitleOf(0xA06));
    }

    // Our own WM_SETTEXT produces a real NAMECHANGE carrying the name we just set. If that were treated
    // as the app rewriting its title, every rename would set the title again, forever.
    [Fact]
    public void Our_own_echo_does_not_rename_anything_a_second_time()
    {
        var manager = Started();
        Appears(monitor, Code(0xA07, "index.ts - TaskSpaces - Visual Studio Code"));
        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA07), on: true).IsSuccess);

        var setsSoFar = titles.Titles.Count;
        titles.Titles.Remove(new WindowHandle(0xA07)); // so a second write is visible as a re-add

        Retitled(monitor, Code(0xA07, "TaskSpaces")); // the echo

        Assert.DoesNotContain(new WindowHandle(0xA07), titles.Titles.Keys);
        Assert.Equal(setsSoFar - 1, titles.Titles.Count);
    }

    // A bare editor has no folder to be named after, so the app's rule still answers. Without this the
    // window would sit there with its full title until something was opened in it.
    [Fact]
    public void A_window_with_no_folder_open_falls_back_to_the_apps_rule()
    {
        var manager = Started();
        Appears(monitor, Code(0xA08, "Visual Studio Code"));
        Assert.True(manager.RenameApp(new WindowHandle(0xA08), "VSC").IsSuccess);

        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA08), on: true).IsSuccess);

        Assert.Equal("VSC", TitleOf(0xA08));

        // ...and the moment a folder loads, the folder wins.
        Retitled(monitor, Code(0xA08, "index.ts - TaskSpaces - Visual Studio Code"));
        Assert.Equal("TaskSpaces", TitleOf(0xA08));
    }

    // Turning it off has to leave a name someone can explain: the rule's, or the app's own title back.
    [Fact]
    public void Turning_it_off_falls_back_to_the_rule()
    {
        var manager = Started();
        Appears(monitor, Code(0xA09, "index.ts - TaskSpaces - Visual Studio Code"));
        Assert.True(manager.RenameApp(new WindowHandle(0xA09), "VSC").IsSuccess);
        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA09), on: true).IsSuccess);
        Assert.Equal("TaskSpaces", TitleOf(0xA09));

        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA09), on: false).IsSuccess);

        Assert.Equal("VSC", TitleOf(0xA09));
        Assert.False(manager.NamesByFolder("Code"));
    }

    [Fact]
    public void Turning_it_off_with_no_rule_restores_the_apps_own_title()
    {
        var manager = Started();
        Appears(monitor, Code(0xA10, "index.ts - TaskSpaces - Visual Studio Code"));
        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA10), on: true).IsSuccess);
        Assert.Equal("TaskSpaces", TitleOf(0xA10));

        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA10), on: false).IsSuccess);

        Assert.Equal("index.ts - TaskSpaces - Visual Studio Code", TitleOf(0xA10));
    }

    // Restore has to give back the title the APP wrote, not the previous name we wrote. The ledger keeps
    // the first original for exactly this, and a folder rename must not overwrite it.
    [Fact]
    public void Restoring_a_title_gives_back_what_the_app_wrote()
    {
        var manager = Started();
        Appears(monitor, Code(0xA11, "index.ts - TaskSpaces - Visual Studio Code"));
        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA11), on: true).IsSuccess);
        Retitled(monitor, Code(0xA11, "seed.ts - dice-to-seed - Visual Studio Code"));
        Assert.Equal("dice-to-seed", TitleOf(0xA11));

        Assert.True(manager.RestoreTitle(new WindowHandle(0xA11)).IsSuccess);

        Assert.Equal("index.ts - TaskSpaces - Visual Studio Code", TitleOf(0xA11));
    }

    // The setting is per app, and it is only ever offered for apps whose title shape is known. An app
    // not on that list has no container to be named after, so the item is absent rather than inert.
    [Fact]
    public void Only_apps_with_a_known_title_shape_can_be_named_by_folder()
    {
        Assert.True(TitleToken.Knows("Code"));
        Assert.True(TitleToken.Knows("rider64"));
        Assert.True(TitleToken.Knows("devenv"));
        Assert.True(TitleToken.Knows("RemoteDesktopManager"));

        // Browsers are excluded deliberately: a tab title is the page, not a container.
        Assert.False(TitleToken.Knows("msedge"));
        Assert.False(TitleToken.Knows("chrome"));
        // ...and so are single-window apps, where the roster already has the answer.
        Assert.False(TitleToken.Knows("Beeper"));
    }

    // The sweep is the safety net for a dropped NAMECHANGE, and it has one more job here than it does
    // for a rule: the wanted name can have CHANGED, so re-applying the ledger's name is not enough.
    [Fact]
    public void The_sweep_catches_a_folder_change_whose_event_went_missing()
    {
        var manager = Started();
        Appears(monitor, Code(0xA12, "index.ts - TaskSpaces - Visual Studio Code"));
        Assert.True(manager.NameWindowsByFolder(new WindowHandle(0xA12), on: true).IsSuccess);

        // The event arrives, but nothing renames from it: this stands in for the NAMECHANGE that
        // WINEVENT_OUTOFCONTEXT dropped under load, with the app's real title on screen.
        titles.Titles[new WindowHandle(0xA12)] = "seed.ts - dice-to-seed - Visual Studio Code";
        Retitled(monitor, Code(0xA12, "seed.ts - dice-to-seed - Visual Studio Code"));
        titles.Titles[new WindowHandle(0xA12)] = "seed.ts - dice-to-seed - Visual Studio Code";

        manager.ReapplyRenames();

        Assert.Equal("dice-to-seed", TitleOf(0xA12));
    }
}

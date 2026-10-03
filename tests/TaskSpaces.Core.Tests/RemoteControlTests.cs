using System.Text.Json;
using TaskSpaces.Core.Control;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

// Petre: "i want you to add ability to create workspaces and move windows into them programatically,
// so that when i have a claude session start working in a new worktree, i can tell it to move the
// windows to a new workspace which it creates."
//
// RemoteControl is the half of that which knows nothing about pipes: a command line in, an exit code
// and some JSON out. These tests drive it exactly the way a second copy of the exe does, by handing it
// the arguments that followed `ctl`.
public class RemoteControlTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    // A VS Code window opened on a worktree carries the worktree's folder name in its title, which is
    // what makes title matching the natural selector for the flow this exists for.
    static WindowInfo Code(nint hwnd, string folder) =>
        new(new WindowHandle(hwnd), 700, "Code", @"C:\apps\Code.exe", $"main.cs - {folder} - Visual Studio Code", null);

    static WindowInfo Notepad(nint hwnd = 0x50) =>
        new(new WindowHandle(hwnd), 900, "notepad", @"C:\windows\notepad.exe", "notes.txt - Notepad", null);

    WorkspaceManager Started(params WindowInfo[] windows)
    {
        monitor.InitialWindows.AddRange(windows);
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static ControlReply Run(WorkspaceManager manager, params string[] args) => new RemoteControl(manager).Execute(args);

    static JsonElement Json(ControlReply reply) => JsonDocument.Parse(reply.Output).RootElement;

    Guid? DesktopOf(WorkspaceManager manager, string name) =>
        manager.State.Workspaces.Single(w => w.Name == name).DesktopId;

    // --- create ---------------------------------------------------------------------------

    [Fact]
    public void Create_makes_a_workspace_backed_by_a_new_virtual_desktop()
    {
        var manager = Started();

        var reply = Run(manager, "create", "wt-login");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Json(reply).GetProperty("created").GetBoolean());
        Assert.Contains(desktops.Desktops, d => d.Id == DesktopOf(manager, "wt-login"));
    }

    // An agent that runs the same setup twice should not be told it failed. The answer says which it
    // was, so a caller that cares can still tell.
    [Fact]
    public void Creating_a_workspace_that_already_exists_is_not_an_error()
    {
        var manager = Started();
        Run(manager, "create", "wt-login");

        var reply = Run(manager, "create", "WT-LOGIN");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.False(Json(reply).GetProperty("created").GetBoolean());
        Assert.Single(manager.State.Workspaces);
    }

    // --- move -----------------------------------------------------------------------------

    [Fact]
    public void Move_by_title_sends_every_matching_window_and_nothing_else()
    {
        var manager = Started(Code(0x10, "wt-login"), Code(0x11, "main-checkout"), Notepad());
        Run(manager, "create", "Login");

        var reply = Run(manager, "move", "Login", "--title", "WT-LOGIN");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(DesktopOf(manager, "Login"), desktops.WindowPlacements[new WindowHandle(0x10)]);
        Assert.False(desktops.WindowPlacements.ContainsKey(new WindowHandle(0x11)));
        Assert.False(desktops.WindowPlacements.ContainsKey(new WindowHandle(0x50)));
    }

    [Fact]
    public void Move_with_create_makes_the_workspace_when_it_is_missing()
    {
        var manager = Started(Code(0x10, "wt-login"));

        var reply = Run(manager, "move", "wt-login", "--create", "--title", "wt-login");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Json(reply).GetProperty("created").GetBoolean());
        Assert.Equal(DesktopOf(manager, "wt-login"), desktops.WindowPlacements[new WindowHandle(0x10)]);
    }

    // Without --create a typo in the workspace name must not quietly invent a new workspace.
    [Fact]
    public void Move_to_a_missing_workspace_without_create_fails_and_creates_nothing()
    {
        var manager = Started(Code(0x10, "wt-login"));

        var reply = Run(manager, "move", "wt-login", "--title", "wt-login");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Empty(manager.State.Workspaces);
        Assert.Empty(desktops.WindowPlacements);
    }

    // Exit code 3 is what the client's --wait retries on: the window an agent just asked VS Code to
    // open has not appeared yet. Nothing is created in the meantime, so a wrong title does not leave
    // an empty workspace behind.
    [Fact]
    public void Move_that_matches_no_window_reports_nothing_matched_and_creates_nothing()
    {
        var manager = Started(Notepad());

        var reply = Run(manager, "move", "wt-login", "--create", "--title", "wt-login");

        Assert.Equal(ControlReply.NothingMatched, reply.ExitCode);
        Assert.Empty(manager.State.Workspaces);
    }

    [Fact]
    public void Move_by_window_handle_accepts_hex_and_decimal()
    {
        var manager = Started(Code(0x10, "a"), Code(0x11, "b"));
        Run(manager, "create", "Target");

        var reply = Run(manager, "move", "Target", "--hwnd", "0x10", "--hwnd", "17");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(DesktopOf(manager, "Target"), desktops.WindowPlacements[new WindowHandle(0x10)]);
        Assert.Equal(DesktopOf(manager, "Target"), desktops.WindowPlacements[new WindowHandle(0x11)]);
    }

    // The app shortens titles, so the folder name an agent searches for may only survive in the
    // ORIGINAL title the rename ledger kept.
    [Fact]
    public void Title_matching_sees_the_original_title_of_a_renamed_window()
    {
        store.Stored = AppState.Empty with
        {
            RenameRules = [new RenameRule(RuleMatchKind.TitleRegex, "Visual Studio Code", "VS")],
        };
        var code = Code(0x10, "wt-login");
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, code));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, code with { Title = "VS" }));
        Run(manager, "create", "Login");
        // The premise, checked: the live title no longer carries the folder name.
        Assert.Equal("VS", manager.KnownWindows.Single().Title);

        var reply = Run(manager, "move", "Login", "--title", "wt-login");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(DesktopOf(manager, "Login"), desktops.WindowPlacements[code.Handle]);
    }

    [Fact]
    public void Move_needs_at_least_one_selector()
    {
        var manager = Started(Code(0x10, "wt-login"));

        var reply = Run(manager, "move", "Login", "--create");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Empty(manager.State.Workspaces);
    }

    // --- listings ---------------------------------------------------------------------------

    [Fact]
    public void Windows_lists_handle_title_process_and_workspace()
    {
        var manager = Started(Code(0x10, "wt-login"));
        Run(manager, "move", "Login", "--create", "--hwnd", "0x10");

        var reply = Run(manager, "windows");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        var window = Json(reply).EnumerateArray().Single();
        Assert.Equal("0x10", window.GetProperty("hwnd").GetString());
        Assert.Equal("main.cs - wt-login - Visual Studio Code", window.GetProperty("title").GetString());
        Assert.Equal("Code", window.GetProperty("process").GetString());
        Assert.Equal("Login", window.GetProperty("workspace").GetString());
    }

    [Fact]
    public void Workspaces_lists_every_workspace_by_name()
    {
        var manager = Started();
        Run(manager, "create", "One");
        Run(manager, "create", "Two");

        var reply = Run(manager, "workspaces");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(["One", "Two"], Json(reply).EnumerateArray().Select(w => w.GetProperty("name").GetString()));
    }

    // --- the command line itself -------------------------------------------------------------

    [Fact]
    public void An_unknown_verb_fails_with_the_usage_text()
    {
        var manager = Started();

        var reply = Run(manager, "frobnicate");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("usage", reply.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wait_is_taken_off_the_command_line_before_it_is_sent()
    {
        var split = ControlArguments.SplitWait(["move", "x", "--wait", "15", "--title", "y"]);

        Assert.True(split.IsSuccess);
        Assert.Equal(["move", "x", "--title", "y"], split.Value.Forwarded);
        Assert.Equal(TimeSpan.FromSeconds(15), split.Value.Wait);
    }

    [Fact]
    public void No_wait_means_try_once()
    {
        var split = ControlArguments.SplitWait(["windows"]);

        Assert.Equal(TimeSpan.Zero, split.Value.Wait);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-1")]
    public void A_wait_that_is_not_a_number_of_seconds_is_refused(string value) =>
        Assert.True(ControlArguments.SplitWait(["move", "x", "--wait", value]).IsFailure);
}

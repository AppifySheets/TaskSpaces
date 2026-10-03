using System.Text.Json;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Control;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "expose all the commands that are available via context menu", "moving existing workspaces,
// renaming, etc."
//
// One test per menu command at least, each checking the STATE the command left behind rather than only
// its reply, because the reply is built from the state and a test of the reply alone could pass on a
// command that changed nothing.
//
//   row 1  Main            (holds a VS Code window)
//   row 2  [EC]  Services, Alta2PG, UI
//   row 3  Personal
public class RemoteControlMenuTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeActivator activator = new();

    readonly Group ec = new(Guid.NewGuid(), "EC");

    static readonly WindowInfo Code =
        new(new WindowHandle(0x10), 700, "Code", @"C:\apps\Code.exe", "main.cs - wt-login - Visual Studio Code", null);

    WorkspaceManager Started()
    {
        Workspace W(string name, Group? group = null) => new(Guid.NewGuid(), name, Guid.NewGuid()) { GroupId = group?.Id };
        var workspaces = new[] { W("Main"), W("Services", ec), W("Alta2PG", ec), W("UI", ec), W("Personal") };
        workspaces.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        desktops.WindowPlacements[Code.Handle] = workspaces[0].DesktopId!.Value;
        monitor.InitialWindows.Add(Code);
        store.Stored = AppState.Empty with { Workspaces = workspaces, Groups = [ec] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242, activator: activator);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static ControlReply Run(WorkspaceManager manager, params string[] args) => new RemoteControl(manager).Execute(args);

    static JsonElement Json(ControlReply reply) => JsonDocument.Parse(reply.Output).RootElement;

    static IReadOnlyList<string> Order(WorkspaceManager manager) => manager.State.Workspaces.Select(w => w.Name).ToList();

    static Workspace Named(WorkspaceManager manager, string name) => manager.State.Workspaces.Single(w => w.Name == name);

    static void Ok(ControlReply reply) => Assert.True(reply.ExitCode == ControlReply.Ok, reply.Output);

    // --- workspace row menu ------------------------------------------------------------------

    [Fact]
    public void Rename_renames_and_case_does_not_matter_for_the_old_name()
    {
        var manager = Started();

        Ok(Run(manager, "rename", "personal", "Home"));

        Assert.Equal(["Main", "Services", "Alta2PG", "UI", "Home"], Order(manager));
    }

    [Fact]
    public void Rename_refuses_a_name_another_workspace_has()
    {
        var manager = Started();

        var reply = Run(manager, "rename", "Personal", "main");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("Personal", Order(manager));
    }

    // Among peers, as the menu's moves are: an ungrouped row passes the whole EC box in one step.
    [Fact]
    public void Reorder_up_moves_an_ungrouped_row_past_a_whole_group()
    {
        var manager = Started();

        var reply = Run(manager, "reorder", "Personal", "up");

        Ok(reply);
        Assert.Equal(["Main", "Personal", "Services", "Alta2PG", "UI"], Order(manager));
        Assert.Equal(2, Json(reply).GetProperty("row").GetInt32());
    }

    [Fact]
    public void Reorder_inside_a_group_stays_inside_it()
    {
        var manager = Started();

        Ok(Run(manager, "reorder", "UI", "top"));

        Assert.Equal(["Main", "UI", "Services", "Alta2PG", "Personal"], Order(manager));
        Assert.Equal(ec.Id, Named(manager, "UI").GroupId);
    }

    [Theory]
    [InlineData("end", new[] { "Services", "Alta2PG", "UI", "Personal", "Main" })]
    [InlineData("3", new[] { "Services", "Alta2PG", "UI", "Personal", "Main" })]
    [InlineData("down", new[] { "Services", "Alta2PG", "UI", "Main", "Personal" })]
    public void Reorder_to_end_to_a_position_or_down(string where, string[] expected)
    {
        var manager = Started();

        Ok(Run(manager, "reorder", "Main", where));

        Assert.Equal(expected, Order(manager));
    }

    [Fact]
    public void Reorder_refuses_a_direction_it_does_not_know()
    {
        var manager = Started();

        var reply = Run(manager, "reorder", "Main", "sideways");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Equal("Main", Order(manager)[0]);
    }

    [Fact]
    public void Add_child_nests_a_new_workspace_under_its_parent()
    {
        var manager = Started();

        Ok(Run(manager, "add-child", "Personal", "Bills"));

        Assert.Equal(Named(manager, "Personal").GroupId, Named(manager, "Bills").GroupId);
        Assert.Equal(Named(manager, "Personal").Id, manager.State.LendsWindowsTo(Named(manager, "Bills").Id));
    }

    [Fact]
    public void Minimize_row_and_restore_row()
    {
        var manager = Started();

        Ok(Run(manager, "minimize-row", "Main"));
        Assert.True(Named(manager, "Main").Minimized);

        Ok(Run(manager, "restore-row", "Main"));
        Assert.False(Named(manager, "Main").Minimized);
    }

    [Theory]
    [InlineData("teal", "#2E7D6B")]
    [InlineData("#a1b2c3", "#A1B2C3")]
    [InlineData("none", "none")]
    [InlineData("default", null)]
    public void Color_takes_a_palette_name_a_hex_none_or_default(string given, string? stored)
    {
        var manager = Started();
        Run(manager, "color", "Main", "amber");

        Ok(Run(manager, "color", "Main", given));

        Assert.Equal(stored, Named(manager, "Main").Color);
    }

    // The menu's rule, unchanged: colouring a grouped row colours its group.
    [Fact]
    public void Color_on_a_grouped_workspace_colours_its_group()
    {
        var manager = Started();

        Ok(Run(manager, "color", "Alta2PG", "rust"));

        Assert.Equal("#A8562C", manager.State.Groups.Single().Color);
    }

    [Fact]
    public void Color_refuses_what_is_not_a_colour()
    {
        var manager = Started();

        Assert.Equal(ControlReply.Failed, Run(manager, "color", "Main", "chartreuse-ish").ExitCode);
        Assert.Null(Named(manager, "Main").Color);
    }

    [Fact]
    public void Delete_removes_an_empty_workspace_and_its_desktop()
    {
        var manager = Started();
        var desktop = Named(manager, "Personal").DesktopId;

        Ok(Run(manager, "delete", "Personal"));

        Assert.DoesNotContain("Personal", Order(manager));
        Assert.DoesNotContain(desktops.Desktops, d => d.Id == desktop);
    }

    // A command cannot ask "close these?", so closing has to be said.
    [Fact]
    public void Delete_refuses_a_workspace_with_windows_unless_told_to_close_them()
    {
        var manager = Started();

        Assert.Equal(ControlReply.Failed, Run(manager, "delete", "Main").ExitCode);
        Assert.Contains("Main", Order(manager));
        Assert.Empty(activator.Closed);

        Ok(Run(manager, "delete", "Main", "--close-windows"));
        Assert.DoesNotContain("Main", Order(manager));
        Assert.Contains(Code.Handle, activator.Closed);
    }

    [Fact]
    public void Switch_goes_to_the_workspace()
    {
        var manager = Started();

        Ok(Run(manager, "switch", "Personal"));

        Assert.Equal(Named(manager, "Personal").DesktopId, desktops.Switches[^1]);
    }

    // --- group menu ----------------------------------------------------------------------------

    [Fact]
    public void Group_create_makes_a_group_holding_its_first_workspace()
    {
        var manager = Started();

        Ok(Run(manager, "group-create", "Home", "Personal"));

        var home = manager.State.Groups.Single(g => g.Name == "Home");
        Assert.Equal(home.Id, Named(manager, "Personal").GroupId);
    }

    [Fact]
    public void Group_join_puts_the_workspace_at_the_bottom_of_the_group()
    {
        var manager = Started();

        Ok(Run(manager, "group-join", "Main", "ec"));

        Assert.Equal(["Services", "Alta2PG", "UI", "Main", "Personal"], Order(manager));
        Assert.Equal(ec.Id, Named(manager, "Main").GroupId);
    }

    [Fact]
    public void Group_leave_frees_a_member()
    {
        var manager = Started();

        Ok(Run(manager, "group-leave", "UI"));

        Assert.Null(Named(manager, "UI").GroupId);
    }

    [Fact]
    public void Group_rename_and_group_color()
    {
        var manager = Started();

        Ok(Run(manager, "group-rename", "EC", "EuroCredit"));
        Ok(Run(manager, "group-color", "EuroCredit", "steel"));

        var group = manager.State.Groups.Single();
        Assert.Equal("EuroCredit", group.Name);
        Assert.Equal("#2F6FA8", group.Color);
    }

    [Fact]
    public void Ungroup_frees_every_member_and_keeps_their_places()
    {
        var manager = Started();

        Ok(Run(manager, "ungroup", "EC"));

        Assert.Empty(manager.State.Groups);
        Assert.All(manager.State.Workspaces, w => Assert.Null(w.GroupId));
        Assert.Equal(["Main", "Services", "Alta2PG", "UI", "Personal"], Order(manager));
    }

    [Fact]
    public void A_group_that_does_not_exist_is_named_as_missing_with_the_ones_that_do()
    {
        var manager = Started();

        var reply = Run(manager, "group-join", "Main", "BTC");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("EC", reply.Output);
    }

    // --- unnamed desktop menu ----------------------------------------------------------------------

    [Fact]
    public void Desktops_lists_unnamed_ones_and_name_desktop_takes_the_number_it_prints()
    {
        var unnamed = new DesktopInfo(Guid.NewGuid(), "Desktop 6");
        var manager = Started();
        desktops.Desktops.Add(unnamed);

        var listed = Json(Run(manager, "desktops")).EnumerateArray().ToList();
        var row = listed.Single(d => d.GetProperty("id").GetGuid() == unnamed.Id);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("workspace").ValueKind);

        Ok(Run(manager, "name-desktop", row.GetProperty("number").GetInt32().ToString(), "Scratch"));

        Assert.Equal(unnamed.Id, Named(manager, "Scratch").DesktopId);
    }

    // --- window icon menu --------------------------------------------------------------------------

    [Fact]
    public void Rename_window_and_restore_title()
    {
        var manager = Started();

        Ok(Run(manager, "rename-window", "0x10", "Editor"));
        Assert.Equal("Editor", titles.Titles[Code.Handle]);

        Ok(Run(manager, "restore-title", "0x10"));
        Assert.Equal(Code.Title, titles.Titles[Code.Handle]);
    }

    [Fact]
    public void Rename_app_renames_by_process()
    {
        var manager = Started();

        Ok(Run(manager, "rename-app", "0x10", "VS"));

        Assert.Equal("VS", titles.Titles[Code.Handle]);
    }

    [Fact]
    public void Rename_pattern_makes_a_rule_that_renames_matching_windows()
    {
        var manager = Started();

        Ok(Run(manager, "rename-pattern", "*wt-login*", "Login"));

        Assert.Equal("Login", titles.Titles[Code.Handle]);
    }

    [Fact]
    public void Name_by_folder_on_and_off()
    {
        var manager = Started();

        Ok(Run(manager, "name-by-folder", "0x10", "on"));
        Assert.True(manager.NamesByFolder("Code"));

        Ok(Run(manager, "name-by-folder", "0x10", "off"));
        Assert.False(manager.NamesByFolder("Code"));
    }

    [Fact]
    public void Pin_and_unpin()
    {
        var manager = Started();

        Ok(Run(manager, "pin", "0x10"));
        Assert.Contains(Code.Handle, desktops.PinnedWindows);

        Ok(Run(manager, "unpin", "0x10"));
        Assert.DoesNotContain(Code.Handle, desktops.PinnedWindows);
    }

    [Fact]
    public void A_handle_no_window_has_is_refused()
    {
        var manager = Started();

        Assert.Equal(ControlReply.Failed, Run(manager, "rename-window", "0x999", "x").ExitCode);
    }

    // --- bar, and the command line itself ----------------------------------------------------------

    [Fact]
    public void Bar_minimize_and_restore_go_to_whatever_the_app_handed_in()
    {
        var manager = Started();
        var asked = new List<bool>();
        var control = new RemoteControl(manager, visible => { asked.Add(visible); return Result.Success(); });

        Ok(control.Execute(["bar", "minimize"]));
        Ok(control.Execute(["bar", "restore"]));

        Assert.Equal([false, true], asked);
    }

    [Fact]
    public void A_known_command_with_the_wrong_arguments_prints_its_own_usage_line()
    {
        var manager = Started();

        var reply = Run(manager, "rename", "only-one-argument");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        // Written the way it is typed through the batch file, with no `ctl`: following it literally must
        // not send "ctl ctl".
        Assert.Contains("usage: taskspaces.cmd rename <workspace> <new name>", reply.Output);
    }

    // The brief was every menu command. This pins that help lists them all, so a command added to the
    // dispatch without a line in ControlUsage is caught here rather than by a confused agent.
    [Fact]
    public void Help_lists_every_command()
    {
        var help = Run(Started(), "help").Output;

        Assert.All(
            ["workspaces", "windows", "desktops", "create", "move", "rename", "add-child", "reorder", "minimize-row",
             "restore-row", "color", "delete", "switch", "group-create", "group-join", "group-leave", "group-rename",
             "group-color", "ungroup", "name-desktop", "rename-window", "rename-app", "rename-pattern",
             "name-by-folder", "restore-title", "pin", "unpin", "bar"],
            verb => Assert.Contains($"\n  {verb}", help));
    }
}

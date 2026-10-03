using System.Text.Json;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Control;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "let's also incorporate the ability to add a new workspace in a workspace group and at what
// position", then "when adding a new workspace, we should say in which group and, if neither, at what
// position".
//
// The bar below mirrors his: two anchorless groups among ungrouped rows. A position counts the bar's
// TOP-LEVEL rows, a whole group being one row, so no position can drop a workspace into the middle of
// a group's box.
//
//   row 1  Main
//   row 2  [EC]  Services, Alta2PG, UI
//   row 3  Personal
//   row 4  [BTC] slip39, dice
public class RemoteControlPlacementTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    readonly Group ec = new(Guid.NewGuid(), "EC");
    readonly Group btc = new(Guid.NewGuid(), "BTC");

    WorkspaceManager Started()
    {
        Workspace W(string name, Group? group = null) => new(Guid.NewGuid(), name, Guid.NewGuid()) { GroupId = group?.Id };
        var workspaces = new[]
        {
            W("Main"), W("Services", ec), W("Alta2PG", ec), W("UI", ec), W("Personal"), W("slip39", btc), W("dice", btc),
        };
        workspaces.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = workspaces, Groups = [ec, btc] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static ControlReply Run(WorkspaceManager manager, params string[] args) => new RemoteControl(manager).Execute(args);

    static JsonElement Json(ControlReply reply) => JsonDocument.Parse(reply.Output).RootElement;

    // create and move report the workspace they made or found under "workspace".
    static JsonElement Ws(ControlReply reply) => Json(reply).GetProperty("workspace");

    static IReadOnlyList<string> Order(WorkspaceManager manager) => manager.State.Workspaces.Select(w => w.Name).ToList();

    // --- in a group --------------------------------------------------------------------------

    // The bottom of the group, which is where the bar's own "move into group" puts a joiner, and the
    // group stays one contiguous run in the list.
    [Fact]
    public void Create_in_a_group_puts_the_workspace_at_the_bottom_of_that_group()
    {
        var manager = Started();

        var reply = Run(manager, "create", "wt-login", "--group", "ec");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(["Main", "Services", "Alta2PG", "UI", "wt-login", "Personal", "slip39", "dice"], Order(manager));
        Assert.Equal(ec.Id, manager.State.Workspaces.Single(w => w.Name == "wt-login").GroupId);
        Assert.Equal("EC", Ws(reply).GetProperty("group").GetString());
        Assert.Equal(2, Ws(reply).GetProperty("row").GetInt32());
    }

    [Fact]
    public void Create_in_a_group_that_does_not_exist_names_the_groups_there_are_and_creates_nothing()
    {
        var manager = Started();

        var reply = Run(manager, "create", "wt-login", "--group", "Clients");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("EC", reply.Output);
        Assert.Contains("BTC", reply.Output);
        Assert.DoesNotContain("wt-login", Order(manager));
    }

    [Fact]
    public void Move_with_create_can_put_the_new_workspace_in_a_group()
    {
        var code = new WindowInfo(new WindowHandle(0x10), 700, "Code", @"C:\apps\Code.exe", "a - wt-btc - Visual Studio Code", null);
        monitor.InitialWindows.Add(code);
        var manager = Started();

        var reply = Run(manager, "move", "wt-btc", "--create", "--group", "BTC", "--title", "wt-btc");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        var created = manager.State.Workspaces.Single(w => w.Name == "wt-btc");
        Assert.Equal(btc.Id, created.GroupId);
        Assert.Equal(created.DesktopId, desktops.WindowPlacements[code.Handle]);
    }

    // --- at a position -----------------------------------------------------------------------

    [Fact]
    public void Position_one_is_the_top_of_the_bar()
    {
        var manager = Started();

        var reply = Run(manager, "create", "wt-login", "--position", "1");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("wt-login", Order(manager)[0]);
        Assert.Null(manager.State.Workspaces[0].GroupId);
        Assert.Equal(1, Ws(reply).GetProperty("row").GetInt32());
    }

    // Row 3 is Personal, the row after EC's box. The new workspace takes row 3 and lands after the
    // WHOLE group, never between Alta2PG and UI.
    [Fact]
    public void A_position_counts_a_whole_group_as_one_row()
    {
        var manager = Started();

        Run(manager, "create", "wt-login", "--position", "3");

        Assert.Equal(["Main", "Services", "Alta2PG", "UI", "wt-login", "Personal", "slip39", "dice"], Order(manager));
        Assert.Null(manager.State.Workspaces.Single(w => w.Name == "wt-login").GroupId);
    }

    // Past the end is the end, as the bar's own insert treats it, and the reply says which row it got.
    [Fact]
    public void A_position_past_the_last_row_lands_at_the_end_and_says_so()
    {
        var manager = Started();

        var reply = Run(manager, "create", "wt-login", "--position", "99");

        Assert.Equal("wt-login", Order(manager)[^1]);
        Assert.Equal(5, Ws(reply).GetProperty("row").GetInt32());
    }

    [Fact]
    public void Without_group_or_position_a_new_workspace_goes_at_the_end()
    {
        var manager = Started();

        Run(manager, "create", "wt-login");

        Assert.Equal("wt-login", Order(manager)[^1]);
        Assert.Null(manager.State.Workspaces[^1].GroupId);
    }

    // The row menu's "Insert before/after", which works at the target's depth: beside a group member the
    // new workspace joins the group, beside an ungrouped row it stays ungrouped.
    [Fact]
    public void Before_a_group_member_inserts_into_the_group_at_that_place()
    {
        var manager = Started();

        Run(manager, "create", "wt-login", "--before", "UI");

        Assert.Equal(["Main", "Services", "Alta2PG", "wt-login", "UI", "Personal", "slip39", "dice"], Order(manager));
        Assert.Equal(ec.Id, manager.State.Workspaces.Single(w => w.Name == "wt-login").GroupId);
    }

    [Fact]
    public void After_an_ungrouped_row_inserts_an_ungrouped_workspace_right_after_it()
    {
        var manager = Started();

        Run(manager, "create", "wt-login", "--after", "Personal");

        Assert.Equal(["Main", "Services", "Alta2PG", "UI", "Personal", "wt-login", "slip39", "dice"], Order(manager));
        Assert.Null(manager.State.Workspaces.Single(w => w.Name == "wt-login").GroupId);
    }

    // --- what is refused -----------------------------------------------------------------------

    // Petre's rule: a group, or else a position. Both at once would mean a position INSIDE the group,
    // which is not what either option says.
    [Fact]
    public void Group_and_position_together_are_refused()
    {
        var manager = Started();

        var reply = Run(manager, "create", "wt-login", "--group", "EC", "--position", "2");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.DoesNotContain("wt-login", Order(manager));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("top")]
    public void A_position_must_be_a_row_number_from_one(string position)
    {
        var manager = Started();

        var reply = Run(manager, "create", "wt-login", "--position", position);

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.DoesNotContain("wt-login", Order(manager));
    }

    // On a move, placement only means something when the workspace is being created. Accepting it
    // silently for an existing workspace would leave the caller believing it had moved the row.
    [Fact]
    public void Move_refuses_placement_without_create()
    {
        var manager = Started();

        var reply = Run(manager, "move", "Personal", "--group", "EC", "--title", "anything");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Null(manager.State.Workspaces.Single(w => w.Name == "Personal").GroupId);
    }

    // Creating what already exists answers created=false and leaves it where it is: the reply's group
    // and row are where it ACTUALLY is, so a caller that asked for somewhere else can see it was not
    // moved.
    [Fact]
    public void Creating_an_existing_workspace_reports_where_it_already_is()
    {
        var manager = Started();

        var reply = Run(manager, "create", "dice", "--position", "1");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.False(Json(reply).GetProperty("created").GetBoolean());
        Assert.Equal("BTC", Ws(reply).GetProperty("group").GetString());
        Assert.Equal(4, Ws(reply).GetProperty("row").GetInt32());
    }

    // --- listing ------------------------------------------------------------------------------

    [Fact]
    public void Workspaces_lists_each_ones_group_and_row()
    {
        var manager = Started();

        var listed = Json(Run(manager, "workspaces")).EnumerateArray()
            .ToDictionary(w => w.GetProperty("name").GetString()!, w => w);

        Assert.Equal(JsonValueKind.Null, listed["Main"].GetProperty("group").ValueKind);
        Assert.Equal(1, listed["Main"].GetProperty("row").GetInt32());
        Assert.Equal("EC", listed["UI"].GetProperty("group").GetString());
        Assert.Equal(2, listed["UI"].GetProperty("row").GetInt32());
        Assert.Equal(4, listed["dice"].GetProperty("row").GetInt32());
    }
}

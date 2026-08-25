using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// The question asked before a workspace is deleted, which lived inline in the bar's context menu
// until closing its windows became one of the answers (Petre: "delete workspace in context menu,
// close all windows in it").
//
// Pulled out here because it is now the ONLY thing standing between a mis-aimed right-click and
// somebody's unsaved work, and a MessageBox cannot be tested. The bar shows what this builds and
// decides nothing about the wording itself.
public class DeleteWorkspacePromptTests
{
    static readonly Guid Doomed = Guid.NewGuid(), Keeper = Guid.NewGuid(), Third = Guid.NewGuid();
    static readonly Guid Box = Guid.NewGuid();

    static AppState With(params Workspace[] workspaces) =>
        AppState.Empty with { Workspaces = workspaces.ToList() };

    static Workspace Ws(Guid id, string name, Guid? group = null) =>
        new(id, name, Guid.NewGuid()) { GroupId = group };

    [Fact]
    public void It_names_the_workspace_and_says_the_delete_cannot_be_undone()
    {
        var text = DeleteWorkspacePrompt.For(With(Ws(Doomed, "Sparrow")), Doomed, []).Value;

        Assert.Contains("'Sparrow'", text);
        Assert.Contains("cannot be undone", text);
    }

    // A workspace that has gone while the menu sat open has no question to ask. None rather than a
    // sentence about a workspace that is not there.
    [Fact]
    public void A_workspace_that_has_gone_has_nothing_to_ask() =>
        Assert.False(DeleteWorkspacePrompt.For(With(Ws(Keeper, "Keeper")), Doomed, []).HasValue);

    // Nothing to close, so nothing about closing: a warning about something that is not happening
    // is worse than no warning.
    [Fact]
    public void An_empty_workspace_is_not_warned_about_windows() =>
        Assert.DoesNotContain("close", DeleteWorkspacePrompt.For(With(Ws(Doomed, "Sparrow")), Doomed, []).Value);

    // The count is what tells the user how much is at stake, and the names are what tells them
    // whether any of it matters.
    [Fact]
    public void Windows_that_will_close_are_counted_and_named()
    {
        var text = DeleteWorkspacePrompt.For(With(Ws(Doomed, "Sparrow")), Doomed, ["Code", "chrome"]).Value;

        Assert.Contains("2 windows", text);
        Assert.Contains("Code", text);
        Assert.Contains("chrome", text);
        Assert.Contains("closed", text);
    }

    [Fact]
    public void One_window_is_not_two()
    {
        var text = DeleteWorkspacePrompt.For(With(Ws(Doomed, "Sparrow")), Doomed, ["Code"]).Value;

        Assert.Contains("1 window ", text);
        Assert.DoesNotContain("1 windows", text);
    }

    // Seven Chrome windows are seven windows and one app. Repeating the name seven times would say
    // less than saying it once, and would push the sentence past reading.
    [Fact]
    public void An_app_with_several_windows_is_named_once()
    {
        var text = DeleteWorkspacePrompt.For(With(Ws(Doomed, "Sparrow")), Doomed, ["chrome", "chrome", "chrome"]).Value;

        Assert.Contains("3 windows", text);
        Assert.Equal(1, text.Split("chrome").Length - 1);
    }

    // Unsaved work is the whole reason this dialog is worth reading, so it is said rather than
    // implied by the word "closed".
    [Fact]
    public void Closing_windows_warns_about_unsaved_work() =>
        Assert.Contains("unsaved", DeleteWorkspacePrompt.For(With(Ws(Doomed, "Sparrow")), Doomed, ["Code"]).Value,
            StringComparison.OrdinalIgnoreCase);

    // --- what happens to the GROUP, which the bar has said all along --------------------------

    [Fact]
    public void A_workspace_in_no_group_says_nothing_about_groups() =>
        Assert.DoesNotContain("group", DeleteWorkspacePrompt.For(With(Ws(Doomed, "Sparrow")), Doomed, []).Value);

    // A group of one is not a group, so the last one left stands alone. Said because the user loses
    // a box they can see on the bar, not just a row.
    [Fact]
    public void Leaving_one_workspace_behind_dissolves_the_group()
    {
        var state = With(Ws(Doomed, "Sparrow", Box), Ws(Keeper, "Keeper", Box)) with
        {
            Groups = [new Group(Box, "Work")],
        };

        var text = DeleteWorkspacePrompt.For(state, Doomed, []).Value;

        Assert.Contains("'Work'", text);
        Assert.Contains("dissolved", text);
    }

    // Losing the ANCHOR is what stops the borrowing, and only the anchor lends windows -- so this is
    // the one case where the group survives but stops doing something it used to do.
    [Fact]
    public void Deleting_the_anchor_says_the_others_stop_borrowing()
    {
        var state = With(Ws(Doomed, "Sparrow", Box), Ws(Keeper, "Keeper", Box), Ws(Third, "Third", Box)) with
        {
            Groups = [new Group(Box, "Work", Doomed)],
        };

        var text = DeleteWorkspacePrompt.For(state, Doomed, []).Value;

        Assert.Contains("no longer show", text);
        Assert.Contains("2 workspaces", text);
    }

    [Fact]
    public void An_ordinary_member_leaves_the_group_standing()
    {
        var state = With(Ws(Doomed, "Sparrow", Box), Ws(Keeper, "Keeper", Box), Ws(Third, "Third", Box)) with
        {
            Groups = [new Group(Box, "Work", Keeper)],
        };

        var text = DeleteWorkspacePrompt.For(state, Doomed, []).Value;

        Assert.Contains("keeps its other 2 workspaces", text);
        Assert.DoesNotContain("dissolved", text);
    }
}

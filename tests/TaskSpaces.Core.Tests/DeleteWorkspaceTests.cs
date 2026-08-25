using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

// Petre (#73): "if the workspace is empty, it is deleted. if it still has windows in it, deletion
// is refused with a message: it can't be deleted until its contents are moved elsewhere."
//
// The refusal is the feature. Windows' own desktop deletion silently reparents that desktop's
// windows onto a neighbour, so a plain delete scatters whatever was in the workspace and the user
// finds out later, by discovering windows somewhere they never put them. There is deliberately no
// "delete anyway" path to test, because there deliberately is not one.
public class DeleteWorkspaceTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeActivator activator = new();

    (WorkspaceManager manager, Guid target, Guid other) Started()
    {
        var target = new Workspace(Guid.NewGuid(), "Doomed", Guid.NewGuid());
        var other = new Workspace(Guid.NewGuid(), "Keeper", Guid.NewGuid());
        new[] { target, other }.ToList()
            .ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [target, other] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store, activator: activator);
        Assert.True(manager.Start().IsSuccess);
        return (manager, target.Id, other.Id);
    }

    static WindowInfo Window(nint handle, string process) =>
        new(new WindowHandle(handle), (int)handle, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    // Puts a real window on a workspace's desktop, the same way the nesting tests do: the monitor
    // reports it, and the fake desktop service says which desktop it is on.
    WindowInfo PlaceWindowOn(WorkspaceManager manager, Guid workspace, nint handle, string process = "slack")
    {
        var desktop = manager.State.Workspaces.Single(w => w.Id == workspace).DesktopId!.Value;
        var window = Window(handle, process);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        desktops.WindowPlacements[window.Handle] = desktop;
        return window;
    }

    [Fact]
    public void An_empty_workspace_is_deleted()
    {
        var (manager, target, other) = Started();

        Assert.True(manager.DeleteWorkspaceIfEmpty(target).IsSuccess);

        Assert.DoesNotContain(manager.State.Workspaces, w => w.Id == target);
        Assert.Contains(manager.State.Workspaces, w => w.Id == other);
    }

    // The desktop goes with it. Leaving the desktop behind would leave an unnamed one on the bar
    // that the user cannot tell from the workspace they just deleted.
    [Fact]
    public void Deleting_it_removes_its_virtual_desktop()
    {
        var (manager, target, _) = Started();
        var desktop = manager.State.Workspaces.Single(w => w.Id == target).DesktopId!.Value;

        Assert.True(manager.DeleteWorkspaceIfEmpty(target).IsSuccess);

        Assert.DoesNotContain(desktops.Desktops, d => d.Id == desktop);
    }

    // Petre: "deleting a named workspace also discards its roster entry (placement memory), yes."
    //
    // The roster is what puts an app back where you last had it, so a stale entry pointing at a
    // workspace that no longer exists is memory of a place that is gone.
    [Fact]
    public void Deleting_it_discards_its_roster_entry_and_rules()
    {
        var (manager, target, _) = Started();
        var window = PlaceWindowOn(manager, target, 0xA);
        Assert.True(manager.AssignWindow(window.Handle, target).IsSuccess);
        Assert.True(manager.SetRules([new WorkspaceRule(target, RuleMatchKind.ProcessName, "slack")], []).IsSuccess);
        Assert.True(manager.State.Inventory.ContainsKey(target));

        // Moved out first, because a workspace holding windows refuses to be deleted at all.
        Assert.True(manager.AssignWindow(window.Handle, manager.State.Workspaces.Single(w => w.Id != target).Id).IsSuccess);
        Assert.True(manager.DeleteWorkspaceIfEmpty(target).IsSuccess);

        Assert.False(manager.State.Inventory.ContainsKey(target));
        Assert.DoesNotContain(manager.State.WorkspaceRules, r => r.WorkspaceId == target);
    }

    [Fact]
    public void A_workspace_with_a_window_in_it_is_refused()
    {
        var (manager, target, _) = Started();
        PlaceWindowOn(manager, target, 0xA);

        var deleted = manager.DeleteWorkspaceIfEmpty(target);

        Assert.True(deleted.IsFailure);
        Assert.Contains("still has", deleted.Error);
        // Still there, and so is its desktop -- a refusal must not half-happen.
        Assert.Contains(manager.State.Workspaces, w => w.Id == target);
        Assert.Contains(desktops.Desktops, d => d.Id == manager.State.Workspaces.Single(w => w.Id == target).DesktopId);
    }

    // The message is the point of the refusal, so it says how many rather than just "not empty":
    // "one window" reads differently from "seven", and the count tells the user how much work
    // moving them is.
    [Fact]
    public void The_refusal_counts_the_windows()
    {
        var (manager, target, _) = Started();
        PlaceWindowOn(manager, target, 0xA);
        PlaceWindowOn(manager, target, 0xB);

        Assert.Contains("2 windows", manager.DeleteWorkspaceIfEmpty(target).Error);
    }

    [Fact]
    public void Moving_the_windows_out_makes_it_deletable()
    {
        var (manager, target, other) = Started();
        var window = PlaceWindowOn(manager, target, 0xA);
        Assert.True(manager.DeleteWorkspaceIfEmpty(target).IsFailure);

        Assert.True(manager.AssignWindow(window.Handle, other).IsSuccess);

        Assert.True(manager.DeleteWorkspaceIfEmpty(target).IsSuccess);
    }

    // A parent whose own row is empty is deletable, and its children are promoted rather than
    // deleted with it (the #42 rule). Nothing is lost: they keep their desktops and their windows,
    // and refusing would leave no way to undo a nesting decision from the bar.
    [Fact]
    public void An_empty_parent_can_go_and_its_children_are_promoted()
    {
        var (manager, target, other) = Started();
        Assert.True(manager.NestWorkspace(other, target).IsSuccess);
        PlaceWindowOn(manager, other, 0xA);

        Assert.True(manager.DeleteWorkspaceIfEmpty(target).IsSuccess);

        Assert.DoesNotContain(manager.State.Workspaces, w => w.Id == target);
        Assert.Null(manager.State.Workspaces.Single(w => w.Id == other).GroupId);
    }

    [Fact]
    public void Deleting_a_workspace_that_does_not_exist_is_refused() =>
        Assert.True(Started().manager.DeleteWorkspaceIfEmpty(Guid.NewGuid()).IsFailure);

    // --- delete AND close what is in it ---------------------------------------------------------
    //
    // Petre: "delete workspace in context menu, close all windows in it."
    //
    // This does NOT reverse the refusal above, which is still what the bar does for a row that
    // looks empty. It answers the case the refusal had no answer for: a workspace you are finished
    // with, whose windows you are finished with too. Moving five windows somewhere else so that a
    // workspace can be deleted, only to close them there, is work with no purpose.
    //
    // What keeps it safe is that the windows are CLOSED rather than left to Windows' own
    // desktop-merge, which scatters them onto a neighbour and lets the user find out later. Asked
    // to close, not killed: an app with unsaved work gets to put its dialog up, and a window that
    // does so is a survivor.

    [Fact]
    public void Deleting_it_and_closing_it_asks_every_window_in_it_to_close()
    {
        var (manager, target, _) = Started();
        var first = PlaceWindowOn(manager, target, 0xA);
        var second = PlaceWindowOn(manager, target, 0xB);

        Assert.True(manager.DeleteWorkspaceClosingWindows(target).IsSuccess);

        Assert.Equal(new[] { first.Handle, second.Handle }, activator.Closed.OrderBy(h => h.Value).ToArray());
        Assert.DoesNotContain(manager.State.Workspaces, w => w.Id == target);
    }

    // The windows of the workspace NEXT DOOR are not part of this, and neither is a pinned one:
    // both would be somebody's work closed by a click aimed at another row.
    [Fact]
    public void Only_the_windows_in_that_workspace_are_asked_to_close()
    {
        var (manager, target, other) = Started();
        var mine = PlaceWindowOn(manager, target, 0xA);
        var theirs = PlaceWindowOn(manager, other, 0xB);
        var pinned = PlaceWindowOn(manager, target, 0xC);
        Assert.True(manager.PinWindow(pinned.Handle).IsSuccess);

        Assert.True(manager.DeleteWorkspaceClosingWindows(target).IsSuccess);

        Assert.Equal(new[] { mine.Handle }, activator.Closed.ToArray());
        Assert.DoesNotContain(activator.Closed, h => h == theirs.Handle);
    }

    // A window that will not go keeps the whole thing from happening. The alternative is deleting
    // the desktop from under it, which is exactly the scatter the refusal above exists to prevent
    // -- and this path would reach it with the user's own consent to something else.
    [Fact]
    public void A_window_that_will_not_close_keeps_the_workspace()
    {
        var (manager, target, _) = Started();
        var stubborn = PlaceWindowOn(manager, target, 0xA, "WINWORD");
        activator.Stubborn.Add(stubborn.Handle);

        var deleted = manager.DeleteWorkspaceClosingWindows(target);

        Assert.True(deleted.IsFailure);
        Assert.Contains(manager.State.Workspaces, w => w.Id == target);
        Assert.Contains(desktops.Desktops, d => d.Id == manager.State.Workspaces.Single(w => w.Id == target).DesktopId);
    }

    // ...and it says WHICH app, because that is the only part the user can act on: the window is
    // asking a question somewhere, and knowing it is Word is the difference between finding it and
    // clicking delete again in confusion.
    [Fact]
    public void The_refusal_names_the_app_that_would_not_close()
    {
        var (manager, target, _) = Started();
        var stubborn = PlaceWindowOn(manager, target, 0xA, "WINWORD");
        activator.Stubborn.Add(stubborn.Handle);
        PlaceWindowOn(manager, target, 0xB, "chrome");

        Assert.Contains("WINWORD", manager.DeleteWorkspaceClosingWindows(target).Error);
    }

    // The ones that DID close stay closed -- there is no putting a closed window back, and
    // pretending otherwise would be a lie in the message. So the refusal is about the workspace
    // only, and says nothing about undoing anything.
    [Fact]
    public void The_windows_that_did_close_are_not_asked_about_again()
    {
        var (manager, target, _) = Started();
        var stubborn = PlaceWindowOn(manager, target, 0xA, "WINWORD");
        var willing = PlaceWindowOn(manager, target, 0xB, "chrome");
        activator.Stubborn.Add(stubborn.Handle);

        Assert.True(manager.DeleteWorkspaceClosingWindows(target).IsFailure);

        Assert.Contains(willing.Handle, activator.Closed);
    }

    [Fact]
    public void An_empty_workspace_goes_with_nothing_asked_to_close()
    {
        var (manager, target, _) = Started();

        Assert.True(manager.DeleteWorkspaceClosingWindows(target).IsSuccess);

        Assert.Empty(activator.Closed);
        Assert.DoesNotContain(manager.State.Workspaces, w => w.Id == target);
    }

    // A borrowed window belongs to the workspace that LENDS it (#42), and the overview already says
    // so: it stays in its parent's row while it is on the child's desktop. Deleting the child
    // therefore must not close it -- the row being deleted never claimed it.
    [Fact]
    public void Deleting_a_child_does_not_close_the_windows_it_borrowed()
    {
        var (manager, parent, child) = Started();
        Assert.True(manager.NestWorkspace(child, parent).IsSuccess);
        var onParent = PlaceWindowOn(manager, parent, 0xA);
        var childDesktop = manager.State.Workspaces.Single(w => w.Id == child).DesktopId!.Value;
        desktops.CurrentDesktopId = childDesktop;
        desktops.CurrentChangedSubject.OnNext(childDesktop);
        Assert.True(desktops.IsPinned(onParent.Handle).Value); // the borrow really happened

        Assert.True(manager.DeleteWorkspaceClosingWindows(child).IsSuccess);

        Assert.Empty(activator.Closed);
    }

    [Fact]
    public void Deleting_and_closing_a_workspace_that_does_not_exist_is_refused() =>
        Assert.True(Started().manager.DeleteWorkspaceClosingWindows(Guid.NewGuid()).IsFailure);
}

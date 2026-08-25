using CSharpFunctionalExtensions;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Domain;

// What the user is asked before a workspace is deleted.
//
// It lived inline in the bar's context menu until closing the windows became one of the answers
// (Petre: "delete workspace in context menu, close all windows in it"). It is out here now for one
// reason: it is the only thing standing between a mis-aimed right-click and somebody's unsaved
// work, and a MessageBox cannot be tested. The wording is decided here and tested; the bar shows it
// and decides nothing.
//
// The delete itself is irreversible in three separate ways and the text says all three, in the
// order they matter to the person reading it: the WINDOWS go first (the only part that can lose
// work), then the workspace's own memory, then what happens to its group. Anything not happening is
// not mentioned -- a warning about a group that is unaffected teaches the reader to skip the dialog.
public static class DeleteWorkspacePrompt
{
    // `closing` is one entry per window that will be closed, named by its app. Per WINDOW rather
    // than pre-deduplicated, because the two halves of the sentence need different things from it:
    // the count is windows ("3 windows"), the names are apps ("chrome", once).
    //
    // None when the workspace is not there any more, which is the menu that sat open while it was
    // deleted from somewhere else. Nothing to ask, so nothing to show.
    public static Maybe<string> For(AppState state, Guid workspaceId, IReadOnlyList<string> closing) =>
        state.Workspaces.FirstOrDefault(w => w.Id == workspaceId) is not { } workspace
            ? Maybe<string>.None
            : $"Delete '{workspace.Name}'?{PARA}" +
              Closing(closing) +
              "Its virtual desktop, its name, its rules and its placement memory all go. " +
              "This cannot be undone." +
              GroupNote(state, workspaceId);

    // The windows, and it is deliberately the first thing after the question. Everything else in
    // this dialog is recoverable by doing the work again; this part is not.
    static string Closing(IReadOnlyList<string> closing) =>
        closing.Count == 0
            ? ""
            : $"{closing.Count} {(closing.Count == 1 ? "window is" : "windows are")} in it and " +
              $"will be closed: {string.Join(", ", closing.Distinct())}. " +
              $"Any unsaved work in them is lost.{PARA}";

    // What happens to the GROUP, and only when something actually happens to it. Which of the three
    // sentences applies depends on how much of the group is left, so the wording follows the real
    // cases rather than promising one of them.
    static string GroupNote(AppState state, Guid workspaceId) =>
        (state.GroupOf(workspaceId), Others(state, workspaceId)) switch
        {
            (null, _) or (_, 0) => "",
            // A group of one is not a group, so the last one left stands alone.
            ({ } group, 1) => $"{PARA}'{group.Name}' is left with one workspace, so the group is " +
                              "dissolved and that workspace stands on its own. It keeps its windows.",
            // Losing the anchor is what stops the borrowing, and only the anchor lends windows.
            ({ } group, var others) when state.IsAnchor(workspaceId) =>
                $"{PARA}'{group.Name}' keeps its other {others} workspaces and its name, but they " +
                "will no longer show this workspace's windows.",
            ({ } group, var others) => $"{PARA}'{group.Name}' keeps its other {others} workspaces.",
        };

    static int Others(AppState state, Guid workspaceId) =>
        state.GroupOf(workspaceId) is { } group
            ? state.Workspaces.Count(w => w.GroupId == group.Id) - 1
            : 0;

    const string PARA = "\n\n";
}

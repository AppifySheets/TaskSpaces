using TaskSpaces.Core.Domain;

namespace TaskSpaces.App;

// ONE OLE drag payload for every TaskSpaces drag surface — WindowGroupsView rows
// (switcher panel + Manage Windows tab) and FloatingBar icons. Promoted out of
// WindowGroupsView (where it started life as a private record) when the floating bar
// gained icon drag: sharing the format string AND the group-key vocabulary means a drag
// can start on one surface and drop on another, and every surface's "dropped onto its
// own group is a no-op" guard stays a plain string comparison.
//
// Carrying the source group alongside the handle (rather than just the bare handle) is
// what makes that no-op guard possible without any extra desktop query at drop time.
sealed record DraggedWindow(WindowHandle Handle, string SourceGroupKey)
{
    internal const string DragFormat = "TaskSpaces.DraggedWindow";

    // Group-key vocabulary, shared verbatim by every surface: 📌 Pinned, a workspace
    // (by workspace id), a plain OS desktop that isn't a workspace (by desktop id —
    // includes the "Unplaced" catch-all, whose DesktopId is Guid.Empty).
    internal const string PinnedGroupKey = "pinned";
    internal static string WorkspaceGroupKey(Guid workspaceId) => $"workspace:{workspaceId}";
    internal static string DesktopGroupKey(Guid desktopId) => $"desktop:{desktopId}";
}

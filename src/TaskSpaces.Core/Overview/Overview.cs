using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Overview;

// One row per live window. OriginalTitle is present when WE renamed it — the UI shows
// both names (Petre: "show me what the new name is vs the original title").
// IsActive marks the focused window so the surfaces can highlight it (Petre: "active window
// should be highlighted in the floating window"). Defaulted, so the many existing
// constructions that predate it stay valid and simply read "not active".
// WillActivate: Petre, on the per-workspace restore -- "make the last active window in that
// workspace look a bit different... so i know what i'm going to have activated when i land on
// that workspace". A PREDICTION, not a history: it marks the window the restore would actually
// focus, so a remembered window that has since closed or moved stops being marked. Never set on
// the current desktop -- you are already there, so there is nothing to land on, and the window
// in question is the one already wearing IsActive.
public sealed record WindowRow(WindowInfo Window, Maybe<string> OriginalTitle, bool IsActive = false, bool WillActivate = false);

// A workspace's slice of the world: live windows + roster entries not running anywhere.
public sealed record WorkspaceGroup(Workspace Workspace, bool IsCurrent, IReadOnlyList<WindowRow> Running, IReadOnlyList<InventoryEntry> NotRunning);

// A desktop that is NOT a TaskSpaces workspace still has a name ("Desktop 1") — its
// windows group under that name, never under a generic "Unassigned" (Petre's ask).
public sealed record DesktopGroup(Guid DesktopId, string Name, bool IsCurrent, IReadOnlyList<WindowRow> Windows);

public sealed record Overview(IReadOnlyList<WindowRow> Pinned, IReadOnlyList<WorkspaceGroup> Workspaces, IReadOnlyList<DesktopGroup> OtherDesktops);

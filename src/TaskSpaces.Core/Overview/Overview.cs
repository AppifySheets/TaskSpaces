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
//
// Monitor / IsMinimized / IsFrontmostOnMonitor: Petre, "sort icons in workspaces by monitors...
// and i want to have the monitor number on the icon", "can you also identify which window is
// minimized, vs not? or which one is on top?", "maybe we can do 1 in bold, if it's on top".
// IsFrontmostOnMonitor is false on every desktop except the current one -- z-order comes from
// EnumWindows, which skips the cloaked windows every other desktop consists of.
public sealed record WindowRow(
    WindowInfo Window,
    Maybe<string> OriginalTitle,
    bool IsActive = false,
    bool WillActivate = false,
    Maybe<int> Monitor = default,
    bool IsMinimized = false,
    // Tri-state on purpose, and the third state is load-bearing. None means "z-order is not
    // knowable here", which is every desktop but the current one -- EnumWindows skips cloaked
    // windows and that is what a window on another desktop is. Petre wants non-front windows
    // dimmed; without the distinction, "not known to be front" would collapse into "behind" and
    // every icon on every other workspace would render dimmed at once.
    Maybe<bool> IsFrontmostOnMonitor = default,
    // Petre: "when there are multiple similar icons, multiple edges, i want them numbered,
    // arbitrarily, if i'm selecting the second browser, i can see that the other, first got
    // demoted in the bar."
    //
    // Set ONLY where the icon alone cannot tell two windows apart -- two or more windows of the
    // same process inside one group. A number on a lone icon would be answering a question
    // nobody asked, which is what sank the monitor badges.
    //
    // Its one hard requirement is that it must NOT move when z-order does: the whole point is to
    // watch an icon change position while keeping its name. Hence derived from handle order (see
    // OverviewBuilder.Ordinals), which is fixed for a window's lifetime.
    Maybe<int> Ordinal = default);

// A workspace's slice of the world: live windows + roster entries not running anywhere.
public sealed record WorkspaceGroup(Workspace Workspace, bool IsCurrent, IReadOnlyList<WindowRow> Running, IReadOnlyList<InventoryEntry> NotRunning);

// A desktop that is NOT a TaskSpaces workspace still has a name ("Desktop 1") — its
// windows group under that name, never under a generic "Unassigned" (Petre's ask).
public sealed record DesktopGroup(Guid DesktopId, string Name, bool IsCurrent, IReadOnlyList<WindowRow> Windows);

public sealed record Overview(IReadOnlyList<WindowRow> Pinned, IReadOnlyList<WorkspaceGroup> Workspaces, IReadOnlyList<DesktopGroup> OtherDesktops);

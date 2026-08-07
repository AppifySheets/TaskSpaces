using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Rehydration;

// Where a window was last deliberately put. Petre: "last placement in the workspace should
// be where it's placed when started" / "last placement IS the rule".
public enum PlacementKind
{
    Workspace, // in workspace WorkspaceId
    Pinned,    // on ALL workspaces (Windows' own pin)
    Detached,  // deliberately dragged onto a plain desktop -- no workspace owns it
}

public sealed record Placement(PlacementKind Kind, Guid WorkspaceId)
{
    // Pinned/Detached carry no workspace, hence Guid.Empty -- used here purely as "not
    // applicable", never as a lookup key (that overload of Guid.Empty already means the
    // "Unplaced" catch-all over in OverviewBuilder, which is a different notion entirely).
    public static Placement Pinned { get; } = new(PlacementKind.Pinned, Guid.Empty);
    public static Placement Detached { get; } = new(PlacementKind.Detached, Guid.Empty);
    public static Placement In(Guid workspaceId) => new(PlacementKind.Workspace, workspaceId);
}

// Reads the persisted placement of a window BY IDENTITY (exe path + args) rather than by
// window handle. That distinction is the whole point:
//
// Petre's defect -- "move Beeper to pinned, close the app, open again, it is incorrectly
// placed in GEPHA workspace" -- happened because Windows' pin is keyed to the HWND, and
// Beeper is an Electron app that closes to tray, destroying and recreating its window. The
// pin died with the old handle and the fresh window appeared on whatever desktop was
// current. Nothing in TaskSpaces recorded the pin, because AppState had nowhere to put it.
//
// Pure by design (no COM, no OS calls): every fact arrives as AppState, so all the
// precedence rules below are unit-testable. Same shape as OverviewBuilder.
public static class PlacementMemory
{
    // Precedence within memory: an identity is in at most ONE of these three states, so
    // order only matters for a hand-edited state.json. Pinned is checked first because it
    // is the least destructive to re-apply (a pin makes a window appear everywhere; it
    // does not move it).
    public static Maybe<Placement> For(string identity, AppState state) =>
        Claims(state.PinnedApps, identity) ? Maybe<Placement>.From(Placement.Pinned)
        : Claims(state.DetachedApps, identity) ? Maybe<Placement>.From(Placement.Detached)
        : WorkspaceClaiming(identity, state).Map(Placement.In);

    // A window with no readable process path (elevated) has no identity, so nothing can be
    // remembered about it.
    public static Maybe<Placement> For(WindowInfo window, AppState state) =>
        RosterIdentity.Of(window).Bind(identity => For(identity, state));

    // The roster IS the workspace half of placement memory: Place() -> RosterAdd() ->
    // AddEntry() already records identity -> workspace on every placement, and AddEntry
    // strips the identity from every OTHER workspace first, so exactly one workspace can
    // claim it. The Count == 1 guard is therefore defensive (a hand-edited state.json):
    // ambiguity is skipped rather than guessed, so a wrong guess can never move a window
    // somewhere Petre never put it.
    static Maybe<Guid> WorkspaceClaiming(string identity, AppState state) =>
        state.Inventory.Where(kv => Claims(kv.Value, identity)).Select(kv => kv.Key).ToList() is { Count: 1 } single
            ? Maybe<Guid>.From(single[0])
            : Maybe<Guid>.None;

    static bool Claims(IReadOnlyList<InventoryEntry> apps, string identity) =>
        apps.Any(entry => RosterIdentity.Of(entry) == identity);
}

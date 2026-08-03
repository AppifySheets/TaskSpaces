using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Persistence;

// The single unit of persistence — everything under %APPDATA%\TaskSpaces\state.json.
// Inventory maps workspace id -> windows last seen in it (for rehydration prompts).
public sealed record AppState(
    IReadOnlyList<Workspace> Workspaces,
    IReadOnlyList<WorkspaceRule> WorkspaceRules,
    IReadOnlyList<RenameRule> RenameRules,
    IReadOnlyDictionary<Guid, IReadOnlyList<InventoryEntry>> Inventory)
{
    // Manual renames that survive restarts (spec §Persistence). Init property with a
    // default so older state.json files (no such key) deserialize to empty, no migration.
    public IReadOnlyList<PersistedRename> PersistedRenames { get; init; } = [];

    // Task 11 (floating icon bar): position + visibility of the always-on-top bar,
    // same init-property/no-migration pattern as PersistedRenames above. Null means
    // "never configured" — older state.json files (no such key) and brand-new installs
    // both deserialize to null, which the App composition root treats as hidden at the
    // default bottom-right position (spec: "older files load with it hidden/default").
    public FloatingBarState? FloatingBar { get; init; }

    // The two placements the Inventory above cannot express (Petre: "last placement IS the
    // rule"). Inventory already records identity -> workspace on every Place(), but a
    // PINNED window belongs to no single workspace and a DETACHED one belongs to none at
    // all, so both needed somewhere to live. Without this, Windows' HWND-keyed pin was
    // simply lost whenever an app recycled its window — an Electron app closing to tray
    // does that routinely, which is exactly how Petre's pinned Beeper reappeared inside a
    // workspace after a restart.
    //
    // InventoryEntry rather than a bare identity string: identical shape to Inventory's
    // values, human-readable in state.json, and RosterIdentity.Of(entry) derives the
    // identity anyway. Init properties with empty defaults, so older state.json files load
    // without migration (same pattern as PersistedRenames and FloatingBar).
    public IReadOnlyList<InventoryEntry> PinnedApps { get; init; } = [];
    public IReadOnlyList<InventoryEntry> DetachedApps { get; init; } = [];

    public static AppState Empty { get; } = new([], [], [], new Dictionary<Guid, IReadOnlyList<InventoryEntry>>());
}

// Task 11: the floating bar's persisted position (DIPs, screen coordinates) and
// whether it should be shown. Left/Top are the bar's own Window.Left/Top at the
// moment it was dragged or hidden — restored verbatim then clamped into whichever
// monitor's work area contains that point (FloatingBar.xaml.cs), since the monitor
// layout itself isn't part of what we persist.
public sealed record FloatingBarState(double Left, double Top, bool Visible);

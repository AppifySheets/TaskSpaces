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

    public static AppState Empty { get; } = new([], [], [], new Dictionary<Guid, IReadOnlyList<InventoryEntry>>());
}

// Task 11: the floating bar's persisted position (DIPs, screen coordinates) and
// whether it should be shown. Left/Top are the bar's own Window.Left/Top at the
// moment it was dragged or hidden — restored verbatim then clamped into whichever
// monitor's work area contains that point (FloatingBar.xaml.cs), since the monitor
// layout itself isn't part of what we persist.
public sealed record FloatingBarState(double Left, double Top, bool Visible);

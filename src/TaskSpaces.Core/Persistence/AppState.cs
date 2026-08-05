using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Persistence;

// The single unit of persistence — everything under %APPDATA%\TaskSpaces\state.json.
// Inventory maps workspace id -> the apps that belong to it. It is the workspace half of
// placement memory: identity -> workspace, written on every placement, read to put a window
// back where you last had it.
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

    // Petre: "i want it configurable" -- the Alt+Tab-style workspace switcher's chord.
    //
    // Stored as the TEXT the user typed, not as a parsed Chord: state.json stays readable
    // and hand-editable, Chord.Parse stays the single place that decides what a shortcut
    // means, and no serializer has to know about virtual-key codes. Same init-property/
    // no-migration pattern as everything above -- an older state.json with no such key
    // deserializes straight to the default.
    //
    // Nothing reads this property directly; WorkspaceManager.SwitcherShortcut does, and
    // falls back to the default for anything unusable.
    public string SwitcherShortcut { get; init; } = DefaultSwitcherShortcut;

    // Petre: "i think ctrl+tab was commonly used for something, give me other something+tab".
    // He was right -- Ctrl+Tab moves between tabs in browsers, VS Code, Rider, Explorer and
    // Office, and a global hotkey is EXCLUSIVE, so binding it takes that away from all of them
    // for as long as TaskSpaces runs.
    //
    // Win+Ctrl+Tab was chosen by MEASUREMENT, not by guessing. Every *+Tab candidate was tried
    // against RegisterHotKey on a real machine, and this was the only one whose forward AND
    // reverse (+Shift) halves were both free: Alt+Tab, Win+Tab, Ctrl+Alt+Tab and Win+Alt+Tab
    // are all already owned by the shell or something running.
    //
    // It also turns out to be the mnemonically right answer. Win+Ctrl is Windows' OWN
    // virtual-desktop prefix -- Win+Ctrl+Left/Right switches desktop, Win+Ctrl+D creates one,
    // Win+Ctrl+F4 closes one -- and a TaskSpaces workspace IS a virtual desktop. So
    // Win+Ctrl+Tab ("walk them by recent use") lands directly beside Win+Ctrl+arrows ("walk
    // them in order"), which is the association a user already has.
    //
    // One caveat worth knowing rather than discovering: releasing the Win key on its own opens
    // Start. Pressing Tab while it is held is what prevents that, so the gesture is safe, but
    // it is the reason a Win-based chord needs a real key press to confirm rather than just a
    // successful registration.
    public const string DefaultSwitcherShortcut = "Win+Ctrl+Tab";


    public static AppState Empty { get; } = new([], [], [], new Dictionary<Guid, IReadOnlyList<InventoryEntry>>());
}

// Task 11: the floating bar's persisted position (DIPs, screen coordinates) and
// whether it should be shown. Left/Top are the bar's own Window.Left/Top at the
// moment it was dragged or hidden — restored verbatim then clamped into whichever
// monitor's work area contains that point (FloatingBar.xaml.cs), since the monitor
// layout itself isn't part of what we persist.
public sealed record FloatingBarState(double Left, double Top, bool Visible)
{
    // Petre: "when adding more windows, the floating window should grow to the left, not to the
    // right... it'll be stacked next to the right edge of the screen."
    //
    // The bar's width follows its content, so the edge that must stay put is the RIGHT one --
    // and that means the right edge, not Left, is what a restore should reproduce. Restoring
    // Left would put the left edge back and let the right edge land wherever this session's
    // width reaches, which for a bar parked against the screen edge is off it.
    //
    // An init property with no default so files written before this key load as null, which
    // PositionFromState reads as "fall back to Left". Left and Top are still written too, so
    // going back to an older build does not lose the position.
    public double? Right { get; init; }
}

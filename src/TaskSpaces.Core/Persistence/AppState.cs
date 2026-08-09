using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Persistence;

// The single unit of persistence -- everything under %APPDATA%\TaskSpaces\state.json.
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
    // "never configured" -- older state.json files (no such key) and brand-new installs
    // both deserialize to null, which the App composition root treats as hidden at the
    // default bottom-right position (spec: "older files load with it hidden/default").
    public FloatingBarState? FloatingBar { get; init; }

    // Petre: "shrink by twenty percent", and he wanted it adjustable rather than baked in.
    // A uniform scale applied to the whole bar; read through BarScaling.Clamp, which supplies
    // the default and tolerates whatever a hand-edited file contains. Null means "never
    // configured" -- same init-property/no-migration pattern as FloatingBar above.
    public double? BarScale { get; init; }

    // Petre: "when i leave the floating window i want it to fade away, still be visible, but much
    // dimmer, so i can see what's behind it better."
    //
    // How dim the bar sits when the pointer is elsewhere. Read through BarFading.Clamp, which
    // supplies the default and tolerates whatever a hand-edited file contains -- same
    // init-property/no-migration pattern as BarScale above, and 1.0 is the honest way to say
    // "never fade", so no separate on/off flag is needed.
    public double? BarIdleOpacity { get; init; }

    // The two placements the Inventory above cannot express (Petre: "last placement IS the
    // rule"). Inventory already records identity -> workspace on every Place(), but a
    // PINNED window belongs to no single workspace and a DETACHED one belongs to none at
    // all, so both needed somewhere to live. Without this, Windows' HWND-keyed pin was
    // simply lost whenever an app recycled its window -- an Electron app closing to tray
    // does that routinely, which is exactly how Petre's pinned Beeper reappeared inside a
    // workspace after a restart.
    //
    // InventoryEntry rather than a bare identity string: identical shape to Inventory's
    // values, human-readable in state.json, and RosterIdentity.Of(entry) derives the
    // identity anyway. Init properties with empty defaults, so older state.json files load
    // without migration (same pattern as PersistedRenames and FloatingBar).
    // Windows we OS-pinned so a nested workspace could show its parent's windows (#42), and the
    // desktop each one came from.
    //
    // Persisted for exactly one reason: crash recovery. Pinning is a real change to the user's
    // machine, and unpinning leaves a window on whatever desktop is current -- so a crash while
    // inside a nested workspace would strand the parent's windows both pinned and homeless. With
    // this, the next start unpins each one and puts it back where it came from.
    //
    // Raw hwnds rather than roster identities, and that is right HERE while being wrong everywhere
    // else in this file: those windows belong to processes that outlive our crash, so their handles
    // are still valid on the next start -- and a handle is exactly what Unpin and MoveWindow need.
    // Anything that no longer exists is simply dropped.
    public IReadOnlyList<InheritedPin> InheritedPins { get; init; } = [];

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


    // Petre, on the update check (#71): "an opt-out setting -- this would be the app's only
    // phone-home behaviour."
    //
    // Opt-OUT rather than opt-in, and default ON: an update check nobody knows to switch on is an
    // update check that never runs, and the whole point is that a portable exe cannot update
    // itself. Defaulted here by the initializer rather than as a nullable, because unlike
    // BarScale there is no third state worth distinguishing -- "never configured" and "on" want
    // exactly the same behaviour, and an older state.json with no such key gets it.
    //
    // What it gates is the network call and nothing else. Everything about the check is inert
    // without it: no request leaves the machine, and nothing is announced.
    public bool CheckForUpdates { get; init; } = true;

    public static AppState Empty { get; } = new([], [], [], new Dictionary<Guid, IReadOnlyList<InventoryEntry>>());
}

// Task 11: the floating bar's persisted position (DIPs, screen coordinates) and
// whether it should be shown. Left/Top are the bar's own Window.Left/Top at the
// moment it was dragged or hidden -- restored verbatim then clamped into whichever
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

    // The vertical twin of Right, and it exists for exactly the same reason on the other axis
    // (#50): the bar's HEIGHT follows its content too -- a workspace inserted from the bar's own
    // menu, a row wrapping onto another line -- so the bottom edge is what a restore has to
    // reproduce. Restoring Top instead would put the top edge back where it was and let the bottom
    // land wherever this session's rows reach, which for a bar parked at the bottom of the work
    // area is off it.
    //
    // Init property with no default, so files written before this key load as null, which
    // PositionFromState reads as "fall back to Top". Top is still written too, so going back to an
    // older build does not lose the position.
    public double? Bottom { get; init; }

    // Petre: "make the floatingwindow resizeable in width and persist it in settings."
    //
    // Null means "never resized", which is every state.json written before this key and every
    // fresh install -- and it is not merely a missing value, it selects a different LAYOUT: the
    // bar stays SizeToContent and rows wrap at the fixed five icons, exactly as they always have.
    // A width here switches both, to an explicit width and a wrap that fits it.
    //
    // Stored as the WINDOW's width, BarScale included, because that is what gets assigned back to
    // Window.Width on the next start. Changing the scale therefore keeps the bar the same size on
    // screen rather than rescaling a number that was chosen by eye at the old scale.
    public double? Width { get; init; }
}

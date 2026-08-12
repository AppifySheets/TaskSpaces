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

    // Which workspace each CONTAINER lives in (#132): the folder a VS Code window has open, the
    // project a JetBrains IDE has loaded, the session Remote Desktop Manager is showing.
    //
    // The Inventory above cannot express this and never could. It keys on exe path plus arguments, so
    // seven VS Code windows started with no arguments are ONE identity: whatever it remembers is
    // remembered for all of them, which is why placement memory deliberately stands down when two
    // live windows share an identity. That left the reboot case with no answer at all, which is #132.
    //
    // Written ONLY when Petre moves a window by hand, never from where a window is found sitting.
    // That was his ruling and the reason is concrete: when he reported this, every VS Code window was
    // sitting in one workspace after a restart, so learning from position would have memorised the
    // very mess being fixed. See WorkspaceManager.LearnContainer.
    //
    // Init property with an empty default, so a state.json written before this key loads with no
    // migration -- same pattern as everything above it.
    public IReadOnlyList<ContainerHome> ContainerHomes { get; init; } = [];

    // Apps whose windows are named after the CONTAINER each one has open (#134), by process name.
    // Petre: "that rename to SPS is bad. let's do smart-rename for windows that are in folders."
    //
    // It replaces what a rename rule can do for these apps rather than extending it. A rule gives one
    // name to every window of an app, which is exactly the wrong shape for an editor: seven VS Code
    // windows all reading "VSC" tell you nothing about which is which, and the "SPS" in his file was a
    // second rename stacked on top of the first, keyed on the title the first one had already written.
    //
    // Process names, and only ones TitleToken.Knows: an app whose title shape is unknown has no
    // container to be named after.
    public IReadOnlyList<string> NameByFolder { get; init; } = [];

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

    // The groups workspaces belong to (#42 anchored, #84 anchorless). Membership lives on the
    // workspace as GroupId; this holds the group's own name and its anchor, if it has one.
    //
    // Empty by default, so a state.json written before groups loads without migration in the
    // deserializer's sense. It still needs Migrated below, because older files record nesting a
    // different way.
    public IReadOnlyList<Group> Groups { get; init; } = [];

    // --- asking about groups ------------------------------------------------------------------
    //
    // Every surface asks these rather than working membership out for itself. The bar, the
    // switcher, the manager's borrowing and the move logic all need the same three answers, and
    // when they each computed their own the answers drifted: that is how #85 happened, with the
    // bar grouping rows one way and the move operations reordering another.

    public Group? GroupOf(Guid workspaceId) =>
        Workspaces.FirstOrDefault(w => w.Id == workspaceId)?.GroupId is { } group
            ? Groups.FirstOrDefault(g => g.Id == group)
            : null;

    // The members of a group, ANCHOR FIRST and everything else in list order.
    //
    // Anchor first is a rendering rule that belongs here rather than in the bar, because the same
    // order decides which member a group's colour comes from. An anchorless group has no anchor to
    // promote, so it is plain list order.
    public IReadOnlyList<Workspace> MembersOf(Guid groupId)
    {
        var members = Workspaces.Where(w => w.GroupId == groupId).ToList();
        return Groups.FirstOrDefault(g => g.Id == groupId)?.AnchorWorkspaceId is { } anchor
            ? members.Where(w => w.Id == anchor).Concat(members.Where(w => w.Id != anchor)).ToList()
            : members;
    }

    // The workspace whose windows this one borrows, or null when there is nothing to borrow.
    //
    // Null for an ungrouped workspace, for a member of an anchorless group (#84's whole point:
    // there is no parent, so there are no windows to lend), and for the anchor itself, which
    // already has its own windows.
    public Guid? LendsWindowsTo(Guid workspaceId) =>
        GroupOf(workspaceId)?.AnchorWorkspaceId is { } anchor && anchor != workspaceId ? anchor : null;

    public bool IsAnchor(Guid workspaceId) => GroupOf(workspaceId)?.AnchorWorkspaceId == workspaceId;

    // The list position a group's colour comes from when it has no colour of its own: its ANCHOR's,
    // or its first member's when there is no anchor. Lane colours follow list position, and a group
    // has one colour (#90), so it needs one position rather than one per member.
    //
    // -1 for a group with no members at all, which the callers already handle as "no colour": every
    // surface that paints a lane accepts a null brush, because a hand-edited state.json has always
    // been able to produce one.
    //
    // Here rather than in the bar and the switcher separately, because both need the same answer and
    // #85 is what two surfaces computing their own answer looks like.
    public int ColourSlotOf(Group group) =>
        Workspaces.ToList().FindIndex(w => w.Id == (group.AnchorWorkspaceId ?? MembersOf(group.Id).FirstOrDefault()?.Id));

    // Turns a pre-groups state.json into one that uses groups, and is a no-op on anything already
    // migrated or on a file that never had nesting.
    //
    // Before groups, a nested workspace carried ParentId pointing at its parent workspace. That
    // shape cannot express a group with a name and no parent workspace, so each distinct parent
    // becomes an ANCHORED group: the anchor is the parent, the name starts as the parent's name,
    // and the members are the parent together with its children.
    //
    // The parent joins its own group, which is the part worth stating. Under ParentId the parent
    // was outside the relationship and merely pointed at from below; a group is a set, and the
    // parent is in it. That is what lets one render path draw both kinds, and what lets an anchored
    // group survive losing its anchor.
    //
    // ParentId is left on the records rather than cleared. Nothing reads it after this, and leaving
    // it means a downgrade to an older build still finds its nesting where it expects it.
    public AppState Migrated()
    {
        // Already migrated, or nothing to migrate. Checked on Groups rather than on ParentId so
        // running it twice cannot produce a second set of groups for the same parents.
        if (Groups.Count > 0) return this;

        var parents = Workspaces
            .Where(w => w.ParentId is not null)
            .Select(w => w.ParentId!.Value)
            .Distinct()
            // A ParentId naming a workspace that no longer exists is dropped rather than turned
            // into a group with a missing anchor: the children simply come out ungrouped, which is
            // what the bar already drew for them.
            .Where(parent => Workspaces.Any(w => w.Id == parent))
            .ToList();

        if (parents.Count == 0) return this;

        var groups = parents
            .Select(parent => new Group(Guid.NewGuid(), Workspaces.Single(w => w.Id == parent).Name, parent))
            .ToList();

        // parent id -> group id, for both the anchor itself and its children.
        var groupOf = groups.ToDictionary(g => g.AnchorWorkspaceId!.Value, g => g.Id);

        return this with
        {
            Groups = groups,
            Workspaces = Workspaces
                .Select(w => (w.ParentId ?? w.Id) is var key && groupOf.TryGetValue(key, out var group)
                    ? w with { GroupId = group }
                    : w)
                .ToList(),
        };
    }

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

namespace TaskSpaces.Core.Domain;

// A named group of windows, backed 1:1 by a Windows virtual desktop.
// DesktopId is the *live* desktop's GUID -- persisted so we can re-bind to the same
// desktop after an app restart, but desktops don't survive reboots, so reconcile
// logic (WorkspaceManager) may re-create the desktop and update this.
// Petre: "i also want different colors for different workspaces in the lanes", "configurable,
// along with shortcuts".
//
// Color is an optional "#RRGGBB" string, and Shortcut an optional chord like "Ctrl+Alt+1".
// Both are appended as OPTIONAL positional parameters so every existing construction site and
// every state.json written before this keeps working untouched.
//
// Why Shortcut lives on the workspace rather than staying implicit: the old Ctrl+Alt+1..9 bound
// by LIST POSITION, so reordering workspaces silently changed what each chord did. Naming the
// chord here makes the binding survive reordering, which is the whole point of being able to
// reorder. Those positional chords have since been removed and nothing reads Shortcut yet -- it
// is the groundwork for per-workspace direct jumps, done right, if they are wanted back.
public sealed record Workspace(Guid Id, string Name, Guid? DesktopId, string? Color = null, string? Shortcut = null)
{
    // Petre: "minimized workspace rows: right-click to shrink a row to a third of its height."
    //
    // A property OF THE WORKSPACE rather than of the bar, which is what makes it survive a restart
    // without a second store to keep in step -- and it is a fact about the workspace anyway
    // ("this one matters less to me right now"), not about the window that draws it.
    //
    // An init property rather than another positional parameter: the list of those is already
    // five long and every one of them is optional, so a sixth would be one more thing to count
    // past at every construction site. Defaults to false, so every state.json written before this
    // loads with every row full size -- no migration, same pattern as everything in AppState.
    public bool Minimized { get; init; }

    // The group this workspace belongs to, or null for one that stands on its own.
    //
    // App-level metadata over flat OS desktops, which is the same move this app already makes by
    // NAMING them: Windows has no notion of a desktop belonging to a set, and inventing one here
    // costs nothing at the OS level because nothing at the OS level is asked to understand it.
    //
    // ONE LEVEL ONLY: a group holds workspaces, never other groups. Not a limitation of the model
    // but of what can be read at a glance on a bar ten rows tall. Deep nesting would need
    // collapsing, and collapsing needs a whole interaction nobody has asked for.
    //
    // This replaced ParentId (#42's original shape), where a nested workspace pointed straight at
    // its parent workspace. That could not express #84's groups, which have a name and no parent
    // workspace at all, so the parent moved into WorkspaceGroup.AnchorWorkspaceId and membership
    // became a group id. AppState.Migrated converts older files, so nothing has to be re-nested by
    // hand.
    public Guid? GroupId { get; init; }

    // Read by the migration only, and never written since. A state.json from before groups records
    // nesting as ParentId on the child, and this is what lets that file still be understood.
    //
    // Deliberately not used anywhere else: everything reads GroupId. Deleting this property would
    // silently un-nest every workspace on the first load after an upgrade, which is data loss that
    // looks like a rendering bug.
    public Guid? ParentId { get; init; }
}

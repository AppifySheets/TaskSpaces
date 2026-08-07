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
public sealed record Workspace(Guid Id, string Name, Guid? DesktopId, string? Color = null, string? Shortcut = null);
